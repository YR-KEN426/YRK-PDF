namespace Yrk.PdfStudio.Core.Pdf;

public enum PdfPathOp
{
    MoveTo,
    LineTo,

    /// <summary>Cubic Bézier: (X1, Y1) and (X2, Y2) are the control points, (X3, Y3) the end point.</summary>
    CubicTo,
    Close,
}

/// <summary>One path command. A move or line uses only (X1, Y1).</summary>
public readonly record struct PdfPathCommand(PdfPathOp Op, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0, double X3 = 0, double Y3 = 0)
{
    public static PdfPathCommand Move(double x, double y) => new(PdfPathOp.MoveTo, x, y);

    public static PdfPathCommand Line(double x, double y) => new(PdfPathOp.LineTo, x, y);

    public static PdfPathCommand Cubic(double x1, double y1, double x2, double y2, double x3, double y3) =>
        new(PdfPathOp.CubicTo, x1, y1, x2, y2, x3, y3);

    public static PdfPathCommand Closing => new(PdfPathOp.Close);
}

/// <summary>A filled shape: its outline commands, colour and fill rule.</summary>
public sealed record PdfVectorPath(IReadOnlyList<PdfPathCommand> Commands, PdfColor Color, bool EvenOdd = false);

/// <summary>
/// Vector artwork such as a hand-drawn signature, in a box of <paramref name="Width"/> × <paramref name="Height"/>
/// units with the origin at the top left and y going down. It is scaled to the target rectangle when drawn.
/// </summary>
public sealed record PdfVectorArt(double Width, double Height, IReadOnlyList<PdfVectorPath> Paths);
