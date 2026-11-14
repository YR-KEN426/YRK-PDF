using Yrk.PdfStudio.Core.Pdf.Export;

namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>Values established by the step 0 OCR/export experiment (2026-11-14).</summary>
public static class OcrTuning
{
    public const float GarbageLineConfidence = 75;
    public const double ShortWordFraction = 0.60;
    public const int ShortWordMaxLength = 2;
    public const double FontHeightMultiplier = 1.1;
    public const double FontSizeStepPt = 0.5;
    public const float ReviewConfidence = 60;
}

/// <summary>Filtered OCR for Word, plain text and review. Never used by the legacy PDF text layer.</summary>
public static class OcrPageText
{
    /// <summary>Retained words in their original order; the input and its word objects are unchanged.</summary>
    public static IReadOnlyList<OcrWord> FilterWords(OcrPageResult page) =>
        RetainedIndices(page).Select(i => page.Words[i]).ToArray();

    /// <summary>Indices in the original page.Words, after garbage-line filtering, with confidence below 60.
    /// Original indices also distinguish duplicate words and can be used to apply review edits.</summary>
    public static IReadOnlySet<int> GetUncertainWordIndices(OcrPageResult page) =>
        RetainedIndices(page).Where(i => page.Words[i].Confidence < OcrTuning.ReviewConfidence).ToHashSet();

    /// <summary>Converts normalized word boxes to displayed page points, preserving SpaceAfter.
    /// Uses one font size per Tesseract line; old words without metadata fall back to their box heights.</summary>
    public static PdfPageText ToPageText(OcrPageResult page, double widthPt, double heightPt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPt);
        var words = FilterWords(page);
        var heights = words.Where(w => w.Line >= 0).GroupBy(w => w.Line)
            .ToDictionary(g => g.Key, g => g.Max(LineHeight));
        var spans = words.Select(word =>
        {
            var box = word.Bounds;
            var height = word.Line >= 0 ? heights[word.Line] : LineHeight(word);
            var size = Math.Max(OcrTuning.FontSizeStepPt,
                Math.Round(height * heightPt * OcrTuning.FontHeightMultiplier / OcrTuning.FontSizeStepPt,
                    MidpointRounding.AwayFromZero) * OcrTuning.FontSizeStepPt);
            return new PdfTextSpan(word.Text + (word.SpaceAfter ? " " : string.Empty),
                box.X * widthPt, box.Y * heightPt, box.Width * widthPt, box.Height * heightPt,
                size, "THSarabunNew", false, false, new PdfColor(0, 0, 0));
        }).ToArray();
        return new PdfPageText(widthPt, heightPt, spans);
    }

    private static double LineHeight(OcrWord word) => word.LineHeight > 0
        ? word.LineHeight : word.LineBounds is { Height: > 0 } line ? line.Height : word.Bounds.Height;

    private static IEnumerable<int> RetainedIndices(OcrPageResult page)
    {
        var nonempty = Enumerable.Range(0, page.Words.Count)
            .Where(i => !string.IsNullOrWhiteSpace(page.Words[i].Text)).ToArray();
        // Missing line metadata must never make unrelated legacy words into one garbage line.
        var garbage = nonempty.Where(i => page.Words[i].Line >= 0).GroupBy(i => page.Words[i].Line)
            .Where(g => g.Average(i => page.Words[i].LineConfidence) < OcrTuning.GarbageLineConfidence
                && g.Count(i => page.Words[i].Text.Trim().Length <= OcrTuning.ShortWordMaxLength)
                    >= g.Count() * OcrTuning.ShortWordFraction)
            .Select(g => g.Key).ToHashSet();
        // Length is character count, never numeric value: 123 and Thai ๑๒๓ are both long words.
        return nonempty.Where(i => !garbage.Contains(page.Words[i].Line));
    }
}
