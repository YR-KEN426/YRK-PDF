using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Diagnostics;
using Xunit.Abstractions;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class AllWindowsLoadTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Windows => WindowCatalog.Cases.Where(c => c.Create is not null).SelectMany(c => new[] { "th", "en" }
        .Select(language => new object[] { c.Name, language }));

    [Fact]
    public void Construction_table_covers_all_39_windows_with_explicit_exclusions()
    {
        var types = typeof(MainWindow).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(Window).IsAssignableFrom(t)
            && (t == typeof(MainWindow) || t.Namespace == "Yrk.PdfStudio.App.Views")).OrderBy(t => t.FullName).ToArray();
        Assert.Equal(39, types.Length);
        Assert.Equal(types, WindowCatalog.Cases.Select(c => c.Type).OrderBy(t => t.FullName));
        Assert.All(WindowCatalog.Cases, c => Assert.True(c.Create is not null || !string.IsNullOrWhiteSpace(c.Unavailable)));
    }

    [Fact]
    public void WpfTestApp_captures_binding_and_resource_trace_errors()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var text = new TextBlock { DataContext = new object() };
            text.SetBinding(TextBlock.TextProperty, new Binding("MissingFixtureProperty"));
            var window = new Window { Content = text, Width = 300, Height = 200, ShowInTaskbar = false, Left = -32000 };
            try
            {
                window.Show(); WpfTestApp.Layout(window);
                Assert.Contains(app.Errors, e => e.Contains("MissingFixtureProperty", StringComparison.Ordinal));
                PresentationTraceSources.ResourceDictionarySource.TraceEvent(TraceEventType.Error, 999, "MissingFixtureResource");
                Assert.Contains(app.Errors, e => e.Contains("MissingFixtureResource", StringComparison.Ordinal));
            }
            finally { window.Close(); }
            return true;
        });
    }

    [Theory]
    [InlineData("th")]
    [InlineData("en")]
    public void Unavailable_windows_have_documented_reasons(string language)
    {
        foreach (var entry in WindowCatalog.Cases.Where(c => c.Create is null))
        {
            output.WriteLine($"UNAVAILABLE {entry.Name} [{language}]: {entry.Unavailable}");
            Assert.False(string.IsNullOrWhiteSpace(entry.Unavailable));
        }
    }

    [Theory(Skip = "DigitalSignDialog: DigitalIdStore.LoadAll enumerates personal Windows certificates and has no injectable source; see WindowCatalog and tools/WindowRenderer/README.md.")]
    [InlineData("th")]
    [InlineData("en")]
    public void DigitalSignDialog_requires_isolated_certificate_source(string language) =>
        throw new NotSupportedException(language);

    [Theory]
    [MemberData(nameof(Windows))]
    public void Window_loads_layouts_and_opens_popups_without_diagnostics(string name, string language)
    {
        var entry = WindowCatalog.Cases.Single(c => c.Name == name);
        output.WriteLine($"{name} [{language}]: {entry.Fixture}");
        if (entry.Create is null)
        {
            output.WriteLine("UNAVAILABLE (construction table): " + entry.Unavailable);
            // A missing factory remains visible as a failing case until the app exposes an isolated source.
            Assert.Fail(entry.Unavailable);
        }
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            using var fixture = new WindowFixture();
            var window = entry.Create!(fixture);
            try
            {
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000; window.Top = -32000;
                window.Show();
                WpfTestApp.Layout(window);
                output.WriteLine($"Opened {WpfTestApp.ExercisePopups(window)} ComboBox/DatePicker popups.");
            }
            finally { window.Close(); WpfTestApp.Pump(); }
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }
}
