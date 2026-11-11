using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Controls;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Services.Backup;
using Yrk.PdfStudio.App.Views;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class PasswordRevealTests
{
    [Fact]
    public void Hold_reveals_release_clears_and_plaintext_cannot_be_edited_or_copied() => Run(() =>
    {
        var box = MakeBox();
        var text = Part<TextBox>(box, "PART_RevealText");
        Assert.False(text.IsUndoEnabled);
        Assert.True(text.IsReadOnly);
        Assert.False(text.Focusable);
        Assert.False(text.IsTabStop);
        Assert.False(text.IsHitTestVisible);
        Assert.Null(text.ContextMenu);
        Assert.Null(System.Windows.Data.BindingOperations.GetBinding(text, TextBox.TextProperty));
        PasswordReveal.SetPressed(box, true);
        Assert.Equal(box.Password, text.Text);
        Assert.Equal(Visibility.Visible, text.Visibility);
        Assert.Equal(Visibility.Hidden, Part<ScrollViewer>(box, "PART_ContentHost").Visibility);
        foreach (var command in new[] { ApplicationCommands.Copy, ApplicationCommands.Cut, ApplicationCommands.Paste })
            Assert.False(command.CanExecute(null, text));
        PasswordReveal.SetPressed(box, false);
        AssertHidden(box);
        Assert.False(text.CanUndo);
    });

    [Theory]
    [InlineData("unloaded")]
    [InlineData("focus")]
    [InlineData("capture")]
    [InlineData("leave")]
    [InlineData("disabled")]
    [InlineData("password changed")]
    [InlineData("behavior removed")]
    public void Cancellation_immediately_clears_plaintext(string reason) => Run(() =>
    {
        var box = MakeBox();
        var button = Part<Button>(box, "PART_RevealButton");
        PasswordReveal.SetPressed(box, true);
        switch (reason)
        {
            case "unloaded": box.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
            case "focus": button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, button, null)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent }); break;
            case "capture": button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                { RoutedEvent = Mouse.LostMouseCaptureEvent }); break;
            case "leave": button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                { RoutedEvent = Mouse.MouseLeaveEvent }); break;
            case "disabled": box.IsEnabled = false; break;
            case "password changed": box.Password = "changed-test-password"; break;
            case "behavior removed": PasswordReveal.SetEnabled(box, false); break;
        }
        AssertHidden(box);
    });

    [Theory]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    public void Keyboard_hold_and_release_are_handled_without_submitting_the_dialog(Key key) => Run(() =>
    {
        var box = MakeBox();
        var button = Part<Button>(box, "PART_RevealButton");
        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);
        using var source = new HwndSource(new HwndSourceParameters("Password reveal keyboard test"));
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        button.RaiseEvent(down);
        Assert.True(down.Handled);
        Assert.Equal(box.Password, Part<TextBox>(box, "PART_RevealText").Text);
        var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
        button.RaiseEvent(up);
        Assert.True(up.Handled);
        AssertHidden(box);
    });

    [Fact]
    public void Hiding_a_visible_box_clears_plaintext() => Run(() =>
    {
        var box = MakeBox();
        using var source = new HwndSource(new HwndSourceParameters("Password reveal visibility test"));
        source.RootVisual = box;
        Assert.True(box.IsVisible);
        PasswordReveal.SetPressed(box, true);
        box.Visibility = Visibility.Hidden;
        AssertHidden(box);
    });

    [Fact]
    public void Mouse_release_clears_the_password() => Run(() =>
    {
        var box = MakeBox();
        var button = Part<Button>(box, "PART_RevealButton");
        // A never-shown HWND cannot capture the physical mouse. Simulate the press through
        // the same internal entry point, then dispatch the real routed release event.
        PasswordReveal.SetPressed(box, true);
        Assert.Equal(box.Password, Part<TextBox>(box, "PART_RevealText").Text);
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
        button.RaiseEvent(up);
        Assert.True(up.Handled);
        Assert.False(button.IsMouseCaptured);
        AssertHidden(box);
    });

    [Fact]
    public void Replacing_the_template_clears_the_old_plaintext_surface() => Run(() =>
    {
        var box = MakeBox();
        var oldText = Part<TextBox>(box, "PART_RevealText");
        PasswordReveal.SetPressed(box, true);
        box.Template = new ControlTemplate(typeof(PasswordBox));
        box.ApplyTemplate();
        PasswordReveal.SetPressed(box, false);
        Assert.Equal(string.Empty, oldText.Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Window_deactivation_or_close_clears_plaintext(bool close) => Run(() =>
    {
        var box = MakeBox();
        var window = new Window { Content = box };
        try
        {
            box.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PasswordReveal.SetPressed(box, true);
            if (close) window.Close();
            else typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);
            AssertHidden(box);
        }
        finally { if (!close) window.Close(); }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("th")]
    public void All_eleven_original_password_boxes_keep_their_API_and_have_accessible_reveal_buttons(string language) => Run(() =>
    {
        Loc.Language = language;
        var temp = Path.Combine(Path.GetTempPath(), "reveal-" + Guid.NewGuid().ToString("N"));
        (Window Window, string[] Names)[] windows =
        [
            (new BackupDialog(), ["PasswordInput", "ConfirmInput"]),
            (new DigitalSignDialog(new DigitalIdStore(temp), []), ["PasswordInput"]),
            (new CreateDigitalIdDialog(), ["PasswordInput", "ConfirmInput"]),
            (new ImportDialog(new BackupImportCoordinator(), temp, temp), ["PasswordInput"]),
            (new PasswordDialog("test.pdf", false), ["PasswordInput"]),
            (new ProtectDialog(), ["OpenPassword", "OpenConfirm", "OwnerPassword", "OwnerConfirm"]),
        ];
        try
        {
            foreach (var (window, names) in windows)
            {
                window.Measure(new Size(800, 900));
                window.Arrange(new Rect(0, 0, 800, 900));
                Assert.Equal(names.Length, LogicalPasswords(window).Count());
                foreach (var name in names)
                {
                    var box = Assert.IsType<PasswordBox>(window.FindName(name));
                    box.ApplyTemplate();
                    box.Measure(new Size(300, 40));
                    PasswordReveal.SetPressed(box, false);
                    Assert.True(PasswordReveal.GetEnabled(box));
                    var button = Assert.Single(VisualChildren<Button>(box), b => b.Name == "PART_RevealButton");
                    typeof(UiTranslator).GetMethod("TranslateNow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [box]);
                    Assert.Equal(language == "th" ? "แสดงรหัส" : "Show password", AutomationProperties.GetName(button));
                    Assert.Equal(Loc.T("Show password"), button.ToolTip);
                    Assert.False(Part<TextBox>(box, "PART_RevealText").IsUndoEnabled);
                    box.Password = "test-only-password";
                    PasswordReveal.SetPressed(box, true);
                    PasswordReveal.SetPressed(box, false);
                    Assert.Equal("test-only-password", box.Password);
                    using var secure = box.SecurePassword;
                    var pointer = Marshal.SecureStringToBSTR(secure);
                    try { Assert.Equal("test-only-password", Marshal.PtrToStringBSTR(pointer)); }
                    finally { Marshal.ZeroFreeBSTR(pointer); }
                    AssertHidden(box);
                }
                if (window is PasswordDialog password) Assert.Equal("test-only-password", password.Password);
                if (window is CreateDigitalIdDialog digitalId) Assert.Equal("test-only-password", digitalId.Password);
            }
        }
        finally { foreach (var (window, _) in windows) window.Close(); }
    });

    private static PasswordBox MakeBox()
    {
        var box = new PasswordBox { Style = (Style)Application.Current.FindResource("RevealPasswordStyle"), Password = "test-only-password" };
        box.ApplyTemplate();
        box.Measure(new Size(300, 40));
        PasswordReveal.SetPressed(box, false);
        return box;
    }
    private static T Part<T>(PasswordBox box, string name) where T : DependencyObject => Assert.IsType<T>(box.Template.FindName(name, box));
    private static void AssertHidden(PasswordBox box)
    {
        Assert.Equal(string.Empty, Part<TextBox>(box, "PART_RevealText").Text);
        Assert.Equal(Visibility.Collapsed, Part<TextBox>(box, "PART_RevealText").Visibility);
        Assert.Equal(Visibility.Visible, Part<ScrollViewer>(box, "PART_ContentHost").Visibility);
    }
    private static IEnumerable<PasswordBox> LogicalPasswords(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is PasswordBox box) yield return box;
            else foreach (var nested in LogicalPasswords(child)) yield return nested;
        }
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }
    private static void Run(Action test) => Sta.Run(() =>
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(MainWindow).Assembly);
        ResetApplication();
        var app = new Application();
        var language = Loc.Language;
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            test();
            return true;
        }
        finally { Loc.Language = language; app.Shutdown(); ResetApplication(); }
    });
    private static void ResetApplication()
    {
        typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer")!
            .GetField("s_applicationResourceManagerWrapper", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        typeof(Application).GetField("_isShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        foreach (var name in new[] { "_appInstance", "_appCreatedInThisAppDomain" })
            typeof(Application).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, name == "_appInstance" ? null : false);
    }
}
