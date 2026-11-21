using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Testing;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class MessageDialogCloseTests
{
    [Fact]
    public void Closing_several_dirty_tabs_stops_at_cancel_and_can_retry_without_losing_edits()
    {
        Sta.Run(() =>
        {
            using var ui = new WpfTestApp("en");
            var first = new AppTestHarness();
            var second = new AppTestHarness();
            var window = new MainWindow(first.Model, () => throw new NotSupportedException());
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                var firstPath = first.CreatePdf(); var secondPath = second.CreatePdf();
                var firstBytes = File.ReadAllBytes(firstPath); var secondBytes = File.ReadAllBytes(secondPath);
                WpfTestApp.Await(first.OpenAsync(firstPath)); WpfTestApp.Await(second.OpenAsync(secondPath));
                WpfTestApp.Await(first.RotateAsync()); WpfTestApp.Await(second.RotateAsync());
                window.Tabs.Add(second.Model);
                ShowOffscreen(window);
                AnswerWithRealDialog(first, window, MessageBoxResult.No);
                AnswerWithRealDialog(second, window, MessageBoxResult.Cancel);
                CloseAndWait(window);

                Assert.False(closed);
                Assert.True(window.IsVisible);
                Assert.Equal(2, window.Tabs.Count);
                Assert.True(first.Model.IsDirty); Assert.True(second.Model.IsDirty);
                Assert.Single(first.Dialogs.Named("AskSaveChanges")); Assert.Single(second.Dialogs.Named("AskSaveChanges"));
                Assert.Same(second.Model, window.DataContext);
                Assert.Equal(firstBytes, File.ReadAllBytes(firstPath)); Assert.Equal(secondBytes, File.ReadAllBytes(secondPath));

                AnswerWithRealDialog(first, window, MessageBoxResult.No);
                AnswerWithRealDialog(second, window, MessageBoxResult.No);
                CloseAndWait(window);
                Assert.True(closed);
                Assert.Equal(2, first.Dialogs.Named("AskSaveChanges").Length);
                Assert.Equal(2, second.Dialogs.Named("AskSaveChanges").Length);
            }
            finally
            {
                WpfTestApp.Await(second.DisposeAsync().AsTask());
                WpfTestApp.Await(first.DisposeAsync().AsTask());
                window.Close();
            }
            Assert.True(ui.Errors.Count == 0, string.Join(Environment.NewLine, ui.Errors));
            return true;
        });
    }

    [Theory]
    [InlineData("cancel-question")]
    [InlineData("cancel-password")]
    [InlineData("failed-write")]
    public void Cancelled_or_failed_save_never_closes_the_window(string scenario)
    {
        Sta.Run(() =>
        {
            using var ui = new WpfTestApp("en");
            var app = new AppTestHarness(failWriteMidway: scenario == "failed-write");
            var window = new MainWindow(app.Model, () => throw new NotSupportedException());
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                var path = app.CreatePdf();
                if (scenario == "cancel-password")
                {
                    File.WriteAllBytes(path, PdfSecurity.Protect(File.ReadAllBytes(path), new PdfProtection("user", "owner", PdfPermissions.All)));
                    app.Dialogs.Answer("PromptPassword", "user");
                    app.Dialogs.Answer("ConfirmPassword", "user", null); // Editing allowed, then cancel password at save.
                }
                var before = File.ReadAllBytes(path);
                WpfTestApp.Await(app.OpenAsync(path));
                WpfTestApp.Await(app.RotateAsync());
                ShowOffscreen(window);
                AnswerWithRealDialog(app, window, scenario == "cancel-question" ? MessageBoxResult.Cancel : MessageBoxResult.Yes);
                CloseAndWait(window);

                Assert.False(closed);
                Assert.True(window.IsVisible);
                Assert.True(app.Model.IsDirty);
                Assert.True(app.Model.HasDocument);
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.Single(app.Dialogs.Named("AskSaveChanges"));
                if (scenario == "failed-write")
                {
                    Assert.True(app.PartialFileExistedAtFailure);
                    Assert.Single(app.Dialogs.Named("ShowError"));
                    Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-saving"));
                }
                else Assert.Empty(app.Dialogs.Named("ShowError"));
                if (scenario == "cancel-password") Assert.Equal(2, app.Dialogs.Named("ConfirmPassword").Length);
            }
            finally { WpfTestApp.Await(app.DisposeAsync().AsTask()); window.Close(); }
            Assert.True(ui.Errors.Count == 0, string.Join(Environment.NewLine, ui.Errors));
            return true;
        });
    }

    private static void AnswerWithRealDialog(AppTestHarness app, Window owner, MessageBoxResult result) =>
        app.Dialogs.Answer("AskSaveChanges", (Func<object?[], object?>)(args =>
        {
            Exception? failure = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialog>().Single();
                try
                {
                    Assert.Same(owner, dialog.Owner);
                    var buttons = ((WrapPanel)dialog.FindName("Buttons")).Children.OfType<Button>();
                    buttons.Single(b => (MessageBoxResult)b.Tag == result).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            }));
            var choice = new DialogService(owner).AskSaveChanges((string)args[0]!);
            Assert.Null(failure);
            return choice;
        }));

    private static void ShowOffscreen(Window window)
    {
        // Exercise real OnClosing/tab/save behavior in a minimal visual host. Document templates
        // are outside this contract; AllWindowsLoadTests checks the full window in both themes.
        window.Content = new Grid();
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000; window.Top = -32000;
        window.Show(); WpfTestApp.Pump();
    }

    private static void CloseAndWait(MainWindow window)
    {
        window.Close();
        WpfTestApp.Await(WaitForClose());
        async Task WaitForClose()
        {
            var field = typeof(MainWindow).GetField("_closeInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
            while ((bool)field.GetValue(window)!) await Task.Delay(10);
        }
    }
}
