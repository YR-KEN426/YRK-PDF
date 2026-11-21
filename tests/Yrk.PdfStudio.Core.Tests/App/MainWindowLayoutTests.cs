using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    [Theory]
    [InlineData(1280, "th")]
    [InlineData(1280, "en")]
    [InlineData(1100, "th")]
    [InlineData(1100, "en")]
    [InlineData(1000, "th")]
    [InlineData(1000, "en")]
    [InlineData(800, "th")]
    [InlineData(800, "en")]
    public void Document_bars_fit_all_modes_and_status_stays_below_document(int width, string language)
    {
        Sta.Run(() =>
        {
            using var application = new Yrk.PdfStudio.Testing.WpfTestApp(language);
            var harness = new AppTestHarness();
            var window = new MainWindow(harness.Model, () => throw new NotSupportedException())
                { Width = width, Height = 800, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                void Settle()
                {
                    for (var i = 0; i < 3; i++) Yrk.PdfStudio.Testing.WpfTestApp.Layout(window);
                }
                Settle();
                var status = (Border)window.FindName("DocumentStatusBar");
                var home = (ScrollViewer)window.FindName("HomePage");
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                Assert.Equal(0, status.ActualHeight);
                Assert.Equal(2, Grid.GetRow((UIElement)home.Parent));
                Assert.Equal(3, Grid.GetRow(status));
                AwaitBatch(harness.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf")));
                Settle();
                Assert.Equal(Visibility.Visible, status.Visibility);
                Assert.Equal(Visibility.Collapsed, home.Visibility);
                Assert.True(status.ActualHeight > 0);
                foreach (var name in new[] { "CommentTab", "FillSignTab", "EditTab", "MeasureTab", "RedactTab", "PagesTab" })
                {
                    ((RadioButton)window.FindName(name)).IsChecked = true;
                    Settle();
                    var top = (WrapPanel)window.FindName("TopButtons");
                    var modes = (WrapPanel)window.FindName("ModeButtons");
                    foreach (var panel in new[] { top, modes }.Concat(modes.Children.OfType<WrapPanel>()
                                 .Where(p => p.Visibility == Visibility.Visible)))
                    {
                        var rows = panel.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible)
                            .Select(c => Math.Round(LayoutInformation.GetLayoutSlot(c).Y, 1)).Distinct().Count();
                        Assert.True(rows >= 1, $"{width}/{language}/{name}: empty toolbar");
                        // ModeButtons holds the tab line and then the tool panel (by design); each tool panel itself must be one row.
                        if (width == 1280)
                            Assert.True(rows <= (panel == modes ? 2 : 1), $"{width}/{language}/{name}/{panel.Name}: {rows} rows, width {panel.ActualWidth}");
                        foreach (var child in panel.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible))
                            Assert.True(child.TranslatePoint(new Point(child.ActualWidth, 0), panel).X <= panel.ActualWidth + 1,
                                $"{width}/{language}/{name}: child exceeds toolbar width");
                    }
                }
                var input = (TextBox)window.FindName("PageInput");
                Assert.True(harness.Model.Pages.Count > 1);
                input.Text = "2";
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent };
                input.RaiseEvent(key);
                Assert.True(key.Handled);
                Assert.Equal(1, harness.Model.CurrentPageIndex);
                Assert.Same(status, FindAncestor<Border>(input, b => b == status));
                Assert.NotNull(window.FindName("ZoomBox"));
                Assert.Contains(window.InputBindings.Cast<InputBinding>(), b => b is KeyBinding
                    { Key: Key.G, Modifiers: ModifierKeys.Control });
                // Transform bindings inside page overlays have no governing element while the mode tabs switch; known benign noise.
                var errors = application.Errors.Where(e => !e.Contains("Cannot find governing") && !e.StartsWith("System.Windows.Data Error: 2")).ToList();
                Assert.True(errors.Count == 0, string.Join("|", errors));
                AwaitBatch(harness.Model.CloseCommand.ExecuteAsync(null));
                Settle();
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                Assert.Equal(Visibility.Visible, home.Visibility);
                return true;
            }
            finally { window.Close(); AwaitBatch(harness.DisposeAsync().AsTask()); }
        });
    }

    [Theory]
    [InlineData("th")]
    [InlineData("en")]
    public void Search_expands_retains_query_and_collapses_to_icon_when_narrow(string language)
    {
        Sta.Run(() =>
        {
            using var application = new Yrk.PdfStudio.Testing.WpfTestApp(language);
            var harness = new AppTestHarness();
            AwaitBatch(harness.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf")));
            var window = new MainWindow(harness.Model, () => throw new NotSupportedException())
                { Width = 1280, Height = 800, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                void Settle() { for (var i = 0; i < 3; i++) Yrk.PdfStudio.Testing.WpfTestApp.Layout(window); }
                Settle();
                var field = (Grid)window.FindName("SearchField");
                var editor = (TextBox)window.FindName("SearchBox");
                var controls = (StackPanel)window.FindName("SearchControls");
                var icon = (Button)window.FindName("SearchIconButton");
                Assert.Equal(220, field.Width);
                ApplicationCommands.Find.Execute(null, window);
                Settle();
                Assert.True(editor.IsKeyboardFocusWithin, $"focus lost; width {field.Width}; top {((FrameworkElement)window.FindName("TopBar")).ActualWidth}");
                // At 1280 the other buttons leave only ~250-300px; wider windows reach 400.
                Assert.InRange(field.Width, 240, 400);
                Assert.True(editor.IsKeyboardFocusWithin);
                var go = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.G && b.Modifiers == ModifierKeys.Control);
                go.Command.Execute(null);
                Settle();
                Assert.True(((TextBox)window.FindName("PageInput")).IsKeyboardFocusWithin);
                Assert.Equal(220, field.Width);
                editor.Focus();
                editor.Text = "sample";
                go.Command.Execute(null);
                Settle();
                Assert.True(field.Width > 220);
                editor.Clear();
                Settle();
                Assert.Equal(220, field.Width);
                window.Width = 800;
                Settle();
                Assert.Equal(Visibility.Visible, icon.Visibility);
                Assert.Equal(Visibility.Collapsed, controls.Visibility);
                icon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.Equal(Visibility.Visible, controls.Visibility);
                Assert.True(editor.IsKeyboardFocusWithin);
                go.Command.Execute(null);
                Settle();
                Assert.Equal(Visibility.Visible, icon.Visibility);
                ApplicationCommands.Find.Execute(null, window);
                Settle();
                Assert.Equal(Visibility.Visible, controls.Visibility);
                Assert.True(editor.IsKeyboardFocusWithin);
                Assert.Empty(application.Errors.Where(e => !e.Contains("Cannot find governing") && !e.StartsWith("System.Windows.Data Error: 2")));
                return true;
            }
            finally { window.Close(); AwaitBatch(harness.DisposeAsync().AsTask()); }
        });
    }

    /// <summary>Writes document-mode screenshots when YRK_SHOTS_DIR is set; otherwise does nothing.</summary>
    [Theory]
    [InlineData(1280, "th", "light")]
    [InlineData(1280, "th", "dark")]
    [InlineData(1280, "en", "light")]
    [InlineData(1280, "en", "dark")]
    [InlineData(1000, "th", "light")]
    [InlineData(1000, "th", "dark")]
    [InlineData(1000, "en", "light")]
    [InlineData(1000, "en", "dark")]
    public void Capture_main_window_shots(int width, string language, string theme)
    {
        var folder = Environment.GetEnvironmentVariable("YRK_SHOTS_DIR");
        if (string.IsNullOrEmpty(folder)) return;
        Sta.Run(() =>
        {
            using var application = new Yrk.PdfStudio.Testing.WpfTestApp(language, theme);
            var harness = new AppTestHarness();
            AwaitBatch(harness.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf")));
            var window = new MainWindow(harness.Model, () => throw new NotSupportedException())
                { Width = width, Height = 700, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                for (var i = 0; i < 4; i++) Yrk.PdfStudio.Testing.WpfTestApp.Layout(window);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, 700, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(folder);
                using var stream = File.Create(Path.Combine(folder, $"MainWindow-{width}-{language}-{theme}.png"));
                encoder.Save(stream);
                return true;
            }
            finally { window.Close(); AwaitBatch(harness.DisposeAsync().AsTask()); }
        });
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override bool IsDisposed => false;
    }

    private static T? FindAncestor<T>(DependencyObject child, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match && predicate(match)) return match;
        return null;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Multiple_files_skip_failures_reuse_tabs_and_finish_on_first_success(bool fromDialog, bool alreadyOpen)
    {
        Sta.Run(() =>
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ForgetApplicationFromEarlierTest();
            var application = new Application();
            var priorContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            application.Resources.MergedDictionaries.Add(ThemeTestResources.Create());
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            var models = new List<AppTestHarness>();
            AppTestHarness Create()
            {
                var app = new AppTestHarness();
                app.Dialogs.Answer("PromptPassword", (object?)null);
                models.Add(app);
                return app;
            }
            var first = Create();
            try
            {
                var a = first.CreatePdf();
                var b = first.PathFor("second.pdf");
                File.Copy(a, b);
                var bad = first.PathFor("bad.pdf");
                File.WriteAllText(bad, "Not a PDF");
                var locked = first.PathFor("locked.pdf");
                File.WriteAllBytes(locked, Yrk.PdfStudio.Core.Pdf.Security.PdfSecurity.Protect(
                    File.ReadAllBytes(a), new("secret", "owner", Yrk.PdfStudio.Core.Pdf.Security.PdfPermissions.All)));
                if (alreadyOpen) AwaitBatch(first.Model.OpenFileAsync(b));
                var window = new MainWindow(first.Model, () => Create().Model);
                var paths = new[] { bad, locked, a, a, b };
                if (fromDialog)
                {
                    first.Dialogs.Answer("PickPdfFiles", (object)paths);
                    AwaitBatch(first.Model.OpenCommand.ExecuteAsync(null));
                }
                else AwaitBatch(window.OpenManyInTabsAsync(paths));

                Assert.Equal(2, window.Tabs.Count);
                Assert.Equal(alreadyOpen ? new[] { b, a } : new[] { a, b }, window.Tabs.Select(t => t.DocumentPath));
                Assert.Equal(a, Assert.Single(window.Tabs.Where(t => t.IsActiveTab)).DocumentPath);
                if (!alreadyOpen) Assert.Same(first.Model, window.Tabs[0]);
                Assert.Single(models.SelectMany(m => m.Dialogs.Named("ShowError")));
                Assert.Single(models.SelectMany(m => m.Dialogs.Named("PromptPassword")));
                Assert.Empty(models.SelectMany(m => m.Dialogs.Named("Confirm")));

                // The same path always selects its existing tab, including single-file Open.
                var active = models.Single(m => m.Model.IsActiveTab);
                active.Dialogs.Answer("PickPdfFiles", (object)new[] { b });
                AwaitBatch(active.Model.OpenCommand.ExecuteAsync(null));
                Assert.Equal(2, window.Tabs.Count);
                Assert.Equal(b, Assert.Single(window.Tabs.Where(t => t.IsActiveTab)).DocumentPath);
                return true;
            }
            finally
            {
                foreach (var app in models) AwaitBatch(app.DisposeAsync().AsTask());
                application.Shutdown();
                ForgetApplicationFromEarlierTest();
                SynchronizationContext.SetSynchronizationContext(priorContext);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_more_than_twenty_files_requires_confirmation(bool accept)
    {
        Sta.Run(() =>
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ForgetApplicationFromEarlierTest();
            var application = new Application();
            var priorContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            application.Resources.MergedDictionaries.Add(ThemeTestResources.Create());
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            var app = new AppTestHarness();
            try
            {
                var path = app.CreatePdf();
                var window = new MainWindow(app.Model, () => throw new InvalidOperationException("Duplicate created a tab"));
                app.Dialogs.Answer("Confirm", accept);
                app.Dialogs.Answer("PickPdfFiles", (object)Enumerable.Repeat(path, 21).ToArray());
                AwaitBatch(app.Model.OpenCommand.ExecuteAsync(null));
                Assert.Single(app.Dialogs.Named("Confirm"));
                Assert.Equal(accept, app.Model.HasDocument);
                Assert.Single(window.Tabs);
                Assert.Equal(accept ? path : null, app.Model.DocumentPath);
                // Exactly 20 needs no confirmation.
                AwaitBatch(window.OpenManyInTabsAsync(Enumerable.Repeat(path, 20).ToArray()));
                Assert.Single(app.Dialogs.Named("Confirm"));
                Assert.Equal(path, app.Model.DocumentPath);
                return true;
            }
            finally
            {
                AwaitBatch(app.DisposeAsync().AsTask());
                application.Shutdown();
                ForgetApplicationFromEarlierTest();
                SynchronizationContext.SetSynchronizationContext(priorContext);
            }
        });
    }

    private static void AwaitBatch(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Page_clipboard_keeps_text_copy_and_localizes_the_cut_close_dialog()
    {
        Sta.Run(() =>
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            ForgetApplicationFromEarlierTest();
            var application = new Application();
            var priorContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            application.Resources.MergedDictionaries.Add(ThemeTestResources.Create());
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            var language = Yrk.PdfStudio.App.Localization.Loc.Language;
            var app = new AppTestHarness();
            var os = new AppPageClipboardTests.MemoryClipboard();
            using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
            app.Model.PageClipboard = clipboard;
            try
            {
                var opening = app.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"));
                Await(opening);
                Assert.True(opening.Result);
                var window = new MainWindow(app.Model, () => throw new NotSupportedException());
                window.Measure(new Size(1200, 800));
                window.Arrange(new Rect(0, 0, 1200, 800));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var editor = (TextBox)window.FindName("SearchBox");
                editor.Text = "ordinary selected text";
                editor.SelectAll();
                string? copied = null;
                DataObject.AddCopyingHandler(editor, (_, e) =>
                {
                    copied = e.DataObject.GetData(DataFormats.UnicodeText) as string;
                    e.CancelCommand(); // Observe the real TextBox copy without changing the machine's clipboard.
                });

                Assert.True(ApplicationCommands.Copy.CanExecute(null, editor));
                ApplicationCommands.Copy.Execute(null, editor);

                Assert.Equal("ordinary selected text", copied);
                Assert.Null(os.Data.Pages);
                Assert.False(app.Model.IsDirty);
                var thumbnails = (ListBox)window.FindName("ThumbnailList");
                Assert.True(ApplicationCommands.Copy.CanExecute(null, thumbnails));
                thumbnails.SelectAll();
                Assert.False(ApplicationCommands.Cut.CanExecute(null, thumbnails));
                _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();

                foreach (var lang in new[] { "en", "th" })
                {
                    Yrk.PdfStudio.App.Localization.Loc.Language = lang;
                    var dialog = (Yrk.PdfStudio.App.Views.MessageDialog)typeof(MainWindow).GetMethod("CreateCutPagesCloseDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, ["message"])!;
                    var buttons = Assert.IsType<WrapPanel>(dialog.FindName("Buttons"));
                    Assert.Equal((lang == "th" ? "ปิดต่อ" : "Continue closing") + " (_C)",
                        Assert.IsType<AccessText>(Assert.IsType<Button>(buttons.Children[0]).Content).Text);
                    var cancel = Assert.IsType<Button>(buttons.Children[1]);
                    Assert.True(cancel.IsCancel);
                    Assert.True(cancel.IsDefault);
                    Assert.Equal((lang == "th" ? "ยกเลิก" : "Cancel") + " (_A)", Assert.IsType<AccessText>(cancel.Content).Text);
                    Assert.Equal(MessageBoxResult.Cancel, dialog.Result);
                    dialog.Close();
                }
                return true;
            }
            finally
            {
                Yrk.PdfStudio.App.Localization.Loc.Language = language;
                Await(app.DisposeAsync().AsTask());
                application.Shutdown();
                ForgetApplicationFromEarlierTest();
                SynchronizationContext.SetSynchronizationContext(priorContext);
            }
            static void Await(Task task)
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
        });
    }

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
            application.Resources.MergedDictionaries.Add(ThemeTestResources.Create());
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
                Assert.Equal(.4, slider.Value);
                Assert.True(slider.IsMoveToPointEnabled);
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
