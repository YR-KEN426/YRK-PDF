namespace Yrk.PdfStudio.Core.Pdf.Text;

/// <summary>
/// A TrueType font to embed. <see cref="Data"/> is usually a subset that keeps the original glyph ids
/// (the writer maps its own character codes to them through /CIDToGIDMap).
/// </summary>
/// <param name="Widths">Advance widths per glyph id, in 1/1000 em.</param>
/// <param name="Ascent">In 1/1000 em, positive.</param>
/// <param name="Descent">In 1/1000 em, negative.</param>
public sealed record PdfEmbeddedFont(string PostScriptName, byte[] Data, IReadOnlyDictionary<ushort, double> Widths, double Ascent, double Descent);

/// <summary>One shaped glyph. Advance and offsets are in points; <see cref="OffsetY"/> is positive upwards.</summary>
/// <param name="Text">The characters this glyph stands for (for copy and search).</param>
/// <param name="Outline">
/// The glyph as PDF path operators, in points relative to the glyph origin, y up. A run in which a glyph has
/// no <see cref="Text"/> (the extra glyph when Thai SARA AM is split) or is raised (stacked Thai marks) is
/// drawn with these outlines over invisible text, because real text cannot express it in reading order.
/// </param>
public sealed record PdfGlyph(ushort Id, double Advance, double OffsetX, double OffsetY, string Text, string? Outline = null);

/// <summary>Glyphs in one font, starting at a baseline point given in points from the block's top-left.</summary>
public sealed record PdfGlyphRun(PdfEmbeddedFont Font, double FontSize, double X, double Baseline, IReadOnlyList<PdfGlyph> Glyphs);

/// <summary>Text to write on a page. <see cref="TopLeft"/> is relative to the page as displayed (0..1).</summary>
/// <param name="Rotation">Degrees, counter-clockwise as displayed, around <see cref="TopLeft"/>.</param>
/// <param name="WidthFactor">Horizontal stretch (CAD text styles often use 0.7–0.9).</param>
/// <param name="LayerId">An optional content group ("12 0") the text belongs to, so it is shown and hidden with its layer.</param>
/// <param name="Opacity">0–1; below 1 the text is see-through (watermarks).</param>
/// <param name="Behind">Draw under the page's existing content instead of on top.</param>
public sealed record PdfTextBlock(
    int PageIndex,
    NormalizedPoint TopLeft,
    PdfColor Color,
    IReadOnlyList<PdfGlyphRun> Runs,
    double Rotation = 0,
    double WidthFactor = 1,
    string? LayerId = null,
    double Opacity = 1,
    bool Behind = false);
