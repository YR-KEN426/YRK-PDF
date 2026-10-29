using System.IO;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App;

/// <summary>At start: offers back the changes of files the last run could not save (it was cut off).</summary>
public partial class MainWindow
{
    public async Task OfferRecoveryAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); // After the window is up.
        var entries = RecoveryStore.List();
        if (entries.Count == 0)
        {
            return;
        }

        var list = string.Join("\n", entries.Select(e => Loc.F("• {0} (changes of {1:g})", e.Name, e.SavedAt)));
        var dialogs = new DialogService();
        if (!dialogs.AskYesNo(Loc.F(
                "YRK PDF closed before these changes were saved:\n\n{0}\n\nOpen the recovered copies? They are saved next to the originals as \"(recovered)\". No deletes them.",
                list)))
        {
            RecoveryStore.DeleteAllButWritten();
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                await OpenInTabAsync(RecoveryStore.Restore(entry));
            }
            catch (IOException ex)
            {
                dialogs.ShowError(Loc.F("Couldn't recover \"{0}\".\n\n{1}", entry.Name, ex.Message));
            }
        }
    }
}
