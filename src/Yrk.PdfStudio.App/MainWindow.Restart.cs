using System.ComponentModel;
using System.IO;
using System.Windows;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App;

public partial class MainWindow
{
    private bool _restartPending;

    internal async Task<bool> RestartAfterImportAsync(Func<bool>? launch = null)
    {
        if (_restartPending || Tabs.Any(t => t.IsBusy)) return false;
        _restartPending = true;
        try
        {
            if (!await ConfirmAllTabsAsync()) return false;
            if (!(launch ?? AppRestart.Start)()) return false;
            _closeConfirmed = true;
            Close();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            App.LogMessage("Restart failed: " + ex.GetType().Name);
            MessageBox.Show(this, Loc.T("YRK PDF could not restart. Close and reopen it when you are ready."),
                "YRK PDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        finally { _restartPending = false; }
    }
}
