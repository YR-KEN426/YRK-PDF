using System.Diagnostics;
using System.IO;
using System.Windows;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App.Views;

public partial class ConvertScanResultDialog : Window
{
    private readonly ConvertScanResult _result;
    internal Action<string> CopyText { get; set; } = Clipboard.SetText;
    public ConvertScanResultDialog(ConvertScanResult result)
    {
        InitializeComponent();
        _result = result;
        OutputWarningText.Visibility = _result.OutputFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = result.Summary;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var folder in _result.OutputFiles.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var error = ErrorCatalog.ConvertScanOpenFolder;
            App.LogError(error, ex);
            ErrorText.Text = Loc.T(error.Message) + "\n" + Loc.T(error.NextStep);
        }
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        try { CopyText(_result.AllText); }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            var error = ErrorCatalog.ConvertScanCopyText;
            App.LogError(error, ex);
            ErrorText.Text = Loc.T(error.Message) + "\n" + Loc.T(error.NextStep);
        }
    }
}
