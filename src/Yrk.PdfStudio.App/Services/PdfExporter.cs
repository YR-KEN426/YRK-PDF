using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Export;

namespace Yrk.PdfStudio.App.Services;

public enum ExportFormat
{
    Word,
    Excel,
    PowerPoint,
    Png,
    Jpeg,
    Text,
}

/// <param name="Pages">0-based page indices, in order.</param>
/// <param name="Dpi">Resolution of exported images.</param>
/// <param name="JpegQuality">40–100.</param>
/// <param name="SheetPerPage">Excel: one worksheet per page, or all pages one after the other on one sheet.</param>
public sealed record ExportSettings(ExportFormat Format, IReadOnlyList<int> Pages, int Dpi = 200, int JpegQuality = 85, bool SheetPerPage = true)
{
    public string Extension => Format switch
    {
        ExportFormat.Word => ".docx",
        ExportFormat.Excel => ".xlsx",
        ExportFormat.PowerPoint => ".pptx",
        ExportFormat.Png => ".png",
        ExportFormat.Jpeg => ".jpg",
        _ => ".txt",
    };

    public string Filter => Format switch
    {
        ExportFormat.Word => "Word document (*.docx)|*.docx",
        ExportFormat.Excel => "Excel workbook (*.xlsx)|*.xlsx",
        ExportFormat.PowerPoint => "PowerPoint presentation (*.pptx)|*.pptx",
        ExportFormat.Png => "PNG image (*.png)|*.png",
        ExportFormat.Jpeg => "JPG image (*.jpg)|*.jpg",
        _ => "Text file (*.txt)|*.txt",
    };

    public bool IsImage => Format is ExportFormat.Png or ExportFormat.Jpeg;

    /// <summary>Pictures of the pages (images, slides): allowed where printing is, as they show what printing would.</summary>
    public bool IsPicture => IsImage || Format == ExportFormat.PowerPoint;
}

/// <summary>What an export wrote, for the status bar.</summary>
/// <param name="PicturePages">Word pages that had no text and went in as pictures.</param>
public sealed record ExportResult(IReadOnlyList<string> Files, int PicturePages);

/// <summary>Exports pages to Word, Excel, images or text. Everything runs on this computer.</summary>
public static class PdfExporter
{
    /// <summary>Images larger than this (pixels) are made at a lower resolution so they can still be opened.</summary>
    private const double MaxImagePixels = 100_000_000;

    /// <summary>
    /// The files an image export writes: <paramref name="path"/> itself for one page, otherwise
    /// "name-01.png", "name-02.png" and so on (page numbers padded so they sort in order).
    /// </summary>
    public static IReadOnlyList<string> ImagePaths(string path, IReadOnlyList<int> pages, int pageCount)
    {
        if (pages.Count == 1)
        {
            return [path];
        }

        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var digits = pageCount.ToString(CultureInfo.InvariantCulture).Length;
        return pages.Select(p => Path.Combine(folder, $"{name}-{(p + 1).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(2, digits), '0')}{extension}")).ToList();
    }

    public static async Task<ExportResult> ExportAsync(
        IPdfDocument document, ExportSettings settings, string path, IProgress<int> progress, CancellationToken cancellationToken)
    {
        switch (settings.Format)
        {
            case ExportFormat.Png or ExportFormat.Jpeg:
                var files = ImagePaths(path, settings.Pages, document.PageCount);
                for (var i = 0; i < settings.Pages.Count; i++)
                {
                    progress.Report(i);
                    var bytes = await RenderImageAsync(document, settings.Pages[i], settings.Dpi, settings.Format, settings.JpegQuality, cancellationToken);
                    await WriteAtomicallyAsync(files[i], stream => stream.Write(bytes), cancellationToken);
                }

                return new ExportResult(files, 0);

            case ExportFormat.Word:
                var docxPages = new List<DocxPage>();
                var pictures = 0;
                for (var i = 0; i < settings.Pages.Count; i++)
                {
                    progress.Report(i);
                    var page = settings.Pages[i];
                    var text = await document.GetPageTextSpansAsync(page, cancellationToken);
                    if (text.Spans.Count > 0)
                    {
                        // Paragraphs, tables and pictures where they are on the page.
                        var graphics = await document.GetPageGraphicsAsync(page, cancellationToken);
                        var blocks = PageLayout.Analyze(text, graphics);
                        var images = new Dictionary<LayoutPicture, DocxImage>();
                        foreach (var picture in AllPictures(blocks))
                        {
                            images[picture] = await RenderRegionAsync(document, page, picture, cancellationToken);
                        }

                        docxPages.Add(new DocxPage(text.Width, text.Height, [], Blocks: blocks, Images: images));
                        continue;
                    }

                    // A scan: keep what is on the page as a picture.
                    pictures++;
                    var (bytes, width, height) = await RenderAsync(document, page, 150, ExportFormat.Jpeg, 85, cancellationToken);
                    docxPages.Add(new DocxPage(text.Width, text.Height, [], new DocxImage(bytes, "jpeg", width, height)));
                }

                await WriteAtomicallyAsync(path, stream => DocxWriter.Write(stream, docxPages), cancellationToken);
                return new ExportResult([path], pictures);

            case ExportFormat.PowerPoint:
                var slides = new List<PptxSlide>();
                for (var i = 0; i < settings.Pages.Count; i++)
                {
                    progress.Report(i);
                    var size = document.PageSizes[settings.Pages[i]];
                    var (jpeg, _, _) = await RenderAsync(document, settings.Pages[i], 200, ExportFormat.Jpeg, 85, cancellationToken);
                    slides.Add(new PptxSlide(jpeg, size.Width, size.Height));
                }

                await WriteAtomicallyAsync(path, stream => PptxWriter.Write(stream, slides), cancellationToken);
                return new ExportResult([path], 0);

            case ExportFormat.Excel:
                var pageSheets = new List<XlsxSheet>();
                for (var i = 0; i < settings.Pages.Count; i++)
                {
                    progress.Report(i);
                    var page = settings.Pages[i];
                    var text = await document.GetPageTextSpansAsync(page, cancellationToken);
                    var blocks = PageLayout.Analyze(text, await document.GetPageGraphicsAsync(page, cancellationToken));
                    pageSheets.Add(SpreadsheetLayout.Sheet(Loc.F("Page {0}", page + 1), text, blocks));
                }

                IReadOnlyList<XlsxSheet> sheets = settings.SheetPerPage ? pageSheets : [SpreadsheetLayout.Combine(Loc.T("Pages"), pageSheets)];
                await WriteAtomicallyAsync(path, stream => XlsxWriter.Write(stream, sheets), cancellationToken);
                return new ExportResult([path], 0);

            default:
                var all = new StringBuilder();
                for (var i = 0; i < settings.Pages.Count; i++)
                {
                    progress.Report(i);
                    var text = await document.GetPageTextSpansAsync(settings.Pages[i], cancellationToken);
                    if (i > 0)
                    {
                        all.Append("\r\n\r\n");
                    }

                    all.Append(TextLayout.PlainText(text).ReplaceLineEndings("\r\n"));
                }

                // UTF-8 with a byte order mark, so every editor shows Thai correctly.
                var data = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(all.ToString())).ToArray();
                await WriteAtomicallyAsync(path, stream => stream.Write(data), cancellationToken);
                return new ExportResult([path], 0);
        }
    }

    /// <summary>The pictures on a page, those in table cells included.</summary>
    private static IEnumerable<LayoutPicture> AllPictures(IEnumerable<LayoutBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case LayoutPicture picture:
                    yield return picture;
                    break;
                case LayoutTable table:
                    foreach (var inner in AllPictures(table.Cells.SelectMany(c => c.Content)))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// A picture's part of the page, rendered at 200 dpi (less for very large ones): photos as JPEG, drawings
    /// and charts as PNG so their lines and text stay sharp.
    /// </summary>
    private static async Task<DocxImage> RenderRegionAsync(IPdfDocument document, int page, LayoutPicture picture, CancellationToken cancellationToken)
    {
        var size = document.PageSizes[page];
        var scale = 200 / 72.0;
        var pixels = picture.Width * scale * picture.Height * scale;
        if (pixels > 16_000_000)
        {
            scale *= Math.Sqrt(16_000_000 / pixels);
        }

        var pageWidth = Math.Max(1, (int)Math.Round(size.Width * scale));
        var pageHeight = Math.Max(1, (int)Math.Round(size.Height * scale));
        var x = Math.Clamp((int)Math.Floor(picture.X * scale), 0, pageWidth - 1);
        var y = Math.Clamp((int)Math.Floor(picture.Y * scale), 0, pageHeight - 1);
        var width = Math.Clamp((int)Math.Ceiling(picture.Width * scale), 1, pageWidth - x);
        var height = Math.Clamp((int)Math.Ceiling(picture.Height * scale), 1, pageHeight - y);
        var bitmap = await document.RenderPageRegionAsync(page, pageWidth, pageHeight, new PdfPixelRegion(x, y, width, height), PdfWorkPriority.Background, cancellationToken);
        var bytes = await Task.Run(
            () =>
            {
                var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null, bitmap.Pixels, bitmap.Stride);
                BitmapEncoder encoder = picture.IsPhoto ? new JpegBitmapEncoder { QualityLevel = 85 } : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture.IsPhoto ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            },
            cancellationToken);
        return new DocxImage(bytes, picture.IsPhoto ? "jpeg" : "png", bitmap.Width, bitmap.Height);
    }

    private static async Task<byte[]> RenderImageAsync(IPdfDocument document, int page, int dpi, ExportFormat format, int quality, CancellationToken cancellationToken) =>
        (await RenderAsync(document, page, dpi, format, quality, cancellationToken)).Bytes;

    private static async Task<(byte[] Bytes, int Width, int Height)> RenderAsync(
        IPdfDocument document, int page, int dpi, ExportFormat format, int quality, CancellationToken cancellationToken)
    {
        var size = document.PageSizes[page];
        var scale = dpi / 72.0;
        var pixels = size.Width * scale * size.Height * scale;
        if (pixels > MaxImagePixels)
        {
            scale *= Math.Sqrt(MaxImagePixels / pixels); // Very large sheets (A0 at 600 dpi) would not fit in memory.
        }

        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));

        // As printed: form fields without their highlight, comments included, hidden layers left out.
        var bitmap = await document.RenderPageForPrintAsync(page, width, height, grayscale: false, comments: true, cancellationToken);
        var effectiveDpi = scale * 72;
        var bytes = await Task.Run(
            () =>
            {
                var source = BitmapSource.Create(bitmap.Width, bitmap.Height, effectiveDpi, effectiveDpi, PixelFormats.Bgr32, null, bitmap.Pixels, bitmap.Stride);
                BitmapEncoder encoder = format == ExportFormat.Jpeg ? new JpegBitmapEncoder { QualityLevel = quality } : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(format == ExportFormat.Jpeg ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            },
            cancellationToken);
        return (bytes, bitmap.Width, bitmap.Height);
    }

    /// <summary>Writes next to the target first and moves it into place, so a failure never leaves half a file.</summary>
    private static async Task WriteAtomicallyAsync(string path, Action<Stream> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = path + ".yrk-tmp";
        try
        {
            await Task.Run(
                () =>
                {
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        write(stream);
                    }

                    File.Move(temporary, path, overwrite: true);
                },
                cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
