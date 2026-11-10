using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>
/// Builds the real main window in memory (never shown) and evaluates its bindings, so a XAML mistake such as a
/// TwoWay binding to a read-only property fails here instead of stopping the window from opening.
/// </summary>
[Collection("WPF Application")] // Only one System.Windows.Application can exist at a time; shared with ETaxTests.
public sealed class MainWindowLayoutTests
{
    [Fact]
    public void Main_window_lays_out_without_binding_errors_and_shows_download_progress_banner()
    {
        Sta.Run(() =>
        {
            // MainWindow.xaml uses root-relative pack URIs (/Assets/app.ico) that resolve against the resource
            // assembly, which is the test host here, so point it at the app assembly.
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ForgetApplicationFromEarlierTest();
            var application = new Application();
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative),
            });
            var dispatcherErrors = new List<Exception>();
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
            AppTestHarness? harness = null;
            var modelType = typeof(MainViewModel);
            var setState = modelType.GetMethod("SetUpdateState", BindingFlags.NonPublic | BindingFlags.Static)!;
            var none = Enum.Parse(modelType.GetNestedType("UpdateState", BindingFlags.NonPublic)!, "None");
            try
            {
                harness = new AppTestHarness();
                var window = new MainWindow(harness.Model, () => throw new NotSupportedException("Tabs are not created in this test."));

                // Bindings activate from the dispatcher queue, so run it until idle; each pass is followed by a layout.
                void Settle()
                {
                    window.Measure(new Size(1200, 800));
                    window.Arrange(new Rect(0, 0, 1200, 800));
                    window.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    window.UpdateLayout();
                    Assert.True(dispatcherErrors.Count == 0, string.Join(Environment.NewLine, dispatcherErrors));
                }

                Settle();
                // Proof that bindings really were evaluated: the hidden update banner follows the view model.
                var banner = (Border)window.FindName("UpdateBanner");
                Assert.Equal(Visibility.Collapsed, banner.Visibility);

                // The banner while a download is running.
                var progress = new UpdateDownloadProgress(30_000_000);
                progress.Report(42, DateTimeOffset.UtcNow);
                SetStatic("s_downloadProgress", progress);
                SetStatic("s_downloadingVersion", "9.9.9-test");
                SetStatic("s_downloadSize", 30_000_000L);
                setState.Invoke(null, [Enum.Parse(modelType.GetNestedType("UpdateState", BindingFlags.NonPublic)!, "Downloading")]);
                Settle();

                Assert.Equal(Visibility.Visible, banner.Visibility);
                var bar = FindVisualChild<ProgressBar>(banner);
                Assert.NotNull(bar);
                Assert.Equal(42, bar!.Value);
                Assert.Equal(Visibility.Visible, bar.Visibility);

                // Materialize the overlay template too: an empty main window never evaluates
                // bindings on the rotation controls or on the opacity slider inside its popup.
                var image = System.Windows.Media.Imaging.BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32,
                    null, new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, 8);
                image.Freeze();
                var item = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Picture, 400, 300, image);
                item.Bounds = item.ToBounds(100, 100, 100, 50);
                item.Rotation = 30;
                item.Opacity = 0.4;
                harness.Model.SelectItem(item);
                var presenter = new ContentPresenter
                {
                    Content = item,
                    ContentTemplate = (DataTemplate)window.Resources["PlacedItemTemplate"],
                };
                var canvas = new Canvas();
                canvas.Children.Add(presenter);
                window.Content = canvas;
                presenter.ApplyTemplate();
                presenter.Measure(new Size(400, 300));
                presenter.Arrange(new Rect(0, 0, 400, 300));
                Settle();
                var overlay = FindVisualChild<Grid>(presenter)!;
                Assert.Equal(new Point(0.5, 0.5), overlay.RenderTransformOrigin);
                Assert.Equal(30, Assert.IsType<RotateTransform>(overlay.RenderTransform).Angle);
                Assert.Equal(0.4, Assert.IsType<Grid>(VisualTreeHelper.GetParent(FindVisualChild<Image>(overlay)!)).Opacity);
                var toggle = FindVisualChild<ToggleButton>(overlay)!;
                Assert.Equal(Visibility.Visible, toggle.Visibility);
                var popup = FindVisualChild<Popup>(overlay)!;
                var slider = FindVisualChild<Slider>(popup.Child)!;
                popup.Child.Measure(new Size(200, 100));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.Equal(0.1, slider.Minimum);
                Assert.Equal(1, slider.Maximum);
                Assert.Equal(0.4, slider.Value);
                Assert.Equal(0.4, item.Opacity); // Activating the slider must not change the model.
                FindVisualChild<Button>(overlay)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(120, item.Rotation);
                harness.Model.UndoCommand.Execute(null);
                Assert.Equal(30, item.Rotation);

                var pdfItem = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Signature, 400, 300, image,
                    SignatureArt.FromPdf([], new Yrk.PdfStudio.Core.Pdf.NormalizedRect(0, 0, 1, 1), image));
                harness.Model.SelectItem(pdfItem);
                presenter.Content = pdfItem;
                presenter.Measure(new Size(400, 300));
                Settle();
                Assert.Equal(Visibility.Collapsed, FindVisualChild<ToggleButton>(presenter)!.Visibility);

                var saved = PlacedItemViewModel.CreateSaved(new(new(Guid.NewGuid(), 0, 0, 0),
                    new(.1, .1, .25, .2), true, YrkMarkKind.Image, Guid.NewGuid().ToString("N"), .4, PdfItemOpacityMode.FadeOnly), 400, 300);
                harness.Model.SelectItem(saved);
                presenter.Content = saved;
                presenter.Measure(new Size(400, 300));
                Settle();
                Assert.Equal(Visibility.Visible, FindVisualChild<ToggleButton>(presenter)!.Visibility);
                popup = FindVisualChild<Popup>(presenter)!;
                popup.Child.Measure(new Size(250, 150));
                Settle();
                slider = FindVisualChild<Slider>(popup.Child)!;
                Assert.Equal(1, slider.Value);
                Assert.Equal(1, slider.Maximum);
                var note = Assert.IsType<StackPanel>(Assert.IsType<Border>(popup.Child).Child).Children.OfType<TextBlock>()
                    .Single(t => t.Text == saved.OpacityNote);
                Assert.False(string.IsNullOrWhiteSpace(note.Text));
                Assert.False(saved.ShowResizeHandle);
                Assert.True(saved.ShowRotationHandle);

                var unmarked = PlacedItemViewModel.CreateSaved(saved.SavedItem! with
                    { HasYrkMark = false, Group = null, OpacityMode = PdfItemOpacityMode.DeleteOnly }, 400, 300);
                harness.Model.SelectItem(unmarked);
                presenter.Content = unmarked;
                presenter.Measure(new Size(400, 300));
                Settle();
                Assert.Equal(Visibility.Collapsed, FindVisualChild<ToggleButton>(presenter)!.Visibility);
                Assert.Equal(Visibility.Collapsed, FindVisualChild<Button>(presenter)!.Visibility); // 90° button.
                Assert.False(unmarked.ShowRotationHandle);
                Assert.True(unmarked.IsSelected); // The existing dashed frame and Delete button remain.
                return true;
            }
            finally
            {
                setState.Invoke(null, [none]);
                SetStatic("s_downloadProgress", null);
                SetStatic("s_downloadingVersion", null);
                SetStatic("s_downloadSize", 0L);
                if (harness is not null) harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
                application.Shutdown();
                ForgetApplicationFromEarlierTest();
            }

            static void SetStatic(string name, object? value) =>
                typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
        });
    }

    // Shutdown() without Run() leaves Application.Current set, and a second Application on another thread then throws.
    private static void ForgetApplicationFromEarlierTest()
    {
        // Also drop the cached resource manager, which remembers the first resource assembly it was asked about.
        var container = typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer")!;
        container.GetField("s_applicationResourceManagerWrapper", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        typeof(Application).GetField("_isShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        foreach (var name in new[] { "_appInstance", "_appCreatedInThisAppDomain" })
            typeof(Application).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, name == "_appInstance" ? null : false);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
