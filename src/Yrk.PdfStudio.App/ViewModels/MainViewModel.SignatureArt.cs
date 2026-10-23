using System.IO;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Signatures supplied as PDF, and writing placed signatures into the page.</summary>
public sealed partial class MainViewModel
{
    private const int SignaturePdfPixels = 1600;
    private const int InkThreshold = 200;

    /// <summary>
    /// Renders the first page of a signature PDF, finds the ink and returns the artwork cropped to it
    /// (with a small margin). Null when the page is blank.
    /// </summary>
    private async Task<SignatureArt?> PrepareSignaturePdfAsync(byte[] pdf)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "YRK PDF", "signatures"));
        var path = Path.Combine(folder.FullName, Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, pdf);
        try
        {
            await using var document = await _engine.OpenAsync(path);
            var size = document.PageSizes[0];
            var scale = SignaturePdfPixels / Math.Max(size.Width, size.Height);
            int width = Math.Max(1, (int)(size.Width * scale)), height = Math.Max(1, (int)(size.Height * scale));
            var page = await document.RenderPageAsync(0, width, height);
            if (InkBox(page) is not { } ink)
            {
                return null;
            }

            var margin = (int)(4 * scale);
            int x0 = Math.Max(0, ink.X - margin), y0 = Math.Max(0, ink.Y - margin);
            int x1 = Math.Min(width, ink.X + ink.Width + margin), y1 = Math.Min(height, ink.Y + ink.Height + margin);
            var crop = new NormalizedRect((double)x0 / width, (double)y0 / height, (double)(x1 - x0) / width, (double)(y1 - y0) / height);

            var pixels = new byte[(x1 - x0) * (y1 - y0) * 4];
            for (var y = y0; y < y1; y++)
            {
                Buffer.BlockCopy(page.Pixels, (y * page.Stride) + (x0 * 4), pixels, (y - y0) * (x1 - x0) * 4, (x1 - x0) * 4);
            }

            var bitmap = BitmapFactory.Create(new PdfBitmap(x1 - x0, y1 - y0, (x1 - x0) * 4, pixels));
            return SignatureArt.FromPdf(pdf, crop, SignatureImaging.RemovePaper(bitmap));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static PdfPixelRegion? InkBox(PdfBitmap page)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                var o = (y * page.Stride) + (x * 4);
                if (Math.Min(page.Pixels[o], Math.Min(page.Pixels[o + 1], page.Pixels[o + 2])) < InkThreshold)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? null : new PdfPixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Writes a placed signature into the page as vectors, a PDF page or an image.</summary>
    private static Task WriteSignatureAsync(IPdfDocument document, PlacedItemViewModel item, SignatureArt art) => art.Kind switch
    {
        SignatureArtKind.Vector => document.AddVectorAsync(item.PageIndex, art.Vector!, item.Bounds),
        SignatureArtKind.Pdf => document.AddPdfPageAsync(item.PageIndex, art.Pdf!, art.PdfCrop!.Value, item.Bounds),
        _ => document.AddImageAsync(item.PageIndex, ImageConversion.ToPdfImage(art.Preview), item.Bounds),
    };
}
