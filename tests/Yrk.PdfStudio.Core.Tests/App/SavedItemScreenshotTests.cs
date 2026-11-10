using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

// Also serializes the shared fixture tests with the other WPF Application tests.
[Collection("WPF Application")]
public sealed partial class SavedItemEditingTests
{
    // Discovery skips before constructing a harness, window or output directory.
    public sealed class ScreenshotFactAttribute : FactAttribute
    {
        public ScreenshotFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YRK_SHOT_DIR")))
                Skip = "Set YRK_SHOT_DIR to opt in to reviewer PNGs.";
        }
    }

    [ScreenshotFact]
    public void Saved_item_reviewer_screenshots()
    {
        var output = System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("YRK_SHOT_DIR")!);
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            const int width = 1440, height = 1000;
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ResetApplication();
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative),
            });
            var oldLanguage = Loc.Language;
            // MainWindow binds the home recent-file list: keep even reads away from personal data.
            var recents = typeof(RecentFiles).GetField("s_items", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldRecents = recents.GetValue(null);
            recents.SetValue(null, new ObservableCollection<RecentFile>());
            var dispatcherErrors = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler onError = (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
            Dispatcher.CurrentDispatcher.UnhandledException += onError;
            try
            {
                foreach (var language in new[] { Loc.Thai, Loc.English })
                foreach (var kind in new[] { "stamp", "unmarked", "legacy" })
                {
                    Loc.Language = language;
                    var app = new AppTestHarness();
                    MainWindow? window = null;
                    Popup? popup = null;
                    try
                    {
                        var path = CreateSource(app, kind, language);
                        window = new MainWindow(app.Model, () => throw new NotSupportedException())
                        {
                            Width = width, Height = height,
                            WindowStartupLocation = WindowStartupLocation.Manual,
                            Left = 0, Top = 0,
                        };
                        window.Show();
                        Assert.True(Await(app.OpenAsync(path)));
                        app.Model.FitPageCommand.Execute(null);
                        ((RadioButton)window.FindName("EditTab")).IsChecked = true;
                        Assert.Equal(AnnotationTool.EditText, app.Model.CurrentTool);
                        Settle(window);
                        Await(Select(app));
                        var frame = Frame(app);
                        Settle(window);
                        PumpUntil(() => app.Model.Pages[0].Image is not null);
                        Settle(window);

                        var overlay = Descendants<Grid>(window).Single(g => ReferenceEquals(g.DataContext, frame)
                            && g.Children.OfType<Rectangle>().Any(r => r.StrokeDashArray.Count > 0));
                        var selection = overlay.Children.OfType<Rectangle>().Single(r => r.StrokeDashArray.Count > 0);
                        Assert.True(selection.IsVisible);
                        var frameRect = VisibleRect(selection, window);
                        var pageImage = Descendants<Image>(window).Single(i => ReferenceEquals(i.Source, app.Model.Pages[0].Image));
                        var pageRect = VisibleRect(pageImage, window);
                        Assert.True(pageRect.Contains(frameRect));
                        Assert.True(pageRect.Width > 400 && pageRect.Height > 400);
                        Assert.True(frameRect.Width > 90 && frameRect.Height > 40);
                        Assert.Single(Descendants<Button>(overlay), b => b.IsVisible && b.Command == app.Model.DeleteSelectedCommand);
                        var opacity = Descendants<ToggleButton>(overlay).Single();
                        Assert.Equal(kind != "unmarked", opacity.IsVisible);
                        Assert.Equal(kind != "unmarked", overlay.Children.OfType<Thumb>().Single(t => t.Width == 10).IsVisible);
                        Assert.False(overlay.Children.OfType<Thumb>().Single(t => t.Width == 7).IsVisible);
                        Assert.Equal(kind == "legacy", frame.SavedItem!.OpacityMode == PdfItemOpacityMode.FadeOnly);
                        Assert.Equal(kind != "unmarked", frame.SavedItem.HasYrkMark);
                        Translate(window);
                        Assert.Equal(Loc.T("Edit"), ((RadioButton)window.FindName("EditTab")).Content);

                        // Popup is a separate HWND. Render it separately and composite at its actual
                        // window-relative position; RTB.Render(window) alone omits the legacy note.
                        if (kind == "legacy")
                        {
                            opacity.IsChecked = true;
                            popup = Descendants<Popup>(overlay).Single();
                            Settle(window);
                            var child = (FrameworkElement)popup.Child;
                            Translate(child);
                            child.UpdateLayout();
                            Assert.True(popup.IsOpen);
                            Assert.Contains(Descendants<TextBlock>(child), t => t.IsVisible && t.Text == frame.OpacityNote);
                            Assert.Equal(Loc.T("This item has its opacity saved already; it can only be made fainter."), frame.OpacityNote);
                        }

                        // WPF's Window size includes non-client chrome that RTB cannot paint.
                        var client = (FrameworkElement)window.Content;
                        var pixelWidth = (int)Math.Ceiling(client.ActualWidth);
                        var pixelHeight = (int)Math.Ceiling(client.ActualHeight);
                        Assert.True(pixelWidth >= 1280 && pixelHeight >= 800);
                        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        if (popup?.Child is FrameworkElement content)
                        {
                            var topLeft = window.PointFromScreen(content.PointToScreen(new Point()));
                            var rect = new Rect(topLeft, content.RenderSize);
                            Assert.True(new Rect(0, 0, pixelWidth, pixelHeight).Contains(rect));
                            var layer = new DrawingVisual();
                            using (var dc = layer.RenderOpen()) dc.DrawRectangle(new VisualBrush(content), null, rect);
                            bitmap.Render(layer);
                        }
                        VerifyPixels(bitmap, pageRect, frameRect, selection.Stroke);
                        Assert.Empty(dispatcherErrors);
                        Assert.Empty(app.Dialogs.Named("ShowError"));
                        Directory.CreateDirectory(output);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(System.IO.Path.Combine(output, $"shot-{kind}-{language}.png"));
                        encoder.Save(stream);
                    }
                    finally
                    {
                        if (popup is not null) popup.IsOpen = false;
                        window?.Close();
                        Await(app.DisposeAsync().AsTask());
                    }
                }
                return true;
            }
            finally
            {
                Loc.Language = oldLanguage;
                recents.SetValue(null, oldRecents);
                Dispatcher.CurrentDispatcher.UnhandledException -= onError;
                application.Shutdown();
                ResetApplication();
            }
        });
    }

    private static string CreateSource(AppTestHarness app, string kind, string language)
    {
        var path = app.PathFor(kind + ".pdf");
        if (kind == "legacy") File.WriteAllBytes(path, Await(LegacyImageAsync()));
        else if (kind == "unmarked")
        {
            var document = Await(app.Engine.OpenBytesAsync(Blank(), app.PathFor("blank.pdf")));
            try
            {
                Await(document.AddImageAsync(0, Red(), new(.25, .3, .35, .2)));
                Await(document.SaveAsync(path));
            }
            finally { Await(document.DisposeAsync().AsTask()); }
        }
        else
        {
            var blank = app.PathFor("blank.pdf"); File.WriteAllBytes(blank, Blank());
            Assert.True(Await(app.OpenAsync(blank)));
            var label = language == Loc.Thai ? "สำเนาถูกต้อง" : "CERTIFIED TRUE COPY";
            var art = StampRenderer.RenderCertifiedCopy(new CertifiedCopyOptions(label, false, null));
            var item = PlacedItemViewModel.CreateImage(0, PlacedItemKind.Stamp, 400, 400, art.Image, stamp: art);
            item.Bounds = new(.2, .3, .45, .22);
            Assert.True(item.HasStampText);
            Attach(app, item);
            Assert.True(Await(app.SaveToAsync(path)));
            Assert.Contains(label, Await(Document(app).GetPageTextAsync(0)));
        }
        return path;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static Rect VisibleRect(FrameworkElement element, Window window)
    {
        Assert.True(element.IsVisible);
        var rect = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        Assert.True(new Rect(window.RenderSize).Contains(rect), $"Element outside screenshot: {rect}");
        return rect;
    }

    private static void VerifyPixels(BitmapSource image, Rect page, Rect frame, Brush stroke)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight]; image.CopyPixels(pixels, stride, 0);
        var colors = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 4) colors.Add(BitConverter.ToInt32(pixels, i));
        Assert.True(colors.Count > 100, "MainWindow render is empty or nearly uniform.");
        var accent = Assert.IsType<SolidColorBrush>(stroke).Color;
        int White = 0, border = 0;
        for (var y = (int)page.Top; y < (int)page.Bottom; y++)
        for (var x = (int)page.Left; x < (int)page.Right; x++)
        {
            var i = y * stride + x * 4;
            if (pixels[i] > 245 && pixels[i + 1] > 245 && pixels[i + 2] > 245) White++;
            var onBorder = Math.Abs(x - frame.Left) < 3 || Math.Abs(x - frame.Right) < 3
                || Math.Abs(y - frame.Top) < 3 || Math.Abs(y - frame.Bottom) < 3;
            if (onBorder && x >= frame.Left - 3 && x <= frame.Right + 3 && y >= frame.Top - 3 && y <= frame.Bottom + 3
                && Math.Abs(pixels[i] - accent.B) < 65 && Math.Abs(pixels[i + 1] - accent.G) < 65
                && Math.Abs(pixels[i + 2] - accent.R) < 65) border++;
        }
        Assert.True(White > page.Width * page.Height / 2, "Document page is absent from rendered pixels.");
        Assert.True(border > 30, $"Selected dashed frame is absent from rendered pixels ({border}).");
    }

    private static void Translate(DependencyObject root) => typeof(UiTranslator)
        .GetMethod("TranslateNow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root]);

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static T Await<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "Timed out waiting for real app rendering.");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(5);
        }
    }

    private static void ResetApplication() => typeof(MainWindowLayoutTests)
        .GetMethod("ForgetApplicationFromEarlierTest", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
}
