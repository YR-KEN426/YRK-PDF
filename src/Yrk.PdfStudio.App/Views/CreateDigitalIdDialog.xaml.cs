using System.Windows;

namespace Yrk.PdfStudio.App.Views;

public partial class CreateDigitalIdDialog : Window
{
    private const int MinPasswordLength = 6;

    public CreateDigitalIdDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameInput.Focus();
    }

    public string SignerName => NameInput.Text.Trim();

    public string? Email => string.IsNullOrWhiteSpace(EmailInput.Text) ? null : EmailInput.Text.Trim();

    public string? Organization => string.IsNullOrWhiteSpace(OrganizationInput.Text) ? null : OrganizationInput.Text.Trim();

    public string Password => PasswordInput.Password;

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var problem = SignerName.Length == 0 ? Loc.T("Enter your name.")
            : Email is { } email && !email.Contains('@', StringComparison.Ordinal) ? Loc.T("The email address doesn't look right.")
            : Password.Length < MinPasswordLength ? Loc.F("Use a password of at least {0} characters.", MinPasswordLength)
            : Password != ConfirmInput.Password ? Loc.T("The passwords don't match.")
            : null;
        if (problem is not null)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
