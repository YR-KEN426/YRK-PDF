using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yrk.PdfStudio.App.Imaging;

/// <summary>
/// Draws the visible part of a digital signature, in the familiar layout: the handwritten signature
/// (or the signer's name) on one side and "Digitally signed by … / Date … / Reason …" on the other.
/// </summary>
internal static class DigitalSignatureRenderer
{
    private const double PixelsPerPoint = 4;
    private const double MaxPixels = 4_000_000;
    private const double Gap = 4; // points

    private static readonly Brush NameBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));

    public static BitmapSource Render(double widthPoints, double heightPoints, string signerName, DateTimeOffset time, string? reason, BitmapSource? handwritten)
    {
        var scale = Math.Min(PixelsPerPoint, Math.Sqrt(MaxPixels / Math.Max(1, widthPoints * heightPoints)));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));

            // Side by side when the box is wide, stacked when it is closer to square.
            var sideBySide = widthPoints >= heightPoints * 1.8;
            var first = sideBySide
                ? new Rect(0, 0, (widthPoints - Gap) * 0.45, heightPoints)
                : new Rect(0, 0, widthPoints, (heightPoints - Gap) * 0.5);
            var second = sideBySide
                ? new Rect(first.Right + Gap, 0, widthPoints - first.Width - Gap, heightPoints)
                : new Rect(0, first.Bottom + Gap, widthPoints, heightPoints - first.Height - Gap);

            if (handwritten is not null)
            {
                DrawUniform(context, handwritten, first);
            }
            else
            {
                DrawFitted(context, signerName, first, NameBrush, FontWeights.SemiBold, maxSize: 28, center: true);
            }

            var lines = new List<string>
            {
                "Digitally signed by " + signerName,
                "Date: " + FormatTime(time),
            };
            if (!string.IsNullOrWhiteSpace(reason))
            {
                lines.Add("Reason: " + reason.Trim());
            }

            DrawFitted(context, string.Join("\n", lines), second, TextBrush, FontWeights.Normal, maxSize: 10, center: false);
        }

        return ImageConversion.Render(visual, (int)Math.Ceiling(widthPoints * scale), (int)Math.Ceiling(heightPoints * scale));
    }

    /// <summary>Like Acrobat: 2026.10.18 14:22:01 +07'00'.</summary>
    public static string FormatTime(DateTimeOffset time)
    {
        var offset = time.Offset;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{time:yyyy.MM.dd HH:mm:ss} {(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }

    private static void DrawUniform(DrawingContext context, BitmapSource image, Rect area)
    {
        var aspect = (double)image.PixelWidth / Math.Max(1, image.PixelHeight);
        var width = Math.Min(area.Width, area.Height * aspect);
        var height = width / aspect;
        context.DrawImage(image, new Rect(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    /// <summary>Draws text at the largest size (up to <paramref name="maxSize"/>) that fits the area.</summary>
    private static void DrawFitted(DrawingContext context, string text, Rect area, Brush brush, FontWeight weight, double maxSize, bool center)
    {
        var formatted = TextRendering.Create(text, maxSize, brush, weight);
        var fit = Math.Min(1, Math.Min(area.Width / Math.Max(1, formatted.WidthIncludingTrailingWhitespace), area.Height / Math.Max(1, formatted.Height)));
        formatted = TextRendering.Create(text, Math.Max(2, maxSize * fit), brush, weight);
        if (center)
        {
            formatted.TextAlignment = TextAlignment.Center;
        }

        var x = center ? area.X + (area.Width / 2) : area.X;
        context.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
