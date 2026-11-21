using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class ThemeManagerTests
{
    [Fact]
    public void Loaded_window_resolves_swapped_palettes_and_preserves_fixed_dictionary()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var system = new SystemSource();
            var titleBars = new List<(Window Window, bool Dark)>();
            using var manager = new ThemeManager(Application.Current, system, (w, dark) => titleBars.Add((w, dark)));
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var fixedPalette = dictionaries[0];
            var styles = dictionaries[2];
            var paper = Application.Current.Resources["PaperBrush"];
            var window = new Window { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
            window.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
            try
            {
                window.Show(); WpfTestApp.Pump();
                var light = Assert.IsType<SolidColorBrush>(window.Background).Color;
                foreach (var preference in new[] { "dark", "light", "dark", "light" })
                {
                    var previous = dictionaries[1];
                    manager.Apply(preference); WpfTestApp.Layout(window);
                    Assert.NotSame(previous, dictionaries[1]);
                    Assert.Same(dictionaries[1]["SurfaceBrush"], window.Background);
                    Assert.Equal(preference == "light", light == Assert.IsType<SolidColorBrush>(window.Background).Color);
                    Assert.Same(fixedPalette, dictionaries[0]);
                    Assert.Same(styles, dictionaries[2]);
                    Assert.Same(paper, window.FindResource("PaperBrush"));
                    Assert.Equal(3, dictionaries.Count);
                    Assert.Equal((window, preference == "dark"), titleBars.Last());
                }
                manager.Apply("dark");
                system.HighContrast = true;
                system.RaiseHighContrast(); WpfTestApp.Pump();
                Assert.Equal((window, false), titleBars.Last());
                system.HighContrast = false;
                system.RaiseHighContrast(); WpfTestApp.Pump();
                Assert.Equal((window, true), titleBars.Last());
                Assert.Equal("dark", manager.Preference);
            }
            finally { window.Close(); WpfTestApp.Pump(); }
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public void System_callbacks_dispatch_and_high_contrast_restores_choice_without_saving(string preference)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            using var settings = new SettingsFile();
            AppSettings.Update(s => s.Theme = preference, settings.Path);
            var saved = File.ReadAllBytes(settings.Path);
            var system = new SystemSource { AppsUseLightTheme = false, HighContrast = true };
            using var manager = new ThemeManager(Application.Current, system);
            manager.Apply(AppSettings.Load(settings.Path).Theme);
            Assert.Equal("HighContrast", manager.EffectiveTheme);
            Assert.Equal(preference, manager.Preference);

            // A worker thread must only queue the resource update until the UI pumps.
            system.HighContrast = false;
            Task.Run(system.RaiseHighContrast).GetAwaiter().GetResult();
            Assert.Equal("HighContrast", manager.EffectiveTheme);
            WpfTestApp.Pump();
            Assert.Equal(preference == "light" ? "Light" : "Dark", manager.EffectiveTheme);

            system.AppsUseLightTheme = true;
            Task.Run(system.RaisePreference).GetAwaiter().GetResult();
            WpfTestApp.Pump();
            Assert.Equal(preference == "dark" ? "Dark" : "Light", manager.EffectiveTheme);
            system.HighContrast = true;
            Task.Run(system.RaiseHighContrast).GetAwaiter().GetResult();
            WpfTestApp.Pump();
            Assert.Equal("HighContrast", manager.EffectiveTheme);
            Assert.Equal(preference, manager.Preference);
            Assert.Equal(saved, File.ReadAllBytes(settings.Path));
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }

    [Fact]
    public void Theme_menu_saves_user_choice_and_keeps_exactly_one_check_even_in_high_contrast()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("th");
            using var settings = new SettingsFile();
            var system = new SystemSource { HighContrast = true };
            using var manager = new ThemeManager(Application.Current, system);
            manager.Apply("system");
            var menu = MainWindow.CreateThemeMenu(manager, settings.Path);
            var button = new Button();
            var host = new Window { Content = button, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
            var popup = new ContextMenu { PlacementTarget = button, Items = { menu } };
            try
            {
                host.Show(); WpfTestApp.Layout(host);
                popup.IsOpen = true;
                menu.IsSubmenuOpen = true;
                WpfTestApp.Pump();
                Assert.Equal("ธีม", menu.Header);
                var choices = menu.Items.Cast<MenuItem>().ToArray();
                Assert.Equal(new[] { "ตามระบบ", "สว่าง", "มืด" }, choices.Select(c => c.Header));
                Assert.Equal("system", Assert.Single(choices, c => c.IsChecked).CommandParameter);
                foreach (var code in new[] { "dark", "dark", "light", "system" })
                {
                    var choice = choices.Single(c => Equals(c.CommandParameter, code));
                    // MenuItem toggles itself before raising Click, including unchecking an already selected item.
                    choice.IsChecked = !choice.IsChecked;
                    choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, choice));
                    Assert.Equal(code, AppSettings.Load(settings.Path).Theme);
                    Assert.Equal(code, manager.Preference);
                    Assert.Equal("HighContrast", manager.EffectiveTheme);
                    Assert.Same(choice, Assert.Single(choices, c => c.IsChecked));
                }
                popup.IsOpen = false;
                var reopened = MainWindow.CreateThemeMenu(manager, settings.Path);
                popup.Items.Clear(); popup.Items.Add(reopened);
                popup.IsOpen = true; reopened.IsSubmenuOpen = true; WpfTestApp.Pump();
                Assert.Equal("system", Assert.Single(reopened.Items.Cast<MenuItem>(), c => c.IsChecked).CommandParameter);
                system.HighContrast = false;
                system.RaiseHighContrast(); WpfTestApp.Pump();
                Assert.Equal("Light", manager.EffectiveTheme);
                Assert.Equal("system", AppSettings.Load(settings.Path).Theme);
            }
            finally { popup.IsOpen = false; host.Close(); WpfTestApp.Pump(); }
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Theme\":null}")]
    [InlineData("{\"Theme\":\"unknown-theme\"}")]
    [InlineData("{\"Theme\":\"DARK\"}")]
    public void Missing_or_unknown_theme_reads_as_system_without_rewriting_file(string json)
    {
        using var settings = new SettingsFile();
        Assert.Equal("system", AppSettings.Load(settings.Path).Theme);
        Assert.False(File.Exists(settings.Path));
        File.WriteAllText(settings.Path, json);
        Assert.Equal("system", AppSettings.Load(settings.Path).Theme);
        Assert.Equal(json, File.ReadAllText(settings.Path));
    }

    [Fact]
    public void Older_settings_reader_accepts_and_preserves_theme_as_extension_data()
    {
        using var settings = new SettingsFile();
        AppSettings.Update(s => s.Theme = "dark", settings.Path);
        var older = JsonSerializer.Deserialize<OlderSettings>(File.ReadAllText(settings.Path))!;
        Assert.Equal("dark", older.Extra!["Theme"].GetString());
        Assert.Equal("dark", JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(older))!.Theme);
    }

    [Fact]
    public void Exit_unsubscribes_both_system_events_and_ignores_already_queued_refresh()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var system = new SystemSource();
            using var manager = new ThemeManager(Application.Current, system);
            system.AppsUseLightTheme = false;
            manager.Apply("system");
            Assert.Equal(1, system.PreferenceSubscribers);
            Assert.Equal(1, system.PropertySubscribers);
            // Queue before Exit, then verify the pending callback cannot modify the disposed manager.
            system.AppsUseLightTheme = true;
            system.RaisePreference();
            // Raise the actual Exit event through Application's lifecycle method without shutting down
            // the shared test dispatcher (WpfTestApp owns that teardown).
            typeof(Application).GetMethod("OnExit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Application.Current, [Activator.CreateInstance(typeof(ExitEventArgs),
                    BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [0], culture: null)]);
            Assert.Equal(0, system.PreferenceSubscribers);
            Assert.Equal(0, system.PropertySubscribers);
            system.RaisePreference(); system.RaiseHighContrast(); WpfTestApp.Pump();
            manager.Apply("light");
            Assert.Equal("system", manager.Preference);
            Assert.Equal("Dark", manager.EffectiveTheme);
            return true;
        });
    }

    private sealed class SystemSource : IThemeSystemSource
    {
        public bool AppsUseLightTheme { get; set; } = true;
        public bool HighContrast { get; set; }
        public event UserPreferenceChangedEventHandler? UserPreferenceChanged;
        public event PropertyChangedEventHandler? StaticPropertyChanged;
        public int PreferenceSubscribers => UserPreferenceChanged?.GetInvocationList().Length ?? 0;
        public int PropertySubscribers => StaticPropertyChanged?.GetInvocationList().Length ?? 0;
        public void RaisePreference() => UserPreferenceChanged?.Invoke(this, new UserPreferenceChangedEventArgs(UserPreferenceCategory.General));
        public void RaiseHighContrast() => StaticPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemParameters.HighContrast)));
    }

    private sealed class SettingsFile : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(System.IO.Path.Combine(
            PrivateTempDirectory.Path, "theme-settings-" + Guid.NewGuid().ToString("N"))).FullName;
        public string Path => System.IO.Path.Combine(_root, "settings.json");
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class OlderSettings
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
