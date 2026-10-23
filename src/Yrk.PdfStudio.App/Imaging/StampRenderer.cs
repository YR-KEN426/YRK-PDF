using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yrk.PdfStudio.App.Imaging;

/// <param name="Purpose">What the copy may be used for, e.g. "สมัครงานบริษัท ABC". Null to omit.</param>
/// <param name="Signature">Signature drawn inside the stamp. Null to sign separately.</param>
public sealed record CertifiedCopyOptions(string? Purpose, bool IncludeDate, BitmapSource? Signature);

/// <summary>One line of a stamp's text, its top-left corner in points from the stamp's top-left.</summary>
public sealed record StampLine(string Text, double FontSize, bool Bold, Point TopLeft);

/// <summary>
/// A stamp as shown (<see cref="Image"/>) and as saved: <see cref="Frame"/> (border and signature, no text) under
/// <see cref="Lines"/> written as real text in <see cref="Ink"/>, so the words can be found and copied.
/// </summary>
public sealed record RenderedStamp(BitmapSource Image, Size Size, BitmapSource? Frame = null, IReadOnlyList<StampLine>? Lines = null, Color Ink = default);

/// <summary>Draws the Thai "certified true copy" (สำเนาถูกต้อง) stamp.</summary>
internal static class StampRenderer
{
    private const string Title = "สำเนาถูกต้อง";
    private const string PurposePrefix = "ใช้สำหรับ";
    private const string PurposeSuffix = "เท่านั้น";

    private static readonly Color InkColor = Color.FromRgb(0x1D, 0x3F, 0x9A);
    private static readonly CultureInfo Thai = new("th-TH"); // Buddhist calendar, Thai month names.

    /// <returns>The artwork and its natural size in points.</returns>
    public static RenderedStamp RenderCertifiedCopy(CertifiedCopyOptions options, double pixelsPerPoint = TextRendering.DefaultPixelsPerPoint)
    {
        const double paddingX = 10, paddingY = 6, gap = 2, borderThickness = 1.5, signatureHeight = 34;

        var ink = new SolidColorBrush(InkColor);
        ink.Freeze();

        const double titleSize = 18, lineSize = 9;
        var title = TextRendering.Create(Title, titleSize, ink, FontWeights.Bold);
        var texts = new List<string>();
        if (FormatPurpose(options.Purpose) is { } purpose)
        {
            texts.Add(purpose);
        }

        if (options.IncludeDate)
        {
            texts.Add("วันที่ " + DateTime.Now.ToString("d MMMM yyyy", Thai));
        }

        var lines = texts.Select(t => TextRendering.Create(t, lineSize, ink)).ToList();

        var signatureSize = options.Signature is { } signature
            ? FitSignature(signature, signatureHeight)
            : Size.Empty;

        var contentWidth = Math.Max(title.Width, lines.Select(l => l.Width).DefaultIfEmpty(0).Max());
        if (!signatureSize.IsEmpty)
        {
            contentWidth = Math.Max(contentWidth, signatureSize.Width);
        }

        var width = contentWidth + (2 * paddingX);
        var height = (2 * paddingY) + title.Height + lines.Sum(l => l.Height + gap)
            + (signatureSize.IsEmpty ? 0 : signatureSize.Height + gap);

        var placed = new List<StampLine>();
        BitmapSource Draw(bool withText)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new ScaleTransform(pixelsPerPoint, pixelsPerPoint));
                var half = borderThickness / 2;
                context.DrawRoundedRectangle(null, new Pen(ink, borderThickness), new Rect(half, half, width - borderThickness, height - borderThickness), 4, 4);

                void Text(FormattedText text, string value, double size, bool bold, double top)
                {
                    var at = new Point((width - text.Width) / 2, top);
                    if (withText)
                    {
                        context.DrawText(text, at);
                    }
                    else
                    {
                        placed.Add(new StampLine(value, size, bold, at));
                    }
                }

                var y = paddingY;
                Text(title, Title, titleSize, true, y);
                y += title.Height;

                if (options.Signature is { } image && !signatureSize.IsEmpty)
                {
                    y += gap;
                    context.DrawImage(image, new Rect((width - signatureSize.Width) / 2, y, signatureSize.Width, signatureSize.Height));
                    y += signatureSize.Height;
                }

                for (var i = 0; i < lines.Count; i++)
                {
                    y += gap;
                    Text(lines[i], texts[i], lineSize, false, y);
                    y += lines[i].Height;
                }
            }

            return ImageConversion.Render(visual, (int)Math.Ceiling(width * pixelsPerPoint), (int)Math.Ceiling(height * pixelsPerPoint));
        }

        return new RenderedStamp(Draw(withText: true), new Size(width, height), Draw(withText: false), placed, InkColor);
    }

    /// <summary>"สมัครงาน" becomes "ใช้สำหรับสมัครงานเท่านั้น"; text that already has the wording is kept.</summary>
    internal static string? FormatPurpose(string? purpose)
    {
        purpose = purpose?.Trim();
        if (string.IsNullOrEmpty(purpose))
        {
            return null;
        }

        // Thai is written without spaces, but Latin text or digits next to the Thai wording need one
        // ("สมัครงาน ABC เท่านั้น", not "ABCเท่านั้น").
        if (!purpose.StartsWith(PurposePrefix, StringComparison.Ordinal))
        {
            purpose = PurposePrefix + (IsThai(purpose[0]) ? string.Empty : " ") + purpose;
        }

        return purpose.EndsWith(PurposeSuffix, StringComparison.Ordinal)
            ? purpose
            : purpose + (IsThai(purpose[^1]) ? string.Empty : " ") + PurposeSuffix;
    }

    private static bool IsThai(char c) => c is >= '฀' and <= '๿';

    private static Size FitSignature(BitmapSource signature, double height)
    {
        var aspect = (double)signature.PixelWidth / Math.Max(1, signature.PixelHeight);
        var width = Math.Min(140, height * aspect);
        return new Size(width, width / aspect);
    }
}
