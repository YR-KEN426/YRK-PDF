using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Clean up scan: every page is checked for a tilt, dark scanner edges and blankness; the user picks what to fix,
/// and it is done in memory as one step that undo takes back.
/// </summary>
public sealed partial class MainViewModel
{
    private IAsyncRelayCommand? _cleanUpScanCommand;

    public IAsyncRelayCommand CleanUpScanCommand => _cleanUpScanCommand ??= new AsyncRelayCommand(CleanUpScanAsync);

    private async Task CleanUpScanAsync()
    {
        if (_document is not { } document)
        {
            return;
        }

        var checks = new ScanPageCheck[document.PageCount];
        if (!await RunBusyAsync("Checking the pages…", async () =>
            {
                for (var i = 0; i < checks.Length; i++)
                {
                    StatusMessage = Loc.T("Checking the pages…") + $" {i + 1}/{checks.Length}";
                    var size = document.PageSizes[i];
                    var scale = ScanCleanup.Dpi / 72.0;
                    var bitmap = await document.RenderPageAsync(
                        i, Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)), PdfWorkPriority.Background);
                    checks[i] = await Task.Run(() => ScanCleanup.Check(bitmap));
                }
            }))
        {
            return;
        }

        var tilted = Enumerable.Range(0, checks.Length).Where(i => checks[i].Straighten != 0).ToList();
        var edged = Enumerable.Range(0, checks.Length).Where(i => checks[i].Keep is not null).ToList();
        var blank = Enumerable.Range(0, checks.Length).Where(i => checks[i].IsBlank).ToList();
        if (blank.Count == checks.Length)
        {
            blank.Clear(); // Every page blank: something else is wrong; a PDF must keep a page anyway.
        }

        if (tilted.Count + edged.Count + blank.Count == 0)
        {
            StatusMessage = Loc.T("The pages are straight, with no dark edges or blank pages");
            return;
        }

        if (_dialogs.AskScanCleanup(tilted, edged, blank) is not { } choice)
        {
            StatusMessage = string.Empty;
            return;
        }

        var turns = choice.Straighten ? tilted.Select(i => (Page: i, Degrees: checks[i].Straighten)).ToList() : [];
        var crops = choice.TrimEdges ? edged.Select(i => (Page: i, Keep: checks[i].Keep!.Value)).ToList() : [];
        var deletes = choice.DeleteBlank ? blank.ToArray() : [];
        if (turns.Count + crops.Count + deletes.Length == 0)
        {
            return;
        }

        // Edges first (found on the page as scanned), then the turn about the trimmed page's centre, then the deletes.
        async Task Forward(IPdfDocument d)
        {
            foreach (var (page, keep) in crops)
            {
                await d.CropPageAsync(page, keep);
            }

            foreach (var (page, degrees) in turns)
            {
                await d.StraightenPageAsync(page, degrees);
            }

            if (deletes.Length > 0)
            {
                await d.DeletePagesAsync(deletes);
            }
        }

        PageSnapshot? removed = null;
        await RunPageToolAsync("Cleaning up…", async d =>
        {
            removed = deletes.Length > 0 ? await SnapshotPagesAsync(d, deletes) : null;
            await Forward(d);
            if (deletes.Length > 0)
            {
                _undo.KeepOnlyPageSteps();
            }

            _undo.Push(
                () => _ = RunPageToolAsync("Undoing the clean-up…", async u =>
                {
                    if (removed is not null)
                    {
                        await RestorePagesAsync(u, removed, deletes);
                    }

                    foreach (var (page, degrees) in turns)
                    {
                        await u.StraightenPageAsync(page, -degrees);
                    }

                    foreach (var (page, keep) in crops)
                    {
                        await u.CropPageAsync(page, new NormalizedRect(-keep.X / keep.Width, -keep.Y / keep.Height, 1 / keep.Width, 1 / keep.Height));
                    }
                }, 0),
                () => _ = RunPageToolAsync("Cleaning up…", Forward, 0),
                pageStep: true);
        }, 0, commitFirst: true);

        var done = new List<string>();
        if (turns.Count > 0)
        {
            done.Add(Loc.F("straightened {0}", turns.Count));
        }

        if (crops.Count > 0)
        {
            done.Add(Loc.F("trimmed {0}", crops.Count));
        }

        if (deletes.Length > 0)
        {
            done.Add(Loc.F("deleted {0} blank", deletes.Length));
        }

        StatusMessage = Loc.F("Clean-up done: {0} pages. Save to keep it.", string.Join(", ", done));
    }
}
