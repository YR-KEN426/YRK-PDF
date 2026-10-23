namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Whole-file tools: combine and images to PDF. Compression lives in <see cref="Compression.PdfCompressor"/>.</summary>
public sealed partial class PdfiumEngine
{
    private static readonly (double Short, double Long) A4 = (595.28, 841.89);
    private static readonly (double Short, double Long) Letter = (612, 792);

    public Task CombineAsync(IReadOnlyList<IPdfDocument> sources, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var documents = sources.Select(source => source as PdfiumDocument
            ?? throw new ArgumentException("Every source must be opened by the PDFium engine.", nameof(sources))).ToArray();
        if (documents.Length == 0)
        {
            throw new ArgumentException("Choose at least one PDF.", nameof(sources));
        }

        return InvokeAsync(
            () =>
            {
                PdfiumDocument.WriteNewDocument(outputPath, target =>
                {
                    var index = 0;
                    foreach (var document in documents)
                    {
                        if (PdfiumNative.FPDF_ImportPagesByIndex(target, document.Handle, null, 0, index) == 0)
                        {
                            return false;
                        }

                        index += document.PageCount;
                    }

                    return true;
                });
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task CreateFromImagesAsync(IReadOnlyList<PdfImageInput> images, ImagesToPdfOptions options, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (images.Count == 0)
        {
            throw new ArgumentException("Choose at least one image.", nameof(images));
        }

        return InvokeAsync(
            () =>
            {
                PdfiumDocument.WriteNewDocument(outputPath, document =>
                {
                    for (var i = 0; i < images.Count; i++)
                    {
                        AddImagePage(document, i, images[i], options);
                    }

                    return true;
                });
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    private static unsafe void AddImagePage(IntPtr document, int index, PdfImageInput input, ImagesToPdfOptions options)
    {
        var (pixelWidth, pixelHeight) = input switch
        {
            PdfJpegInput jpeg => (jpeg.PixelWidth, jpeg.PixelHeight),
            PdfBitmapInput bitmap => (bitmap.Image.Width, bitmap.Image.Height),
            _ => throw new ArgumentException("Unsupported image input.", nameof(input)),
        };
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new PdfException($"Image {index + 1} has no size.");
        }

        double pageWidth, pageHeight, drawWidth, drawHeight;
        if (options.PageSize == PdfPageSizeMode.MatchImage)
        {
            pageWidth = drawWidth = pixelWidth * 72 / options.ImageDpi;
            pageHeight = drawHeight = pixelHeight * 72 / options.ImageDpi;
        }
        else
        {
            var paper = options.PageSize == PdfPageSizeMode.Letter ? Letter : A4;
            var landscape = pixelWidth > pixelHeight;
            pageWidth = landscape ? paper.Long : paper.Short;
            pageHeight = landscape ? paper.Short : paper.Long;
            var scale = Math.Min((pageWidth - (2 * options.Margin)) / pixelWidth, (pageHeight - (2 * options.Margin)) / pixelHeight);
            drawWidth = pixelWidth * scale;
            drawHeight = pixelHeight * scale;
        }

        var page = PdfiumNative.FPDFPage_New(document, index, pageWidth, pageHeight);
        if (page == IntPtr.Zero)
        {
            throw new PdfException($"Page {index + 1} could not be created.");
        }

        try
        {
            var image = PdfiumNative.FPDFPageObj_NewImageObj(document);
            bool loaded;
            if (input is PdfJpegInput jpegInput)
            {
                using var access = new PdfiumIo.MemoryFileAccess(jpegInput.Data);
                loaded = PdfiumNative.FPDFImageObj_LoadJpegFileInline(IntPtr.Zero, 0, image, access.Pointer) != 0;
            }
            else
            {
                var pixels = ((PdfBitmapInput)input).Image;
                fixed (byte* data = pixels.Pixels)
                {
                    var bitmap = PdfiumNative.FPDFBitmap_CreateEx(pixels.Width, pixels.Height, PdfiumNative.FPDFBitmap_BGRA, (IntPtr)data, pixels.Width * 4);
                    loaded = bitmap != IntPtr.Zero && PdfiumNative.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap) != 0;
                    if (bitmap != IntPtr.Zero)
                    {
                        PdfiumNative.FPDFBitmap_Destroy(bitmap);
                    }
                }
            }

            if (!loaded)
            {
                PdfiumNative.FPDFPageObj_Destroy(image);
                throw new PdfException($"Image {index + 1} could not be added. The file may be damaged or in an unsupported format.");
            }

            PdfiumNative.FPDFImageObj_SetMatrix(image, drawWidth, 0, 0, drawHeight, (pageWidth - drawWidth) / 2, (pageHeight - drawHeight) / 2);
            PdfiumNative.FPDFPage_InsertObject(page, image);
            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                throw new PdfException($"Page {index + 1} could not be written.");
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }
}
