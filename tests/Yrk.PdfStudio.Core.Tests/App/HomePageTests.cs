using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Converters;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>
/// The start page in a real MainWindow: the top bar (buttons that need a document), the two-column layout, the drop zone, the
/// recent files panel, the privacy option and (later) the tool cards. Every file name is made up.
/// </summary>
[Collection("WPF Application")]
public sealed partial class HomePageTests
{
    /// <summary>Builds a MainWindow over a real engine and shows it off screen at the given size, with the recent files of the test.</summary>
    private static void WithWindow(Action<AppTestHarness, MainWindow, WpfTestApp> test, double width = 1280, double height = 820,
        string language = "en", int recentFiles = 0, Func<int, string>? name = null)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            var harness = new AppTestHarness();
            var documents = Directory.CreateDirectory(PrivateTempDirectory.Path + "-documents-" + Guid.NewGuid().ToString("N")).FullName;
            MainWindow? window = null;
            try
            {
                for (var i = 0; i < recentFiles; i++)
                {
                    var path = Path.Combine(documents, (name ?? (n => $"example-file-{n:00}.pdf"))(i));
                    File.WriteAllText(path, "x");
                    RecentFiles.Add(path);
                }

                window = new MainWindow(harness.Model, () => throw new NotSupportedException());
                window.Width = width;
                window.Height = height;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
                WpfTestApp.Layout(window);
                PumpUntil(() => recentFiles == 0 || ((ItemsControl)window.FindName("HomeRecentRows")).Items.Count > 0);
                WpfTestApp.Layout(window);
                test(harness, window, app);
                var errors = UnexpectedErrors(app.Errors);
                Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
            }
            finally
            {
                WpfTestApp.Await(harness.Model.CloseTabAsync(keepAlive: true));
                window?.Close();
                WpfTestApp.Await(harness.DisposeAsync().AsTask());
                Directory.Delete(documents, true);
            }
            return true;
        });
    }

    private static T Wait<T>(Task<T> task)
    {
        WpfTestApp.Await(task);
        return task.Result;
    }

    /// <summary>Binding traces that are not about this page: detached Freezable bindings of the document templates, known from step 1.</summary>
    private static List<string> UnexpectedErrors(IEnumerable<string> errors) =>
        errors.Where(e => !System.Text.RegularExpressions.Regex.IsMatch(e.Trim(), @"^System\.Windows\.Data Error: \d+ :$"))
            .Where(e => !e.Contains("Transform'") && !e.Contains("'SolidColorBrush'"))
            .ToList();

    private static void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < until)
        {
            WpfTestApp.Pump();
            Thread.Sleep(10);
        }
        Assert.True(done(), "The condition was not met in 15 seconds.");
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Logical(child)) yield return nested;
    }

    private static T Find<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);

    private static IEnumerable<FrameworkElement> TopBarElements(MainWindow window) =>
        WpfTestApp.Descendants(Find<Grid>(window, "TopBar")).OfType<FrameworkElement>();

    private static string? VisibilityPath(FrameworkElement element) =>
        BindingOperations.GetBinding(element, UIElement.VisibilityProperty)?.Path.Path;

    // --- Top bar ------------------------------------------------------------------------------------

    private static readonly string[] DocumentButtons =
        ["Start page", "View", "Insert", "Open", "Recent files", "Save", "Save as", "Print", "Reading marks"];

    private static readonly string[] AlwaysThere = ["Sidebar", "File", "Tools", "Settings", "Help"];

    private static string? NameOf(FrameworkElement element) => AutomationProperties.GetName(element) is { Length: > 0 } name ? name
        : element is Button { Command: { } command } && ReferenceEquals(command, ApplicationCommands.Open) ? "Open" : null;

    private static Visibility VisibilityOfNamed(MainWindow window, string name) =>
        TopBarElements(window).First(e => NameOf(e) == name).Visibility;

    [Fact]
    public void Document_buttons_are_hidden_on_the_start_page_through_one_binding_to_ShowsDocument()
    {
        WithWindow((app, window, _) =>
        {
            var bound = TopBarElements(window).Where(e => VisibilityPath(e) is not null).ToList();
            // Every binding that hides something in the top bar is on ShowsDocument, never on HasDocument (which is false
            // for a moment while a saved file is opened again, and would flash the start page's bar).
            Assert.All(bound, e => Assert.Equal(nameof(MainViewModel.ShowsDocument), VisibilityPath(e)));
            foreach (var name in DocumentButtons)
            {
                var button = TopBarElements(window).First(e => NameOf(e) == name);
                Assert.IsType<BooleanToVisibilityConverter>(BindingOperations.GetBinding(button, UIElement.VisibilityProperty)!.Converter);
            }

            Assert.All(new[] { "SearchPanel", "ViewMenuButton", "InsertMenuButton" }, n =>
                Assert.Equal(nameof(MainViewModel.ShowsDocument), VisibilityPath(Find<FrameworkElement>(window, n))));
            foreach (var name in AlwaysThere)
                Assert.Null(VisibilityPath(TopBarElements(window).First(e => NameOf(e) == name)));
            // The logo and the language pair are the other way round: shown only while there is no document.
            foreach (var name in new[] { "TopBarBrand", "TopBarLanguage" })
                Assert.IsType<InverseBooleanToVisibilityConverter>(
                    BindingOperations.GetBinding(Find<FrameworkElement>(window, name), UIElement.VisibilityProperty)!.Converter);

            // On the start page: only the menu button, logo, four menus and the language pair are there.
            Assert.False(app.Model.ShowsDocument);
            foreach (var name in DocumentButtons) Assert.Equal(Visibility.Collapsed, VisibilityOfNamed(window, name));
            foreach (var name in AlwaysThere) Assert.Equal(Visibility.Visible, VisibilityOfNamed(window, name));
            Assert.Equal(Visibility.Collapsed, Find<FrameworkElement>(window, "SearchPanel").Visibility);
            Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "TopBarBrand").Visibility);
            Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "TopBarLanguage").Visibility);
            Assert.All(bound, e => Assert.True(e.Visibility == (IsInverse(e) ? Visibility.Visible : Visibility.Collapsed)));
            Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "HomePage").Visibility);
        });
    }

    private static bool IsInverse(FrameworkElement element) =>
        BindingOperations.GetBinding(element, UIElement.VisibilityProperty)!.Converter is InverseBooleanToVisibilityConverter;

    private static void AssertDocumentBar(MainWindow window, bool shown)
    {
        var visible = shown ? Visibility.Visible : Visibility.Collapsed;
        var other = shown ? Visibility.Collapsed : Visibility.Visible;
        foreach (var name in DocumentButtons) Assert.Equal(visible, VisibilityOfNamed(window, name));
        Assert.Equal(visible, Find<FrameworkElement>(window, "SearchPanel").Visibility);
        Assert.Equal(other, Find<FrameworkElement>(window, "TopBarBrand").Visibility);
        Assert.Equal(other, Find<FrameworkElement>(window, "TopBarLanguage").Visibility);
        Assert.Equal(visible, Find<FrameworkElement>(window, "ViewMenuButton").Visibility);
        // The group of page, zoom and ruler buttons and the dividers come and go with the rest.
        Assert.All(TopBarElements(window).Where(e => VisibilityPath(e) is not null && !IsInverse(e)), e => Assert.Equal(visible, e.Visibility));
    }

    [Fact]
    public void Opening_a_file_brings_every_button_back_and_closing_it_returns_to_the_start_bar()
    {
        WithWindow((app, window, _) =>
        {
            AssertDocumentBar(window, shown: false);
            Assert.True(Wait(app.OpenAsync(app.CreatePdf())));
            WpfTestApp.Pump();
            AssertDocumentBar(window, shown: true);
            Assert.Equal(Visibility.Collapsed, Find<FrameworkElement>(window, "HomePage").Visibility);

            // Document, start page, document again.
            WpfTestApp.Await(app.Model.CloseTabAsync(keepAlive: true));
            WpfTestApp.Pump();
            AssertDocumentBar(window, shown: false);
            Assert.True(Wait(app.OpenAsync(app.CreatePdf())));
            WpfTestApp.Pump();
            AssertDocumentBar(window, shown: true);
        });
    }

    [Fact]
    public void A_cancelled_or_failed_open_keeps_the_start_bar()
    {
        WithWindow((app, window, _) =>
        {
            WpfTestApp.Await(app.Model.OpenCommand.ExecuteAsync(null)); // The scripted file chooser is cancelled.
            WpfTestApp.Pump();
            AssertDocumentBar(window, shown: false);
            Assert.False(Wait(app.OpenAsync(app.PathFor("missing.pdf")))); // A file that cannot be opened.
            WpfTestApp.Pump();
            AssertDocumentBar(window, shown: false);
            Assert.NotEmpty(app.Dialogs.Named("ShowError"));
        });
    }

    [Fact]
    public void Saving_and_opening_the_file_again_never_flashes_the_start_bar()
    {
        WithWindow((app, window, _) =>
        {
            var source = app.CreatePdf();
            Assert.True(Wait(app.OpenAsync(source)));
            WpfTestApp.Pump();
            var samples = new List<(string? Property, bool Hidden, bool BrandShown)>();
            var view = Find<FrameworkElement>(window, "ViewMenuButton");
            var brand = Find<FrameworkElement>(window, "TopBarBrand");
            var sawReopen = false;
            void Record(object? sender, PropertyChangedEventArgs e)
            {
                sawReopen |= app.Model.IsReopeningDocument && !app.Model.HasDocument;
                samples.Add((e.PropertyName, view.Visibility != Visibility.Visible, brand.Visibility == Visibility.Visible));
            }

            app.Model.PropertyChanged += Record;
            try
            {
                WpfTestApp.Await(app.RotateAsync()); // Changes something so that Save has work to do.
                WpfTestApp.Await(app.Model.SaveCommand.ExecuteAsync(null));
                WpfTestApp.Await(app.SaveToAsync(source)); // Save a copy over the same file: the document is opened again.
            }
            finally { app.Model.PropertyChanged -= Record; }

            Assert.NotEmpty(samples);
            Assert.True(sawReopen, "The test must pass through the moment HasDocument is false.");
            Assert.DoesNotContain(samples, s => s.Hidden || s.BrandShown);
            AssertDocumentBar(window, shown: true);
        });
    }

    [Fact]
    public void The_language_pair_lives_in_the_top_bar_and_switches_the_language()
    {
        var language = Loc.Language;
        WithWindow((app, window, _) =>
        {
            var thai = Find<RadioButton>(window, "HomeThai");
            var english = Find<RadioButton>(window, "HomeEnglish");
            Assert.True(TopBarElements(window).Contains(thai));
            Assert.Equal("ไทย (Thai)", AutomationProperties.GetName(thai));
            Assert.Equal("English", AutomationProperties.GetName(english));
            Assert.True(english.IsChecked);
            Assert.False(thai.IsChecked);
            Assert.DoesNotContain(WpfTestApp.Descendants(Find<ScrollViewer>(window, "HomePage")), e => e is RadioButton { Tag: "keep-text" });
            Loc.Language = Loc.Thai;
            WpfTestApp.Pump();
            Assert.True(thai.IsChecked);
        });
        Assert.Equal(language, Loc.Language);
    }

    // --- Drop zone ------------------------------------------------------------------------------------

    [Fact]
    public void The_drop_zone_has_a_dashed_frame_that_turns_brand_red_while_a_file_is_dragged_over()
    {
        WithWindow((app, window, _) =>
        {
            var frame = Find<Rectangle>(window, "HomeDropFrame");
            Assert.Equal(16, frame.RadiusX);
            Assert.Equal(2, frame.StrokeThickness);
            Assert.NotEmpty(frame.StrokeDashArray);
            var resources = Application.Current.Resources;
            Assert.Same(resources["HomeBorderStrongBrush"], frame.Stroke);
            Assert.Same(resources["SurfaceBrush"], frame.Fill);
            var activate = typeof(MainWindow).GetMethod("SetHomeDropActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
            activate.Invoke(window, [true]);
            Assert.Same(resources["BrandBrush"], frame.Stroke);
            Assert.Same(resources["BrandFaintBrush"], frame.Fill);
            activate.Invoke(window, [false]);
            Assert.Same(resources["HomeBorderStrongBrush"], frame.Stroke);
            Assert.Same(resources["SurfaceBrush"], frame.Fill);
        });
    }

    [Fact]
    public void Brand_red_on_the_start_page_is_the_open_button_the_logo_and_the_drop_zone_icon_only()
    {
        WithWindow((app, window, _) =>
        {
            var resources = Application.Current.Resources;
            var page = Find<ScrollViewer>(window, "HomePage");
            var brandButtons = WpfTestApp.Descendants(page).OfType<Button>().Where(b => ReferenceEquals(b.Style, resources["BrandButton"])).ToList();
            var open = Assert.Single(brandButtons);
            Assert.Equal("Open file", AutomationProperties.GetName(open));
            Assert.Same(ApplicationCommands.Open, open.Command);
            Assert.Same(resources["BrandBrush"], open.Background);
            Assert.NotEqual(((SolidColorBrush)resources["AccentBrush"]).Color, ((SolidColorBrush)resources["BrandBrush"]).Color);
        });
    }

    // --- Layout -----------------------------------------------------------------------------------------

    private static double Left(FrameworkElement element, FrameworkElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo).X;
    private static double Top(FrameworkElement element, FrameworkElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo).Y;

    [Fact]
    public void A_wide_window_with_recent_files_has_tools_on_the_left_and_a_340_wide_panel_on_the_right()
    {
        WithWindow((app, window, _) =>
        {
            var panel = Find<Border>(window, "HomeRecentPanel");
            var main = Find<StackPanel>(window, "HomeMain");
            var root = Find<Grid>(window, "HomeColumns");
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(1, Grid.GetColumn(panel));
            Assert.Equal(0, Grid.GetRow(panel));
            Assert.Equal(340, panel.ActualWidth, 1);
            Assert.True(Left(panel, root) >= Left(main, root) + main.ActualWidth);
            Assert.Equal(root.ActualWidth, Left(panel, root) + panel.ActualWidth, 1); // Flush with the right edge.
            Assert.Equal(24, Left(panel, root) - (Left(main, root) + main.ActualWidth), 1); // Gap of 24.
        }, width: 1280, recentFiles: 3);
    }

    [Fact]
    public void A_window_narrower_than_1100_puts_the_recent_files_under_the_tools_across_the_page()
    {
        WithWindow((app, window, _) =>
        {
            var panel = Find<Border>(window, "HomeRecentPanel");
            var main = Find<StackPanel>(window, "HomeMain");
            var root = Find<Grid>(window, "HomeColumns");
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(0, Grid.GetColumn(panel));
            Assert.Equal(1, Grid.GetRow(panel));
            Assert.True(Top(panel, root) >= Top(main, root) + main.ActualHeight);
            Assert.Equal(root.ActualWidth, panel.ActualWidth, 1);
        }, width: 1000, recentFiles: 3);
    }

    [Fact]
    public void Without_recent_files_the_tools_are_at_most_900_wide_and_in_the_middle()
    {
        WithWindow((app, window, _) =>
        {
            var panel = Find<Border>(window, "HomeRecentPanel");
            var main = Find<StackPanel>(window, "HomeMain");
            var root = Find<Grid>(window, "HomeColumns");
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.Equal(0, Find<Grid>(window, "HomeColumns").ColumnDefinitions[1].ActualWidth, 1);
            Assert.True(main.ActualWidth <= 900.5);
            Assert.Equal(900, main.ActualWidth, 1);
            Assert.InRange(Math.Abs(Left(main, root) - (root.ActualWidth - Left(main, root) - main.ActualWidth)), 0, 1.5); // Centered.
        });
    }

    [Fact]
    public void In_a_narrow_window_the_tools_alone_use_the_whole_page()
    {
        WithWindow((app, window, _) =>
        {
            var main = Find<StackPanel>(window, "HomeMain");
            Assert.Equal(Find<Grid>(window, "HomeColumns").ActualWidth, main.ActualWidth, 1);
            Assert.True(Find<ScrollViewer>(window, "HomePage").ExtentWidth <= 1000);
        }, width: 760);
    }

    [Fact]
    public void Recent_files_show_six_rows_and_view_all_shows_twelve()
    {
        WithWindow((app, window, _) =>
        {
            var rows = Find<ItemsControl>(window, "HomeRecentRows");
            Assert.Equal(6, rows.Items.Count);
            var toggle = WpfTestApp.Descendants(Find<Border>(window, "HomeRecentPanel")).OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "View all recent files");
            Assert.Equal(Visibility.Visible, toggle.Visibility);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WpfTestApp.Pump();
            Assert.Equal(12, rows.Items.Count);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WpfTestApp.Pump();
            Assert.Equal(6, rows.Items.Count);
        }, recentFiles: 12);
    }

    [Fact]
    public void Turning_the_option_off_makes_the_recent_panel_go_and_on_brings_it_back()
    {
        var saveSuspended = typeof(AppSettings).GetField("s_saveSuspended", BindingFlags.Static | BindingFlags.NonPublic)!;
        var wasSuspended = saveSuspended.GetValue(null);
        saveSuspended.SetValue(null, true); // Nothing here may change the user's real settings file.
        WithWindow((app, window, _) =>
        {
            var original = app.Model.ShowRecentOnHome;
            try
            {
                var panel = Find<Border>(window, "HomeRecentPanel");
                app.Model.ShowRecentOnHome = true;
                PumpUntil(() => panel.Visibility == Visibility.Visible);
                app.Model.ShowRecentOnHome = false;
                PumpUntil(() => panel.Visibility == Visibility.Collapsed);
                WpfTestApp.Layout(window);
                Assert.Equal(0, Find<Grid>(window, "HomeColumns").ColumnDefinitions[1].ActualWidth, 1);
                Assert.Equal(3, RecentFiles.Items.Count); // Only hidden: the history is still kept.
                app.Model.ShowRecentOnHome = true;
                PumpUntil(() => panel.Visibility == Visibility.Visible);
            }
            finally { app.Model.ShowRecentOnHome = original; }
        }, recentFiles: 3);
        saveSuspended.SetValue(null, wasSuspended);
    }

    [Fact]
    public void A_file_deleted_while_the_start_page_is_away_is_gone_from_the_panel_but_not_from_the_history()
    {
        WithWindow((app, window, _) =>
        {
            var rows = Find<ItemsControl>(window, "HomeRecentRows");
            Assert.Equal(3, rows.Items.Count);
            var gone = RecentFiles.Items[0].Path;
            Assert.True(Wait(app.OpenAsync(app.CreatePdf())));
            File.Delete(gone);
            WpfTestApp.Await(app.Model.CloseTabAsync(keepAlive: true)); // Back to the start page.
            PumpUntil(() => rows.Items.Count == 2);
            Assert.DoesNotContain(rows.Items.Cast<RecentRow>(), r => r.Path == gone);
            Assert.Contains(RecentFiles.Items, i => i.Path == gone);
        }, recentFiles: 3);
    }

    [Fact]
    public void A_row_names_the_file_in_a_badge_and_shows_its_time_and_the_full_path_in_a_tooltip()
    {
        WithWindow((app, window, _) =>
        {
            var rows = Find<ItemsControl>(window, "HomeRecentRows");
            var row = Assert.IsType<Button>(VisualTreeHelperChild(rows, 0));
            var item = rows.Items.Cast<RecentRow>().First();
            Assert.Equal(item.Path, row.ToolTip);
            Assert.Equal(item.Name, AutomationProperties.GetName(row));
            Assert.Equal(item.Path, row.Tag);
            Assert.True(row.MinHeight >= 52);
            var texts = WpfTestApp.Descendants(row).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("PDF", texts);
            Assert.Contains(item.Name, texts);
            Assert.StartsWith("Today", texts.Last());
            var menu = row.ContextMenu!.Items.Cast<MenuItem>().Select(m => (string)m.Header).ToArray();
            Assert.Equal(new[] { "Open containing folder", "Remove from list" }, menu);
        }, recentFiles: 2);
    }

    private static DependencyObject VisualTreeHelperChild(ItemsControl items, int index)
    {
        var container = items.ItemContainerGenerator.ContainerFromIndex(index);
        return WpfTestApp.Descendants(container).OfType<Button>().First();
    }

    [Fact]
    public void Right_click_remove_takes_the_file_off_the_list_and_never_touches_the_file()
    {
        WithWindow((app, window, _) =>
        {
            var rows = Find<ItemsControl>(window, "HomeRecentRows");
            var path = RecentFiles.Items[0].Path;
            var row = (Button)VisualTreeHelperChild(rows, 0);
            Assert.Equal(path, row.Tag);
            var menu = row.ContextMenu!;
            menu.PlacementTarget = row;
            menu.IsOpen = true; // Opening it gives its items the row as their data, as a right click does.
            WpfTestApp.Pump();
            var remove = menu.Items.Cast<MenuItem>().Single(m => (string)m.Header == "Remove from list");
            Assert.IsType<RecentRow>(remove.DataContext);
            remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            WpfTestApp.Pump();
            Assert.DoesNotContain(RecentFiles.Items, i => i.Path == path);
            Assert.True(File.Exists(path));
            Assert.Equal(2, rows.Items.Count);
        }, recentFiles: 3);
    }

    [Fact]
    public void Open_containing_folder_selects_the_file_in_explorer()
    {
        Assert.Equal("/select,\"C:\\Example\\Documents\\my file.pdf\"", MainWindow.RevealArguments(@"C:\Example\Documents\my file.pdf"));
    }

    [Fact]
    public void Clicking_a_row_opens_that_file()
    {
        WithWindow((app, window, _) =>
        {
            var rows = Find<ItemsControl>(window, "HomeRecentRows");
            var source = app.CreatePdf();
            // A file in the list that is a real PDF.
            var documents = Directory.CreateDirectory(PrivateTempDirectory.Path + "-documents-" + Guid.NewGuid().ToString("N")).FullName;
            var path = Path.Combine(documents, "example-open.pdf");
            File.Copy(source, path);
            RecentFiles.Add(path);
            PumpUntil(() => rows.Items.Cast<RecentRow>().Any(r => r.Path == path));
            var row = rows.Items.Cast<RecentRow>().Select((r, i) => (r, i)).First(x => x.r.Path == path).i;
            ((Button)VisualTreeHelperChild(rows, row)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => app.Model.HasDocument);
            Assert.Equal(path, app.Model.DocumentPath);
            Directory.Delete(documents, true);
        }, recentFiles: 2);
    }

    // --- Privacy ----------------------------------------------------------------------------------------

    [Fact]
    public void The_privacy_window_offers_the_recent_option_on_by_default_and_names_everything_the_clear_button_clears()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var dialog = new PrivacyDialog();
            try
            {
                var box = (CheckBox)dialog.FindName("ShowRecentBox");
                Assert.True(box.IsChecked);
                Assert.Equal("Show recent files on the start page", box.Content);
                var clear = (Button)dialog.FindName("ClearRecentButton");
                var text = (string)clear.Content;
                Assert.Contains("recent files", text);
                Assert.Contains("reading history", text);
                var all = Logical(dialog).OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains(all, t => t.Contains("reading bookmarks") && t.Contains("stopped reading") && t.Contains("not touched"));
                Assert.Contains(all, t => t.Contains("only decides whether the list appears on the start page")
                    && t.Contains("still remembers recent files"));
            }
            finally { dialog.Close(); }
            return true;
        });
    }

    [Fact]
    public void The_privacy_checkbox_changes_the_setting_and_tells_every_window()
    {
        var saveSuspended = typeof(AppSettings).GetField("s_saveSuspended", BindingFlags.Static | BindingFlags.NonPublic)!;
        var wasSuspended = saveSuspended.GetValue(null);
        saveSuspended.SetValue(null, true);
        try
        {
            Sta.Run(() =>
            {
                using var app = new WpfTestApp("en");
                var harness = new AppTestHarness();
                var raised = 0;
                EventHandler count = (_, _) => raised++;
                var original = harness.Model.ShowRecentOnHome;
                try
                {
                    MainViewModel.ShowRecentOnHomeChanged += count;
                    harness.Model.ShowRecentOnHome = true;
                    var dialog = new PrivacyDialog(new MainViewModel.AboutModel(harness.Model));
                    var box = (CheckBox)dialog.FindName("ShowRecentBox");
                    Assert.True(box.IsChecked);
                    raised = 0;
                    box.IsChecked = false;
                    Assert.False(harness.Model.ShowRecentOnHome);
                    Assert.Equal(1, raised);
                    box.IsChecked = true;
                    Assert.True(harness.Model.ShowRecentOnHome);
                    Assert.Equal(2, raised);
                    dialog.Close();
                }
                finally
                {
                    MainViewModel.ShowRecentOnHomeChanged -= count;
                    harness.Model.ShowRecentOnHome = original;
                    WpfTestApp.Await(harness.DisposeAsync().AsTask());
                }
                return true;
            });
        }
        finally { saveSuspended.SetValue(null, wasSuspended); }
    }

    [Fact]
    public void The_setting_is_on_unless_an_older_settings_file_says_otherwise_and_is_saved_with_the_others()
    {
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.ShowRecentOnHome);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Language\":\"th\"}")!.ShowRecentOnHome);
        var off = new AppSettings { ShowRecentOnHome = false };
        var again = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(off))!;
        Assert.False(again.ShowRecentOnHome);
    }

    // --- Language texts -----------------------------------------------------------------------------------

    [Fact]
    public void Times_read_today_yesterday_or_as_a_date_in_both_languages()
    {
        var language = Loc.Language;
        try
        {
            var now = new DateTimeOffset(2026, 3, 10, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 10)));
            DateTimeOffset At(int month, int day, int hour, int minute, int year = 2026) =>
                new(year, month, day, hour, minute, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));
            Loc.Language = Loc.English;
            Assert.Equal("Today 10:42", RecentTimeText.Format(At(3, 10, 10, 42), now));
            Assert.Equal("Yesterday", RecentTimeText.Format(At(3, 9, 23, 59), now));
            Assert.Equal("5 Jan", RecentTimeText.Format(At(1, 5, 8, 0), now));
            Assert.Equal("3 Nov 2025", RecentTimeText.Format(At(11, 3, 8, 0, 2025), now));
            Assert.Equal("", RecentTimeText.Format(null, now));
            Loc.Language = Loc.Thai;
            Assert.Equal("วันนี้ 10:42", RecentTimeText.Format(At(3, 10, 10, 42), now));
            Assert.Equal("เมื่อวาน", RecentTimeText.Format(At(3, 9, 23, 59), now));
            Assert.Equal("5 ม.ค.", RecentTimeText.Format(At(1, 5, 8, 0), now));
            Assert.Equal("3 พ.ย. 2568", RecentTimeText.Format(At(11, 3, 8, 0, 2025), now));
        }
        finally { Loc.Language = language; }
    }

    [Fact]
    public void Every_new_start_page_text_has_a_Thai_translation()
    {
        var keys = Loc.ThaiKeys.ToHashSet();
        foreach (var text in new[]
        {
            "What would you like to do with your files today?", "Search tools, for example: merge, Word, sign", "Search tools",
            "Drop a file here", "PDF, DWG, DXF and AI", "View all", "Show fewer", "View all recent files", "Today {0}", "Yesterday",
            "Open containing folder", "Remove from list", "Show recent files on the start page", "Clear recent files and reading history",
        })
            Assert.Contains(text, keys);
    }
}
