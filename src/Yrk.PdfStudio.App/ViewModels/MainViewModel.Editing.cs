using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Fill &amp; Sign: form values, placed signatures/stamps/text, undo and saving.</summary>
public sealed partial class MainViewModel
{
    private const double DefaultSignatureWidth = 150; // points
    private const double MaxSignatureHeight = 60;     // points

    private readonly ISignatureStore _signatureStore;
    private readonly List<PlacedItemViewModel> _placedItems = [];
    private readonly Dictionary<(int PageIndex, int AnnotIndex), PdfFormFieldEdit> _fieldEdits = [];
    private readonly UndoStack _undo = new();
    private string? _password;
    private Placement? _placement;
    private PlacedItemViewModel? _selectedItem;
    private NormalizedRect _dragStartBounds;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    /// <summary>Asks the view to finish in-place editing (commit text boxes) before saving.</summary>
    public event EventHandler? CommitEditsRequested;

    /// <summary>Saved signatures, shared by all tabs (a new one shows up in every tab).</summary>
    public ObservableCollection<SavedSignature> SavedSignatures { get; private set; } = [];

    private static ObservableCollection<SavedSignature>? s_savedSignatures;

    public IAsyncRelayCommand SaveCommand { get; private set; } = null!;

    public IAsyncRelayCommand SaveAsCommand { get; private set; } = null!;

    public IRelayCommand AddTextCommand { get; private set; } = null!;

    public IRelayCommand NewSignatureCommand { get; private set; } = null!;

    public IRelayCommand<SavedSignature> PlaceSignatureCommand { get; private set; } = null!;

    public IRelayCommand<SavedSignature> DeleteSignatureCommand { get; private set; } = null!;

    public IRelayCommand CertifiedCopyCommand { get; private set; } = null!;

    public IRelayCommand DeleteSelectedCommand { get; private set; } = null!;

    public IRelayCommand UndoCommand { get; private set; } = null!;

    public IRelayCommand RedoCommand { get; private set; } = null!;

    public IRelayCommand CancelCommand { get; private set; } = null!;

    /// <summary>True when there are changes that are not yet written to the file.</summary>
    public bool IsDirty => _documentModified || _placedItems.Count > 0 || _fieldEdits.Count > 0 || _fieldLooks.Count > 0 || HasTextEdits || _layerDefaultsChanged || _newFields.Count > 0 || _fieldChanges.Count > 0;

    public bool IsPlacing => _placement is not null || _pendingDigitalSign is not null;

    public string? PlacementHint => _pendingDigitalSign is not null
        ? Loc.T("Drag a box where the signature goes, or click for the default size. Press Esc to cancel.")
        : _placement is { } placement ? Loc.T(placement.Hint) : null;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>The status bar text. Fixed English texts are translated here; built ones use <see cref="Loc.F"/>.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, Loc.T(value));
    }

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels (or saving fails).</summary>
    public async Task<bool> ConfirmDiscardOrSaveAsync()
    {
        if (_document is null || !IsDirty)
        {
            return true;
        }

        return _dialogs.AskSaveChanges(Path.GetFileName(_document.FilePath)) switch
        {
            SaveChangesChoice.Save => await SaveToAsync(_document.FilePath),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    // --- Page interaction (called by the view) ------------------------------------------------

    /// <summary>A click on empty page area, as a fraction of the page size.</summary>
    public void OnPageClicked(PageViewModel page, double x, double y)
    {
        if (_placement is not { } placement)
        {
            SelectItem(null);
            return;
        }

        EndPlacement();
        var pointX = x * page.WidthPoints;
        var pointY = y * page.HeightPoints;
        if (placement.Kind == PlacedItemKind.Text)
        {
            var item = PlacedItemViewModel.CreateText(page.Index, page.WidthPoints, page.HeightPoints, _newTextStyle);
            var lineHeight = TextRendering.Measure("Ag", _newTextStyle).Height;
            item.Bounds = item.ToBounds(pointX, pointY - (lineHeight / 2), 40, lineHeight);
            AttachItem(item); // Recorded for undo once it has text (see CommitText).
            SelectItem(item);
            item.IsEditing = true;
            return;
        }

        var image = placement.Image!;
        var (width, height) = placement.NaturalSize ?? DefaultSignatureSize(image);
        var placed = PlacedItemViewModel.CreateImage(page.Index, placement.Kind, page.WidthPoints, page.HeightPoints, image, placement.Art);
        placed.Bounds = placed.ToBounds(pointX - (width / 2), pointY - (height / 2), width, height);
        AttachItem(placed);
        _undo.Push(() => DetachItem(placed), () => AttachItem(placed));
        SelectItem(placed);
        if (placement.LockAfterwards)
        {
            // Sign-and-lock: the digital signature saves the document with the handwritten one in it.
            App.Current?.Dispatcher.BeginInvoke(async () => await StartDigitalSignAsync(lockOnly: true));
        }
    }

    public void SelectItem(PlacedItemViewModel? item)
    {
        if (_selectedItem == item)
        {
            return;
        }

        if (_selectedItem is not null)
        {
            _selectedItem.IsSelected = false;
        }

        _selectedItem = item;
        if (item is not null)
        {
            item.IsSelected = true;
        }

        UpdateTextStyleTarget();
    }

    public void BeginItemDrag(PlacedItemViewModel item)
    {
        SelectItem(item);
        _dragStartBounds = item.Bounds;
    }

    public void MoveItem(PlacedItemViewModel item, double deltaX, double deltaY) =>
        item.Bounds = item.ToBounds(item.Left + deltaX, item.Top + deltaY, item.Width, item.Height);

    public void ResizeItem(PlacedItemViewModel item, double deltaX, double deltaY)
    {
        var width = item.Width + deltaX;
        var height = item.AspectRatio > 0 ? width / item.AspectRatio : item.Height + deltaY;
        item.Bounds = item.ToBounds(item.Left, item.Top, width, height);
    }

    public void EndItemDrag(PlacedItemViewModel item)
    {
        var before = _dragStartBounds;
        var after = item.Bounds;
        if (before != after)
        {
            _undo.Push(() => item.Bounds = before, () => item.Bounds = after);
        }
    }

    public void BeginEditText(PlacedItemViewModel item)
    {
        if (item.IsText)
        {
            SelectItem(item);
            item.IsEditing = true;
        }
    }

    public void CommitText(PlacedItemViewModel item, string text)
    {
        if (!item.IsEditing)
        {
            return;
        }

        item.IsEditing = false;
        text = text.TrimEnd();
        var oldText = item.Text;
        var oldBounds = item.Bounds;
        if (text == oldText)
        {
            if (text.Length == 0)
            {
                DetachItem(item); // New text box left empty.
            }

            return;
        }

        if (text.Length == 0)
        {
            DetachItem(item);
            _undo.Push(() => { SetText(item, oldText, oldBounds); AttachItem(item); }, () => DetachItem(item));
            return;
        }

        var size = TextRendering.Measure(text, item.Style);
        var newBounds = item.ToBounds(item.Left, item.Top, size.Width, size.Height);
        SetText(item, text, newBounds);
        if (oldText.Length == 0)
        {
            _undo.Push(() => DetachItem(item), () => { SetText(item, text, newBounds); AttachItem(item); });
        }
        else
        {
            _undo.Push(() => SetText(item, oldText, oldBounds), () => SetText(item, text, newBounds));
        }
    }

    // --- Commands -----------------------------------------------------------------------------

    private void InitializeEditing()
    {
        SaveCommand = new AsyncRelayCommand(() =>
            _document is null ? Task.CompletedTask
            : _convertedFrom is not null ? SaveDrawingAsPdfAsync()
            : IsDirty ? SaveToAsync(_document.FilePath) : Task.CompletedTask);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync);
        AddTextCommand = new RelayCommand(() => BeginPlacement(new Placement(
            PlacedItemKind.Text, null, null, "Click where you want to type. Press Esc to cancel.")));
        NewSignatureCommand = new RelayCommand(NewSignature);
        PlaceSignatureCommand = new RelayCommand<SavedSignature>(signature =>
        {
            if (signature is not null)
            {
                BeginSignaturePlacement(signature.Art);
            }
        });
        DeleteSignatureCommand = new RelayCommand<SavedSignature>(DeleteSignature);
        CertifiedCopyCommand = new RelayCommand(CertifiedCopy);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        UndoCommand = new RelayCommand(_undo.Undo, () => _undo.CanUndo);
        RedoCommand = new RelayCommand(_undo.Redo, () => _undo.CanRedo);
        CancelCommand = new RelayCommand(() =>
        {
            EndPlacement();
            CancelDigitalPlacement();
            CancelMeasurementDraft();
            SelectItem(null);
            ClearTextSelection();
            SelectedComment = null;
            if (IsFieldTool(CurrentTool) || CurrentTool == AnnotationTool.FieldEdit)
            {
                CurrentTool = AnnotationTool.Select; // Esc puts the form field tool down.
            }
        });

        _undo.Changed += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };

        if (s_savedSignatures is { } shared)
        {
            SavedSignatures = shared;
            return;
        }

        SavedSignatures = s_savedSignatures = [];
        try
        {
            foreach (var signature in _signatureStore.LoadAll())
            {
                SavedSignatures.Add(signature);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saved signatures are a convenience; the app works without them.
        }
    }

    private void NewSignature()
    {
        if (_document is null || _dialogs.CreateSignature(PrepareSignaturePdfAsync) is not { } result)
        {
            return;
        }

        if (result.Remember)
        {
            try
            {
                SavedSignatures.Insert(0, _signatureStore.Save(result.Art));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _dialogs.ShowError(Loc.F("The signature could not be saved for later use.\n\n{0}", ex.Message));
            }
        }

        BeginSignaturePlacement(result.Art, result.LockWithDigitalId);
    }

    /// <param name="lockAfterwards">Once placed, continue with signing the document with a digital ID.</param>
    private void BeginSignaturePlacement(SignatureArt art, bool lockAfterwards = false)
    {
        if (_document is not null)
        {
            BeginPlacement(new Placement(
                PlacedItemKind.Signature,
                art.Preview,
                null,
                lockAfterwards
                    ? "Click on the page where you want to sign. Then choose your digital ID to lock the document. Press Esc to cancel."
                    : "Click on the page where you want to sign. Press Esc to cancel.",
                art,
                lockAfterwards));
        }
    }

    private void DeleteSignature(SavedSignature? signature)
    {
        if (signature is null)
        {
            return;
        }

        try
        {
            _signatureStore.Delete(signature.Id);
            SavedSignatures.Remove(signature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Loc.F("The signature could not be deleted.\n\n{0}", ex.Message));
        }
    }

    private void CertifiedCopy()
    {
        if (_document is null || _dialogs.AskCertifiedCopy(SavedSignatures) is not { } options)
        {
            return;
        }

        var stamp = StampRenderer.RenderCertifiedCopy(options);
        BeginPlacement(new Placement(
            PlacedItemKind.Stamp, stamp.Image, (stamp.Size.Width, stamp.Size.Height), "Click where the stamp should go. Press Esc to cancel."));
    }

    private void DeleteSelected()
    {
        if (_selectedItem is null && SelectedComment is { } comment)
        {
            _ = DeleteCommentAsync(comment);
            return;
        }

        if (_selectedItem is not { IsEditing: false } item)
        {
            return;
        }

        DetachItem(item);
        _undo.Push(() => AttachItem(item), () => DetachItem(item));
    }

    private void BeginPlacement(Placement placement)
    {
        if (_document is null)
        {
            return;
        }

        CancelDigitalPlacement();
        SelectItem(null);
        _placement = placement;
        OnPropertyChanged(nameof(IsPlacing));
        OnPropertyChanged(nameof(PlacementHint));
        UpdateTextStyleTarget();
    }

    private void EndPlacement()
    {
        if (_placement is null)
        {
            return;
        }

        _placement = null;
        OnPropertyChanged(nameof(IsPlacing));
        OnPropertyChanged(nameof(PlacementHint));
        UpdateTextStyleTarget();
    }

    // --- Items and fields ---------------------------------------------------------------------

    private void AttachItem(PlacedItemViewModel item)
    {
        if (!_placedItems.Contains(item))
        {
            _placedItems.Add(item);
        }

        if (item.PageIndex < Pages.Count && !Pages[item.PageIndex].Items.Contains(item))
        {
            Pages[item.PageIndex].Items.Add(item);
        }

        OnEditsChanged();
    }

    private void DetachItem(PlacedItemViewModel item)
    {
        item.IsEditing = false;
        if (_selectedItem == item)
        {
            SelectItem(null);
        }

        _placedItems.Remove(item);
        if (item.PageIndex < Pages.Count)
        {
            Pages[item.PageIndex].Items.Remove(item);
        }

        OnEditsChanged();
    }

    private static void SetText(PlacedItemViewModel item, string text, NormalizedRect bounds)
    {
        item.Text = text;
        item.Bounds = bounds;
    }

    private void RequestFormFields(PageViewModel page)
    {
        if (_document is not { HasForm: true } document || page.FieldsRequested)
        {
            return;
        }

        page.FieldsRequested = true;
        _ = LoadFormFieldsAsync(document, page);
    }

    /// <summary>The turned text fields of the open file, read once from the saved file when a page has text fields.</summary>
    private (IPdfDocument Document, Task<IReadOnlyDictionary<(int PageIndex, int AnnotIndex), int>> Rotations)? _fieldRotations;

    private Task<IReadOnlyDictionary<(int PageIndex, int AnnotIndex), int>> FieldRotationsAsync(IPdfDocument document)
    {
        if (_fieldRotations is not { } cached || cached.Document != document)
        {
            var path = document.FilePath;
            cached = (document, Task.Run(() =>
            {
                try
                {
                    return PdfFieldAppearance.TextRotations(File.ReadAllBytes(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (IReadOnlyDictionary<(int PageIndex, int AnnotIndex), int>)new Dictionary<(int PageIndex, int AnnotIndex), int>();
                }
            }));
            _fieldRotations = cached;
        }

        return cached.Rotations;
    }

    private async Task LoadFormFieldsAsync(IPdfDocument document, PageViewModel page)
    {
        try
        {
            var fields = await document.GetFormFieldsAsync(page.Index);
            var rotations = fields.Any(f => f.Type == PdfFormFieldType.Text) ? await FieldRotationsAsync(document) : null;
            if (document != _document)
            {
                return;
            }

            foreach (var field in fields)
            {
                if (field.IsReadOnly || field.Type is PdfFormFieldType.Unknown or PdfFormFieldType.Signature)
                {
                    continue;
                }

                var rotation = rotations is not null && rotations.TryGetValue((field.PageIndex, field.AnnotIndex), out var turn) ? turn : 0;
                var viewModel = new FormFieldViewModel(field, page.WidthPoints, page.HeightPoints, OnFieldChanged, rotation);
                if (field.Type == PdfFormFieldType.PushButton && !viewModel.IsImageField)
                {
                    continue; // Other buttons run scripts, which this app does not.
                }

                if (_fieldEdits.TryGetValue(viewModel.Key, out var pending))
                {
                    viewModel.Restore(pending);
                }

                RestoreFieldLook(viewModel);

                page.Fields.Add(viewModel);
            }
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            // A page whose fields cannot be read is still readable.
        }
    }

    private void OnFieldChanged(FormFieldViewModel field)
    {
        if (field.ToEdit() is { } edit)
        {
            _fieldEdits[field.Key] = edit;
        }
        else
        {
            _fieldEdits.Remove(field.Key);
        }

        TrackFieldLook(field);
        OnEditsChanged();
    }

    private void OnEditsChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TabTitle));
    }

    private void ClearEdits()
    {
        EndPlacement();
        SelectItem(null);
        foreach (var page in Pages)
        {
            page.Items.Clear();
        }

        _placedItems.Clear();
        _fieldEdits.Clear();
        ClearFieldLooks();
        ClearTextEdits();
        ClearNewFields();
        _undo.Clear();
        ReleasePageSnapshots();
        _documentModified = false;
        OnEditsChanged();
    }

    /// <summary>
    /// Writes pending overlays and field values into the open document (in memory). Page tools call
    /// this first, because they renumber pages; saving calls it before writing the file.
    /// </summary>
    /// <param name="keepText">Leave added text as overlays: the caller writes it as real text into the saved
    /// bytes (<see cref="ApplyPlacedText"/>). Page tools flatten it here instead.</param>
    private async Task CommitOverlaysAsync(IPdfDocument document, bool keepText = false)
    {
        if (_placedItems.Count == 0 && _fieldEdits.Count == 0)
        {
            return;
        }

        EndPlacement();
        foreach (var item in _placedItems.ToList())
        {
            if (keepText && item.IsText)
            {
                continue;
            }

            if (item.Art is { } art)
            {
                await WriteSignatureAsync(document, item, art);
            }
            else
            {
                await document.AddImageAsync(item.PageIndex, ItemRenderer.Render(item), item.Bounds);
            }

            DetachItem(item); // Now part of the page: never add it twice, even if a later step fails.
            _documentModified = true;
        }

        if (_fieldEdits.Count > 0)
        {
            await document.ApplyFormEditsAsync(_fieldEdits.Values.ToList());
            _fieldEdits.Clear();
            _documentModified = true;
        }

        // Overlay undo steps refer to items that are now page content.
        _undo.Clear();
        foreach (var page in Pages)
        {
            page.Fields.Clear();
            page.FieldsRequested = false; // Reload with the committed values as the new baseline.
            page.ReleaseImage();
        }

        OnEditsChanged();
        UpdateVisiblePages();
    }

    private static (double Width, double Height) DefaultSignatureSize(BitmapSource image)
    {
        var aspect = (double)image.PixelWidth / Math.Max(1, image.PixelHeight);
        var width = DefaultSignatureWidth;
        var height = width / aspect;
        if (height > MaxSignatureHeight)
        {
            height = MaxSignatureHeight;
            width = height * aspect;
        }

        return (width, height);
    }

    // --- Saving -------------------------------------------------------------------------------

    private async Task SaveAsAsync()
    {
        if (_document is null || _dialogs.PickSavePdfFile(_document.FilePath) is not { } path)
        {
            return;
        }

        await SaveToAsync(path);
    }

    /// <summary>
    /// Writes every change into the document, saves it to a temporary file next to the target as an
    /// incremental update, then swaps the file in and reopens it. The original is only replaced once
    /// the new file is completely written.
    /// </summary>
    private async Task<bool> SaveToAsync(string targetPath)
    {
        if (_document is null || IsBusy || (IsDirty && !await EnsureAllowedAsync(SavePermission, "save changes to it")))
        {
            return false;
        }

        if (_document is not { } document)
        {
            return false;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty);
        EndPlacement();
        IsBusy = true;
        StatusMessage = "Saving…";
        var restore = CaptureViewState();
        var targetName = Path.GetFileName(targetPath);
        var tempPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(targetPath))!,
            $"~{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.yrk-saving");
        try
        {
            try
            {
                var realText = CanWriteRealText(document);
                var fieldLooks = BuildFieldLooks(document); // Before the fields are committed and reloaded.
                await CommitOverlaysAsync(document, keepText: realText);
                await document.SaveAsync(tempPath);

                // PDFium writes every object it has loaded, changed or not (about 1 MB more per save of a filled
                // form); only the objects that really changed are kept.
                var saved = await TrimSaveAsync(document.FilePath, await File.ReadAllBytesAsync(tempPath));
                if (HasTextEdits || _layerDefaultsChanged || _newFields.Count > 0 || _fieldChanges.Count > 0 || (realText && HasPlacedText) || fieldLooks.Count > 0)
                {
                    // Text edits, added text, field looks and layer defaults work on the file itself (PDFium cannot shape Thai).
                    // Field looks come first: they find widgets by their place in /Annots, which new fields extend.
                    saved = ApplyNewFields(ApplyLayerDefaults(ApplyTextEdits(ApplyPlacedText(ApplyFieldLooks(saved, fieldLooks)))));
                }

                await File.WriteAllBytesAsync(tempPath, saved);
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                // Nothing was replaced. The changes stay in the open document, so saving can be retried.
                TryDelete(tempPath);
                _dialogs.ShowError(Loc.F("Couldn't save \"{0}\".\n\n{1}", targetName, ex.Message));
                return false;
            }

            // From here the complete new file exists, so the user's work can no longer be lost.
            await ReleaseDocumentAsync();
            var savedPath = targetPath;
            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                savedPath = UniqueSiblingPath(targetPath, "recovered");
                File.Move(tempPath, savedPath);
                _dialogs.ShowError(Loc.F(
                    "Couldn't replace \"{0}\". Is it open in another program?\n\n{1}\n\nYour changes were saved to \"{2}\" instead.",
                    targetName,
                    ex.Message,
                    Path.GetFileName(savedPath)));
            }

            ClearEdits();
            await OpenDocumentAsync(savedPath, _password, restore, keepEdits: false);
            RecentFiles.Add(savedPath);
            StatusMessage = Loc.F("Saved {0}", Path.GetFileName(savedPath));
            return savedPath == targetPath;
        }
        finally
        {
            IsBusy = false;
            if (StatusMessage == Loc.T("Saving…"))
            {
                StatusMessage = string.Empty;
            }
        }
    }

    private ViewState CaptureViewState() => new(_zoomMode, _zoom, _scrollOffset, _currentPageIndex);

    /// <summary>The saved bytes with PDFium's unchanged objects left out; the bytes as they are if that is not possible.</summary>
    private static async Task<byte[]> TrimSaveAsync(string originalPath, byte[] saved)
    {
        try
        {
            var original = await File.ReadAllBytesAsync(originalPath);
            return await Task.Run(() => PdfFiles.TrimIncrementalSave(original, saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfException)
        {
            return saved;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Placement(
        PlacedItemKind Kind, BitmapSource? Image, (double Width, double Height)? NaturalSize, string Hint, SignatureArt? Art = null, bool LockAfterwards = false);

    private sealed record ViewState(ZoomMode ZoomMode, double Zoom, double ScrollOffset, int PageIndex);
}
