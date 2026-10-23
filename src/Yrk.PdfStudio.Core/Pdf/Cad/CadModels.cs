namespace Yrk.PdfStudio.Core.Pdf.Cad;

/// <param name="Monochrome">Draw everything black, as plotting with monochrome.ctb does.</param>
public sealed record CadConversionOptions(bool Monochrome = false);

public enum CadTextVertical
{
    Baseline,
    Bottom,
    Middle,
    Top,
}

/// <summary>
/// A text of the drawing, placed on the PDF page. The app shapes it (Thai, fallback fonts) and writes it as
/// real text, because shaping needs the Windows text engine.
/// </summary>
/// <param name="X">Anchor point in PDF points (origin bottom-left).</param>
/// <param name="Height">Capital-letter height in points (AutoCAD's text height).</param>
/// <param name="Rotation">Degrees, counter-clockwise.</param>
/// <param name="HorizontalAlign">Where the anchor is along the text: 0 left, 0.5 centre, 1 right.</param>
/// <param name="FontFile">The text style's font file, for example "romans.shx" or "angsana.ttf".</param>
/// <param name="LayerId">The PDF layer (optional content group) it belongs to.</param>
/// <param name="PageIndex">The page it is on (0 = the first).</param>
public sealed record CadTextItem(
    string Text,
    double X,
    double Y,
    double Height,
    double Rotation,
    double WidthFactor,
    double HorizontalAlign,
    CadTextVertical Vertical,
    string FontFile,
    PdfColor Color,
    string LayerId,
    int PageIndex = 0);

/// <param name="Pdf">
/// The drawing without its text (see <see cref="Texts"/>): the model space and each layout as a page, with a
/// layer per CAD layer.
/// </param>
/// <param name="PageWidth">Size of the first page, in points.</param>
/// <param name="Scale">The scale of the first page, for example "1:25".</param>
/// <param name="Warnings">Kinds of objects that were not drawn, with counts.</param>
public sealed record CadPdfResult(
    byte[] Pdf,
    double PageWidth,
    double PageHeight,
    IReadOnlyList<CadTextItem> Texts,
    string Scale,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The pages in order: "Model" first (when it has anything), then the layouts.</summary>
    public IReadOnlyList<CadPageInfo> Pages { get; init; } = [new("Model", PageWidth, PageHeight, Scale, false)];
}

/// <summary>One page of a converted drawing.</summary>
/// <param name="Scale">The model scale for the model page; "1:1" for a layout (its viewports have their own scales).</param>
public sealed record CadPageInfo(string Name, double Width, double Height, string Scale, bool IsLayout);
