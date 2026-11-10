using System.Diagnostics;
using System.IO;
using System.Windows;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App.Views;

/// <summary>What the app does and does not do with the user's data, and where it keeps its own.</summary>
public partial class PrivacyDialog : Window
{
    private readonly MainViewModel.AboutModel? _about;
    private bool _ready;

    /// <summary>UI-only preview; no settings, recent files or user stores are loaded.</summary>
    public PrivacyDialog() : this(null) { }

    public PrivacyDialog(MainViewModel.AboutModel? about)
    {
        _about = about;
        InitializeComponent();
        AutoUpdateBox.IsChecked = about?.CheckAutomatically ?? false;
        AutoUpdateBox.Visibility = about?.CanUpdate == true ? Visibility.Visible : Visibility.Collapsed;
        ShowUpdateLine();
        _ready = true;
    }

    /// <summary>The update line says exactly what this copy does.</summary>
    private void ShowUpdateLine() =>
        UpdatesText.Text = _about?.UsesStoreUpdates == true
            ? Loc.T("1. Updates: updates are managed through Microsoft Store.")
            : _about?.CanUpdate != true
            ? Loc.T("1. Updates: this copy has no update address, so it never checks.")
            : _about.CheckAutomatically
                ? Loc.T("1. Updates: shortly after the app starts, it asks its download page on GitHub whether a newer version exists, over an encrypted connection. Nothing about you or your files is sent; like any website, GitHub sees your internet address.")
                : Loc.T("1. Updates: automatic checks are off. Nothing is sent unless you click \"Check for updates now\" in About.");

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _about is null) return;
        _about.CheckAutomatically = AutoUpdateBox.IsChecked == true;
        ShowUpdateLine();
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        RecentFiles.Clear();
        ClearRecentButton.IsEnabled = false;
    }

    private void OnReadPrivacyPolicy(object sender, RoutedEventArgs e) =>
        new PrivacyPolicyDialog { Owner = this }.ShowDialog();

    private void OnBackup(object sender, RoutedEventArgs e) => new BackupDialog { Owner = this }.ShowDialog();
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new ImportDialog { Owner = this };
        dialog.ShowDialog();
        if (!dialog.RestartRequested) return;
        var main = Owner as MainWindow;
        Close(); // End both modal dialogs before asking about documents in the main window.
        if (main is not null) await main.RestartAfterImportAsync();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        foreach (var folder in PackageIdentity.Current.GetDataFolders())
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
        }
    }
}
