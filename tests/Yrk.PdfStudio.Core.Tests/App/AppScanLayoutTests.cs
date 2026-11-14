using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
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
                    PageCount = state is "adjust" or "card" or "crop" ? 1 : 3,
                    PageStyle = state == "adjust" ? FakeScanPageStyle.GrayFaintText
                        : state is "card" or "crop" ? FakeScanPageStyle.CardOnWhiteGlass : FakeScanPageStyle.Default,
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
                    if (state == "scope-all") model.SetAdjustmentScope(ScanAdjustmentScope.AllPages);
                    if (state == "adjust")
                        Await(model.ApplyAdjustmentsAsync(new(Brightness: 15, Sharpness: 25, WhiteBackground: true, Straighten: false, Contrast: 20), false));
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
                    if (state == "crop")
                    {
                        model.Current!.Page.Crop = null;
                        Await(window.EnterCropAsync());
                        var draft = (ScanCropFrame)typeof(ScanDialog).GetField("_cropDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                        Assert.NotNull(draft.Detected);
                        Assert.Equal(draft.Detected, draft.Box);
                        Assert.Equal(8, ((Canvas)window.FindName("CropHandles")).Children.Count);
                        Assert.True(((FrameworkElement)window.FindName("CropPanel")).IsVisible);
                    }
                    if (state == "zoom")
                    {
                        window.ZoomTo(2, new Point(300, 250));
                        Await((Task)typeof(ScanDialog).GetMethod("RefreshPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
                        Assert.Equal("200%", ((TextBlock)window.FindName("ZoomText")).Text);
                        Assert.NotNull(((Image)window.FindName("RegionImage")).Source);
                    }
                    typeof(UiTranslator).GetMethod("TranslateNow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window]);
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var list = (ListBox)window.FindName("PageList");
                    Assert.Equal(state == "empty" ? 0 : state is "adjust" or "card" or "crop" ? 1 : 3, list.Items.Count);
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
                    Assert.True((state == "crop" ? Visibility.Visible : Visibility.Collapsed) == ((FrameworkElement)window.FindName("CropFrame")).Visibility, $"{language}-{state}");
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("CancelCardButton")).Visibility);
                    var scopeRow = (WrapPanel)window.FindName("ScopeRow");
                    var scopeChip = Assert.Single(scopeRow.Children.OfType<RadioButton>(), chip => chip.IsChecked == true);
                    Assert.Equal(state == "scope-all" ? "AllPages" : state == "multi" ? "Selected" : "ThisPage", scopeChip.Tag);
                    Assert.All(scopeRow.Children.OfType<RadioButton>(), chip =>
                    {
                        Assert.True(chip.Focusable);
                        Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(chip)));
                    });
                    Assert.Equal(state == "multi", ((RadioButton)window.FindName("SelectedScope")).IsEnabled);
                    if (state == "scope-all")
                    {
                        // Exercise the panel's routed ValueChanged path and gesture handlers.
                        var brightness = (Slider)window.FindName("Brightness");
                        var start = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                            0, System.Windows.Input.MouseButton.Left);
                        typeof(ScanDialog).GetMethod("OnAdjustmentStart", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, [brightness, start]);
                        brightness.Value = 8;
                        brightness.Value = 16;
                        brightness.Value = 24;
                        typeof(ScanDialog).GetMethod("OnAdjustmentEnd", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, [brightness, new RoutedEventArgs()]);
                        Assert.All(model.Pages, page => Assert.Equal(24, page.Page.Brightness));
                        Await(model.UndoAsync());
                        Assert.All(model.Pages, page => Assert.Equal(0, page.Page.Brightness));
                        Assert.False(model.CanUndo);
                        Await(model.RedoAsync());
                        Assert.All(model.Pages, page => Assert.Equal(24, page.Page.Brightness));
                        typeof(ScanDialog).GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                        Await((Task)typeof(ScanDialog).GetMethod("RefreshPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
                        Assert.Equal(Loc.T("All pages"), scopeChip.Content);
                        Assert.Equal(Loc.F("Selected ({0})", 1), ((RadioButton)window.FindName("SelectedScope")).Content);
                    }
                    if (state == "adjust")
                    {
                        Assert.StartsWith(language == Loc.English ? "1 page ·" : "1 หน้า ·", model.SizeSummary);
                        Assert.Equal(new(Brightness: 15, Sharpness: 25, WhiteBackground: true, Straighten: false, Contrast: 20), model.Current!.Page.Adjustments);
                        Assert.Equal(15, ((Slider)window.FindName("Brightness")).Value);
                        Assert.Equal(20, ((Slider)window.FindName("Contrast")).Value);
                        Assert.Equal(25, ((Slider)window.FindName("Sharpness")).Value);
                        var whiteBackground = (CheckBox)window.FindName("WhiteBackground");
                        Assert.True(whiteBackground.IsVisible && whiteBackground.IsEnabled);
                        Assert.True(whiteBackground.IsChecked);
                    }
                    if (state == "card")
                    {
                        var selected = Assert.Single(((WrapPanel)window.FindName("PresetRow")).Children.OfType<RadioButton>(), chip => chip.IsChecked == true);
                        Assert.Equal("TwoSidedCard", selected.Tag);
                        Assert.True(selected.Focusable);
                        Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(selected)));
                        Assert.True(((Button)window.FindName("CreateButton")).IsEnabled);
                        var preview = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                        Assert.InRange((double)preview.PixelWidth / preview.PixelHeight, 0.70, 0.72);
                    }
                    if (state == "pages")
                    {
                        var closing = typeof(ScanDialog).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
                        var cancelClose = new System.ComponentModel.CancelEventArgs();
                        window.CloseChoice = () => Yrk.PdfStudio.App.Services.SaveChangesChoice.Cancel;
                        closing.Invoke(window, [window, cancelClose]);
                        Assert.True(cancelClose.Cancel);
                        Assert.Null(window.Request);
                        var discardClose = new System.ComponentModel.CancelEventArgs();
                        window.CloseChoice = () => Yrk.PdfStudio.App.Services.SaveChangesChoice.Discard;
                        closing.Invoke(window, [window, discardClose]);
                        Assert.False(discardClose.Cancel);
                        model.MarkSaved();
                        window.CloseChoice = () => throw new InvalidOperationException("Saved pages must not prompt");
                        var savedClose = new System.ComponentModel.CancelEventArgs();
                        closing.Invoke(window, [window, savedClose]);
                        Assert.False(savedClose.Cancel);
                        window.CloseChoice = () => Yrk.PdfStudio.App.Services.SaveChangesChoice.Discard;
                        Assert.StartsWith(language == Loc.English ? "3 pages ·" : "3 หน้า ·", model.SizeSummary);
                        var region = new Yrk.PdfStudio.Core.Pdf.NormalizedRect(0.1, 0.1, 0.8, 0.8);
                        model.Settings = model.Settings with { Region = region };
                        var prompts = 0;
                        window.Confirm = _ => { prompts++; return false; };
                        typeof(ScanDialog).GetMethod("OnClearArea", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, [window, new RoutedEventArgs()]);
                        Assert.Equal(1, prompts);
                        Assert.Equal(region, model.Settings.Region);
                        Assert.Equal(3, model.Pages.Count);
                        model.Settings = model.Settings with { Region = null };

                        prompts = 0;
                        window.OrientationData = new(Path.Combine(session.DirectoryPath, "missing-osd"));
                        var orientation = (CheckBox)window.FindName("AutoOrientation");
                        Assert.False(orientation.IsChecked);
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            orientation.IsChecked = true;
                            PumpUntil(() => !(bool)typeof(ScanDialog).GetField("_working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                            Assert.False(orientation.IsChecked);
                            Assert.False(model.AutoOrientationEnabled);
                        }
                        Assert.Equal(1, prompts);
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
                    using var output = File.Create(Path.Combine(folder, $"{(customDirectory || state is "scope-all" or "crop" ? "6b" : "scan")}-{state}-{language}.png"));
                    encoder.Save(output);
                    if (state == "crop") VerifyCropEditing(window, model, language);
                    if (state == "empty")
                    {
                        Assert.Equal(string.Empty, ((TextBlock)window.FindName("ZoomText")).Text);
                        window.ZoomTo(8, new(100, 100));
                        Assert.Equal(string.Empty, ((TextBlock)window.FindName("ZoomText")).Text);
                        var oldLogFolder = Yrk.PdfStudio.App.App.LogFolder;
                        try
                        {
                            Yrk.PdfStudio.App.App.LogFolder = session.DirectoryPath;
                            foreach (var kind in Enum.GetValues<ScannerErrorKind>())
                            {
                                var error = new ScannerException(kind, "Raw driver error 0x80210001", hresult: unchecked((int)0x80210001));
                                var message = ScanDialog.ErrorText(error);
                                Assert.False(string.IsNullOrWhiteSpace(message)); Assert.DoesNotContain("0x", message);
                                Assert.DoesNotContain("Raw driver", message);
                                if (language == Loc.Thai) Assert.Matches("[ก-๙]", message);
                                window.ShowError(error);
                                Assert.Equal(message, ((TextBlock)window.FindName("Message")).Text);
                                Assert.Contains("0x80210001", ((TextBlock)window.FindName("ErrorCode")).Text);
                                Assert.Equal(Visibility.Visible, ((Expander)window.FindName("ErrorDetails")).Visibility);
                                Assert.False(((Expander)window.FindName("ErrorDetails")).IsExpanded);
                            }
                            Assert.Contains("0x80210001", File.ReadAllText(Directory.GetFiles(session.DirectoryPath, "errors-*.log").Single()));
                        }
                        finally { Yrk.PdfStudio.App.App.LogFolder = oldLogFolder; }
                        session.Add(ScanSessionTests.Page(300, 400));
                        Await(model.LoadPagesAsync());
                        Await((Task)typeof(ScanDialog).GetMethod("RefreshPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
                        var view = (ScanViewTransform)typeof(ScanDialog).GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                        Assert.True(view.IsFit);
                        Assert.NotEqual(string.Empty, ((TextBlock)window.FindName("ZoomText")).Text);
                        typeof(ScanDialog).GetMethod("OnAutoCrop", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, [window, new RoutedEventArgs()]);
                        PumpUntil(() => !(bool)typeof(ScanDialog).GetField("_working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                        Assert.Equal(Loc.T("No edges to crop were found"), ((TextBlock)window.FindName("Message")).Text);
                    }
                }
                finally { window.CloseChoice = () => Yrk.PdfStudio.App.Services.SaveChangesChoice.Discard; window.Close(); }
            }
        }
        finally { Loc.Language = oldLanguage; application.Shutdown(); ResetApplication(); }
        return true;
    });

    private static readonly string[] Languages = [Loc.Thai, Loc.English];
    private static readonly string[] States = ["empty", "pages", "multi", "adjust", "card", "scope-all", "zoom", "crop"];
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void VerifyCropEditing(ScanDialog window, ScanViewModel model, string language)
    {
        object? Invoke(string name, params object?[] args) => typeof(ScanDialog)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, args);
        ScanCropFrame Draft() => (ScanCropFrame)typeof(ScanDialog).GetField("_cropDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Key(Key key, string handler = "OnKeyDown")
        {
            Keyboard.ClearFocus();
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (handler == "OnKeyDown") window.RaiseEvent(e); else Invoke(handler, window, e);
            Assert.True(e.Handled);
        }
        void Number(string field, double pixels, double dpi)
        {
            ((TextBox)window.FindName(field)).Text = ScanCropFrame.Format(pixels, dpi, System.Globalization.RegionInfo.CurrentRegion.IsMetric);
            Key(System.Windows.Input.Key.Enter, "OnCropNumberKey");
        }
        var entry = model.Current!;
        var previous = new Yrk.PdfStudio.Core.Pdf.NormalizedRect(.1, .1, .8, .8);
        entry.Page.Crop = previous;
        Await(window.EnterCropAsync());
        Assert.Equal(previous, Draft().Normalized);
        VerifyPan(window);
        Number("CropLeft", 100, entry.Page.DpiX);
        Assert.Equal(100, Draft().Box.X, 3);
        Assert.Equal(previous, entry.Page.Crop); // Editing is a draft, with no undo records.
        ((TextBox)window.FindName("CropWidth")).Text = "invalid";
        Invoke("OnCropNumberCommit", window, new RoutedEventArgs());
        Assert.NotEqual("invalid", ((TextBox)window.FindName("CropWidth")).Text);
        var beforeArrow = Draft().Box.X;
        Key(System.Windows.Input.Key.Right);
        Assert.Equal(beforeArrow + 1, Draft().Box.X, 5);
        Assert.Same(entry, model.Current); // Global page navigation must not receive the arrow.
        Key(System.Windows.Input.Key.Escape);
        Assert.Equal(previous, entry.Page.Crop);
        Assert.False(model.CanUndo);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("CropPanel")).Visibility);
        Await((Task)Invoke("RefreshPreviewAsync")!);
        Await(window.EnterCropAsync());
        Number("CropLeft", 120, entry.Page.DpiX);
        var applied = Draft().Normalized;
        Key(System.Windows.Input.Key.Enter);
        PumpUntil(() => !(bool)typeof(ScanDialog).GetField("_working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
        Assert.Equal(applied, entry.Page.Crop);
        Assert.True(model.CanUndo);
        Await(model.UndoAsync()); Assert.Equal(previous, entry.Page.Crop); Assert.False(model.CanUndo);

        // Glass acquisition is unchanged; its region shares the frame and restores on Esc.
        Await(model.PreviewGlassAsync(FakeScannerService.Device));
        Await((Task)Invoke("RefreshPreviewAsync")!);
        Assert.Equal(Loc.T("Area for the next scan"), ((TextBlock)window.FindName("CropPurpose")).Text);
        Assert.Equal(language == Loc.Thai ? "พื้นที่สำหรับการสแกนครั้งถัดไป" : "Area for the next scan",
            ((TextBlock)window.FindName("CropPurpose")).Text);
        Assert.Null(model.Settings.Region);
        VerifyPan(window);
        Number("CropLeft", 50, model.GlassPreview!.Page.DpiX);
        Assert.NotNull(model.Settings.Region);
        Key(System.Windows.Input.Key.Escape); Assert.Null(model.Settings.Region);
        Await((Task)Invoke("RefreshPreviewAsync")!);
        model.ClearGlassPreview();
        model.ApplyPreset(ScanPreset.TwoSidedCard);
        model.Settings = model.Settings with { Dpi = 100 };
        Await(model.ScanCardAsync(FakeScannerService.Device));
        Await((Task)Invoke("RefreshPreviewAsync")!);
        var front = model.TemporaryPreview!;
        Number("CropLeft", 60, front.Page.DpiX);
        var frontCrop = Draft().Normalized;
        Await((Task)Invoke("ApplyCropAsync", false)!);
        Assert.Equal(frontCrop, front.Page.Crop);
        Assert.False(model.CanUndo);
        model.CancelCard();
        // The same editor must initialize from ink on white glass, including its physical margin.
        model.ApplyPreset(ScanPreset.ColorDocument);
        model.Settings = model.Settings with { AutoStraighten = false };
        var white = new ScanPageEntry(model.Session.Add(ScanGlassRegressionTests.WhiteGlass()));
        model.Pages.Add(white); model.Select(model.Pages.Count - 1);
        Await(window.EnterCropAsync());
        Assert.Null(Draft().Detected); Assert.NotNull(Draft().Content);
        Assert.Equal(150 - 3 * 100 / 25.4, Draft().Box.X, 5);
        Assert.Equal(250 - 3 * 100 / 25.4, Draft().Box.Y, 5);
        var blank = new ScanPageEntry(model.Session.Add(ScanGlassRegressionTests.WhiteGlass(false)));
        model.Pages.Add(blank); model.Select(model.Pages.Count - 1);
        Await(window.EnterCropAsync());
        Assert.Null(Draft().Content); Assert.Null(Draft().Detected);
        Assert.Equal(Loc.T("No edges to crop were found"), ((TextBlock)window.FindName("Message")).Text);
    }
    private static void VerifyPan(ScanDialog window)
    {
        var view = (ScanViewTransform)typeof(ScanDialog).GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var viewport = (Canvas)window.FindName("PreviewScroll");
        window.ZoomTo(2, new(200, 200));
        Await((Task)typeof(ScanDialog).GetMethod("RefreshPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
        window.UpdateLayout();
        Assert.True(((FrameworkElement)window.FindName("HorizontalPan")).IsVisible);
        Assert.True(((FrameworkElement)window.FindName("VerticalPan")).IsVisible);
        foreach (var middle in new[] { true, false })
        {
            Keyboard.ClearFocus();
            if (!middle)
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Space)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            viewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, middle ? MouseButton.Middle : MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });
            Assert.True((bool)typeof(ScanDialog).GetField("_panning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            Assert.Same(Cursors.Hand, viewport.Cursor);
            var start = (Point)typeof(ScanDialog).GetField("_panStart", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var before = view.Offset;
            window.ContinuePan(start + new Vector(-50, -60));
            Assert.True(view.Offset.X < before.X); Assert.True(view.Offset.Y < before.Y);
            viewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, middle ? MouseButton.Middle : MouseButton.Left)
            { RoutedEvent = Mouse.MouseUpEvent });
            if (!middle)
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Space)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        }
        var bar = (System.Windows.Controls.Primitives.ScrollBar)window.FindName("HorizontalPan");
        bar.Value = 100; Assert.Equal(-100, view.Offset.X, 5);
        // A handle kept near the viewport edge continues scrolling, even with no new mouse move.
        typeof(ScanDialog).GetField("_drawingCrop", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        var old = view.Offset;
        window.AutoScrollCrop(new(view.View.Width - 1, view.View.Height - 1));
        Assert.True(view.Offset.X < old.X); Assert.True(view.Offset.Y < old.Y);
        typeof(ScanDialog).GetField("_drawingCrop", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
        typeof(ScanDialog).GetMethod("OnFit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
        Await((Task)typeof(ScanDialog).GetMethod("RefreshPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
    }
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
