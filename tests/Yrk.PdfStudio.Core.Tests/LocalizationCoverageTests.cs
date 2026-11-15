using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class LocalizationCoverageTests
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
            throw new DirectoryNotFoundException("Cannot locate src/Yrk.PdfStudio.App for localization guards.");
        }
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p) is ".cs" or ".xaml")
            .Concat(Directory.EnumerateDirectories(directory)
                .Where(p => Path.GetFileName(p) is not ("bin" or "obj"))
                .SelectMany(SourceFiles));

    private static JsonDocument ReadThai() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppSource, "Localization", "th.json")),
        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    [Fact]
    public void Thai_dictionary_has_unique_keys_and_nonempty_values()
    {
        using var document = ReadThai();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();
        // EnumerateObject preserves duplicate properties; deserializing a Dictionary would hide them.
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) failures.Add($"Duplicate key: {Display(property.Name)}");
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                failures.Add($"Empty/non-string translation: {Display(property.Name)}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Thai_translations_preserve_the_set_of_format_argument_indices()
    {
        using var document = ReadThai();
        var failures = new List<string>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var original = Placeholders(property.Name);
            var translated = Placeholders(property.Value.GetString()!);
            if (!original.SetEquals(translated))
                failures.Add($"{Display(property.Name)}: expected [{string.Join(", ", original.Order())}], got [{string.Join(", ", translated.Order())}]");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static HashSet<int> Placeholders(string value)
    {
        var result = new HashSet<int>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '{') continue;
            if (i + 1 < value.Length && value[i + 1] == '{') { i++; continue; }
            // Includes alignment/format specifiers, e.g. {0,10:N2}; repeated indices are a set.
            var match = Regex.Match(value[(i + 1)..], @"^(\d+)(?:\s*,\s*-?\d+)?(?::[^{}]*)?\}");
            if (match.Success) result.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return result;
    }

    [Fact]
    public void All_English_UI_literals_have_Thai_translations()
    {
        using var document = ReadThai();
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var uses = new List<TextUse>();
        foreach (var file in SourceFiles(AppSource).Order(StringComparer.Ordinal))
        {
            var relative = "src/Yrk.PdfStudio.App/" + Path.GetRelativePath(AppSource, file).Replace('\\', '/');
            var source = File.ReadAllText(file);
            uses.AddRange(Path.GetExtension(file) == ".xaml"
                ? XamlTexts(source, relative) : LocCalls(source, relative));
        }

        // Read the actual catalogue, including entries not referenced by a Loc call.
        var catalogFile = Path.Combine(AppSource, "Services", "Errors", "ErrorCatalog.cs");
        var catalogSource = File.ReadAllText(catalogFile);
        var catalogLiterals = Tokens(catalogSource).Where(t => t.Literal).ToArray();
        foreach (var key in ErrorCatalog.All.SelectMany(e => new[] { e.Message, e.NextStep }).Distinct(StringComparer.Ordinal))
        {
            var token = catalogLiterals.FirstOrDefault(t => t.Value == key);
            // A future computed catalogue value still participates even without an exact literal location.
            var line = token is null ? 1 : Line(catalogSource, token.Offset);
            uses.Add(new(key, "src/Yrk.PdfStudio.App/Services/Errors/ErrorCatalog.cs", line));
        }

        var missing = uses.Where(u => NeedsTranslation(u.Key) && !keys.Contains(u.Key))
            .GroupBy(u => u.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{Display(g.Key)} — {string.Join("; ", g.Select(u => $"{u.File}:{u.Line}").Distinct().Order(StringComparer.Ordinal))}")
            .ToArray();
        if (missing.Length == 0) return;

        // Write only on failure. One unique key per line, with every observed source location.
        var report = string.Join(Environment.NewLine, missing);
        // The test module redirects TEMP into PrivateTempDirectory and deletes it at exit.
        // Its parent is the original runner TEMP; keep this requested failure artifact there.
        var reportPath = Path.Combine(Path.GetDirectoryName(PrivateTempDirectory.Path)!, "missing-th.txt");
        File.WriteAllText(reportPath, report + Environment.NewLine, new UTF8Encoding(false));
        Assert.Fail($"Missing Thai translations: {missing.Length} keys ({reportPath}){Environment.NewLine}{report}");
    }

    [Fact]
    public void Non_ETax_Xaml_UI_attributes_do_not_contain_hardcoded_Thai()
    {
        var failures = new List<string>();
        foreach (var file in SourceFiles(AppSource).Where(p => Path.GetExtension(p) == ".xaml"
                     && !Path.GetFileName(p).StartsWith("ETax", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Path.GetRelativePath(AppSource, file).Replace('\\', '/');
            var document = XDocument.Parse(File.ReadAllText(file), LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants().Attributes()
                         .Where(a => CoveredProperty(a.Name.LocalName) && Regex.IsMatch(a.Value, @"[\u0E00-\u0E7F]")))
            {
                if (KeptThaiAttributes.Contains((relative, attribute.Name.LocalName, attribute.Value))) continue;
                failures.Add($"{relative}:{((IXmlLineInfo)attribute).LineNumber} {attribute.Name.LocalName}={Display(attribute.Value)}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static readonly HashSet<(string File, string Attribute, string Value)> KeptThaiAttributes =
    [
        // Language selector: show the native language name, including its accessible name.
        ("MainWindow.xaml", "Text", "ไทย"),
        ("MainWindow.xaml", "AutomationProperties.Name", "ไทย (Thai)"),
        // Amount-in-words example describes the Thai document output.
        ("MainWindow.xaml", "ToolTip", "10000 becomes หนึ่งหมื่นบาทถ้วน"),
        // Official ETDA standard name stays Thai (also in the excluded e-Tax UI).
        ("Views/ETaxInvoiceDialog.xaml", "Text", "• Follows ETDA standard ขมธอ. 3-2560, version 2.0"),
    ];

    private static bool NeedsTranslation(string text)
    {
        // No Latin letters: numbers, punctuation, symbols and Thai document/UI text need no English key.
        // This deliberately allows Thai e-Tax document content without excluding any e-Tax source file.
        if (!Regex.IsMatch(text, "[A-Za-z]")) return false;
        return !UntranslatedLiterals.Contains(text);
    }

    private static readonly HashSet<string> UntranslatedLiterals = new(StringComparer.Ordinal)
    {
        "YRK PDF", // Product brand, kept identical in both languages.
        "YRK LAB", // Publisher brand, kept identical in both languages.
        "Aa", // Case-matching visual sample; its tooltip supplies the translated explanation.
        "B", // Bold icon glyph; the parent button has a translated name and tooltip.
        "I", // Italic icon glyph; the parent button has a translated name and tooltip.
        "U", // Underline icon glyph; the tool has a separate translated label and tooltip.
        "S", // Strikeout icon glyph; the tool has a separate translated label and tooltip.
    };

    private sealed record TextUse(string Key, string File, int Line);
    private sealed record Token(string Value, int Offset, bool Literal = false);
    private static int Line(string source, int offset) => source[..offset].Count(c => c == '\n') + 1;
    private static string Display(string key) => JsonSerializer.Serialize(key, new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    // Tokenize comments and whole strings before looking for calls, avoiding commented-out examples.
    // Interpolated strings are not constant literal keys; consume them without treating them as keys.
    private static IEnumerable<Token> Tokens(string source)
    {
        const string pattern = "//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|(?:\\$@|@\\$|@)\"(?:\"\"|[^\"])*\"|\\$?\"(?:\\\\[\\s\\S]|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|[A-Za-z_][A-Za-z_0-9]*|[^\\s]";
        foreach (Match match in Regex.Matches(source, pattern))
        {
            var value = match.Value;
            if (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("/*", StringComparison.Ordinal)) continue;
            if (value.StartsWith("@\"", StringComparison.Ordinal))
                yield return new(value[2..^1].Replace("\"\"", "\""), match.Index, true);
            else if (value[0] == '"')
                yield return new(Unescape(value[1..^1]), match.Index, true);
            else yield return new(value, match.Index);
        }
    }

    private static string Unescape(string value) => Regex.Replace(value, @"\\(x[0-9a-fA-F]{1,4}|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|.)", m =>
    {
        var escape = m.Groups[1].Value;
        if (escape[0] is 'x' or 'u' or 'U')
        {
            var code = int.Parse(escape[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return escape[0] == 'U' ? char.ConvertFromUtf32(code) : ((char)code).ToString();
        }
        return escape switch
        {
            "0" => "\0", "a" => "\a", "b" => "\b", "f" => "\f", "n" => "\n",
            "r" => "\r", "t" => "\t", "v" => "\v", _ => escape,
        };
    });

    private static IEnumerable<TextUse> LocCalls(string source, string file)
    {
        var tokens = Tokens(source).ToArray();
        for (var i = 0; i + 4 < tokens.Length; i++)
        {
            if (tokens[i].Value != "Loc" || tokens[i + 1].Value != "."
                || tokens[i + 2].Value is not ("T" or "F") || tokens[i + 3].Value != "(") continue;
            var start = i + 4;
            var end = start;
            if (ReadConstant(tokens, ref end, out var key) && end < tokens.Length && tokens[end].Value is "," or ")")
                yield return new(key, file, Line(source, tokens[start].Offset));
        }
    }

    private static bool ReadConstant(Token[] tokens, ref int index, out string value)
    {
        value = "";
        if (!ReadTerm(tokens, ref index, out value)) return false;
        while (index < tokens.Length && tokens[index].Value == "+")
        {
            index++;
            if (!ReadTerm(tokens, ref index, out var next)) return false;
            value += next;
        }
        return true;
    }

    private static bool ReadTerm(Token[] tokens, ref int index, out string value)
    {
        value = "";
        if (index >= tokens.Length) return false;
        if (tokens[index].Literal) { value = tokens[index++].Value; return true; }
        if (tokens[index++].Value != "(" || !ReadConstant(tokens, ref index, out value)
            || index >= tokens.Length || tokens[index++].Value != ")") return false;
        return true;
    }

    private static bool CoveredProperty(string name) => name == "AutomationProperties.Name"
        || name[(name.LastIndexOf('.') + 1)..] is "Text" or "Header" or "Content" or "ToolTip" or "Title";

    private static string? XamlLiteral(string value)
    {
        // WPF's {} escape denotes a literal beginning with a brace. Other markup extensions
        // (Binding, StaticResource, DynamicResource, x:Static, etc.) are expressions, skipped at runtime.
        if (value.StartsWith("{}", StringComparison.Ordinal)) return value[2..];
        return value.TrimStart().StartsWith('{') ? null : value;
    }

    private static IEnumerable<TextUse> XamlTexts(string source, string file)
    {
        var document = XDocument.Parse(source, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        foreach (var element in document.Descendants())
        {
            // KeepText applies to this element only, not its descendants (matching TranslateTree).
            if ((string?)element.Attribute("Tag") == "keep-text") continue;
            foreach (var attribute in element.Attributes().Where(a => CoveredProperty(a.Name.LocalName)))
            {
                // UiTranslator handles TextBlock/Run.Text, never editable TextBox.Text or shape data.
                if (attribute.Name.LocalName == "Text" && element.Name.LocalName is not ("TextBlock" or "Run")) continue;
                if (XamlLiteral(attribute.Value) is { } key)
                    yield return new(key, file, ((IXmlLineInfo)attribute).LineNumber);
            }

            // Style/template setters set the same localizable dependency properties.
            if (element.Name.LocalName == "Setter" && CoveredProperty((string?)element.Attribute("Property") ?? "")
                && element.Attribute("Value") is { } setterValue && XamlLiteral(setterValue.Value) is { } setterKey)
                yield return new(setterKey, file, ((IXmlLineInfo)setterValue).LineNumber);

            var name = element.Name.LocalName;
            var dot = name.IndexOf('.');
            var propertyElement = dot >= 0 && CoveredProperty(name);
            if (propertyElement && (string?)element.Parent?.Attribute("Tag") == "keep-text") continue;
            // Direct text is WPF implicit Text/Content, including <Run> and <Button>Label</Button>.
            var implicitText = name is "TextBlock" or "Run" || IsContentControl(name);
            if (!propertyElement && !implicitText) continue;
            foreach (var text in element.Nodes().OfType<XText>().Where(t => !string.IsNullOrWhiteSpace(t.Value)))
            {
                var preserve = element.AncestorsAndSelf().Attributes(XNamespace.Xml + "space").FirstOrDefault()?.Value == "preserve";
                var normalized = preserve ? text.Value : Regex.Replace(text.Value, @"\s+", " ").Trim();
                if (XamlLiteral(normalized) is { } key)
                    yield return new(key, file, ((IXmlLineInfo)text).LineNumber);
            }
        }
    }

    private static bool IsContentControl(string name)
    {
        var type = typeof(System.Windows.Controls.ContentControl).Assembly.GetType("System.Windows.Controls." + name);
        return type is not null && typeof(System.Windows.Controls.ContentControl).IsAssignableFrom(type);
    }

    [Theory]
    [InlineData("Loc.T(\"Open\" + (\" file\"))", "Open file")]
    [InlineData("Loc.F(@\"Path \"\"{0}\"\"\", path)", "Path \"{0}\"")]
    [InlineData("// Loc.T(\"Ignored\")\nLoc.T(\"Line\\n\\u0041\")", "Line\nA")]
    public void CSharp_reader_decodes_literal_keys(string source, string expected) =>
        Assert.Equal(expected, Assert.Single(LocCalls(source, "Example.cs")).Key);

    [Fact]
    public void Xaml_reader_matches_expressions_keep_text_and_literal_forms()
    {
        const string source = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <TextBlock Text="A &amp; B" AutomationProperties.Name="Accessible" />
              <TextBlock Text="{Binding Name}" /><Button Content="{StaticResource Label}" />
              <TextBlock Tag="keep-text" Text="English" /><TextBox Text="Editable" />
              <Button><Button.ToolTip>Help</Button.ToolTip>Open</Button>
              <TextBlock><Run>Inline</Run></TextBlock>
              <Setter Property="Content" Value="Styled" />
              <TextBlock Text="{}{0} literal" /><TextBlock Text="ใบกำกับภาษี" />
            </StackPanel>
            """;
        Assert.Equal(new[] { "A & B", "Accessible", "Open", "Help", "Inline", "Styled", "{0} literal", "ใบกำกับภาษี" },
            XamlTexts(source, "Example.xaml").Select(t => t.Key));
        Assert.False(NeedsTranslation("ใบกำกับภาษี"));
        Assert.True(Placeholders("{{escaped}} {0:N2} {1,5} {0}").SetEquals([0, 1]));
    }
}
