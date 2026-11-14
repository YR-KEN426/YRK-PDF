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
    /// <summary>Body line height = median line height (robust to a heading on short pages); body lines share one font size.</summary>
    public const double BodyLinePercentile = 0.5;
    /// <summary>Lines at least this much taller than the body keep their own size (headings).</summary>
    public const double HeadingLineFactor = 1.3;
    /// <summary>Lines at most this fraction of the body keep their own size (notes/small text).</summary>
    public const double SmallLineFactor = 0.75;
    /// <summary>Lines of short words only and lower than this fraction of the body are stray marks.</summary>
    public const double StrayLineFactor = 0.5;
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
    public static PdfPageText ToPageText(OcrPageResult page, double widthPt, double heightPt, bool filtered = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPt);
        var words = filtered ? FilterWords(page) : page.Words;
        var heights = LineHeights(page, words);
        var body = BodyHeight(heights);
        var spans = words.Select(word =>
        {
            var box = word.Bounds;
            var lineHeight = word.Line < 0 ? LineHeight(word) : heights[word.Line];
            var height = word.Line < 0 || lineHeight >= body * OcrTuning.HeadingLineFactor
                || lineHeight <= body * OcrTuning.SmallLineFactor ? lineHeight : body;
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
        var kept = nonempty.Where(i => !garbage.Contains(page.Words[i].Line)).ToArray();
        // Stray upper marks that Tesseract read confidently: a low line of short pieces only.
        var heights = LineHeights(page, kept.Select(i => page.Words[i]));
        var body = BodyHeight(heights);
        var stray = kept.Where(i => page.Words[i].Line >= 0).GroupBy(i => page.Words[i].Line)
            .Where(g => heights[g.Key] < body * OcrTuning.StrayLineFactor
                && g.All(i => page.Words[i].Text.Trim().Length <= OcrTuning.ShortWordMaxLength)
                && !g.Any(i => IsPageNumber(page.Words[i].Text)))
            .Select(g => g.Key).ToHashSet();
        return kept.Where(i => !stray.Contains(page.Words[i].Line));
    }

    private static bool IsPageNumber(string text)
    {
        var word = text.Trim();
        return word.Length >= 2 && word.All(char.IsDigit);
    }

    private static Dictionary<int, double> LineHeights(OcrPageResult page, IEnumerable<OcrWord> words) =>
        words.Where(w => w.Line >= 0).GroupBy(w => w.Line).ToDictionary(g => g.Key, g => g.Max(LineHeight));

    private static double BodyHeight(Dictionary<int, double> heights)
    {
        if (heights.Count == 0) return 0;
        var sorted = heights.Values.Order().ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * OcrTuning.BodyLinePercentile))];
    }
}
