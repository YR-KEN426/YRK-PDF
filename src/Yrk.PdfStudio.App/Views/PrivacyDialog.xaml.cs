using System.Diagnostics;
using System.IO;
using System.Windows;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;

namespace Yrk.PdfStudio.App.Views;

/// <summary>What the app does and does not do with the user's data, and where it keeps its own.</summary>
public partial class PrivacyDialog : Window
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf");

    private readonly MainViewModel.AboutModel _about;

    public PrivacyDialog(MainViewModel.AboutModel about)
    {
        _about = about;
        InitializeComponent();
        AutoUpdateBox.IsChecked = about.CheckAutomatically;
        AutoUpdateBox.Visibility = about.CanUpdate ? Visibility.Visible : Visibility.Collapsed;
        ShowUpdateLine();
        ClearRecentButton.IsEnabled = RecentFiles.Items.Count > 0;
    }

    /// <summary>The update line says exactly what this copy does.</summary>
    private void ShowUpdateLine() =>
        UpdatesText.Text = !_about.CanUpdate
            ? Loc.T("1. Updates: this copy has no update address, so it never checks.")
            : _about.CheckAutomatically
                ? Loc.T("1. Updates: shortly after the app starts, it asks its download page on GitHub whether a newer version exists, over an encrypted connection. Nothing about you or your files is sent; like any website, GitHub sees your internet address.")
                : Loc.T("1. Updates: automatic checks are off. Nothing is sent unless you click \"Check for updates now\" in About.");

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        _about.CheckAutomatically = AutoUpdateBox.IsChecked == true;
        ShowUpdateLine();
    }

    private void OnClearRecent(object sender, RoutedEventArgs e)
    {
        RecentFiles.Clear();
        ClearRecentButton.IsEnabled = false;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = false });
    }
}
