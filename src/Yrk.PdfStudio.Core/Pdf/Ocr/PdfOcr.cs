using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Scan;

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
        var results = await RecognizePagesAsync(document, options, data, progress, cancellationToken).ConfigureAwait(false);
        // Keep the original no-op bytes, and never filter or auto-rotate this legacy path.
        var output = results.Count == 0 ? pdf : AddTextLayer(pdf, results);
        return new OcrSummary(output, results.Count, document.PageCount - results.Count, results.Sum(r => r.Words.Count));
    }

    /// <summary>Recognises unfiltered words without adding a PDF text layer or correcting orientation.</summary>
    public static Task<IReadOnlyList<OcrPageResult>> RecognizePagesAsync(
        IPdfDocument document,
        OcrOptions options,
        OcrLanguageData data,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RecognizePagesCoreAsync(document, options, data, progress, cancellationToken);

    /// <summary>
    /// Opt-in orientation correction followed by OCR on upright pixels. Returns PDF bytes with corrected
    /// /Rotate entries and unfiltered words in that displayed coordinate system. Open the returned Pdf to
    /// obtain corrected page sizes/previews. The caller installs OSD using EnsureOrientationDataAsync first.
    /// </summary>
    public static async Task<OcrDocumentResult> RecognizeOrientedPagesAsync(
        IPdfDocument document,
        byte[] pdf,
        OcrOptions options,
        OcrLanguageData data,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!data.IsInstalled("osd"))
            throw new PdfException("OCR orientation data is missing. Install it using EnsureOrientationDataAsync first.");

        var source = CosDocument.Load(pdf);
        if (source.IsEncrypted)
            throw new PdfException("Password-protected PDFs cannot be made searchable yet. Save an unprotected copy first.");
        var rotations = new Dictionary<int, int>();
        var results = await RecognizePagesCoreAsync(document, options, data, progress, cancellationToken,
            new TesseractScanOrientationDetector(), rotations).ConfigureAwait(false);
        if (rotations.Count == 0) return new OcrDocumentResult(pdf, results);

        var pages = source.GetPages();
        var update = new CosIncrementalUpdate(source);
        foreach (var (index, correction) in rotations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[index];
            var dictionary = page.Dictionary.Clone();
            dictionary["Rotate"] = new CosInteger((page.Rotate + correction) % 360);
            update.Replace(page.Id, dictionary);
        }
        return new OcrDocumentResult(update.Build().Bytes, results);
    }

    private static async Task<IReadOnlyList<OcrPageResult>> RecognizePagesCoreAsync(
        IPdfDocument document, OcrOptions options, OcrLanguageData data,
        IProgress<OcrProgress>? progress, CancellationToken cancellationToken,
        IScanOrientationDetector? detector = null, Dictionary<int, int>? rotations = null)
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
            return [];
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
            if (detector is not null)
            {
                var correction = await Task.Run(() => detector.Detect(bitmap, data.Folder, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (correction is 90 or 180 or 270)
                {
                    bitmap = ScanPageProcessor.Rotate(bitmap, correction.Value, cancellationToken);
                    rotations!.Add(index, correction.Value);
                }
            }
            var words = await Task.Run(() => engine.Recognize(bitmap, cancellationToken), cancellationToken).ConfigureAwait(false);
            results.Add(new OcrPageResult(index, words));
        }

        progress?.Report(new OcrProgress(pages.Count, pages.Count, pages[^1]));
        return results;
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
