namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>A recognised word, positioned relative to the page as displayed (0..1, origin top-left).</summary>
/// <param name="SpaceAfter">True when a space follows in the text (Thai words are usually written without one).</param>
/// <param name="Line">Page-local Tesseract line number; -1 when line metadata is unavailable.</param>
/// <param name="LineHeight">Height of the TextLine box relative to the displayed page height (0..1).</param>
/// <param name="LineConfidence">Tesseract TextLine confidence (0..100), independent of word confidence.</param>
/// <param name="LineBounds">The TextLine box relative to the displayed page, when available.</param>
public sealed record OcrWord(string Text, NormalizedRect Bounds, float Confidence, bool SpaceAfter,
    int Line = -1, double LineHeight = 0, float LineConfidence = 100, NormalizedRect? LineBounds = null);

public sealed record OcrPageResult(int PageIndex, IReadOnlyList<OcrWord> Words);

/// <summary>OCR coordinates refer to Pdf as displayed, after orientation correction. Pdf has no added text layer;
/// pass it and Pages to PdfOcr.AddTextLayer after any review edits. The input document is not modified.</summary>
public sealed record OcrDocumentResult(byte[] Pdf, IReadOnlyList<OcrPageResult> Pages);

public enum OcrPageSelection
{
    /// <summary>Only pages with (almost) no text, typically scans. Pages that already have text are kept as they are.</summary>
    PagesWithoutText,
    AllPages,
}

public enum OcrSegmentationMode
{
    Auto,
    SingleColumn,
}

/// <param name="Languages">Tesseract language codes, for example ["tha", "eng"].</param>
/// <param name="OnlyPages">Optional 0-based page subset, intersected with Pages. Null preserves the original selection.</param>
/// <param name="SegmentationMode">Page layout assumption; Auto preserves legacy OCR and scanner behavior.</param>
public sealed record OcrOptions(IReadOnlyList<string> Languages, OcrPageSelection Pages = OcrPageSelection.PagesWithoutText, int Dpi = 300,
    IReadOnlyCollection<int>? OnlyPages = null, OcrSegmentationMode SegmentationMode = OcrSegmentationMode.Auto);

public sealed record OcrProgress(int Done, int Total, int PageIndex);

public sealed record OcrSummary(byte[] Output, int PagesRecognised, int PagesSkipped, int Words);
