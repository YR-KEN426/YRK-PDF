using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Lays out and renders text the same way for the on-page preview and for the saved PDF.
/// WPF shapes Thai correctly (vowel and tone mark placement), so text is saved as a high-resolution
/// transparent image. Saving real, searchable text needs HarfBuzz shaping and font embedding (later phase).
/// </summary>
internal static class TextRendering
{
    /// <summary>Padding around text, in points. The preview TextBlock uses the same value.</summary>
    public const double Padding = 1;

    public const double DefaultPixelsPerPoint = 300.0 / 72.0;

    public static FontFamily Font { get; } = new("Leelawadee UI, Segoe UI");

    public static FormattedText Create(string text, double size, Brush brush, FontWeight? weight = null) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            1.0);

    /// <summary>Text in a chosen font, size, weight and slant (added text).</summary>
    public static FormattedText Create(string text, ViewModels.TextStyle style) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, style.Typeface, style.FontSize, new SolidColorBrush(style.Color), 1.0);

    /// <summary>Size of the rendered text including padding, in points.</summary>
    public static Size Measure(string text, ViewModels.TextStyle style)
    {
        var formatted = Create(text.Length == 0 ? " " : text, style);
        return new Size(formatted.WidthIncludingTrailingWhitespace + (2 * Padding), formatted.Height + (2 * Padding));
    }

    public static BitmapSource Render(string text, ViewModels.TextStyle style, double pixelsPerPoint = DefaultPixelsPerPoint)
    {
        var size = Measure(text, style);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(pixelsPerPoint, pixelsPerPoint));
            context.DrawText(Create(text, style), new Point(Padding, Padding));
        }

        return ImageConversion.Render(
            visual,
            (int)Math.Ceiling(size.Width * pixelsPerPoint),
            (int)Math.Ceiling(size.Height * pixelsPerPoint));
    }
}
