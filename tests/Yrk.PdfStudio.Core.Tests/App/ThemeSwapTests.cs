using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Controls;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Testing;
using Path = System.Windows.Shapes.Path;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class ThemeSwapTests
{
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Open_window_follows_whole_palette_swaps_at_the_same_position(string initialTheme)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en", initialTheme);
            using var fixture = new WindowFixture();
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var fixedPalette = dictionaries[0];
            var styles = dictionaries[2];
            var window = WindowCatalog.Cases.Single(c => c.Name == "MainWindow").Create!(fixture);
            try
            {
                Show(window);
                var cards = (Panel)window.FindName("HomeCards");
                var card = WpfTestApp.Descendants(cards).OfType<Button>().First();
                foreach (var palette in new[] { "Dark", "Light", "Dark", "HighContrast", "Light" })
                {
                    var replacement = ThemeTestResources.Load(palette);
                    dictionaries[1] = replacement;
                    WpfTestApp.Layout(window);
                    Assert.Same(fixedPalette, dictionaries[0]);
                    Assert.Same(styles, dictionaries[2]);
                    Assert.Equal(3, dictionaries.Count);
                    Assert.Same(replacement["SurfaceBrush"], window.Background);
                    Assert.Same(replacement["HomeTextBrush"], card.Foreground);
                    Assert.Same(replacement["HomeBackgroundBrush"], ((ScrollViewer)window.FindName("HomePage")).Background);
                    Assert.Same(fixedPalette["PaperBrush"], Application.Current.Resources["PaperBrush"]);
                    var swatches = WpfTestApp.Descendants(window).OfType<Button>().Where(b => b.DataContext is Color).ToArray();
                    Assert.NotEmpty(swatches);
                    Assert.All(swatches, b => Assert.Equal((Color)b.DataContext, ColorOf(b.Background)));
                }
            }
            finally { window.Close(); WpfTestApp.Pump(); }
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }

    [Fact]
    public void Open_home_and_generated_visuals_follow_replaced_brushes()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            using var fixture = new WindowFixture();
            var window = WindowCatalog.Cases.Single(c => c.Name == "MainWindow").Create!(fixture);
            using var resources = new ResourceScope();
            try
            {
                Show(window);
                var cards = (Panel)window.FindName("HomeCards");
                var card = WpfTestApp.Descendants(cards).OfType<Button>().First();
                var content = (Grid)card.Content;
                var icon = content.Children.OfType<Border>().Single();
                var texts = content.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().ToArray();
                var description = texts[1];
                var title = texts[0];
                var divider = WpfTestApp.Descendants(window).OfType<Border>()
                    .First(b => ReferenceEquals(b.Background, Application.Current.Resources["DividerBrush"]));
                var original = new[] { ColorOf(window.Background), ColorOf(card.Foreground), ColorOf(icon.Background), ColorOf(description.Foreground) };

                var surface = resources.Replace("SurfaceBrush", 30, 10, 60);
                var text = resources.Replace("HomeTextBrush", 240, 30, 170);
                var accent = resources.Replace("AccentBrush", 0, 240, 30);
                var line = resources.Replace("DividerBrush", 240, 220, 0);
                var hover = resources.Replace("HoverBrush", 50, 120, 220);
                var secondary = resources.Replace("HomeMutedTextBrush", 0, 210, 210);
                var category = resources.Replace("OrganizeSoftBrush", 220, 20, 240);
                var brand = resources.Replace("BrandBrush", 250, 120, 10);
                var homeBackground = resources.Replace("HomeBackgroundBrush", 10, 120, 80);
                WpfTestApp.Layout(window);
                Assert.Same(surface, window.Background);
                Assert.Same(surface, card.Background);
                Assert.Same(surface, ((Border)card.Template.FindName("Card", card)).Background);
                Assert.Same(text, card.Foreground);
                Assert.Same(text, title.Foreground);
                Assert.Same(secondary, description.Foreground);
                Assert.Same(category, icon.Background);
                Assert.Same(line, divider.Background);
                Assert.NotEqual(original[0], ColorOf(window.Background));
                Assert.NotEqual(original[1], ColorOf(card.Foreground));
                Assert.NotEqual(original[2], ColorOf(icon.Background));
                Assert.NotEqual(original[3], ColorOf(description.Foreground));

                // Exercise the real HomeCard template trigger without depending on the OS mouse position.
                Assert.Contains(card.Template.Triggers.OfType<Trigger>(), t => t.Property == UIElement.IsMouseOverProperty);
                var primary = WpfTestApp.Descendants(window).OfType<Button>()
                    .First(b => ReferenceEquals(b.Background, accent));
                Assert.Same(accent, primary.Background);
                Assert.NotNull(hover);
                // The start page follows replaced brushes too: its background, and the one brand red button.
                Assert.Same(homeBackground, ((ScrollViewer)window.FindName("HomePage")).Background);
                var open = WpfTestApp.Descendants(window).OfType<Button>().First(b => ReferenceEquals(b.Background, brand));
                Assert.Same(brand, open.Background);
            }
            finally { window.Close(); WpfTestApp.Pump(); }
            Assert.True(app.Errors.Count == 0, string.Join(Environment.NewLine, app.Errors));
            return true;
        });
    }

    [Theory]
    [InlineData("ScanDialog", false)]
    [InlineData("PrintDialog", false)]
    [InlineData("StampDialog", true)]
    public void Open_styled_popups_follow_replaced_resources(string name, bool calendar)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            using var fixture = new WindowFixture();
            using var resources = new ResourceScope();
            var window = WindowCatalog.Cases.Single(c => c.Name == name).Create!(fixture);
            Control? control = null;
            try
            {
                Show(window);
                control = calendar ? WpfTestApp.Descendants(window).OfType<DatePicker>().First()
                    : WpfTestApp.Descendants(window).OfType<ComboBox>().First(c => c.Items.Count > 0);
                control.ApplyTemplate();
                if (control is ComboBox combo) combo.IsDropDownOpen = true;
                else ((DatePicker)control).IsDropDownOpen = true;
                WpfTestApp.Pump();
                var popup = Assert.IsType<Popup>(control.Template.FindName("PART_Popup", control));
                Assert.True(popup.IsOpen);
                var child = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                child.Measure(new Size(1000, 800)); child.Arrange(new Rect(child.DesiredSize)); child.UpdateLayout();
                var labels = WpfTestApp.Descendants(child).OfType<TextBlock>().ToArray();
                Assert.NotEmpty(labels);
                var before = labels.Select(t => ColorOf(t.Foreground)).ToArray();
                var nativeCalendar = WpfTestApp.Descendants(child).OfType<Calendar>().SingleOrDefault();
                var chrome = Assert.IsType<Border>(calendar
                    ? nativeCalendar!.Template.FindName("Chrome", nativeCalendar)
                    : control.Template.FindName("PopupChrome", control));

                var surface = resources.Replace("SurfaceBrush", 30, 10, 60);
                var foreground = resources.Replace("TextBrush", 240, 30, 170);
                var border = resources.Replace("BorderStrongBrush", 60, 20, 100);
                var focus = resources.Replace("FocusBrush", 20, 220, 100);
                var accent = resources.Replace("AccentBrush", 80, 10, 140);
                var onAccent = resources.Replace("OnAccentTextBrush", 240, 250, 210);
                WpfTestApp.Pump(); child.UpdateLayout();
                Assert.True(popup.IsOpen);
                Assert.Same(surface, chrome.Background);
                Assert.Same(nativeCalendar?.IsKeyboardFocusWithin == true ? focus : border, chrome.BorderBrush);
                Assert.Same(foreground, control.Foreground);
                if (calendar)
                {
                    Assert.Same(surface, nativeCalendar!.Background);
                    var selectedDay = WpfTestApp.Descendants(nativeCalendar).OfType<CalendarDayButton>().Single(b => b.IsSelected);
                    Assert.Same(accent, ((Border)selectedDay.Template.FindName("Chrome", selectedDay)).Background);
                    Assert.Same(onAccent, selectedDay.Foreground);
                }
                else Assert.Contains(labels.Select((t, i) => ColorOf(t.Foreground) != before[i]), changed => changed);
            }
            finally
            {
                if (control is ComboBox combo) combo.IsDropDownOpen = false;
                if (control is DatePicker date) date.IsDropDownOpen = false;
                window.Close(); WpfTestApp.Pump();
            }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Fact]
    public void Tabs_color_rings_and_focus_frames_follow_replaced_resources()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            using var resources = new ResourceScope();
            var tab = new TabItem { Header = "Draw" };
            var ink = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            var ring = new RadioButton { Style = (Style)Application.Current.Resources["ColorRing"], Background = ink, IsChecked = true };
            var combo = new ComboBox();
            var date = new DatePicker();
            var host = new Window { Content = new StackPanel { Children = { new TabControl { Items = { tab } }, ring, combo, date } } };
            try
            {
                Show(host);
                var accent = resources.Replace("AccentBrush", 210, 10, 140);
                var focus = resources.Replace("FocusBrush", 10, 210, 140);
                var text = resources.Replace("TextBrush", 140, 210, 10);
                WpfTestApp.Pump();
                Assert.Same(accent, ((Border)tab.Template.FindName("Underline", tab)).Background);
                Assert.Same(text, tab.Foreground);
                Assert.Same(accent, ((Ellipse)ring.Template.FindName("Ring", ring)).Stroke);
                Assert.Same(ink, ring.Background); // Content ink must not follow a screen theme.
                Assert.Same(focus, ((Ellipse)ring.Template.FindName("Focus", ring)).Stroke);
                foreach (var control in new Control[] { tab, combo, date })
                    Assert.Same(focus, ((Border)control.Template.FindName("Focus", control)).BorderBrush);
            }
            finally { host.Close(); WpfTestApp.Pump(); }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Fact]
    public void Ruler_and_preview_references_survive_theme_changes_and_document_paint_transitions()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            using var resources = new ResourceScope();
            var ruler = new Ruler();
            var path = new Path();
            var host = new Window { Content = new StackPanel { Children = { ruler, path } } };
            Show(host);
            var documentFill = new SolidColorBrush(Color.FromRgb(20, 30, 40));
            path.Style = new Style(typeof(Path)) { Setters = { new Setter(Shape.FillProperty, documentFill) } };
            ThemeResources.SetFillKey(path, "SelectionOverlayBrush");
            var fill = resources.Replace("SelectionOverlayBrush", 240, 0, 160);
            var foreground = resources.Replace("RulerTextBrush", 20, 200, 40);
            WpfTestApp.Pump();
            Assert.Same(fill, path.Fill);
            Assert.Same(foreground, ruler.Foreground);
            ThemeResources.SetFillKey(path, null);
            Assert.Same(documentFill, path.Fill);
            ThemeResources.SetFillKey(path, "SelectionOverlayBrush");
            var next = resources.Replace("SelectionOverlayBrush", 0, 240, 160);
            WpfTestApp.Pump();
            Assert.Same(next, path.Fill);
            host.Close();
            Assert.Empty(app.Errors);
            return true;
        });
    }

    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static void Show(Window window)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -32000;
        window.Show(); WpfTestApp.Layout(window);
    }

    private sealed class ResourceScope(ResourceDictionary? dictionary = null) : IDisposable
    {
        private readonly ResourceDictionary _resources = dictionary ?? Application.Current.Resources;
        private readonly Dictionary<object, (bool Local, object? Value)> _originals = [];
        public SolidColorBrush Replace(object key, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            ReplaceValue(key, brush);
            return brush;
        }
        public void ReplaceValue(object key, object value)
        {
            if (!_originals.ContainsKey(key)) _originals[key] = (_resources.Keys.Cast<object>().Contains(key), _resources[key]);
            _resources[key] = value;
        }
        public void Dispose()
        {
            foreach (var (key, original) in _originals)
                if (original.Local) _resources[key] = original.Value;
                else _resources.Remove(key);
            WpfTestApp.Pump();
        }
    }
}
