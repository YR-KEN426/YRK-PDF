using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Services.Backup;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

[CollectionDefinition("Import restart", DisableParallelization = true)]
public sealed class ImportRestartCollection;

[Collection("Import restart")]
public sealed class ImportRestartTests
{
    private const string Password = "import-test-password";
    private const string Notice = "Data imported. Setting changes will not be saved until you restart YRK PDF.";
    private static readonly Type[] Stores = [typeof(AppSettings), typeof(StampSettings), typeof(ETaxSettings), typeof(SignatureStore)];

    private sealed class Protection : IBackupSignatureProtection
    {
        public byte[] Protect(byte[] data) => data.ToArray();
        public byte[] Unprotect(byte[] data) => data.ToArray();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("th")]
    public void Unsupported_text_error_uses_the_selected_language(string language) => RunUi(root =>
    {
        Loc.Language = language;
        var app = new AppTestHarness();
        try
        {
            Assert.True(Await(app.OpenAsync(TextEditAlignmentTests.CreateSource(app, unsupported: true))));
            var document = (IPdfDocument)typeof(MainViewModel).GetField("_document", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app.Model)!;
            var bounds = Await(document.FindTextAsync(0, "ทนาย", PdfSearchOptions.None))[0].Rects[0];
            Await((Task)typeof(MainViewModel).GetMethod("BeginTextEditAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(app.Model, [app.Model.Pages[0], new NormalizedPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)])!);
            app.Model.EndTextEdit(app.Model.ActiveTextEdit!, "replacement", cancel: false, apply: true);
            PumpUntil(() => !app.Model.IsBusy);
            var error = Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]!.ToString()!;
            var expected = Loc.F("The text on page {0} cannot be edited: it uses a font without exact measurements, or it overlaps an image. Use a white box and Add text instead.", "1");
            Assert.Contains(expected, error);
            Assert.Equal(language == "th", expected.Contains("แก้ไขข้อความ"));
        }
        finally { Await(app.DisposeAsync().AsTask()); }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("th")]
    public void Item_tooltip_delete_name_and_import_option_are_translated(string language) => RunUi(root =>
    {
        Loc.Language = language;
        var app = new AppTestHarness();
        var window = new MainWindow(app.Model, () => throw new NotSupportedException());
        var dialog = new ImportDialog(new(), Path.Combine(root, "data"), Path.Combine(root, "temp"));
        try
        {
            var template = (DataTemplate)window.Resources["PlacedItemTemplate"];
            foreach (var kind in new[] { PlacedItemKind.Text, PlacedItemKind.Picture, PlacedItemKind.Stamp })
            {
                var item = kind == PlacedItemKind.Text
                    ? PlacedItemViewModel.CreateText(0, 400, 300, TextStyle.Default)
                    : PlacedItemViewModel.CreateImage(0, kind, 400, 300, System.Windows.Media.Imaging.BitmapSource.Create(
                        1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4));
                var grid = (Grid)template.LoadContent();
                grid.DataContext = item;
                grid.Measure(new Size(400, 300));
                grid.Arrange(new Rect(0, 0, 400, 300));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var thumb = grid.Children.OfType<Thumb>().First();
                var tip = Assert.IsType<StackPanel>(thumb.ToolTip);
                // ToolTipService supplies this inherited context when the popup opens.
                tip.DataContext = thumb.DataContext;
                tip.Measure(new Size(400, 100));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Translate(tip);
                Assert.Equal(Loc.T(kind == PlacedItemKind.Text ? "Drag to move. Double-click text to edit." : "Drag to move"),
                    Assert.Single(tip.Children.OfType<TextBlock>(), t => t.Visibility == Visibility.Visible).Text);
                Translate(grid);
                var delete = grid.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Last();
                Assert.Equal(Loc.T("Remove (Delete)"), AutomationProperties.GetName(delete));
            }
            Translate(dialog);
            Assert.Equal(Loc.T("Replace existing settings (including stamps)"), ((CheckBox)dialog.FindName("ReplaceBox")).Content);
        }
        finally { dialog.Close(); window.Close(); app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        static void Translate(DependencyObject root) => typeof(UiTranslator).GetMethod("TranslateNow", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [root]);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("th")]
    public void Wrong_password_is_reported_inline_and_inspection_can_be_retried(string language) => RunUi(root =>
    {
        Loc.Language = language;
        var source = Path.Combine(root, "source");
        Put(source, "settings.json", "{}");
        var archive = Path.Combine(root, "test.yrkbackup");
        new BackupWriter().Write(archive, source, Password, "test");
        var data = Path.Combine(root, "data");
        var dialog = new ImportDialog(new BackupImportCoordinator(), data, Path.Combine(root, "temp"));
        try
        {
            ((TextBox)dialog.FindName("FileInput")).Text = archive;
            ((PasswordBox)dialog.FindName("PasswordInput")).Password = "wrong-password";
            Inspect();
            Assert.Equal(Loc.T("The backup could not be read. The password is incorrect or the file is damaged."), ((TextBlock)dialog.FindName("Status")).Text);
            Assert.False(((Button)dialog.FindName("ImportButton")).IsEnabled);
            Assert.True(((Button)dialog.FindName("CloseButton")).IsEnabled);
            Assert.True(((StackPanel)dialog.FindName("Inputs")).IsEnabled);
            Assert.Empty(((ListBox)dialog.FindName("Contents")).Items);
            Assert.False(Directory.Exists(data));
            ((PasswordBox)dialog.FindName("PasswordInput")).Password = Password;
            Inspect();
            Assert.True(((Button)dialog.FindName("ImportButton")).IsEnabled);
            Assert.Equal(Loc.T("Contents checked. You can now import this backup."), ((TextBlock)dialog.FindName("Status")).Text);
            Assert.False(Directory.Exists(data));
        }
        finally { dialog.Close(); }

        void Inspect()
        {
            typeof(ImportDialog).GetMethod("OnInspect", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [dialog, new RoutedEventArgs()]);
            PumpUntil(() => !(bool)typeof(ImportDialog).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!);
        }
    });

    [Theory]
    [InlineData("settings")]
    [InlineData("stamps")]
    [InlineData("etax")]
    [InlineData("signatures")]
    public void Successful_import_prevents_stale_store_writes_until_restart(string store) => RunUi(root =>
    {
        var data = Path.Combine(root, "data");
        var path = Path.Combine(data, store == "settings" ? "settings.json" : store + ".json");
        var settings = new AppSettings { Language = "en" };
        var stamps = new StampSettings();
        var etax = new ETaxSettings { LastNumber = "OLD" };
        var signatures = new SignatureStore(Path.Combine(data, "Signatures"), new Protection());
        var art = SignatureArt.Deserialize(BackupTests.SignaturePng());
        var oldSignature = signatures.Save(art);
        var dialog = Import(root);
        var expected = File.ReadAllBytes(Path.Combine(data, store == "signatures" ? "settings.json" : Path.GetFileName(path)));
        switch (store)
        {
            case "settings": settings.Save(path); new AppSettings().Save(path); break;
            case "stamps": stamps.Save(path); new StampSettings().Save(path); break;
            case "etax": etax.Save(path); new ETaxSettings().Save(path); break;
            default:
                var before = Directory.GetFiles(Path.Combine(data, "Signatures")).Order().ToArray();
                signatures.Save(art);
                signatures.Delete(oldSignature.Id);
                // A second tab/new instance must obey the same process lifetime flag.
                new SignatureStore(Path.Combine(data, "Signatures"), new Protection()).Save(art);
                Assert.Equal(before, Directory.GetFiles(Path.Combine(data, "Signatures")).Order());
                break;
        }
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(data, store == "signatures" ? "settings.json" : Path.GetFileName(path))));
        Assert.Equal(Loc.T(Notice), ((TextBlock)dialog.FindName("Status")).Text);
        Assert.Equal(Visibility.Visible, ((Button)dialog.FindName("RestartButton")).Visibility);
        dialog.Close();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_failure_mentions_safety_backup_only_when_one_was_verified(bool existingData) => RunUi(root =>
    {
        var data = Path.Combine(root, "data");
        if (existingData) Put(data, "settings.json", "{\"old\":true}");
        var coordinator = new BackupImportCoordinator(import: (_, _, _, _, _) => throw new IOException("private path"));
        var dialog = Import(root, coordinator, expectSuccess: false);
        var text = ((TextBlock)dialog.FindName("Status")).Text;
        Assert.Equal(existingData, text.Contains("Backups", StringComparison.Ordinal));
        Assert.DoesNotContain("private path", text);
        Assert.Equal(existingData, Directory.Exists(Path.Combine(data, "Backups")));
        // Failed imports must not disable saves.
        new AppSettings().Save(Path.Combine(data, "can-save.json"));
        Assert.True(File.Exists(Path.Combine(data, "can-save.json")));
        dialog.Close();
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("save")]
    [InlineData("discard")]
    [InlineData("launch-fails")]
    [InlineData("save-fails")]
    [InlineData("second-tab-cancel")]
    public void Restart_button_uses_existing_document_questions_before_closing(string answer) => RunUi(root =>
    {
        var app = new AppTestHarness(failWriteMidway: answer == "save-fails");
        var second = answer == "second-tab-cancel" ? new AppTestHarness() : null;
        MainWindow? window = null;
        try
        {
            Assert.True(Await(app.OpenAsync(app.CreatePdf())));
            var original = File.ReadAllBytes(app.Model.DocumentPath!);
            Await(app.RotateAsync());
            window = new MainWindow(app.Model, () => throw new NotSupportedException());
            if (second is not null)
            {
                Assert.True(Await(second.OpenAsync(second.CreatePdf())));
                Await(second.RotateAsync());
                window.Tabs.Add(second.Model);
                second.Dialogs.Answer("AskSaveChanges", SaveChangesChoice.Cancel);
            }
            var dialog = Import(root);
            var button = Assert.IsType<Button>(dialog.FindName("RestartButton"));
            Assert.Equal(Visibility.Visible, button.Visibility);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True((bool)typeof(ImportDialog).GetProperty("RestartRequested")!.GetValue(dialog)!);

            app.Dialogs.Answer("AskSaveChanges", answer is "cancel" ? SaveChangesChoice.Cancel
                : answer is "discard" or "second-tab-cancel" or "launch-fails" ? SaveChangesChoice.Discard : SaveChangesChoice.Save);
            var saved = app.Model.DocumentPath!;
            var launches = 0;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            var method = typeof(MainWindow).GetMethod("RestartAfterImportAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var ok = Await((Task<bool>)method.Invoke(window, new object[] { (Func<bool>)(() => { launches++; return answer != "launch-fails"; }) })!);
            var expected = answer is "save" or "discard";
            Assert.Equal(expected, ok);
            Assert.Equal(expected || answer == "launch-fails" ? 1 : 0, launches);
            Assert.Equal(expected, closed);
            Assert.Single(app.Dialogs.Named("AskSaveChanges"));
            if (second is not null) Assert.Single(second.Dialogs.Named("AskSaveChanges"));
            if (!expected) Assert.True(app.Model.HasDocument);
            if (answer == "save")
            {
                Assert.False(app.Model.IsDirty);
                Assert.NotEqual(original, File.ReadAllBytes(saved));
            }
        }
        finally
        {
            if (window is not null)
            {
                typeof(MainWindow).GetField("_closeConfirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                window.Close();
            }
            Await(app.DisposeAsync().AsTask());
            if (second is not null) Await(second.DisposeAsync().AsTask());
        }
    });

    [Fact]
    public void Import_restart_and_save_notice_have_Thai_translations() => RunUi(_ =>
    {
        Loc.Language = Loc.Thai;
        Assert.True(Loc.TryTranslate("Restart now", out var restart));
        Assert.Equal("เปิดโปรแกรมใหม่ตอนนี้", restart);
        Assert.True(Loc.TryTranslate(Notice, out var notice));
        Assert.NotEqual(Notice, notice);
    });

    [Fact]
    public void Restart_child_waits_for_previous_process_before_continuing()
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Threading.Thread]::Sleep(300)" })
            start.ArgumentList.Add(arg);
        using var previous = System.Diagnostics.Process.Start(start)!;
        Assert.False(previous.HasExited);
        string[] args = ["--restart-after", previous.Id.ToString()];
        Assert.True(AppRestart.WaitForPreviousInstance(ref args));
        Assert.True(previous.HasExited);
        Assert.Empty(args);
        string[] normal = ["document.pdf"];
        Assert.True(AppRestart.WaitForPreviousInstance(ref normal));
        Assert.Equal(["document.pdf"], normal);
    }

    private static ImportDialog Import(string root, BackupImportCoordinator? coordinator = null, bool expectSuccess = true)
    {
        var source = Path.Combine(root, "source");
        Put(source, "settings.json", "{\"Language\":\"th\"}");
        Put(source, "stamps.json", "{\"MyStamps\":[],\"Language\":\"Thai\"}");
        Put(source, "etax.json", "{\"LastNumber\":\"IMPORTED\"}");
        var archive = Path.Combine(root, "test.yrkbackup");
        var protection = new Protection();
        new BackupWriter(protection).Write(archive, source, Password, "test");
        var reader = new BackupReader(protection);
        coordinator ??= new BackupImportCoordinator(reader, new BackupWriter(protection));
        var dialog = new ImportDialog(coordinator, Path.Combine(root, "data"), Path.Combine(root, "temp"));
        ((TextBox)dialog.FindName("FileInput")).Text = archive;
        ((PasswordBox)dialog.FindName("PasswordInput")).Password = Password;
        typeof(ImportDialog).GetField("_inspected", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, true);
        ((Button)dialog.FindName("ImportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !(bool)typeof(ImportDialog).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!);
        if (expectSuccess) Assert.Contains("imported", ((TextBlock)dialog.FindName("Status")).Text);
        return dialog;
    }

    private static void Put(string root, string file, string text)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, file), text);
    }

    private static void RunUi(Action<string> action) => Sta.Run(() =>
    {
        ResetApplication();
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(MainWindow).Assembly);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
        var root = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "restart-" + Guid.NewGuid().ToString("N"))).FullName;
        var language = Loc.Language;
        try { Loc.Language = Loc.English; action(root); return true; }
        finally
        {
            foreach (var store in Stores) store.GetField("s_saveSuspended", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
            Loc.Language = language;
            application.Shutdown();
            ResetApplication();
            Directory.Delete(root, true);
        }
    });

    private static void ResetApplication()
    {
        typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer")!
            .GetField("s_applicationResourceManagerWrapper", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        typeof(Application).GetField("_isShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        typeof(Application).GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
    }

    private static T Await<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < limit, "Dispatcher operation timed out.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
    }
}
