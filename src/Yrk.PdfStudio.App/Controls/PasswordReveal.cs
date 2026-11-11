using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Yrk.PdfStudio.App.Controls;

/// <summary>Hold to reveal. Only the template's temporary, non-editable TextBox holds plaintext.</summary>
public static class PasswordReveal
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PasswordReveal), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(PasswordReveal));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not PasswordBox box) return;
        if (box.GetValue(StateProperty) is State old) old.Dispose();
        box.SetValue(StateProperty, (bool)e.NewValue ? new State(box) : null);
    }

    // Also used by STA tests after ApplyTemplate, without showing a window.
    internal static void SetPressed(PasswordBox box, bool pressed)
    {
        if (box.GetValue(StateProperty) is State state)
        {
            state.ConnectTemplate();
            state.Show(pressed);
        }
    }

    private sealed class State
    {
        private readonly PasswordBox _box;
        private Button? _button;
        private TextBox? _text;
        private ScrollViewer? _masked;
        private Window? _window;

        internal State(PasswordBox box)
        {
            _box = box;
            box.Loaded += OnLoaded;
            box.Unloaded += OnUnloaded;
            box.LayoutUpdated += OnLayoutUpdated;
            box.IsVisibleChanged += OnUnavailable;
            box.IsEnabledChanged += OnUnavailable;
            box.PasswordChanged += OnPasswordChanged;
        }

        internal void ConnectTemplate()
        {
            _box.ApplyTemplate();
            var button = _box.Template?.FindName("PART_RevealButton", _box) as Button;
            if (ReferenceEquals(button, _button)) return;
            DisconnectTemplate();
            _button = button;
            _text = _box.Template?.FindName("PART_RevealText", _box) as TextBox;
            _masked = _box.Template?.FindName("PART_ContentHost", _box) as ScrollViewer;
            if (_button is null) return;
            _button.PreviewMouseLeftButtonDown += OnMouseDown;
            _button.PreviewMouseLeftButtonUp += OnMouseUp;
            _button.MouseLeave += OnCancel;
            _button.LostMouseCapture += OnCancel;
            _button.LostKeyboardFocus += OnCancel;
            _button.PreviewKeyDown += OnKeyDown;
            _button.PreviewKeyUp += OnKeyUp;
            _button.Unloaded += OnCancel;
            if (_text is not null)
                foreach (var command in new[] { ApplicationCommands.Copy, ApplicationCommands.Cut, ApplicationCommands.Paste })
                    _text.CommandBindings.Add(new CommandBinding(command, BlockCommand, BlockCommandQuery));
        }

        internal void Show(bool pressed)
        {
            if (_text is null || _masked is null) return;
            // Hide the masked surface, not its ancestor: the eye must remain visible and focusable.
            // No binding, field, or view model retains a second copy of the password.
            _text.Text = pressed ? _box.Password : string.Empty;
            _text.Visibility = pressed ? Visibility.Visible : Visibility.Collapsed;
            _masked.Visibility = pressed ? Visibility.Hidden : Visibility.Visible;
        }

        private void Cancel()
        {
            Show(false);
            if (_button?.IsMouseCaptured == true) _button.ReleaseMouseCapture();
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _button!.Focus();
            if (_button.CaptureMouse()) Show(true);
            e.Handled = true;
        }
        private void OnMouseUp(object sender, MouseButtonEventArgs e) { Cancel(); e.Handled = true; }
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Space or Key.Enter)) return;
            if (!e.IsRepeat) Show(true);
            e.Handled = true; // Enter must not submit the dialog.
        }
        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Space or Key.Enter)) return;
            Cancel();
            e.Handled = true;
        }
        private void OnCancel(object? sender, RoutedEventArgs e) => Cancel();
        private void OnUnavailable(object sender, DependencyPropertyChangedEventArgs e)
        { if (!(bool)e.NewValue) Cancel(); }
        private void OnPasswordChanged(object sender, RoutedEventArgs e) => Cancel();
        private void OnLayoutUpdated(object? sender, EventArgs e) => ConnectTemplate();
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ConnectTemplate();
            DetachWindow();
            _window = Window.GetWindow(_box);
            if (_window is null) return;
            _window.Deactivated += OnWindowCancelled;
            _window.Closed += OnWindowCancelled;
        }
        private void OnWindowCancelled(object? sender, EventArgs e) => Cancel();
        private void OnUnloaded(object sender, RoutedEventArgs e) { Cancel(); DetachWindow(); }
        private void DetachWindow()
        {
            if (_window is null) return;
            _window.Deactivated -= OnWindowCancelled;
            _window.Closed -= OnWindowCancelled;
            _window = null;
        }
        private void DisconnectTemplate()
        {
            Cancel();
            if (_button is null) return;
            _button.PreviewMouseLeftButtonDown -= OnMouseDown;
            _button.PreviewMouseLeftButtonUp -= OnMouseUp;
            _button.MouseLeave -= OnCancel;
            _button.LostMouseCapture -= OnCancel;
            _button.LostKeyboardFocus -= OnCancel;
            _button.PreviewKeyDown -= OnKeyDown;
            _button.PreviewKeyUp -= OnKeyUp;
            _button.Unloaded -= OnCancel;
            _button = null;
            _text = null;
            _masked = null;
        }
        internal void Dispose()
        {
            DisconnectTemplate();
            DetachWindow();
            _box.Loaded -= OnLoaded;
            _box.Unloaded -= OnUnloaded;
            _box.LayoutUpdated -= OnLayoutUpdated;
            _box.IsVisibleChanged -= OnUnavailable;
            _box.IsEnabledChanged -= OnUnavailable;
            _box.PasswordChanged -= OnPasswordChanged;
        }
        private static void BlockCommand(object sender, ExecutedRoutedEventArgs e) => e.Handled = true;
        private static void BlockCommandQuery(object sender, CanExecuteRoutedEventArgs e)
        { e.CanExecute = false; e.Handled = true; }
    }
}
