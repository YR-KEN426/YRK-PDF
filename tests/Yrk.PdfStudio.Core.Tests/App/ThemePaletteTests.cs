using System.Collections;
using System.Resources;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class ThemePaletteTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement[] Entries(string palette) => XDocument.Load(
        Path.Combine(ThemeTestResources.AppSource, "Themes", $"Colors.{palette}.xaml")).Root!.Elements().ToArray();

    private static string Key(XElement entry) => (string)entry.Attribute(Xaml + "Key")!;

    [Fact]
    public void Theme_key_sets_match_exactly_and_fixed_keys_are_disjoint()
    {
        var keys = ThemeTestResources.PaletteNames.ToDictionary(p => p, p => Entries(p).Select(Key).ToArray());
        foreach (var set in keys.Values)
        {
            Assert.NotEmpty(set);
            Assert.Equal(set.Length, set.Distinct(StringComparer.Ordinal).Count());
        }
        foreach (var palette in new[] { "Light", "Dark", "HighContrast" })
        {
            Assert.Equal(keys["Light"].Order(StringComparer.Ordinal), keys[palette].Order(StringComparer.Ordinal));
            Assert.Empty(keys["Fixed"].Intersect(keys[palette], StringComparer.Ordinal));
        }
        Assert.Equal(107, keys["Fixed"].Length + keys["Light"].Length);
        foreach (var required in new[] { "FocusBrush", "PaperBrush", "PageBackgroundBrush", "SignatureValidBrush",
                     "SignatureWarningBrush", "SignatureInvalidBrush", "OrganizeBrush", "OrganizeSoftBrush",
                     "ConvertBrush", "ConvertSoftBrush", "EditBrush", "EditSoftBrush", "AccountingBrush",
                     "AccountingSoftBrush", "DrawingsBrush", "DrawingsSoftBrush", "SecurityBrush", "SecuritySoftBrush" })
            Assert.Contains(required, keys["Fixed"]);
    }

    private static readonly Dictionary<string, string> HighContrastButtonColors = new()
    {
        ["AccentBrush"] = "#C62828", ["AccentHoverBrush"] = "#AD2222", ["AccentPressedBrush"] = "#961D1D",
        ["BrandBrush"] = "#C62828", ["BrandHoverBrush"] = "#AD2222", ["BrandPressedBrush"] = "#961D1D",
        ["OnAccentTextBrush"] = "#FFFFFF"
    };

    [Fact]
    public void High_contrast_uses_system_colors_and_exact_red_button_roles()
    {
        foreach (var entry in Entries("HighContrast"))
        {
            if (HighContrastButtonColors.TryGetValue(Key(entry), out var expected))
            {
                Assert.Equal(expected, (string?)entry.Attribute("Color"));
            }
            else if (entry.Name.LocalName == "Static")
            {
                // WPF Color values cannot hold expressions; use the system value for these five keys.
                Assert.StartsWith("SystemColors.", (string?)entry.Attribute("Member"));
                Assert.EndsWith("Color", (string?)entry.Attribute("Member"));
            }
            else
            {
                Assert.Equal("SolidColorBrush", entry.Name.LocalName);
                var value = (string)entry.Attribute("Color")!;
                Assert.StartsWith("{DynamicResource {x:Static SystemColors.", value);
                Assert.EndsWith("ColorKey}}", value);
            }
        }
    }

    [Fact]
    public void Project_embeds_all_four_palettes_and_they_load_with_compatible_value_types()
    {
        var project = XDocument.Load(Path.Combine(ThemeTestResources.AppSource, "Yrk.PdfStudio.App.csproj"));
        var pages = project.Descendants("Page").SelectMany(e =>
            ((string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "").Split(';')).ToArray();
        var assembly = typeof(MainWindow).Assembly;
        using var stream = assembly.GetManifestResourceStream(Assert.Single(assembly.GetManifestResourceNames(),
            n => n.EndsWith(".g.resources", StringComparison.Ordinal)))!;
        using var reader = new ResourceReader(stream);
        var embedded = reader.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var palette in ThemeTestResources.PaletteNames)
        {
            Assert.Contains($"Themes\\Colors.{palette}.xaml", pages);
            Assert.Contains($"themes/colors.{palette.ToLowerInvariant()}.baml", embedded);
        }
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var light = ThemeTestResources.Load("Light");
            foreach (var palette in new[] { "Light", "Dark", "HighContrast" })
            {
                var resources = ThemeTestResources.Create(palette);
                Assert.Equal(2, resources.MergedDictionaries.Count);
                foreach (var key in light.Keys)
                    if (light[key] is Color) Assert.IsType<Color>(resources[key]);
                    else Assert.IsAssignableFrom<Brush>(resources[key]);
                if (palette == "HighContrast")
                    foreach (var entry in Entries(palette).Where(e => e.Name.LocalName == "SolidColorBrush" && !HighContrastButtonColors.ContainsKey(Key(e))))
                    {
                        var brush = Assert.IsType<SolidColorBrush>(resources[Key(entry)]);
                        var expression = (string)entry.Attribute("Color")!;
                        var member = expression[expression.IndexOf("SystemColors.", StringComparison.Ordinal)..]
                            .Replace("SystemColors.", "", StringComparison.Ordinal).Replace("Key}}", "", StringComparison.Ordinal);
                        Assert.Equal((Color)typeof(SystemColors).GetProperty(member)!.GetValue(null)!, brush.Color);
                        Assert.True(DependencyPropertyHelper.GetValueSource(brush, SolidColorBrush.ColorProperty).IsExpression);
                    }
            }
            var highContrast = ThemeTestResources.Create("HighContrast");
            foreach (var (key, value) in HighContrastButtonColors)
                Assert.Equal((Color)ColorConverter.ConvertFromString(value),
                    Assert.IsType<SolidColorBrush>(highContrast[key]).Color);
            Assert.Empty(app.Errors);
            return true;
        });
    }

    // Explicit UI roles, including hover/pressed states and the real host of translucent overlays.
    // DocumentInk is a fixture for the PDF text editor's bound document foreground, not UI TextBrush.
    private sealed record Pair(string Foreground, string Background, string? Underlay = null);
    private static readonly string[] AccentTextBackgrounds =
    [
        "SurfaceBrush", "ToolbarBrush", "SidebarBrush", "HoverBrush", "PressedBrush", "CheckedBrush",
        "HomeBackgroundBrush", "HomeFillBrush", "InformationBackgroundBrush", "InformationSubtleBrush"
    ];
    private static readonly Pair[] TextPairs =
    [
        ..new[] { "SurfaceBrush", "ToolbarBrush", "SidebarBrush", "ViewerBrush", "HoverBrush", "PressedBrush",
            "CheckedBrush", "InformationBackgroundBrush", "InformationSubtleBrush", "WarningBannerBrush",
            "SignatureBannerBrush", "ScanPanelBrush", "ScanDividerBrush", "PreviewBackgroundBrush" }
            .Select(background => new Pair("TextBrush", background)),
        ..new[] { "SurfaceBrush", "ToolbarBrush", "SidebarBrush", "HoverBrush", "PressedBrush", "CheckedBrush",
            "InformationBackgroundBrush", "InformationSubtleBrush", "WarningBannerBrush", "SignatureBannerBrush" }
            .Select(background => new Pair("SecondaryTextBrush", background)),
        ..new[] { "HomeBackgroundBrush", "SurfaceBrush", "HomeFillBrush", "HoverBrush", "PressedBrush", "BrandFaintBrush" }
            .SelectMany(background => new[] { new Pair("HomeTextBrush", background), new Pair("HomeMutedTextBrush", background) }),
        ..new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "BrandBrush", "BrandHoverBrush",
            "BrandPressedBrush", "MeasureAccentBrush", "MeasureTextBrush" }
            .Select(background => new Pair("OnAccentTextBrush", background)),
        ..AccentTextBackgrounds.Select(background => new Pair("AccentTextBrush", background)),
        new("BrandBrush", "BrandSoftBrush"),
        new("PaperTextBrush", "PaperBrush"),
        new("PdfBadgeTextBrush", "BrandSoftBrush"),
        ..new[] { "SurfaceBrush", "HoverBrush", "PressedBrush", "CheckedBrush" }
            .Select(background => new Pair("ErrorBrush", background)),
        new("SuccessBrush", "SurfaceBrush"), new("FormSuccessTextBrush", "FormSuccessBackgroundBrush"),
        new("TextBrush", "ToolTipBackgroundBrush"), new("ToolTipMutedTextBrush", "ToolTipBackgroundBrush"),
        new("ScanNoticeTextBrush", "ScanNoticeBrush"), new("ScanWarningTextBrush", "SurfaceBrush"),
        new("ScanHintTextBrush", "ScanPanelBrush"), new("RulerTextBrush", "ToolbarBrush"),
        new("TextBrush", "ScanOverlayBrush", "PaperBrush"), new("TextBrush", "CropLabelBrush", "PaperBrush"),
        // The image-field prompt uses FieldPromptBrush (fixed) over these PDF overlays, rather than a UI surface.
        new("FieldPromptBrush", "FieldTintBrush", "PageBackgroundBrush"),
        new("FieldPromptBrush", "FieldSelectedBrush", "PageBackgroundBrush"),
        new("DocumentInk", "TextEditorBackgroundBrush", "PageBackgroundBrush"),
        ..new[] { "CalendarTopColor", "CalendarUpperColor", "CalendarLowerColor", "CalendarBottomColor" }
            .SelectMany(background => new[] { new Pair("TextBrush", background), new Pair("SecondaryTextBrush", background) }),
        new("OrganizeBrush", "OrganizeSoftBrush"), new("ConvertBrush", "ConvertSoftBrush"),
        new("EditBrush", "EditSoftBrush"), new("AccountingBrush", "AccountingSoftBrush"),
        new("DrawingsBrush", "DrawingsSoftBrush"), new("SecurityBrush", "SecuritySoftBrush")
    ];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Text_and_background_pairs_have_at_least_4_point_5_contrast(string palette)
    {
        var colors = Entries("Fixed").Concat(Entries(palette)).Where(e => e.Name.LocalName is "SolidColorBrush" or "Color")
            .ToDictionary(Key, e => (Color)ColorConverter.ConvertFromString(
                e.Name.LocalName == "Color" ? e.Value : (string)e.Attribute("Color")!));
        Color Foreground(string key) => key == "DocumentInk" ? Color.FromRgb(0x1F, 0x1F, 0x1F) : colors[key];
        var failures = new List<string>();
        foreach (var pair in TextPairs)
        {
            var background = Rgb(colors[pair.Background]);
            if (pair.Underlay is { } underlay)
            {
                Assert.Equal((byte)255, colors[underlay].A);
                background = Composite(colors[pair.Background], Rgb(colors[underlay]));
            }
            else Assert.Equal((byte)255, colors[pair.Background].A);
            var foreground = Composite(Foreground(pair.Foreground), background);
            var ratio = Contrast(foreground, background);
            if (ratio < 4.5)
                failures.Add($"{palette}: {pair.Foreground}/{pair.Background} on {pair.Underlay ?? "opaque surface"}: {ratio:F4}:1");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Red_accent_and_amber_error_roles_are_distinct_and_focus_remains_blue(string palette)
    {
        var entries = Entries(palette).ToDictionary(Key);
        Color Brush(string key) => (Color)ColorConverter.ConvertFromString((string)entries[key].Attribute("Color")!);
        Assert.Equal(Color.FromRgb(0xC6, 0x28, 0x28), Brush("AccentBrush"));
        var error = Brush("ErrorBrush");
        Assert.True(error.R > error.G && error.G >= error.B + 40);
        Assert.NotEqual(Brush("AccentBrush"), error);
        var focus = Entries("Fixed").Single(e => Key(e) == "FocusBrush");
        Assert.Equal("#1565C0", (string)focus.Attribute("Color")!);
    }

    [Fact]
    public void High_contrast_accent_text_is_readable_on_its_actual_UI_surfaces()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var resources = ThemeTestResources.Create("HighContrast");
            var foreground = ((SolidColorBrush)resources["AccentTextBrush"]).Color;
            foreach (var key in AccentTextBackgrounds)
            {
                var background = ((SolidColorBrush)resources[key]).Color;
                Assert.True(Contrast(Rgb(foreground), Rgb(background)) >= 4.5, key);
            }
            foreach (var key in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush" })
                Assert.True(Contrast(Rgb(((SolidColorBrush)resources["OnAccentTextBrush"]).Color),
                    Rgb(((SolidColorBrush)resources[key]).Color)) >= 4.5);
            Assert.NotEqual(((SolidColorBrush)resources["AccentBrush"]).Color,
                ((SolidColorBrush)resources["ErrorBrush"]).Color);
            return true;
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void Paper_stays_white_and_black_and_blue_signature_ink_remain_readable(string palette)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var resources = ThemeTestResources.Create(palette);
            var paper = ((SolidColorBrush)resources["PaperBrush"]).Color;
            Assert.Equal(Colors.White, paper);
            foreach (var ink in new[] { Colors.Black, Color.FromRgb(0x1D, 0x3F, 0x9A) })
                Assert.True(Contrast(Rgb(ink), Rgb(paper)) >= 4.5);
            return true;
        });
    }

    private static (double R, double G, double B) Rgb(Color color) => (color.R / 255d, color.G / 255d, color.B / 255d);

    private static (double R, double G, double B) Composite(Color foreground, (double R, double G, double B) background)
    {
        var alpha = foreground.A / 255d;
        var value = Rgb(foreground);
        return (value.R * alpha + background.R * (1 - alpha), value.G * alpha + background.G * (1 - alpha),
            value.B * alpha + background.B * (1 - alpha));
    }

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Luminance((double R, double G, double B) v) =>
            0.2126 * Linear(v.R) + 0.7152 * Linear(v.G) + 0.0722 * Linear(v.B);
        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
