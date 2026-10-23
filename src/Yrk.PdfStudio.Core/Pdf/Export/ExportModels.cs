namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>
/// A piece of text in one style on one line of a page. Positions are in points on the page as displayed
/// (rotation applied), origin at the top-left corner.
/// <see cref="CharLefts"/>, when known, is where each character of <see cref="Text"/> starts (marks: their letter's start).
/// </summary>
public sealed record PdfTextSpan(
    string Text,
    double X,
    double Y,
    double Width,
    double Height,
    double FontSize,
    string FontName,
    bool Bold,
    bool Italic,
    PdfColor Color,
    IReadOnlyList<double>? CharLefts = null)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>The text of one page, for exporting to Word, Excel or plain text.</summary>
public sealed record PdfPageText(double Width, double Height, IReadOnlyList<PdfTextSpan> Spans);
