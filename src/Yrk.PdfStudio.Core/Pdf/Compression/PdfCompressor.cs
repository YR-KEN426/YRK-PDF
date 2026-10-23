using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Yrk.PdfStudio.Core.Pdf.Compression;

/// <summary>
/// "Compress PDF" with PDFsharp: image streams are rewritten in place (downsampled, re-encoded as
/// JPEG) and the file is saved without unreachable objects or old revisions. Bookmarks, forms and
/// links are kept because the document structure is untouched.
/// </summary>
/// <remarks>
/// Only images that can be changed safely are touched: 8-bit grey or RGB (device or ICC based), stored
/// as JPEG or Flate, without a colour-key mask or decode array. 1-bit scans (CCITT, JBIG2), JPEG 2000,
/// CMYK and indexed images are left alone, and a new image is kept only when it is clearly smaller.
/// </remarks>
public static class PdfCompressor
{
    private const int MinimumPixels = 64 * 64;
    private const double RequiredSaving = 0.9; // The new stream must be at most 90 % of the old one.

    public static Task<PdfCompressionResult> CompressAsync(
        string sourcePath,
        string? password,
        string outputPath,
        PdfCompressionOptions options,
        IPdfImageCodec codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codec);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Save the compressed copy under a different name.", nameof(outputPath));
        }

        return Task.Run(() => Compress(sourcePath, password, outputPath, options, codec, cancellationToken), cancellationToken);
    }

    private static PdfCompressionResult Compress(
        string sourcePath, string? password, string outputPath, PdfCompressionOptions options, IPdfImageCodec codec, CancellationToken cancellationToken)
    {
        PdfDocument document;
        try
        {
            document = password is null
                ? PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(sourcePath, password, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new PdfException("This PDF can't be compressed (it uses features the compressor does not support).", ex);
        }

        using (document)
        {
            // An image is never shown larger than the biggest page, so this bounds its useful resolution.
            var maxPageInches = document.Pages.Cast<PdfPage>()
                .Select(page => Math.Max(page.Width.Point, page.Height.Point) / 72.0)
                .DefaultIfEmpty(11.7)
                .Max();
            var maxPixels = (int)Math.Ceiling(maxPageInches * options.MaxImageDpi);

            var recompressed = 0;
            foreach (var item in document.Internals.GetAllObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is PdfDictionary { Stream: not null } image
                    && image.Elements.GetName("/Subtype") == "/Image"
                    && TryRecompress(image, maxPixels, options, codec))
                {
                    recompressed++;
                }
            }

            document.Options.CompressContentStreams = true;
            document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;

            var fullOutput = Path.GetFullPath(outputPath);
            var temp = Path.Combine(Path.GetDirectoryName(fullOutput)!, $"~{Path.GetFileNameWithoutExtension(fullOutput)}.{Guid.NewGuid():N}.yrk-saving");
            try
            {
                document.Save(temp);
                File.Move(temp, fullOutput, overwrite: true);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }

            return new PdfCompressionResult(new FileInfo(sourcePath).Length, new FileInfo(fullOutput).Length, recompressed);
        }
    }

    private static bool TryRecompress(PdfDictionary image, int maxPixels, PdfCompressionOptions options, IPdfImageCodec codec)
    {
        var elements = image.Elements;
        var width = elements.GetInteger("/Width");
        var height = elements.GetInteger("/Height");
        var components = ComponentCount(elements["/ColorSpace"]);
        var filter = SingleFilter(elements["/Filter"]);
        if ((long)width * height < MinimumPixels
            || elements.GetInteger("/BitsPerComponent") != 8
            || elements.GetBoolean("/ImageMask")
            || elements.ContainsKey("/Mask")
            || elements.ContainsKey("/Decode")
            || components is not (1 or 3)
            || filter is not ("/DCTDecode" or "/FlateDecode"))
        {
            return false;
        }

        var scale = Math.Min(1.0, (double)maxPixels / Math.Max(width, height));
        var isJpeg = filter == "/DCTDecode";
        if (scale > 0.9 && (isJpeg || !options.RecompressLosslessImages))
        {
            return false; // Already small enough, and no lossless→JPEG conversion was asked for.
        }

        var original = image.Stream!.Value;
        var pixels = isJpeg ? codec.DecodeJpeg(original) : DecodeFlate(image, width, height, components);
        if (pixels is null || pixels.Width != width || pixels.Height != height)
        {
            return false;
        }

        var jpeg = codec.EncodeJpeg(
            pixels,
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)),
            Math.Clamp(options.JpegQuality, 10, 100));
        if (jpeg.Length >= original.Length * RequiredSaving)
        {
            return false;
        }

        image.Stream.Value = jpeg;
        elements.SetName("/Filter", "/DCTDecode");
        elements.Remove("/DecodeParms");
        elements.SetInteger("/Width", Math.Max(1, (int)Math.Round(width * scale)));
        elements.SetInteger("/Height", Math.Max(1, (int)Math.Round(height * scale)));
        return true;
    }

    /// <summary>Raw 8-bit samples of a Flate image, as BGR or grey pixels.</summary>
    private static PdfRawPixels? DecodeFlate(PdfDictionary image, int width, int height, int components)
    {
        byte[] samples;
        try
        {
            samples = image.Stream!.UnfilteredValue;
        }
        catch (Exception)
        {
            return null; // Unsupported predictor or damaged data: leave the image alone.
        }

        var rowBytes = width * components;
        if (samples.Length < rowBytes * height)
        {
            return null;
        }

        if (components == 1)
        {
            return new PdfRawPixels(width, height, rowBytes, PdfPixelFormat.Gray8, samples);
        }

        var bgr = new byte[rowBytes * height];
        for (var i = 0; i < bgr.Length; i += 3)
        {
            bgr[i] = samples[i + 2];
            bgr[i + 1] = samples[i + 1];
            bgr[i + 2] = samples[i];
        }

        return new PdfRawPixels(width, height, rowBytes, PdfPixelFormat.Bgr24, bgr);
    }

    /// <summary>1 or 3 for grey or RGB (device or ICC based); 0 for anything else.</summary>
    private static int ComponentCount(PdfItem? colorSpace)
    {
        switch (Resolve(colorSpace))
        {
            case PdfName name:
                return name.Value switch { "/DeviceGray" => 1, "/DeviceRGB" => 3, _ => 0 };
            case PdfArray { Elements.Count: 2 } array when Resolve(array.Elements[0]) is PdfName { Value: "/ICCBased" }:
                return Resolve(array.Elements[1]) is PdfDictionary profile && profile.Elements.GetInteger("/N") is var n && n is 1 or 3 ? n : 0;
            default:
                return 0;
        }
    }

    private static string? SingleFilter(PdfItem? filter) => Resolve(filter) switch
    {
        PdfName name => name.Value,
        PdfArray { Elements.Count: 1 } array => (Resolve(array.Elements[0]) as PdfName)?.Value,
        _ => null,
    };

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
}
