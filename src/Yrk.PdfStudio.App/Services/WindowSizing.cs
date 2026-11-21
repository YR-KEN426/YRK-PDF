using System.Windows;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Keeps a window inside the screen's usable area (small screens, 150%/200% scaling).
/// Windows with a large design size shrink to fit; their content scrolls instead of being cut off.</summary>
internal static class WindowSizing
{
    private const double Margin = 24;

    public static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MaxHeight = Math.Min(window.MaxHeight, Math.Max(200, area.Height - Margin));
        window.MaxWidth = Math.Min(window.MaxWidth, Math.Max(300, area.Width - Margin));
        window.MinHeight = Math.Min(window.MinHeight, window.MaxHeight);
        window.MinWidth = Math.Min(window.MinWidth, window.MaxWidth);
    }
}
