using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private string? _savedItemSignatureAcknowledgedFor;
    private int _savedItemSelectionRequest;
    private int _savedItemModeGeneration;
    private bool _savedItemEditInProgress;
    internal Task SavedItemEditTask { get; private set; } = Task.CompletedTask;

    public void ClearSavedItemSelection()
    {
        ++_savedItemSelectionRequest;
        ++_savedItemModeGeneration;
        if (_selectedItem?.IsSaved == true) SelectItem(null);
    }

    /// <summary>Called only by the Edit tab. Active drawing/placement tools retain their clicks.</summary>
    public async Task<bool> TrySelectSavedItemAsync(PageViewModel page, double x, double y)
    {
        if (_document is not { } document || IsBusy || IsPlacing
            || CurrentTool is not (AnnotationTool.Select or AnnotationTool.EditText)) return false;
        var request = ++_savedItemSelectionRequest;
        try
        {
            var items = await document.GetPageItemsAsync(page.Index);
            if (document != _document || request != _savedItemSelectionRequest || !Pages.Contains(page)
                || IsBusy || IsPlacing || CurrentTool is not (AnnotationTool.Select or AnnotationTool.EditText)) return true;
            var hit = items.LastOrDefault(i => x >= i.Bounds.X && x <= i.Bounds.X + i.Bounds.Width
                && y >= i.Bounds.Y && y <= i.Bounds.Y + i.Bounds.Height);
            SelectItem(null);
            if (hit is null) return false;
            // Let the page's EditText gesture open the existing line editor (including
            // alignment controls), just as it does for text without a YRK mark.
            if (CurrentTool == AnnotationTool.EditText && hit is { HasYrkMark: true, Kind: YrkMarkKind.Text })
                return false;
            if (hit.EditError is not null)
            {
                _dialogs.ShowInfo(Loc.T("This item cannot be edited or deleted safely because its layout is unsupported (clipping, separated group members, or a complex form)."));
                return true;
            }
            ShowSavedItem(hit);
            return true;
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            StatusMessage = "The saved items on this page could not be read safely.";
            return true;
        }
    }

    private void ShowSavedItem(PdfPageItem item)
    {
        var page = Pages[item.Reference.PageIndex];
        var frame = PlacedItemViewModel.CreateSaved(item, page.WidthPoints, page.HeightPoints);
        page.Items.Add(frame); // Selection frame only; never added to the pending placements.
        SelectItem(frame);
    }

    private Task EditSavedItemAsync(PlacedItemViewModel frame, NormalizedPoint delta = default,
        double rotation = 0, double? opacity = null, bool delete = false)
    {
        if (frame.SavedItem is not { EditError: null } item || IsBusy || _savedItemEditInProgress
            || frame != _selectedItem
            || (!delete && !frame.CanTransform)) return Task.CompletedTask;
        return EditAsync();

        async Task EditAsync()
        {
            var mode = _savedItemModeGeneration;
            _savedItemEditInProgress = true; // Keep the guard through reopen and modal dialogs, too.
            IsBusy = true;
            try
            {
                if (_document is not { } initial) return;
                if (!(await initial.GetPageItemsAsync(item.Reference.PageIndex)).Any(i => i.Reference == item.Reference))
                {
                    SelectItem(null);
                    _dialogs.ShowInfo(Loc.T("The item changed or its layout is unsupported. Select it again; no item changes were applied."));
                    return;
                }
                if (initial != _document || !ConfirmFirstEdit()
                    || !await EnsureAllowedAsync(PdfPermissions.Modify, "edit it") || _document is not { } document) return;
                await _signatureFirstPassTask; // Includes signatures even immediately after opening.
                if (document != _document) return;
                var acknowledged = _savedItemSignatureAcknowledgedFor == document.FilePath;
                if (HasSignatures && !acknowledged)
                {
                    if (!_dialogs.Confirm(Loc.T("Editing this item will make digital signatures show that the document was changed after signing. Continue?"))) return;
                    _savedItemSignatureAcknowledgedFor = document.FilePath;
                    acknowledged = true;
                }
                var options = new PdfItemEditOptions(acknowledged);
                IsBusy = false; // ApplyNowAsync owns the busy state while creating/reopening versions.
                await ApplyNowAsync("Editing saved item…", "Saved item changed. Undo (Ctrl+Z) brings it back.",
                    "Couldn't edit this saved item safely.\n\n{0}", () => true, async candidate =>
                    {
                        try
                        {
                            // Serialization and every native operation expire references. Resolve again each time.
                            async Task<PdfPageItem> ResolveAsync() => (await candidate.GetPageItemsAsync(item.Reference.PageIndex))
                                .Single(i => item.HasYrkMark ? i.Group == item.Group : i.Reference.Index == item.Reference.Index);
                            if (delete) await candidate.RemovePageItemAsync((await ResolveAsync()).Reference, options: options);
                            else
                            {
                                if (delta != default) await candidate.MovePageItemAsync((await ResolveAsync()).Reference, delta, options: options);
                                if (rotation != 0) await candidate.RotatePageItemAsync((await ResolveAsync()).Reference, rotation, options: options);
                                if (opacity is { } alpha)
                                    // Core sets the wrapper alpha absolutely, also for migrated FadeOnly artwork.
                                    await candidate.SetPageItemOpacityAsync((await ResolveAsync()).Reference,
                                        alpha, options: options);
                            }
                        }
                        catch (Exception ex) when (ex is PdfException or InvalidOperationException or NotSupportedException or ArgumentException)
                        {
                            App.LogMessage("Saved item edit rejected: " + ex);
                            throw new PdfException(Loc.T("The item changed or its layout is unsupported. Select it again; no item changes were applied."));
                        }
                    });
                IsBusy = true;
                SelectItem(null);
                if (_document is { } current && item.Reference.PageIndex < Pages.Count)
                {
                    var refreshed = await current.GetPageItemsAsync(item.Reference.PageIndex);
                    if (!delete && mode == _savedItemModeGeneration && current == _document
                        && refreshed.FirstOrDefault(i => i.Group == item.Group && i.HasYrkMark && i.EditError is null) is { } updated)
                        ShowSavedItem(updated);
                }
            }
            catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
            {
                ClearSavedItemSelection();
                StatusMessage = "The saved items on this page could not be read safely.";
            }
            finally { IsBusy = false; _savedItemEditInProgress = false; }
        }
    }
}
