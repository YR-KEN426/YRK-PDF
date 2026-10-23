using System.Windows;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.App.Views;

public partial class ProtectDialog : Window
{
    private const int MinPasswordLength = 4;

    public ProtectDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => OpenPassword.Focus();
        Update();
    }

    public PdfProtection? Protection { get; private set; }

    private void OnChanged(object sender, RoutedEventArgs e) => Update();

    private void Update()
    {
        if (OkButton is null || RestrictPanel is null)
        {
            return; // Raised while the window is being built.
        }

        OpenPanel.IsEnabled = OpenBox.IsChecked == true;
        RestrictPanel.IsEnabled = RestrictBox.IsChecked == true;
        ErrorText.Text = Problem() ?? string.Empty;
        OkButton.IsEnabled = Problem() is null;
    }

    private string? Problem()
    {
        var open = OpenBox.IsChecked == true;
        var restrict = RestrictBox.IsChecked == true;
        if (!open && !restrict)
        {
            return Loc.T("Choose a password to open, restrictions, or both.");
        }

        if (open && OpenPassword.Password.Length < MinPasswordLength)
        {
            return Loc.F("Use a password of at least {0} characters.", MinPasswordLength);
        }

        if (open && OpenPassword.Password != OpenConfirm.Password)
        {
            return Loc.T("The passwords don't match.");
        }

        if (restrict && OwnerPassword.Password.Length < MinPasswordLength)
        {
            return Loc.F("Use an owner password of at least {0} characters.", MinPasswordLength);
        }

        if (restrict && OwnerPassword.Password != OwnerConfirm.Password)
        {
            return Loc.T("The owner passwords don't match.");
        }

        return open && restrict && OpenPassword.Password == OwnerPassword.Password
            ? Loc.T("The owner password must be different from the password to open.")
            : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Problem() is not null)
        {
            return;
        }

        var allowed = PdfPermissions.All;
        if (RestrictBox.IsChecked == true)
        {
            allowed = PdfPermissions.Accessibility;
            if (AllowPrint.IsChecked == true)
            {
                allowed |= PdfPermissions.Print | PdfPermissions.PrintHighQuality;
            }

            if (AllowCopy.IsChecked == true)
            {
                allowed |= PdfPermissions.Copy;
            }

            if (AllowFillForms.IsChecked == true)
            {
                allowed |= PdfPermissions.FillForms;
            }

            if (AllowComments.IsChecked == true)
            {
                allowed |= PdfPermissions.Annotate | PdfPermissions.FillForms;
            }

            if (AllowModify.IsChecked == true)
            {
                allowed |= PdfPermissions.Modify;
            }

            if (AllowAssemble.IsChecked == true)
            {
                allowed |= PdfPermissions.Assemble;
            }
        }

        Protection = new PdfProtection(
            OpenBox.IsChecked == true ? OpenPassword.Password : null,
            RestrictBox.IsChecked == true ? OwnerPassword.Password : null,
            allowed);
        DialogResult = true;
    }
}
