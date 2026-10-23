namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>
/// A straight horizontal or vertical line drawn on the page (a table border, an underline), in points on the
/// page as displayed, origin at the top-left. X1 ≤ X2 and Y1 ≤ Y2.
/// </summary>
public sealed record PdfRule(double X1, double Y1, double X2, double Y2)
{
    public bool IsHorizontal => X2 - X1 >= Y2 - Y1;

    public double Length => Math.Max(X2 - X1, Y2 - Y1);
}

public enum PdfGraphicKind
{
    /// <summary>A picture (photo, scan, logo).</summary>
    Image,

    /// <summary>Vector drawing: shapes, curves, chart bars. <see cref="PdfGraphic.Fill"/> is set for a plain filled rectangle.</summary>
    Drawing,
}

/// <summary>The box of a picture or drawing on the page, in the same points as <see cref="PdfRule"/>.</summary>
public sealed record PdfGraphic(PdfGraphicKind Kind, double X, double Y, double Width, double Height, PdfColor? Fill = null)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>What is drawn on a page besides text, for rebuilding tables and placing pictures when exporting.</summary>
public sealed record PdfPageGraphics(double Width, double Height, IReadOnlyList<PdfRule> Rules, IReadOnlyList<PdfGraphic> Graphics)
{
    public static PdfPageGraphics Empty(double width, double height) => new(width, height, [], []);
}
