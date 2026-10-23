using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Services;

public enum SignatureArtKind
{
    /// <summary>A photo or scan (PNG or JPEG with the paper made transparent).</summary>
    Image,

    /// <summary>Drawn with mouse, pen or touch, typed in a handwriting font, or imported from SVG.</summary>
    Vector,

    /// <summary>The first page of a PDF (as exported by many signing apps), cropped to its ink.</summary>
    Pdf,
}

/// <summary>
/// A signature as it will be written into the document: vector outlines, an embedded PDF page, or an
/// image. <see cref="Preview"/> is a transparent bitmap used on screen and for the signature lists.
/// </summary>
public sealed class SignatureArt
{
    private SignatureArt(SignatureArtKind kind, BitmapSource preview, PdfVectorArt? vector, byte[]? pdf, NormalizedRect? pdfCrop)
    {
        Kind = kind;
        Preview = preview;
        Vector = vector;
        Pdf = pdf;
        PdfCrop = pdfCrop;
    }

    public SignatureArtKind Kind { get; }

    public BitmapSource Preview { get; }

    public PdfVectorArt? Vector { get; }

    public byte[]? Pdf { get; }

    /// <summary>The part of the PDF page with ink (page-relative, top-left origin).</summary>
    public NormalizedRect? PdfCrop { get; }

    /// <summary>Width / height as placed.</summary>
    public double AspectRatio => Kind switch
    {
        SignatureArtKind.Vector => Vector!.Width / Vector.Height,
        _ => (double)Preview.PixelWidth / Preview.PixelHeight,
    };

    public static SignatureArt FromImage(BitmapSource image) => new(SignatureArtKind.Image, image, null, null, null);

    public static SignatureArt FromVector(PdfVectorArt art) => new(SignatureArtKind.Vector, VectorArtBuilder.RenderPreview(art), art, null, null);

    /// <param name="preview">The cropped page as a transparent bitmap.</param>
    public static SignatureArt FromPdf(byte[] pdf, NormalizedRect crop, BitmapSource preview) => new(SignatureArtKind.Pdf, preview, null, pdf, crop);

    // --- Saving (the store encrypts the bytes) -------------------------------------------------

    private static readonly byte[] Magic = "YRKSIG2\n"u8.ToArray();

    private sealed record Stored(string Kind, string? Vector, string? Pdf, double[]? Crop, string Preview);

    public byte[] Serialize()
    {
        var stored = new Stored(
            Kind.ToString(),
            Vector is null ? null : VectorArtBuilder.Serialize(Vector),
            Pdf is null ? null : Convert.ToBase64String(Pdf),
            PdfCrop is { } c ? [c.X, c.Y, c.Width, c.Height] : null,
            Convert.ToBase64String(EncodePng(Preview)));
        return [.. Magic, .. JsonSerializer.SerializeToUtf8Bytes(stored)];
    }

    /// <summary>Reads a saved signature; signatures saved before vector support are plain PNG images.</summary>
    /// <exception cref="FormatException">The data is damaged.</exception>
    public static SignatureArt Deserialize(byte[] data)
    {
        if (!data.AsSpan().StartsWith(Magic))
        {
            return FromImage(DecodePng(data));
        }

        Stored stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(data.AsSpan(Magic.Length)) ?? throw new FormatException("The saved signature is empty.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("The saved signature is damaged.", ex);
        }

        var preview = DecodePng(Convert.FromBase64String(stored.Preview));
        return Enum.Parse<SignatureArtKind>(stored.Kind) switch
        {
            SignatureArtKind.Vector when stored.Vector is { } vector => new SignatureArt(SignatureArtKind.Vector, preview, VectorArtBuilder.Deserialize(vector), null, null),
            SignatureArtKind.Pdf when stored.Pdf is { } pdf && stored.Crop is [var x, var y, var w, var h] =>
                new SignatureArt(SignatureArtKind.Pdf, preview, null, Convert.FromBase64String(pdf), new NormalizedRect(x, y, w, h)),
            _ => FromImage(preview),
        };
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);
        return png.ToArray();
    }

    private static BitmapSource DecodePng(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
