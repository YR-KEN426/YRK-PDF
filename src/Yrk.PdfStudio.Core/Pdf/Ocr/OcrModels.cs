namespace Yrk.PdfStudio.Core.Pdf.Ocr;

/// <summary>A recognised word, positioned relative to the page as displayed (0..1, origin top-left).</summary>
/// <param name="SpaceAfter">True when a space follows in the text (Thai words are usually written without one).</param>
public sealed record OcrWord(string Text, NormalizedRect Bounds, float Confidence, bool SpaceAfter);

public sealed record OcrPageResult(int PageIndex, IReadOnlyList<OcrWord> Words);

public enum OcrPageSelection
{
    /// <summary>Only pages with (almost) no text, typically scans. Pages that already have text are kept as they are.</summary>
    PagesWithoutText,
    AllPages,
}

/// <param name="Languages">Tesseract language codes, for example ["tha", "eng"].</param>
public sealed record OcrOptions(IReadOnlyList<string> Languages, OcrPageSelection Pages = OcrPageSelection.PagesWithoutText, int Dpi = 300);

public sealed record OcrProgress(int Done, int Total, int PageIndex);

public sealed record OcrSummary(byte[] Output, int PagesRecognised, int PagesSkipped, int Words);
