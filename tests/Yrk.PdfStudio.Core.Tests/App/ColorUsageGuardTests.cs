using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml.Linq;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ColorUsageGuardTests
{
    private static string AppSource
    {
        get
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var source = Path.Combine(folder.FullName, "src", "Yrk.PdfStudio.App");
                if (Directory.Exists(source)) return source;
            }
            throw new DirectoryNotFoundException("Cannot locate src/Yrk.PdfStudio.App for color guards.");
        }
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p) is ".cs" or ".xaml")
            .Concat(Directory.EnumerateDirectories(directory)
                .Where(p => Path.GetFileName(p) is not ("bin" or "obj"))
                .SelectMany(SourceFiles));

    private sealed record Allowance(string File, string Value, int Count, string Reason);
    private sealed record ColorUse(string File, string Value);

    [Fact]
    public void All_source_xaml_uses_dynamic_references_to_theme_brushes_and_colors()
    {
        var palettes = ThemeTestResources.PaletteNames
            .Select(name => Path.Combine(AppSource, "Themes", $"Colors.{name}.xaml")).ToArray();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = palettes.SelectMany(p => XDocument.Load(p).Root!.Elements())
            .Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);
        var failures = SourceFiles(Directory.GetParent(AppSource)!.FullName)
            .Where(p => Path.GetExtension(p) == ".xaml" && !IsPalette(Path.GetRelativePath(AppSource, p).Replace('\\', '/')))
            .SelectMany(p => StaticThemeReferences(File.ReadAllText(p), keys)
                .Select(key => $"{Path.GetRelativePath(AppSource, p)}: StaticResource {key}"));
        Assert.Empty(failures);
    }

    [Fact]
    public void Accent_foregrounds_use_the_contrast_checked_text_role()
    {
        foreach (var file in SourceFiles(AppSource).Where(p => Path.GetExtension(p) == ".xaml"))
        {
            var foregrounds = XDocument.Load(file).Descendants().SelectMany(e => e.Attributes().Where(a =>
                a.Name.LocalName == "Foreground" || (a.Name.LocalName == "Value"
                    && (string?)e.Attribute("Property") == "Foreground")));
            Assert.DoesNotContain(foregrounds, a => a.Value == "{DynamicResource AccentBrush}");
        }
        foreach (var file in SourceFiles(AppSource).Where(p => Path.GetExtension(p) == ".cs"))
            Assert.DoesNotMatch(@"ForegroundProperty\s*,\s*""AccentBrush""", File.ReadAllText(file));
    }

    [Fact]
    public void Keyboard_focus_uses_the_blue_focus_role_instead_of_the_red_accent()
    {
        var styles = XDocument.Load(Path.Combine(AppSource, "Themes", "Styles.xaml"));
        var focusTriggers = styles.Descendants().Where(e => e.Name.LocalName == "Trigger"
            && (string?)e.Attribute("Property") is "IsKeyboardFocusWithin" or "IsKeyboardFocused");
        Assert.DoesNotContain(focusTriggers.SelectMany(e => e.Descendants()).Attributes("Value"),
            a => a.Value == "{DynamicResource AccentBrush}");
    }

    private static IEnumerable<string> StaticThemeReferences(string source, HashSet<string> keys) =>
        XDocument.Parse(source).Descendants().SelectMany(e => e.Attributes())
            .SelectMany(a => Regex.Matches(a.Value, @"\{StaticResource\s+(?:ResourceKey\s*=\s*)?['"" ]?([\w]+)['"" ]?\s*\}"))
            .Select(m => m.Groups[1].Value).Where(keys.Contains);

    [Fact]
    public void Static_reference_guard_reads_attributes_and_ignores_comments_and_other_resources()
    {
        const string source = """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Background="{StaticResource SurfaceBrush}" Style="{StaticResource ExampleStyle}">
              <!-- {StaticResource TextBrush} -->
              <Setter Property="Foreground" Value="{StaticResource ResourceKey=TextBrush}" />
            </Border>
            """;
        Assert.Equal(new[] { "SurfaceBrush", "TextBrush" }, StaticThemeReferences(source, ["SurfaceBrush", "TextBrush"]));
    }

    // ยกเว้นถาวร: document content / content-matching swatches / fixed external branding.
    // Each entry has an exact value and count; no file or directory is excluded from scanning.
    private static readonly Allowance[] Permanent =
    [
        new("Imaging/DigitalSignatureRenderer.cs", "Color.FromRgb(0x1F,0x1F,0x1F)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/DigitalSignatureRenderer.cs", "Color.FromRgb(0x30,0x30,0x30)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/RubberStamp.cs", "Color.FromRgb(0x1B,0x7A,0x3A)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/RubberStamp.cs", "Color.FromRgb(0x1D,0x3F,0x9A)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/RubberStamp.cs", "Color.FromRgb(0x20,0x20,0x20)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/RubberStamp.cs", "Color.FromRgb(0xC6,0x28,0x28)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/StampRenderer.cs", "Color.FromRgb(0x1D,0x3F,0x9A)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/SvgImport.cs", "Brushes.Black", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/SvgImport.cs", "Color.FromArgb((byte)Math.Clamp(color.A*opacity,0,255),color.R,color.G,color.B)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/SvgImport.cs", "Color.FromRgb(Channel(rgb.Groups[1].Value),Channel(rgb.Groups[2].Value),Channel(rgb.Groups[3].Value))", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/SvgImport.cs", "Colors.Black", 3, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/TextShaper.cs", "Brushes.Black", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/VectorArtBuilder.cs", "Brushes.Black", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Imaging/VectorArtBuilder.cs", "Color.FromArgb(path.Color.A,path.Color.R,path.Color.G,path.Color.B)", 1, "PDF artwork, ink or text shaping uses document colors independently of the UI theme."),
        new("Scanning/FakeScannerService.cs", "Brushes.Beige", 1, "Synthetic scanned-page pixels are scanner fixture content, not UI paint."),
        new("Scanning/FakeScannerService.cs", "Brushes.Black", 2, "Synthetic scanned-page pixels are scanner fixture content, not UI paint."),
        new("Scanning/FakeScannerService.cs", "Brushes.Gray", 2, "Synthetic scanned-page pixels are scanner fixture content, not UI paint."),
        new("Scanning/FakeScannerService.cs", "Brushes.LightGray", 2, "Synthetic scanned-page pixels are scanner fixture content, not UI paint."),
        new("Scanning/FakeScannerService.cs", "Brushes.LightSteelBlue", 1, "Synthetic scanned-page pixels are scanner fixture content, not UI paint."),
        new("Scanning/FakeScannerService.cs", "Brushes.White", 1, "Synthetic scanned-page pixels are scanner fixture content, not UI paint."),
        new("ViewModels/CommentViewModel.cs", "Color.FromRgb(0x2E,0x7D,0x32)", 1, "Comment indicator reproduces annotation content colors or their document-kind defaults."),
        new("ViewModels/CommentViewModel.cs", "Color.FromRgb(0x75,0x75,0x75)", 1, "Comment indicator reproduces annotation content colors or their document-kind defaults."),
        new("ViewModels/CommentViewModel.cs", "Color.FromRgb(0xC6,0x28,0x28)", 1, "Comment indicator reproduces annotation content colors or their document-kind defaults."),
        new("ViewModels/CommentViewModel.cs", "Color.FromRgb(0xFF,0xD4,0x3B)", 1, "Comment indicator reproduces annotation content colors or their document-kind defaults."),
        new("ViewModels/CommentViewModel.cs", "Color.FromRgb(c.R,c.G,c.B)", 1, "Comment indicator reproduces annotation content colors or their document-kind defaults."),
        new("ViewModels/FormFieldViewModel.cs", "Colors.Black", 2, "PDF form text color and its default-color comparison must preserve document semantics."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromArgb(0x66,_annotationColor.R,_annotationColor.G,_annotationColor.B)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromRgb(0x21,0x21,0x21)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromRgb(0x2E,0x7D,0x32)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromRgb(0x64,0xB5,0xF6)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromRgb(0x7E,0xE0,0x81)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromRgb(0xE5,0x39,0x35)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromRgb(0xF4,0x8F,0xB1)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Annotations.cs", "Color.FromRgb(0xFF,0xE1,0x35)", 1, "Annotation ink palette and content-matching highlight previews must retain PDF colors."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(0,114,206)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(0,150,160)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(110,90,40)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(142,68,173)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(200,30,140)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(214,40,40)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(230,126,0)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.Count.cs", "Color.FromRgb(46,139,87)", 1, "Count mark palette is written to PDF comments and must match its document preview."),
        new("ViewModels/MainViewModel.FieldLooks.cs", "Colors.Black", 1, "PDF form text color and its default-color comparison must preserve document semantics."),
        new("ViewModels/MainViewModel.Measure.cs", "Color.FromArgb(0x30,MeasureColor.R,MeasureColor.G,MeasureColor.B)", 1, "Measurement preview matches the measurement ink saved in the PDF."),
        new("ViewModels/MainViewModel.Measure.cs", "Color.FromRgb(MeasureColor.R,MeasureColor.G,MeasureColor.B)", 1, "Measurement preview matches the measurement ink saved in the PDF."),
        new("ViewModels/MainViewModel.ReadingMarks.cs", "Brushes.Crimson", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("ViewModels/MainViewModel.ReadingMarks.cs", "Brushes.DarkOrange", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("ViewModels/MainViewModel.ReadingMarks.cs", "Brushes.DodgerBlue", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("ViewModels/MainViewModel.ReadingMarks.cs", "Brushes.Gold", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("ViewModels/MainViewModel.ReadingMarks.cs", "Brushes.MediumPurple", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("ViewModels/MainViewModel.ReadingMarks.cs", "Brushes.SeaGreen", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("ViewModels/MainViewModel.Symbols.cs", "Colors.Black", 1, "Default drawing ink becomes PDF vector content."),
        new("ViewModels/MainViewModel.TextEdits.cs", "Brushes.Black", 1, "Text shaping measures PDF text; the placeholder ink is not visible UI paint."),
        new("ViewModels/PageMarksViewModel.cs", "Color.FromRgb(Color.R,Color.G,Color.B)", 1, "Watermark and page-mark colors reproduce the content drawn into the PDF."),
        new("ViewModels/PageMarksViewModel.cs", "Color.FromRgb(mark.Color.R,mark.Color.G,mark.Color.B)", 1, "Watermark and page-mark colors reproduce the content drawn into the PDF."),
        new("ViewModels/TextEditViewModel.cs", "Color.FromRgb(line.Color.R,line.Color.G,line.Color.B)", 1, "PDF text ink and its color picker must preserve document colors."),
        new("ViewModels/TextStyles.cs", "Color.FromRgb(0x1A,0x4F,0xA0)", 1, "PDF text ink and its color picker must preserve document colors."),
        new("ViewModels/TextStyles.cs", "Color.FromRgb(0x2E,0x7D,0x32)", 1, "PDF text ink and its color picker must preserve document colors."),
        new("ViewModels/TextStyles.cs", "Color.FromRgb(0x61,0x61,0x61)", 1, "PDF text ink and its color picker must preserve document colors."),
        new("ViewModels/TextStyles.cs", "Color.FromRgb(0xC6,0x28,0x28)", 1, "PDF text ink and its color picker must preserve document colors."),
        new("ViewModels/TextStyles.cs", "Colors.Black", 2, "PDF text ink and its color picker must preserve document colors."),
        new("Views/ReaderBookmarkDialog.xaml", "Crimson", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("Views/ReaderBookmarkDialog.xaml", "DarkOrange", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("Views/ReaderBookmarkDialog.xaml", "DodgerBlue", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("Views/ReaderBookmarkDialog.xaml", "Gold", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("Views/ReaderBookmarkDialog.xaml", "MediumPurple", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("Views/ReaderBookmarkDialog.xaml", "SeaGreen", 1, "User-selected saved reader-mark palette; swatches must match the stored mark color identity."),
        new("Views/SignatureDialog.xaml.cs", "Color.FromRgb(0x1D,0x3F,0x9A)", 1, "Handwritten or typed signature ink is embedded in the PDF."),
        new("Views/SignatureDialog.xaml.cs", "Colors.Black", 2, "Handwritten or typed signature ink is embedded in the PDF."),
        new("Views/StampDialog.xaml", "#1B7A3A", 1, "Stamp ink swatches must match RubberStamp output in the PDF."),
        new("Views/StampDialog.xaml", "#1D3F9A", 1, "Stamp ink swatches must match RubberStamp output in the PDF."),
        new("Views/StampDialog.xaml", "#202020", 1, "Stamp ink swatches must match RubberStamp output in the PDF."),
        new("Views/StampDialog.xaml", "#C62828", 1, "Stamp ink swatches must match RubberStamp output in the PDF."),
        new("Views/SupportBrands.xaml", "#202020", 2, "External sponsor logo artwork retains its fixed brand colors."),
        new("Views/SupportBrands.xaml", "#FF5A16", 1, "External sponsor logo artwork retains its fixed brand colors."),
        new("Views/SupportBrands.xaml", "white", 4, "External sponsor logo artwork retains its fixed brand colors."),
        new("Views/SupportDialog.xaml", "#202020", 1, "Sponsor and Ko-fi button brand palette, including reviewed contrast and interaction states."),
        new("Views/SupportDialog.xaml", "#5A8DDA", 1, "Sponsor and Ko-fi button brand palette, including reviewed contrast and interaction states."),
        new("Views/SupportDialog.xaml", "#6699E6", 1, "Sponsor and Ko-fi button brand palette, including reviewed contrast and interaction states."),
        new("Views/SupportDialog.xaml", "#72A5F2", 1, "Sponsor and Ko-fi button brand palette, including reviewed contrast and interaction states."),
        new("Views/SupportDialog.xaml", "#8E2866", 1, "Sponsor and Ko-fi button brand palette, including reviewed contrast and interaction states."),
        new("Views/SupportDialog.xaml", "#A72F78", 1, "Sponsor and Ko-fi button brand palette, including reviewed contrast and interaction states."),
        new("Views/SupportDialog.xaml", "#BF3989", 1, "Sponsor and Ko-fi button brand palette, including reviewed contrast and interaction states."),
    ];

    [Fact]
    public void App_colors_are_centralized_or_explicitly_accounted_for()
    {
        var uses = SourceFiles(AppSource).Order(StringComparer.Ordinal).SelectMany(file =>
        {
            var relative = Path.GetRelativePath(AppSource, file).Replace('\\', '/');
            var source = File.ReadAllText(file);
            return Path.GetExtension(file) == ".xaml"
                ? XamlColors(source, relative) : CSharpColors(source, relative);
        }).ToArray();
        var failures = Validate(uses, Permanent);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static List<string> Validate(IEnumerable<ColorUse> uses, Allowance[] allowances)
    {
        var failures = new List<string>();
        var actual = uses.GroupBy(u => (u.File, u.Value)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var duplicate in allowances.GroupBy(a => (a.File, a.Value)).Where(g => g.Count() > 1))
            failures.Add($"Duplicate allowance: {duplicate.Key}");
        foreach (var entry in allowances)
        {
            if (entry.Count <= 0 || string.IsNullOrWhiteSpace(entry.Reason))
                failures.Add($"Allowance needs positive count and reason: {entry.File} {entry.Value}");
            var count = actual.GetValueOrDefault((entry.File, entry.Value));
            // Exact counts catch stale entries, partial removals and additions. Update the list when migrating.
            if (count != entry.Count)
                failures.Add($"Stale/exceeded allowance: {entry.File} {entry.Value}: expected {entry.Count}, found {count}");
        }
        var allowed = allowances.Select(a => (a.File, a.Value)).ToHashSet();
        foreach (var (key, count) in actual.Where(pair => !allowed.Contains(pair.Key)).OrderBy(pair => pair.Key))
            failures.Add($"Unlisted: new({JsonSerializer.Serialize(key.File)}, {JsonSerializer.Serialize(key.Value)}, {count}, \"TODO\"),");
        return failures;
    }

    private static readonly HashSet<string> NamedColors = typeof(Colors)
        .GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => p.Name)
        .Where(n => n != "Transparent").ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Transparent expresses hit-testing / absence of paint, not a theme-dependent visible color.
    // Permit it in both languages; opaque White/Black/etc. still require a token or an exact exception.
    private static readonly Regex HexColor = new(@"(?<![\w#])#(?:[\da-fA-F]{8}|[\da-fA-F]{6}|[\da-fA-F]{4}|[\da-fA-F]{3})(?![\da-fA-F\w])");

    private static bool ColorProperty(string name)
    {
        name = name[(name.LastIndexOf('.') + 1)..];
        return name is "Color" or "Background" or "Foreground" or "Fill" or "Stroke" or "OpacityMask"
            || name.EndsWith("Brush", StringComparison.Ordinal);
    }

    private static bool IsPalette(string file) => Regex.IsMatch(file, @"^Themes/Colors\.[^/\\]+\.xaml$");

    private static IEnumerable<ColorUse> XamlColors(string source, string file)
    {
        if (IsPalette(file)) yield break;
        var document = XDocument.Parse(source);
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                foreach (Match match in HexColor.Matches(attribute.Value))
                    yield return new(file, match.Value);
                var property = attribute.Name.LocalName;
                if (property == "Value" && element.Name.LocalName is "Setter" or "Trigger" or "Condition")
                    property = (string?)element.Attribute("Property") ?? "";
                var colorValue = ColorProperty(property)
                    || (element.Name.LocalName is "ColorAnimation" or "ColorAnimationUsingKeyFrames" or "LinearColorKeyFrame"
                        or "DiscreteColorKeyFrame" or "SplineColorKeyFrame" or "EasingColorKeyFrame"
                        && property is "From" or "To" or "By" or "Value");
                if (colorValue && NamedColors.Contains(attribute.Value.Trim()))
                    yield return new(file, attribute.Value.Trim());
                if (colorValue)
                    foreach (Match member in Regex.Matches(attribute.Value, @"\b(?:Colors|Brushes)\.([A-Za-z]+)\b"))
                        if (NamedColors.Contains(member.Groups[1].Value))
                            yield return new(file, member.Value);
            }
            foreach (var text in element.Nodes().OfType<XText>())
            {
                foreach (Match match in HexColor.Matches(text.Value))
                    yield return new(file, match.Value);
                if ((ColorProperty(element.Name.LocalName) || element.Name.LocalName is "SolidColorBrush")
                    && NamedColors.Contains(text.Value.Trim()))
                    yield return new(file, text.Value.Trim());
            }
        }
    }

    private static IEnumerable<ColorUse> CSharpColors(string source, string file)
    {
        // Consume comments and complete strings before inspecting code, as in LocalizationCoverageTests.
        // Raw strings precede ordinary strings so documentation/examples cannot become false positives.
        const string pattern = "//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|\\$*\"\"\"[\\s\\S]*?\"\"\"|(?:\\$@|@\\$|@)\"(?:\"\"|[^\"])*\"|\\$?\"(?:\\\\[\\s\\S]|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|[A-Za-z_][A-Za-z_0-9]*|[^\\s]";
        var tokens = Regex.Matches(source, pattern).Select(m => m.Value)
            .Where(t => !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("/*", StringComparison.Ordinal)
                && !t.Contains('"') && !t.StartsWith('\''))
            .ToArray();
        for (var i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i + 1] != ".") continue;
            if (tokens[i] is "Colors" or "Brushes")
            {
                // ScanDialog.Colors and PageMarksViewModel.Colors are controls/collections, not Media.Colors.
                if (NamedColors.Contains(tokens[i + 2]))
                    yield return new(file, string.Concat(tokens[i..(i + 3)]));
            }
            else if (tokens[i] == "Color" && tokens[i + 2] is "FromRgb" or "FromArgb"
                     && i + 3 < tokens.Length && tokens[i + 3] == "(")
            {
                var end = i + 4;
                var depth = 1;
                for (; end < tokens.Length && depth > 0; end++)
                {
                    if (tokens[end] == "(") depth++;
                    if (tokens[end] == ")") depth--;
                }
                yield return new(file, string.Concat(tokens[i..end]));
            }
        }
    }

    [Theory]
    [InlineData("#ABC")]
    [InlineData("#1ABC")]
    [InlineData("#AABBCC")]
    [InlineData("#11AABBCC")]
    [InlineData("White")]
    [InlineData("black")]
    [InlineData("Red")]
    public void Xaml_reader_covers_attributes_setters_and_property_elements(string color)
    {
        var source = $"""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{color}">
              <!-- Background="#123" -->
              <Border.BorderBrush>{color}</Border.BorderBrush>
              <Setter Property="Foreground" Value="{color}" />
              <SolidColorBrush Color="{color}" />
            </Border>
            """;
        Assert.Equal(4, XamlColors(source, "Example.xaml").Count());
        foreach (var palette in ThemeTestResources.PaletteNames)
            Assert.Empty(XamlColors(source, $"Themes/Colors.{palette}.xaml"));
        foreach (var other in new[] { "Themes/Colors.xaml", "Colors.Light.xaml", "Themes/Nested/Colors.Light.xaml",
                     "Themes/Styles.xaml", "Other/Colors.Light.xaml", "Themes/Colors.Light.xaml.cs" })
            Assert.Equal(4, XamlColors(source, other).Count());
    }

    [Fact]
    public void Readers_ignore_comments_strings_color_labels_and_transparency()
    {
        const string xaml = """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="Transparent">
              <TextBlock Text="White" Foreground="{StaticResource TextBrush}" />
              <!-- #ABC White -->
            </Border>
            """;
        Assert.Empty(XamlColors(xaml, "Example.xaml"));
        const string code = """"
            // Brushes.White
            /* Color.FromRgb(1, 2, 3) */
            var sample = "Colors.Red";
            var raw = """Brushes.Black""";
            var transparent = Brushes.Transparent;
            Colors.ItemsSource = Colors.FirstOrDefault();
            var brush = System.Windows.Media.Brushes.White;
            var color = Color /* comment */ . FromArgb(128, Channel(1), 2, 3);
            var named = System.Windows.Media.Colors.Red;
            """";
        Assert.Equal(new[] { "Brushes.White", "Color.FromArgb(128,Channel(1),2,3)", "Colors.Red" },
            CSharpColors(code, "Example.cs").Select(u => u.Value));
    }

    [Fact]
    public void Xaml_reader_covers_animation_values_and_static_named_brushes()
    {
        const string source = """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:media="clr-namespace:System.Windows.Media;assembly=PresentationCore"
                    Background="{x:Static media:Brushes.White}"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ColorAnimation From="Red" To="Transparent" />
              <DiscreteColorKeyFrame Value="Black" />
            </Border>
            """;
        Assert.Equal(new[] { "Brushes.White", "Red", "Black" },
            XamlColors(source, "Example.xaml").Select(u => u.Value));
    }

    [Fact]
    public void Allowances_reject_new_excess_stale_partial_and_duplicate_entries()
    {
        ColorUse use = new("Example.xaml", "White");
        Allowance entry = new(use.File, use.Value, 1, "Test fixture");
        Assert.Empty(Validate([use], [entry]));
        Assert.NotEmpty(Validate([use], []));
        Assert.NotEmpty(Validate([use, use], [entry]));
        Assert.NotEmpty(Validate([], [entry]));
        Assert.NotEmpty(Validate([use], [entry with { Count = 2 }]));
        Assert.NotEmpty(Validate([use], [entry, entry]));
    }
}
