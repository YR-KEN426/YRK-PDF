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

        // Thai Windows writes the year in the Buddhist era; the English screens keep the Western year.
        var culture = Loc.IsThai ? System.Globalization.CultureInfo.CurrentCulture : System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        var list = string.Join("\n", entries.Select(e => Loc.F("• {0} (changes of {1})", e.Name, e.SavedAt.ToString("g", culture))));
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
