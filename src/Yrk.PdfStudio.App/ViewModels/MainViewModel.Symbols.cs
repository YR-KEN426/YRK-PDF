using System.Windows.Media;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Symbols for the Insert menu (ticks, crosses, boxes, arrows): vector outlines placed like a picture, so they stay
/// sharp at any size. They take the colour chosen on the toolbar.
/// </summary>
public sealed partial class MainViewModel
{
    public const string SymbolFont = "Segoe UI Symbol";

    private const double SymbolHeight = 18;

    public static IReadOnlyList<string> Symbols { get; } =
        ["✓", "✗", "☐", "☑", "☒", "●", "○", "■", "□", "★", "☆", "◆", "▲", "▼", "→", "←", "↑", "↓", "※"];

    private bool _yellowBeforeDrawing;

    /// <summary>The first colour is the highlighter yellow, which hardly shows as a line or a symbol on paper.</summary>
    private Color DrawingColor => _annotationColor == Palette[0] ? Colors.Black : _annotationColor;

    /// <summary>Chooses a drawing tool from the Insert menu, with a colour that shows on paper.</summary>
    public void ChooseDrawingTool(AnnotationTool tool)
    {
        _yellowBeforeDrawing = _annotationColor == Palette[0];
        AnnotationColor = DrawingColor;
        CurrentTool = tool;
    }

    /// <summary>Starts placing a symbol: the next click on a page puts it there.</summary>
    public void PlaceSymbol(string symbol)
    {
        if (_document is null || VectorArtBuilder.FromText(symbol, new FontFamily(SymbolFont), DrawingColor) is not { } outline)
        {
            return;
        }

        var art = SignatureArt.FromVector(outline);
        CurrentTool = AnnotationTool.Select; // Afterwards a drag moves things; it does not draw.
        BeginPlacement(new Placement(
            PlacedItemKind.Picture,
            art.Preview,
            (SymbolHeight * art.AspectRatio, SymbolHeight),
            "Click where the symbol should go. Press Esc to cancel.",
            art));
    }
}
