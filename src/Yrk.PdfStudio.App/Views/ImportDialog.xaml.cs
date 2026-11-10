using System.IO;
using System.Windows;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Services.Backup;

namespace Yrk.PdfStudio.App.Views;

public partial class ImportDialog : Window
{
    private bool _busy;
    private bool _inspected;

    public ImportDialog()
    {
        InitializeComponent();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => PasswordInput.Clear();
    }

    private void InvalidateContents()
    {
        _inspected = false;
        if (ImportButton is null) return;
        ImportButton.IsEnabled = false;
        Contents.Items.Clear();
        Status.Text = string.Empty;
    }
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => InvalidateContents();

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = Loc.T("Choose backup"), Filter = Loc.T("YRK PDF backups (*.yrkbackup)|*.yrkbackup"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        FileInput.Text = picker.FileName;
        InvalidateContents();
    }

    private async void OnInspect(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FileInput.Text)) { Status.Text = Loc.T("Choose a backup file first."); return; }
        InvalidateContents();
        SetBusy(true);
        Status.Text = Loc.T("Checking encrypted backup…");
        using var secure = PasswordInput.SecurePassword;
        try
        {
            var path = FileInput.Text;
            var manifest = await BackupDialogSupport.RunAsync(secure, secret => new BackupReader().Inspect(path, secret));
            foreach (var group in manifest.Files.GroupBy(file => BackupWriter.Category(file.Path)))
                Contents.Items.Add(Loc.F("{0}: {1} file(s)", CategoryName(group.Key), group.Count()));
            if (manifest.Files.Count == 0) Contents.Items.Add(Loc.T("This backup contains no data."));
            _inspected = true;
            Status.Text = Loc.T("Contents checked. You can now import this backup.");
        }
        catch (Exception ex) when (BackupDialogSupport.IsExpected(ex)) { Status.Text = Loc.T("The backup could not be read. The password is incorrect or the file is damaged."); }
        finally { SetBusy(false); }
    }

    private static string CategoryName(BackupSelection selection) => selection switch
    {
        BackupSelection.Stamps => Loc.T("Stamps"),
        BackupSelection.Signatures => Loc.T("Signature images"),
        BackupSelection.DigitalIds => Loc.T("Digital IDs"),
        BackupSelection.TrustedCertificates => Loc.T("Trusted certificates"),
        BackupSelection.ETax => Loc.T("e-Tax settings"),
        _ => Loc.T("General settings")
    };

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (!_inspected || _busy) return;
        var path = FileInput.Text;
        var replace = ReplaceBox.IsChecked == true;
        SetBusy(true);
        Status.Text = Loc.T("Backing up current data, then importing…");
        using var secure = PasswordInput.SecurePassword;
        try
        {
            await BackupDialogSupport.RunAsync(secure, secret => new BackupImportCoordinator().Import(path,
                BackupDialogSupport.DataDirectory, Path.GetTempPath(), secret, replace, BackupDialogSupport.AppVersion));
            PasswordInput.Clear();
            Status.Text = Loc.T("Data imported. Restart YRK PDF to load the imported settings and stores.");
        }
        catch (Exception ex) when (BackupDialogSupport.IsExpected(ex))
        {
            // Core exceptions are deliberately sanitized; never show arbitrary exception paths.
            Status.Text = ex is InvalidDataException
                ? Loc.T("The backup could not be read. The password is incorrect or the file is damaged.")
                : ex.Message == Loc.T(BackupImportCoordinator.SafetyError)
                    ? Loc.T(BackupImportCoordinator.SafetyError)
                    : Loc.T("The backup could not be imported. Some files may have been imported. Your safety backup is in the app's Backups folder.");
        }
        finally { _inspected = false; SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        Inputs.IsEnabled = CloseButton.IsEnabled = !value;
        ImportButton.IsEnabled = !value && _inspected;
    }
}
