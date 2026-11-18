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
                Assert.NotEmpty(cards.Children.Cast<UIElement>());
                var card = cards.Children.OfType<Button>().First();
                var content = (DockPanel)card.Content;
                var icon = content.Children.OfType<Border>().Single();
                var description = content.Children.OfType<TextBlock>().Last();
                var title = content.Children.OfType<TextBlock>().First();
                var divider = WpfTestApp.Descendants(window).OfType<Border>()
                    .First(b => ReferenceEquals(b.Background, Application.Current.Resources["DividerBrush"]));
                var original = new[] { ColorOf(window.Background), ColorOf(card.Foreground), ColorOf(icon.Background), ColorOf(description.Foreground) };

                var surface = resources.Replace("SurfaceBrush", 30, 10, 60);
                var text = resources.Replace("TextBrush", 240, 30, 170);
                var accent = resources.Replace("AccentBrush", 0, 240, 30);
                var line = resources.Replace("DividerBrush", 240, 220, 0);
                var hover = resources.Replace("HoverBrush", 50, 120, 220);
                var secondary = resources.Replace("SecondaryTextBrush", 0, 210, 210);
                var category = resources.Replace("MeasureAccentBrush", 220, 20, 240);
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
            }
            finally { window.Close(); WpfTestApp.Pump(); }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Theory]
    [InlineData("ScanDialog", false)]
    [InlineData("PrintDialog", false)]
    [InlineData("StampDialog", true)]
    public void Open_native_popups_follow_replaced_resources(string name, bool calendar)
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
                var calendarBackground = nativeCalendar?.Background;
                var calendarBorder = calendarBackground is null ? null : WpfTestApp.Descendants(child).OfType<Border>()
                    .First(b => ReferenceEquals(b.Background, calendarBackground));

                resources.Replace("SurfaceBrush", 30, 10, 60);
                resources.Replace("TextBrush", 240, 30, 170);
                // Step 2b retains the native ComboBox/Calendar templates. Their existing dynamic
                // system keys are also theme resources; step 3 will replace those templates.
                resources.Replace(SystemColors.WindowTextBrushKey, 0, 240, 30);
                resources.Replace(SystemColors.ControlTextBrushKey, 0, 240, 30);
                resources.Replace(SystemColors.WindowBrushKey, 60, 20, 100);
                var calendarBrush = resources.Replace("CalendarBackgroundBrush", 80, 10, 140);
                WpfTestApp.Pump(); child.UpdateLayout();
                Assert.True(popup.IsOpen);
                if (calendar)
                {
                    Assert.Same(calendarBrush, nativeCalendar!.Background);
                    Assert.Same(calendarBrush, calendarBorder!.Background);
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
    public void Calendar_palette_preserves_native_gradient_exactly_and_can_be_replaced()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            using var resources = new ResourceScope();
            var native = new Calendar { Style = null };
            var themed = new Calendar { Style = (Style)Application.Current.Resources["CalendarPaint"] };
            var host = new Window { Content = new StackPanel { Children = { native, themed } } };
            try
            {
                Show(host);
                var original = Assert.IsType<LinearGradientBrush>(native.Background);
                var palette = Assert.IsType<LinearGradientBrush>(themed.Background);
                Assert.Equal(original.StartPoint, palette.StartPoint);
                Assert.Equal(original.EndPoint, palette.EndPoint);
                Assert.Equal(original.GradientStops.Select(s => (s.Offset, s.Color)),
                    palette.GradientStops.Select(s => (s.Offset, s.Color)));
                // Replace the complete palette brush; the stops are its fixed internal construction.
                var replacement = resources.Replace("CalendarBackgroundBrush", 210, 10, 140);
                WpfTestApp.Pump();
                Assert.Same(replacement, themed.Background);
                Assert.Same(original, native.Background);
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
