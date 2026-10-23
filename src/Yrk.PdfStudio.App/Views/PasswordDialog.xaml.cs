using System.Windows;

namespace Yrk.PdfStudio.App.Views;

public partial class PasswordDialog : Window
{
    /// <param name="message">Shown instead of "Enter the password to open …" (for example to unlock restrictions).</param>
    /// <param name="button">Text of the confirm button instead of "Open".</param>
    public PasswordDialog(string fileName, bool previousAttemptFailed, string? message = null, string? button = null)
    {
        InitializeComponent();
        MessageText.Text = message ?? Loc.F("Enter the password to open \"{0}\".", fileName);
        if (button is not null)
        {
            OkButton.Content = button;
        }

        ErrorText.Visibility = previousAttemptFailed ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string Password => PasswordInput.Password;

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
