using System.Text.Json;
using System.Text.RegularExpressions;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ErrorUsageGuardTests
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
            throw new DirectoryNotFoundException("Cannot locate src/Yrk.PdfStudio.App for source guards.");
        }
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs")
            .Concat(Directory.EnumerateDirectories(directory)
                .Where(path => Path.GetFileName(path) is not ("bin" or "obj"))
                .SelectMany(SourceFiles));

    [Fact]
    public void Exception_messages_are_confined_to_details_and_logs()
    {
        var failures = new List<string>();
        foreach (var file in SourceFiles(AppSource))
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(AppSource, file).Replace('\\', '/');
            // ErrorDialog alone renders redacted exception details in its collapsed expander.
            if (relative == "Views/ErrorDialog.xaml.cs") continue;
            failures.AddRange(UnsafeExceptionMessages(source, relative));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<string> UnsafeExceptionMessages(string source, string relative)
    {
        // Include renamed catch variables and typed exception parameters/locals, not just `ex`.
        var names = Regex.Matches(source, @"\b[\w.]*Exception\s*\??\s+(\w+)")
            .Select(match => match.Groups[1].Value).Concat(["ex", "exception"])
            .Distinct().Select(Regex.Escape);
        var pattern = @"\b(?:" + string.Join("|", names) + @")\s*\??\.\s*Message\b"
            + @"|\b(?:Exception|InnerException|GetBaseException\s*\(\s*\))\s*\??\.\s*Message\b";
        foreach (Match match in Regex.Matches(source, pattern))
        {
            var lineStart = source.LastIndexOf('\n', match.Index) + 1;
            var lineEnd = source.IndexOf('\n', match.Index);
            var line = source[lineStart..(lineEnd < 0 ? source.Length : lineEnd)].Trim();
            // Logging may contain technical messages; it must be a standalone log statement.
            if (Regex.IsMatch(line, @"^(?:App\.)?Log(?:Message|Error)\s*\([^;]*\);$")) continue;
            // Import compares one sanitized Core error to choose a fixed translated label;
            // the exception text itself is never assigned to the label or formatted for display.
            if (relative == "Views/ImportDialog.xaml.cs"
                && line == ": ex.Message == Loc.T(BackupImportCoordinator.SafetyError)") continue;
            var number = source[..match.Index].Count(c => c == '\n') + 1;
            yield return $"{relative}:{number}: {line}";
        }
    }

    [Theory]
    [InlineData("catch (IOException failure) { Status.Text = failure.Message; }")]
    [InlineData("catch (Exception trouble) { ShowError(Loc.F(\"Failed: {0}\",\n trouble.Message)); }")]
    [InlineData("void Render(Exception problem) { var text = problem.Message; Summary = text; }")]
    [InlineData("catch (Exception ex) { Error = ex.GetBaseException().Message; }")]
    [InlineData("catch (Exception ex) {\n App.LogMessage(ex.Message); Summary = ex.Message;\n }")]
    public void Guard_rejects_multiline_calls_and_renamed_exception_variables(string source) =>
        Assert.NotEmpty(UnsafeExceptionMessages(source, "Example.cs"));

    [Fact]
    public void Message_boxes_only_exist_in_dialog_service_and_crash_fallback()
    {
        foreach (var file in SourceFiles(AppSource))
        {
            var relative = Path.GetRelativePath(AppSource, file).Replace('\\', '/');
            var source = File.ReadAllText(file);
            if (relative == "App.xaml.cs")
            {
                Assert.Single(Regex.Matches(source, @"\bMessageBox\s*\.\s*Show\s*\("));
                var fallback = source[source.IndexOf("internal static void ShowCrashMessage", StringComparison.Ordinal)
                    ..source.IndexOf("internal static void OnDispatcherUnhandledException", StringComparison.Ordinal)];
                Assert.Single(Regex.Matches(fallback, @"\bMessageBox\s*\.\s*Show\s*\("));
                Assert.Contains("catch (Exception)", fallback);
                Assert.Contains("new Views.MessageDialog", fallback);
                source = source.Replace(
                    "MessageBox.Show(message, DialogService.Caption, MessageBoxButton.OK, MessageBoxImage.Error);",
                    "CrashFallback");
            }
            if (relative == "Services/DialogService.cs")
            {
                var fallback = source[source.IndexOf("catch (Exception dialogError)", StringComparison.Ordinal)
                    ..source.IndexOf("private bool ShowDialog(CommonDialog", StringComparison.Ordinal)];
                Assert.Single(Regex.Matches(fallback, @"\bMessageBox\s*\.\s*Show\s*\("));
                // Exempt this exact fallback statement once, never the method or file.
                var match = Regex.Match(fallback, @"MessageBox\.Show\([\s\S]*?MessageBoxImage\.Error\);");
                Assert.True(match.Success);
                var position = source.IndexOf(match.Value, StringComparison.Ordinal);
                source = source.Remove(position, match.Length);
            }
            Assert.False(Regex.IsMatch(source, @"\bMessageBox\s*\.\s*Show\s*\("), relative);
        }
    }

    [Fact]
    public void Dialog_service_only_accepts_catalogue_errors()
    {
        var source = File.ReadAllText(Path.Combine(AppSource, "Services", "IDialogService.cs"));
        Assert.DoesNotMatch(@"\bShowError\s*\(\s*(?:string|String|System\.String)\b", source);
        var method = Assert.Single(typeof(IDialogService).GetMethods().Where(m => m.Name == "ShowError"));
        Assert.Equal(typeof(ErrorInfo), method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Catalogue_translations_have_no_duplicate_keys_and_preserve_placeholders()
    {
        using var stream = typeof(ErrorCatalog).Assembly.GetManifestResourceStream("Yrk.PdfStudio.App.Localization.th.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in ErrorCatalog.All.SelectMany(error => new[] { error.Message, error.NextStep }))
        {
            var translated = document.RootElement.GetProperty(key).GetString()!;
            Assert.Equal(Regex.Matches(key, @"\{\d+\}").Select(m => m.Value).Order(),
                Regex.Matches(translated, @"\{\d+\}").Select(m => m.Value).Order());
        }
    }
}
