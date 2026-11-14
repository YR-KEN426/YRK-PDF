using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Yrk.PdfStudio.Core.Pdf.Scan;

/// <summary>Builds mixed JPEG and bilevel scans in one PDF, preserving each page's actual DPI.</summary>
public sealed class ScanPdfBuilder(IPdfEngine engine, ScanPageProcessor processor)
{
    public async Task BuildAsync(ScanSession session, ScanSettings settings, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // The caller owns the scan session and may retry after a failed save.
        var pages = session.Pages;
        if (pages.Count == 0) throw new ArgumentException("Scan at least one page.", nameof(session));
        using var document = NewDocument();
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processed = await Task.Run(() => processor.Process(session.Read(page), settings, page.Rotation, page.Crop, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            page.IsBlank = processed.IsBlank;
            await AddPageAsync(document, processed, session, cancellationToken).ConfigureAwait(false);
        }
        await SaveAsync(document, session, outputPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Also accepts individually processed pages with different color modes.</summary>
    public async Task BuildAsync(IReadOnlyList<ProcessedScanPage> pages, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("Scan at least one page.", nameof(pages));
        using var temporary = new ScanSession();
        using var document = NewDocument();
        foreach (var page in pages)
            await AddPageAsync(document, page, temporary, cancellationToken).ConfigureAwait(false);
        await SaveAsync(document, temporary, outputPath, cancellationToken).ConfigureAwait(false);
    }

    private static PdfDocument NewDocument()
    {
        var document = new PdfDocument();
        document.Info.Creator = "YRK PDF";
        document.Info.Elements.SetString("/Producer", "YRK PDF / PDFsharp 6.2.4");
        document.Options.EnableCcittCompressionForBilevelImages = true;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        return document;
    }

    private async Task AddPageAsync(PdfDocument document, ProcessedScanPage page, ScanSession temporary, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (page.Width <= 0 || page.Height <= 0 || !double.IsFinite(page.DpiX) || !double.IsFinite(page.DpiY)
            || page.DpiX <= 0 || page.DpiY <= 0) throw new ArgumentException("Invalid processed scan.", nameof(page));
        var width = page.Width * 72.0 / page.DpiX;
        var height = page.Height * 72.0 / page.DpiY;
        if (page.ColorMode == ScanColorMode.BlackAndWhite)
        {
            var target = document.AddPage();
            target.Width = XUnit.FromPoint(width);
            target.Height = XUnit.FromPoint(height);
            using var stream = new MemoryStream(page.Data, writable: false);
            using var image = XImage.FromStream(stream);
            image.Interpolate = false;
            using var graphics = XGraphics.FromPdfPage(target);
            graphics.DrawImage(image, 0, 0, width, height);
        }
        else
        {
            var path = temporary.CreateTemporaryPath(".pdf");
            try
            {
                await engine.CreateFromImagesAsync([new PdfJpegInput(page.Data, page.Width, page.Height)],
                    new ImagesToPdfOptions(PdfPageSizeMode.MatchImage, 0, page.DpiX), path, ct).ConfigureAwait(false);
                using var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                var target = document.AddPage(source.Pages[0]);
                // The engine accepts one DPI; scale its content along Y for asymmetric resolutions.
                if (page.DpiX != page.DpiY)
                {
                    var content = target.Contents.PrependContent();
                    var scale = (page.DpiX / page.DpiY).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    content.CreateStream(System.Text.Encoding.ASCII.GetBytes($"q 1 0 0 {scale} 0 0 cm\n"));
                    target.Contents.AppendContent().CreateStream("\nQ"u8.ToArray());
                }
                target.Width = XUnit.FromPoint(width);
                target.Height = XUnit.FromPoint(height);
            }
            finally { temporary.DeleteOwnedPath(path); }
        }
    }

    private static async Task SaveAsync(PdfDocument document, ScanSession temporary, string outputPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var full = Path.GetFullPath(outputPath);
        // Publish only a complete PDF; cancellation/errors never leave a partial destination file.
        var path = temporary.CreateTemporaryPath(".pdf");
        await Task.Run(() => document.Save(path), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Move(path, full, overwrite: false);
    }
}
