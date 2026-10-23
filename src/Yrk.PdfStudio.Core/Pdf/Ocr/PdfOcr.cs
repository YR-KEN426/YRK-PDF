using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>Makes scanned pages searchable: renders them, recognises the text and adds it as an invisible layer.</summary>
public static class PdfOcr
{
    private const int MinCharactersForTextPage = 20;
    private const int MaxPixelSide = 7000;

    /// <param name="document">The open document, used to find pages without text and to render them.</param>
    /// <param name="pdf">The file bytes of that document (with any pending edits saved).</param>
    public static async Task<OcrSummary> RecognizeAsync(
        IPdfDocument document,
        byte[] pdf,
        OcrOptions options,
        OcrLanguageData data,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pages = new List<int>();
        for (var i = 0; i < document.PageCount; i++)
        {
            if (options.Pages == OcrPageSelection.AllPages
                || (await document.GetPageTextAsync(i, cancellationToken).ConfigureAwait(false)).Count(c => !char.IsWhiteSpace(c)) < MinCharactersForTextPage)
            {
                pages.Add(i);
            }
        }

        if (pages.Count == 0)
        {
            return new OcrSummary(pdf, 0, document.PageCount, 0);
        }

        using var engine = await Task.Run(() => new TesseractOcrEngine(data, options.Languages), cancellationToken).ConfigureAwait(false);
        var results = new List<OcrPageResult>();
        for (var n = 0; n < pages.Count; n++)
        {
            var index = pages[n];
            progress?.Report(new OcrProgress(n, pages.Count, index));
            var size = document.PageSizes[index];
            var scale = Math.Min(options.Dpi / 72.0, MaxPixelSide / Math.Max(size.Width, size.Height));
            var bitmap = await document.RenderPageAsync(
                index,
                Math.Max(1, (int)Math.Round(size.Width * scale)),
                Math.Max(1, (int)Math.Round(size.Height * scale)),
                PdfWorkPriority.Background,
                cancellationToken).ConfigureAwait(false);
            var words = await Task.Run(() => engine.Recognize(bitmap, cancellationToken), cancellationToken).ConfigureAwait(false);
            results.Add(new OcrPageResult(index, words));
        }

        progress?.Report(new OcrProgress(pages.Count, pages.Count, pages[^1]));
        var output = AddTextLayer(pdf, results);
        return new OcrSummary(output, pages.Count, document.PageCount - pages.Count, results.Sum(r => r.Words.Count));
    }

    /// <summary>
    /// Adds invisible text (render mode 3) for the words, as an incremental update. Each word is stretched
    /// over its box, so searching and selecting match the image underneath.
    /// </summary>
    public static byte[] AddTextLayer(byte[] pdf, IReadOnlyList<OcrPageResult> results)
    {
        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
        {
            throw new PdfException("Password-protected PDFs cannot be made searchable yet. Save an unprotected copy first.");
        }

        if (results.All(r => r.Words.Count == 0))
        {
            return pdf;
        }

        var pages = document.GetPages();
        var update = new CosIncrementalUpdate(document);
        var font = GlyphlessFont.AddTo(update);
        foreach (var result in results.Where(r => r.Words.Count > 0))
        {
            if (result.PageIndex < 0 || result.PageIndex >= pages.Count)
            {
                continue;
            }

            var page = pages[result.PageIndex];
            var edit = new CosPageEdit(document, page);
            var name = edit.AddResource("Font", "YrkOcr", font);
            var content = new StringBuilder("BT\n3 Tr\n");
            foreach (var word in result.Words)
            {
                AppendWord(content, page, name, word);
            }

            edit.Append(content.Append("ET\n").ToString());
            edit.Commit(update);
        }

        return update.Build().Bytes;
    }

    private static void AppendWord(StringBuilder content, CosPage page, string font, OcrWord word)
    {
        var text = word.Text;
        if (text.Length == 0)
        {
            return;
        }

        var b = word.Bounds;
        var origin = page.ToUserSpace(b.X, b.Y + b.Height);
        var right = page.ToUserSpace(b.X + b.Width, b.Y + b.Height);
        var top = page.ToUserSpace(b.X, b.Y);
        var width = text.Length * GlyphlessFont.Advance / (double)GlyphlessFont.UnitsPerEm;

        static string F(double v) => CosWriter.FormatReal(v);
        content.Append(CultureInfo.InvariantCulture, $"/{font} 1 Tf {F((right.X - origin.X) / width)} {F((right.Y - origin.Y) / width)} ")
            .Append(CultureInfo.InvariantCulture, $"{F(top.X - origin.X)} {F(top.Y - origin.Y)} {F(origin.X)} {F(origin.Y)} Tm <");
        foreach (var c in word.SpaceAfter ? text + " " : text)
        {
            content.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        content.Append("> Tj\n");
    }
}
