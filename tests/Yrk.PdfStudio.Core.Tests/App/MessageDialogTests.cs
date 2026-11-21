using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Testing;
using PdfApp = Yrk.PdfStudio.App.App;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class MessageDialogTests
{
    // Contract                      Buttons -> caller results             Enter         Esc / title-bar X
    // AskSaveChanges                Save / Don't save / Cancel           Save          Cancel
    // AskYesNoDefaultNo             Yes / No -> true / false             false         false
    // AskYesNo                      Yes / No -> true / false             true          false
    // Confirm (OKCancel)            OK / Cancel -> true / false          true          false
    // ShowInfo (Show OK)             OK                                   close         close
    // CreateCutPagesCloseDialog     Continue closing / Cancel            Cancel        Cancel
    // ScanDialog.AskCloseChoice     Save / Close without saving / Cancel Cancel        Cancel
    // CrashMessageBox themed path   OK                                   close         close
    // Save pipeline regressions: AppSaveTests covers cancelled/wrong save passwords and failed writes.
    // MessageDialogCloseTests covers several dirty tabs, cancelling save and failed save through OnClosing.
    private static readonly string[] Sites = ["save", "clear-recent", "default-no", "yes-no", "ok-cancel", "info", "cut", "scan", "crash"];

    public static IEnumerable<object[]> Contracts =>
        from site in Sites
        from language in new[] { "en", "th" }
        from action in new[] { "button0", "button1", "button2", "enter", "escape", "x", "alt" }
        where action != "button2" || site is "save" or "scan"
        where action != "button1" || site is not ("info" or "crash")
        select new object[] { site, language, action };

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Every_call_site_preserves_buttons_keyboard_and_native_close(string site, string language, string action)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            using var fixture = new WindowFixture();
            Window? owner = site switch
            {
                "cut" => new MainWindow(fixture.Model, () => throw new NotSupportedException()),
                "scan" => new ScanDialog(),
                _ => null,
            };
            // Give owners an HWND without starting ScanDialog's Loaded/scanner workflow.
            if (owner is not null) new WindowInteropHelper(owner).EnsureHandle();
            Exception? failure = null;
            var visited = false;
            var expectedIndex = action switch
            {
                "button1" => 1, "button2" => 2,
                "enter" => site switch { "default-no" or "clear-recent" => 1, "cut" => 1, "scan" => 2, _ => 0 },
                "escape" or "x" => site switch { "save" or "scan" => 2, "info" or "crash" => 0, _ => 1 },
                _ => 0,
            };
            var labels = site switch
            {
                "save" => new[] { "Save", "Don't save", "Cancel" },
                "scan" => ["Save", "Close without saving", "Cancel"],
                "cut" => ["Continue closing", "Cancel"],
                "yes-no" or "default-no" => ["Yes", "No"],
                "ok-cancel" or "clear-recent" => ["OK", "Cancel"],
                _ => ["OK"],
            };
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialog>().Single();
                try
                {
                    visited = true;
                    Assert.Same(owner, dialog.Owner);
                    Assert.Equal(owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                        dialog.WindowStartupLocation);
                    var buttons = ((WrapPanel)dialog.FindName("Buttons")).Children.OfType<Button>().ToArray();
                    Assert.Equal(labels.Length, buttons.Length);
                    for (var i = 0; i < buttons.Length; i++)
                    {
                        var access = Assert.IsType<AccessText>(buttons[i].Content);
                        Assert.StartsWith(Loc.T(labels[i]) + " (_", access.Text);
                        Assert.Same(Application.Current.Resources[buttons[i].IsDefault ? "PrimaryButton" : "SecondaryButton"], buttons[i].Style);
                    }
                    var defaultIndex = site switch { "default-no" or "clear-recent" or "cut" => 1, "scan" => 2, _ => 0 };
                    Assert.Same(buttons[defaultIndex], Assert.Single(buttons.Where(b => b.IsDefault)));
                    Assert.Same(buttons[defaultIndex], FocusManager.GetFocusedElement(dialog));
                    if (site == "save") Assert.Equal(Loc.F("Save changes to \"{0}\"?", "example.pdf"), ((TextBlock)dialog.FindName("MessageText")).Text);
                    if (site == "scan") Assert.Equal(Loc.F("There are {0} unsaved pages. Close anyway?", 0), ((TextBlock)dialog.FindName("MessageText")).Text);
                    if (site == "clear-recent")
                    {
                        var options = Assert.IsType<StackPanel>(((ContentControl)dialog.FindName("AdditionalContent")).Content);
                        var boxes = options.Children.OfType<CheckBox>().ToArray();
                        Assert.Equal(2, boxes.Length);
                        Assert.Equal(Loc.T("Saved reading positions"), boxes[0].Content);
                        Assert.True(boxes[0].IsChecked);
                        Assert.Equal(Loc.T("Reading marks"), boxes[1].Content);
                        Assert.False(boxes[1].IsChecked);
                    }
                    switch (action)
                    {
                        case "enter": AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "\r", false); break;
                        case "escape":
                            dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Escape)
                                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                            break;
                        case "x":
                            // Native system close, same command as clicking the title-bar X (not DialogResult).
                            SendMessage(new WindowInteropHelper(dialog).Handle, 0x112, new IntPtr(0xF060), IntPtr.Zero);
                            break;
                        case "alt":
                            var text = ((AccessText)buttons[0].Content).Text;
                            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), text[(text.LastIndexOf('_') + 1)..^1], false);
                            break;
                        default:
                            ((IInvokeProvider)new ButtonAutomationPeer(buttons[expectedIndex]).GetPattern(PatternInterface.Invoke)).Invoke();
                            WpfTestApp.Pump();
                            break;
                    }
                    WpfTestApp.Pump();
                    Assert.False(dialog.IsVisible);
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            }));
            try
            {
                var service = new DialogService();
                object? actual = site switch
                {
                    "save" => service.AskSaveChanges("example.pdf"),
                    "clear-recent" => service.ConfirmClearRecent(),
                    "default-no" => service.AskYesNoDefaultNo("Continue closing"),
                    "yes-no" => service.AskYesNo("Continue closing"),
                    "ok-cancel" => service.Confirm("Continue closing"),
                    "info" => Run(() => service.ShowInfo("Continue closing")),
                    "crash" => Run(() => PdfApp.ShowCrashMessage(Loc.T("Something went wrong. Try again. If the problem continues, restart the app."))),
                    "cut" => ((MessageDialog)Call(owner!, "CreateCutPagesCloseDialog", Loc.T("Continue closing"))!).ShowResult(),
                    "scan" => Call(owner!, "AskCloseChoice"),
                    _ => throw new NotSupportedException(site),
                };
                Assert.Null(failure);
                Assert.True(visited);
                object? expected = site switch
                {
                    "save" or "scan" => new[] { SaveChangesChoice.Save, SaveChangesChoice.Discard, SaveChangesChoice.Cancel }[expectedIndex],
                    "cut" => expectedIndex == 0 ? MessageBoxResult.Yes : MessageBoxResult.Cancel,
                    "info" or "crash" => null,
                    "clear-recent" => expectedIndex == 0 ? new ClearRecentChoice(true, false) : null,
                    _ => expectedIndex == 0,
                };
                Assert.Equal(expected, actual);
            }
            finally { owner?.Close(); }
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Fact]
    public void Explicit_owner_is_used_by_dialog_service()
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp("en");
            var owner = new Window();
            new WindowInteropHelper(owner).EnsureHandle();
            Exception? failure = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialog>().Single();
                try { Assert.Same(owner, dialog.Owner); }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            }));
            new DialogService(owner).ShowInfo("Continue closing");
            owner.Close();
            Assert.Null(failure);
            Assert.Empty(app.Errors);
            return true;
        });
    }

    [Theory]
    [InlineData("en")]
    [InlineData("th")]
    public void Home_uses_existing_clear_command_unsubscribes_language_and_privacy_can_cancel(string language)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language);
            using var fixture = new WindowFixture();
            var home = new MainWindow(fixture.Model, () => throw new NotSupportedException());
            home.Show();
            home.UpdateLayout();
            Assert.Same(fixture.Model.ClearRecentCommand, ((Button)home.FindName("HomeClearRecentButton")).Command);
            home.Close();
            var handlers = (Delegate?)typeof(Loc).GetField("LanguageChanged", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Assert.DoesNotContain(handlers?.GetInvocationList() ?? [], handler =>
                ReferenceEquals(handler.Target, home) && handler.Method.Name == "OnHomeLanguageChanged");

            var privacy = new PrivacyDialog();
            new WindowInteropHelper(privacy).EnsureHandle();
            Exception? failure = null;
            var visited = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialog>().Single();
                try
                {
                    visited = true;
                    Assert.Same(privacy, dialog.Owner);
                    Assert.Equal(Loc.T("The recent files list and opening times will always be cleared. Choose any reading data to clear too."), ((TextBlock)dialog.FindName("MessageText")).Text);
                    var buttons = ((WrapPanel)dialog.FindName("Buttons")).Children.OfType<Button>().ToArray();
                    Assert.Equal(MessageBoxResult.Cancel, Assert.Single(buttons.Where(button => button.IsDefault)).Tag);
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            }));
            Call(privacy, "OnClearRecent", privacy, new RoutedEventArgs());
            Assert.Null(failure);
            Assert.True(visited);
            Assert.True(((Button)privacy.FindName("ClearRecentButton")).IsEnabled);
            privacy.Close();
            Assert.Empty(app.Errors);
            return true;
        });
    }

    public static IEnumerable<object[]> ClearChoices =>
        from theme in new[] { "light", "dark" }
        from language in new[] { "en", "th" }
        from positions in new[] { false, true }
        from bookmarks in new[] { false, true }
        select new object[] { theme, language, positions, bookmarks };

    [Theory]
    [MemberData(nameof(ClearChoices))]
    public void Clear_dialog_returns_selected_options_in_both_themes(string theme, string language, bool positions, bool bookmarks)
    {
        Sta.Run(() =>
        {
            using var app = new WpfTestApp(language, theme);
            Exception? failure = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<MessageDialog>().Single();
                try
                {
                    var options = Assert.IsType<StackPanel>(((ContentControl)dialog.FindName("AdditionalContent")).Content);
                    var boxes = options.Children.OfType<CheckBox>().ToArray();
                    Assert.True(boxes[0].IsChecked);
                    Assert.False(boxes[1].IsChecked);
                    foreach (var box in boxes)
                    {
                        Assert.Same(Application.Current.Resources[typeof(CheckBox)], box.Style);
                        Assert.Equal(((System.Windows.Media.SolidColorBrush)Application.Current.Resources["TextBrush"]).Color,
                            ((System.Windows.Media.SolidColorBrush)box.Foreground).Color);
                    }
                    boxes[0].IsChecked = positions;
                    boxes[1].IsChecked = bookmarks;
                    var ok = ((WrapPanel)dialog.FindName("Buttons")).Children.OfType<Button>().Single(b => Equals(b.Tag, MessageBoxResult.OK));
                    ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            }));
            var result = new DialogService().ConfirmClearRecent();
            Assert.Null(failure);
            Assert.Equal(new ClearRecentChoice(positions, bookmarks), result);
            Assert.Empty(app.Errors);
            return true;
        });
    }

    private static object? Run(Action action) { action(); return null; }
    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
