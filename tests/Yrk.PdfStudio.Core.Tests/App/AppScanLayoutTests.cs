using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class AppScanLayoutTests
{
    [Fact]
    public void Thai_and_English_layouts_render_empty_pages_and_multiple_selection() => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        ResetApplication();
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(ScanDialog).Assembly);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
        var oldLanguage = Loc.Language;
        try
        {
            foreach (var language in Languages)
            foreach (var state in States)
            {
                Loc.Language = language;
                using var session = new ScanSession();
                if (state != "empty")
                {
                    var scanner = new FakeScannerService { PageCount = 3 };
                    Await(scanner.ScanAsync(FakeScannerService.Device, new(Dpi: 100),
                        new ScanSessionTests.Sink<ScannedPage>(page => session.Add(page))));
                }
                var window = new ScanDialog(session, new FakeScannerService())
                { ShowActivated = false, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual };
                try
                {
                    window.Show();
                    PumpUntil(() => !(bool)typeof(ScanDialog).GetField("_working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                    var model = (ScanViewModel)typeof(ScanDialog).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    if (state == "multi") model.Select(2, shift: true);
                    Await((Task)typeof(ScanDialog).GetMethod("RefreshPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
                    typeof(UiTranslator).GetMethod("TranslateNow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window]);
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var list = (ListBox)window.FindName("PageList");
                    Assert.Equal(state == "empty" ? 0 : state == "multi" ? 3 : 1, list.SelectedItems.Count);
                    var hint = (TextBlock)window.FindName("EmptyHint");
                    Assert.Equal(state == "empty", hint.IsVisible);
                    Assert.Equal(Loc.T("Place a document on the glass, then press Scan"), hint.Text);
                    var image = (Image)window.FindName("PageImage");
                    Assert.Equal(state != "empty", image.Source is not null);
                    Assert.True(((FrameworkElement)window.FindName("PreviewScroll")).ActualWidth > 450);
                    var client = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)client.ActualWidth, (int)client.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                    {
                        drawing.DrawRectangle(Brushes.White, null, new Rect(client.RenderSize));
                        drawing.DrawRectangle(new VisualBrush(client), null, new Rect(client.RenderSize));
                    }
                    bitmap.Render(visual);
                    var folder = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "scan-screenshots")).FullName;
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(folder, $"scan-{state}-{language}.png"));
                    encoder.Save(output);
                }
                finally { window.Close(); }
            }
        }
        finally { Loc.Language = oldLanguage; application.Shutdown(); ResetApplication(); }
        return true;
    });

    private static readonly string[] Languages = [Loc.Thai, Loc.English];
    private static readonly string[] States = ["empty", "pages", "multi"];
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < limit, "Scan layout timed out.");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
    }
    private static void ResetApplication() => typeof(MainWindowLayoutTests)
        .GetMethod("ForgetApplicationFromEarlierTest", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
}
