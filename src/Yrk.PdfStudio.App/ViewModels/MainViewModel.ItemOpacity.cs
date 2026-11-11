using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private sealed class ItemOpacityChange(PlacedItemViewModel frame, PageViewModel page, IPdfDocument document)
    {
        public PlacedItemViewModel Frame { get; } = frame;
        public PageViewModel Page { get; } = page;
        public IPdfDocument Document { get; } = document;
        public double Before { get; } = frame.Opacity;
        public int Request { get; set; }
        public bool PreviewRunning { get; set; }
        public Task PreviewTask { get; set; } = Task.CompletedTask;
    }

    private ItemOpacityChange? _itemOpacityChange;
    internal Task ItemOpacityPreviewTask => _itemOpacityChange?.PreviewTask ?? Task.CompletedTask;

    /// <summary>Starts before the slider changes, including clicks on its track.</summary>
    public void BeginItemOpacityChange(PlacedItemViewModel item)
    {
        if (_itemOpacityChange?.Frame == item || IsBusy || _savedItemEditInProgress
            || item.IsSaved && _selectedItem != item
            || !item.CanAdjustOpacity || _document is not { } document) return;
        SelectItem(item);
        _itemOpacityChange = new(item, Pages[item.PageIndex], document);
    }

    public void EndItemOpacityChange(PlacedItemViewModel item, bool cancel = false)
    {
        if (_itemOpacityChange is not { } change || change.Frame != item) return;
        var after = item.Opacity;
        CancelItemOpacityChange(); // Duplicate mouse-up/DragCompleted/capture-loss events do nothing.
        if (cancel || after == change.Before) return;
        if (item.IsSaved)
            // Enter the saved-edit guard now, before waiting for the preview copy to dispose.
            SavedItemEditTask = EditSavedItemAsync(item, opacity: after,
                previewTask: change.PreviewTask, expectedDocument: change.Document);
        else
        {
            item.Opacity = after;
            _undo.Push(() => item.Opacity = change.Before, () => item.Opacity = after);
        }
    }

    private void CancelItemOpacityChange()
    {
        if (_itemOpacityChange is not { } change) return;
        _itemOpacityChange = null;
        change.Frame.Opacity = change.Before;
        change.Page.SetOpacityPreview(null);
    }

    private void RequestItemOpacityPreview(ItemOpacityChange change)
    {
        ++change.Request;
        if (change.PreviewRunning) return;
        change.PreviewRunning = true;
        change.PreviewTask = RenderAsync();

        async Task RenderAsync()
        {
            var rendered = 0;
            try
            {
                if (!change.Document.Permissions.HasFlag(PdfPermissions.Modify))
                {
                    rendered = change.Request;
                    return;
                }
                // Extracting does not save/mutate the source or expire its item references.
                var bytes = await change.Document.ExtractPagesToBytesAsync([change.Page.Index]);
                await using var preview = await _engine.OpenBytesAsync(bytes, change.Document.FilePath);
                var width = Math.Clamp(change.Page.RenderedPixelWidth > 0 ? change.Page.RenderedPixelWidth
                    : (int)Math.Ceiling(change.Page.DisplayWidth * _dpiScale), 400, 1600);
                var (pixelWidth, pixelHeight) = GetRenderSize(width / _dpiScale,
                    width * change.Page.HeightPoints / change.Page.WidthPoints / _dpiScale);
                while (_itemOpacityChange == change && _document == change.Document && rendered != change.Request)
                {
                    rendered = change.Request;
                    var alpha = change.Frame.Opacity;
                    var item = (await preview.GetPageItemsAsync(0)).Single(i => i.Group == change.Frame.SavedItem!.Group);
                    await preview.SetPageItemOpacityAsync(item.Reference, alpha);
                    var bitmap = await preview.RenderPageAsync(0, pixelWidth, pixelHeight);
                    if (_itemOpacityChange == change && _document == change.Document && rendered == change.Request)
                        change.Page.SetOpacityPreview(BitmapFactory.Create(bitmap));
                }
            }
            catch (Exception ex) when (ex is PdfException or ObjectDisposedException or NotSupportedException
                or InvalidOperationException or ArgumentException)
            {
                // A preview failure never opens a dialog. The single commit reports any edit failure.
                rendered = change.Request;
            }
            finally
            {
                change.PreviewRunning = false;
                // ValueChanged can arrive while the previous preview copy is being disposed.
                if (_itemOpacityChange == change && _document == change.Document && rendered != change.Request)
                    RequestItemOpacityPreview(change);
            }
        }
    }
}
