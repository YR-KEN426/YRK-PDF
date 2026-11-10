using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Services.Backup;

namespace Yrk.PdfStudio.App.Views;

public partial class BackupDialog : Window
{
    private bool _busy;
    public BackupDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += (_, _) => { PasswordInput.Clear(); ConfirmInput.Clear(); };
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var selection = BackupSelection.None;
        if (StampsBox.IsChecked == true) selection |= BackupSelection.Stamps;
        if (SignaturesBox.IsChecked == true) selection |= BackupSelection.Signatures;
        if (IdsBox.IsChecked == true) selection |= BackupSelection.DigitalIds;
        if (CertificatesBox.IsChecked == true) selection |= BackupSelection.TrustedCertificates;
        if (ETaxBox.IsChecked == true) selection |= BackupSelection.ETax;
        if (SettingsBox.IsChecked == true) selection |= BackupSelection.Settings;
        if (selection == BackupSelection.None) { Status.Text = Loc.T("Choose at least one item to back up."); return; }
        using (var password = PasswordInput.SecurePassword)
        using (var confirmation = ConfirmInput.SecurePassword)
        {
            if (!BackupDialogSupport.PasswordsMatch(password, confirmation))
            { Status.Text = Loc.T("Use at least 12 characters and enter the same password in both boxes."); return; }
        }
        var picker = new SaveFileDialog
        {
            Title = Loc.T("Save backup"), Filter = Loc.T("YRK PDF backups (*.yrkbackup)|*.yrkbackup"),
            FileName = "YRK-PDF-backup-" + DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".yrkbackup",
            DefaultExt = ".yrkbackup", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != true) return;
        if (File.Exists(picker.FileName)) { Status.Text = Loc.T("Choose a new file name. Existing backup files are kept."); return; }
        SetBusy(true);
        Status.Text = Loc.T("Creating encrypted backup…");
        using var secure = PasswordInput.SecurePassword;
        try
        {
            var path = picker.FileName;
            await BackupDialogSupport.RunAsync(secure, secret =>
            {
                var data = BackupDialogSupport.DataDirectory;
                return new BackupWriter().Write(path, data, secret, BackupDialogSupport.AppVersion, selection,
                    checkDestinationLinks: BackupDialogSupport.IsInsideData(path));
            });
            Status.Text = Loc.T("Backup saved. Keep the file and its passwords safe.");
            PasswordInput.Clear(); ConfirmInput.Clear();
        }
        catch (Exception ex) when (BackupDialogSupport.IsExpected(ex)) { Status.Text = Loc.T("The backup could not be created."); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        Inputs.IsEnabled = SaveButton.IsEnabled = CloseButton.IsEnabled = !value;
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (_busy) e.Cancel = true; }
}
