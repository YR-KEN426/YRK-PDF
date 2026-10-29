using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Localization;

namespace Yrk.PdfStudio.App.Imaging;

public enum StampInk
{
    Red,
    Blue,
    Green,
    Black,
}

public enum StampShape
{
    Box,
    DoubleBox,
    Oval,
}

public enum StampDateStyle
{
    None,

    /// <summary>29 ต.ค. 2569</summary>
    ThaiShort,

    /// <summary>29 ตุลาคม 2569</summary>
    ThaiLong,

    /// <summary>29/10/2569</summary>
    ThaiNumeric,

    /// <summary>29 Oct 2026</summary>
    English,
}

public enum StampLanguage
{
    Thai,
    English,

    /// <summary>The Thai word with the English one under it.</summary>
    Both,
}

/// <summary>An office rubber stamp: a big word ("อนุมัติ"), optional smaller lines (a company or a name) and a date.</summary>
/// <param name="Detail">Lines under the word, e.g. "บริษัท ABC จำกัด"; null or empty for none.</param>
public sealed record StampDesign(string Text, string? Detail, StampInk Ink, StampShape Shape, StampDateStyle Date)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> DetailLines => (Detail ?? string.Empty)
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>The stamp to place and the date to show on it.</summary>
public sealed record StampRequest(StampDesign Design, DateTime Date);

/// <summary>Ready-made stamps and drawing of rubber stamps.</summary>
internal static class RubberStamp
{
    private static readonly CultureInfo ThaiCulture = CreateThai();
    private static readonly CultureInfo EnglishCulture = new("en-GB");

    private static readonly (string Thai, string English, StampInk Ink, StampShape Shape, bool Dated)[] Ready =
    [
        ("อนุมัติ", "APPROVED", StampInk.Green, StampShape.DoubleBox, true),
        ("ได้รับแล้ว", "RECEIVED", StampInk.Blue, StampShape.Box, true),
        ("จ่ายแล้ว", "PAID", StampInk.Red, StampShape.DoubleBox, true),
        ("ตรวจสอบแล้ว", "CHECKED", StampInk.Green, StampShape.Box, true),
        ("ต้นฉบับ", "ORIGINAL", StampInk.Red, StampShape.Box, false),
        ("สำเนา", "COPY", StampInk.Blue, StampShape.Box, false),
        ("ด่วน", "URGENT", StampInk.Red, StampShape.Oval, false),
        ("ยกเลิก", "CANCELLED", StampInk.Red, StampShape.DoubleBox, false),
        ("ลับ", "CONFIDENTIAL", StampInk.Red, StampShape.Box, false),
    ];

    /// <summary>Which ready-made stamp, in any language, has these words; -1 for none.</summary>
    public static int PresetIndex(StampDesign design) => Array.FindIndex(Ready, r =>
        (design.Text, design.Detail ?? string.Empty) is var (text, detail)
        && ((text == r.Thai && detail.Length == 0) || (text == r.English && detail.Length == 0) || (text == r.Thai && detail == r.English)));

    /// <summary>The language of the app's interface, for a first stamp.</summary>
    public static StampLanguage DefaultLanguage => Loc.IsThai ? StampLanguage.Thai : StampLanguage.English;

    /// <summary>The ready-made stamps in Thai, English, or Thai with the English word under it.</summary>
    public static IReadOnlyList<StampDesign> Presets(StampLanguage language)
    {
        var date = language == StampLanguage.English ? StampDateStyle.English : StampDateStyle.ThaiShort;
        return Ready.Select(r => new StampDesign(
                language == StampLanguage.English ? r.English : r.Thai,
                language == StampLanguage.Both ? r.English : null,
                r.Ink,
                r.Shape,
                r.Dated ? date : StampDateStyle.None))
            .ToList();
    }

    public static Color ColorOf(StampInk ink) => ink switch
    {
        StampInk.Red => Color.FromRgb(0xC6, 0x28, 0x28),
        StampInk.Blue => Color.FromRgb(0x1D, 0x3F, 0x9A),
        StampInk.Green => Color.FromRgb(0x1B, 0x7A, 0x3A),
        _ => Color.FromRgb(0x20, 0x20, 0x20),
    };

    /// <summary>The date as the stamp shows it; null for <see cref="StampDateStyle.None"/>.</summary>
    public static string? FormatDate(DateTime date, StampDateStyle style) => style switch
    {
        StampDateStyle.ThaiShort => date.ToString("d MMM yyyy", ThaiCulture),
        StampDateStyle.ThaiLong => date.ToString("d MMMM yyyy", ThaiCulture),
        StampDateStyle.ThaiNumeric => date.ToString("dd/MM/yyyy", ThaiCulture),
        StampDateStyle.English => date.ToString("d MMM yyyy", EnglishCulture),
        _ => null,
    };

    /// <returns>The artwork and its natural size in points.</returns>
    public static RenderedStamp Render(StampDesign design, DateTime date, double pixelsPerPoint = TextRendering.DefaultPixelsPerPoint)
    {
        const double titleSize = 20, detailSize = 10, dateSize = 10, gap = 1, stroke = 2, thin = 0.75, ring = 3;

        var color = ColorOf(design.Ink);
        var ink = new SolidColorBrush(color);
        ink.Freeze();

        var texts = new List<(string Text, double Size, bool Bold)>();
        if (design.Text.Trim() is { Length: > 0 } title)
        {
            texts.Add((title, titleSize, true));
        }

        texts.AddRange(design.DetailLines.Select(l => (l, detailSize, true)));
        if (FormatDate(date, design.Date) is { } shown)
        {
            texts.Add((shown, dateSize, true));
        }

        var shaped = texts.Select(t => TextRendering.Create(t.Text, t.Size, ink, t.Bold ? FontWeights.Bold : FontWeights.Normal)).ToList();
        var contentWidth = Math.Max(30, shaped.Select(s => s.Width).DefaultIfEmpty(0).Max());
        var contentHeight = Math.Max(titleSize, shaped.Sum(s => s.Height) + (gap * Math.Max(0, shaped.Count - 1)));

        // Room around the words for the border: a box needs a little, an oval needs the corners of the words inside it.
        var (width, height) = design.Shape switch
        {
            StampShape.Oval => ((contentWidth * Math.Sqrt(2)) + 12, (contentHeight * Math.Sqrt(2)) + 8),
            StampShape.DoubleBox => (contentWidth + 28, contentHeight + 18),
            _ => (contentWidth + 22, contentHeight + 12),
        };

        var placed = new List<StampLine>();
        BitmapSource Draw(bool withText)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new ScaleTransform(pixelsPerPoint, pixelsPerPoint));
                var outer = new Rect(stroke / 2, stroke / 2, width - stroke, height - stroke);
                var inner = new Rect(outer.X + ring, outer.Y + ring, outer.Width - (2 * ring), outer.Height - (2 * ring));
                var heavy = new Pen(ink, stroke);
                var light = new Pen(ink, thin);
                switch (design.Shape)
                {
                    case StampShape.Oval:
                        context.DrawEllipse(null, heavy, Center(outer), outer.Width / 2, outer.Height / 2);
                        context.DrawEllipse(null, light, Center(inner), inner.Width / 2, inner.Height / 2);
                        break;
                    case StampShape.DoubleBox:
                        context.DrawRoundedRectangle(null, heavy, outer, 3, 3);
                        context.DrawRectangle(null, light, inner);
                        break;
                    default:
                        context.DrawRoundedRectangle(null, heavy, outer, 3, 3);
                        break;
                }

                var y = (height - contentHeight) / 2;
                for (var i = 0; i < shaped.Count; i++)
                {
                    var at = new Point((width - shaped[i].Width) / 2, y);
                    if (withText)
                    {
                        context.DrawText(shaped[i], at);
                    }
                    else
                    {
                        placed.Add(new StampLine(texts[i].Text, texts[i].Size, texts[i].Bold, at));
                    }

                    y += shaped[i].Height + gap;
                }
            }

            return ImageConversion.Render(visual, (int)Math.Ceiling(width * pixelsPerPoint), (int)Math.Ceiling(height * pixelsPerPoint));
        }

        return new RenderedStamp(Draw(withText: true), new Size(width, height), Draw(withText: false), placed, color);
    }

    private static Point Center(Rect rect) => new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));

    private static CultureInfo CreateThai()
    {
        // พ.ศ. years and Thai month names, whatever calendar Windows is set to.
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("th-TH").Clone();
        culture.DateTimeFormat.Calendar = new ThaiBuddhistCalendar();
        return culture;
    }
}
