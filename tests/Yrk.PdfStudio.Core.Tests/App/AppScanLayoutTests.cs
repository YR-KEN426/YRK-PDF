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

[CollectionDefinition("WPF Application", DisableParallelization = true)]
public sealed class ScanWpfApplicationGroup;

[Collection("WPF Application")]
public sealed class AppScanLayoutTests
{
    [Fact]
    public void Thai_and_English_layouts_render_empty_pages_multiple_selection_adjustments_and_card() => Sta.Run(() =>
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
                var scanner = new FakeScannerService
                {
                    PageCount = state is "adjust" or "card" ? 1 : 3,
                    PageStyle = state == "adjust" ? FakeScanPageStyle.GrayFaintText
                        : state == "card" ? FakeScanPageStyle.CardOnWhiteGlass : FakeScanPageStyle.Default,
                };
                if (state is not ("empty" or "card"))
                {
                    Await(scanner.ScanAsync(FakeScannerService.Device, new(Dpi: 100),
                        new ScanSessionTests.Sink<ScannedPage>(page => session.Add(page))));
                }
                var window = new ScanDialog(session, scanner)
                { ShowActivated = false, Left = -16000, Top = -16000, WindowStartupLocation = WindowStartupLocation.Manual };
                try
                {
                    window.Show();
                    PumpUntil(() => !(bool)typeof(ScanDialog).GetField("_working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                    var model = (ScanViewModel)typeof(ScanDialog).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    if (state == "multi") model.Select(2, shift: true);
                    if (state == "adjust")
                        Await(model.ApplyAdjustmentsAsync(new(Brightness: 15, Sharpness: 25, WhiteBackground: true, Straighten: false), false));
                    if (state == "card")
                    {
                        model.ApplyPreset(ScanPreset.TwoSidedCard);
                        model.Settings = model.Settings with { Dpi = 100 };
                        typeof(ScanDialog).GetMethod("ShowPreset", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                        Await(model.ScanCardAsync(FakeScannerService.Device));
                        Assert.NotNull(model.CardFront);
                        Assert.NotNull(model.CardFront.Page.Crop);
                        Assert.Null(model.CardBack);
                        Assert.Empty(model.Pages);
                        Await(model.ScanCardAsync(FakeScannerService.Device));
                        Assert.NotNull(model.CardBack);
                        Assert.NotNull(model.CardBack.Page.Crop);
                        Assert.Empty(model.Pages);
                        Await(model.ScanCardAsync(FakeScannerService.Device));
                        var card = Assert.Single(model.Pages);
                        var composed = session.Read(card.Page).Bitmap;
                        Assert.Equal((int)Math.Round(210 / 25.4 * 100), composed.Width);
                        Assert.Equal((int)Math.Round(297 / 25.4 * 100), composed.Height);
                        Assert.Null(model.CardFront);
                        Assert.Null(model.CardBack);
                        Assert.Null(model.TemporaryPreview);
                    }
                    typeof(ScanDialog).GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                    Await((Task)typeof(ScanDialog).GetMethod("RefreshPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
                    typeof(UiTranslator).GetMethod("TranslateNow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window]);
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var list = (ListBox)window.FindName("PageList");
                    Assert.Equal(state == "empty" ? 0 : state is "adjust" or "card" ? 1 : 3, list.Items.Count);
                    Assert.Equal(state == "empty" ? 0 : state == "multi" ? 3 : 1, list.SelectedItems.Count);
                    var hint = (TextBlock)window.FindName("EmptyHint");
                    Assert.Equal(state == "empty", hint.IsVisible);
                    Assert.Equal(Loc.T("Place a document on the glass, then press Scan"), hint.Text);
                    var image = (Image)window.FindName("PageImage");
                    Assert.Equal(state != "empty", image.Source is not null);
                    if (state != "empty")
                    {
                        Assert.True(image.IsVisible);
                        Assert.True(image.ActualWidth > 0 && image.ActualHeight > 0);
                        Assert.All(model.Pages, page => Assert.NotNull(page.Thumbnail));
                    }
                    var adjustments = (FrameworkElement)window.FindName("AdjustmentPanel");
                    Assert.True(adjustments.IsVisible);
                    Assert.Equal(state != "empty", adjustments.IsEnabled);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("CropFrame")).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("CancelCardButton")).Visibility);
                    if (state == "adjust")
                    {
                        Assert.Equal(new(Brightness: 15, Sharpness: 25, WhiteBackground: true, Straighten: false), model.Current!.Page.Adjustments);
                        Assert.Equal(15, ((Slider)window.FindName("Brightness")).Value);
                        Assert.Equal(25, ((Slider)window.FindName("Sharpness")).Value);
                        var whiteBackground = (CheckBox)window.FindName("WhiteBackground");
                        Assert.True(whiteBackground.IsVisible && whiteBackground.IsEnabled);
                        Assert.True(whiteBackground.IsChecked);
                    }
                    if (state == "card")
                    {
                        Assert.Equal(Loc.T("Two-sided card"), ((TextBlock)window.FindName("PresetLabel")).Text);
                        Assert.True(((Button)window.FindName("CreateButton")).IsEnabled);
                        var preview = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                        Assert.InRange((double)preview.PixelWidth / preview.PixelHeight, 0.70, 0.72);
                    }
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
                    var shotDirectory = Environment.GetEnvironmentVariable("YRK_SCAN_SHOT_DIR");
                    var customDirectory = !string.IsNullOrWhiteSpace(shotDirectory);
                    var folder = Directory.CreateDirectory(customDirectory ? shotDirectory! : Path.Combine(AppContext.BaseDirectory, "scan-screenshots")).FullName;
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(folder, $"{(customDirectory ? "6b" : "scan")}-{state}-{language}.png"));
                    encoder.Save(output);
                    if (state == "empty")
                    {
                        session.Add(ScanSessionTests.Page(300, 400));
                        Await(model.LoadPagesAsync());
                        typeof(ScanDialog).GetMethod("OnAutoCrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, [window, new RoutedEventArgs()]);
                        PumpUntil(() => !(bool)typeof(ScanDialog).GetField("_working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                        Assert.Equal(Loc.T("No edges to crop were found"), ((TextBlock)window.FindName("Message")).Text);
                    }
                }
                finally { window.Close(); }
            }
        }
        finally { Loc.Language = oldLanguage; application.Shutdown(); ResetApplication(); }
        return true;
    });

    private static readonly string[] Languages = [Loc.Thai, Loc.English];
    private static readonly string[] States = ["empty", "pages", "multi", "adjust", "card"];
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
