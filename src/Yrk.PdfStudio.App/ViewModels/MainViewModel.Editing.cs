using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
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
    private double _dragStartRotation;
    private double _dragStartOpacity;
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

    public IRelayCommand StampCommand { get; private set; } = null!;

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
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UndoCommand?.NotifyCanExecuteChanged();
                RedoCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The status bar text. Fixed English texts are translated here; built ones use <see cref="Loc.F"/>.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        internal set => SetProperty(ref _statusMessage, Loc.T(value));
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
            var textWidth = placement.Words is { } words ? TextRendering.Measure(words, _newTextStyle).Width + (2 * TextRendering.Padding) : 40;
            item.Text = placement.Words ?? string.Empty;
            item.Bounds = item.ToBounds(pointX, pointY - (lineHeight / 2), textWidth, lineHeight);
            AttachItem(item); // Recorded for undo once it has text (see CommitText).
            SelectItem(item);
            item.IsEditing = true;
            return;
        }

        var image = placement.Image!;
        var (width, height) = placement.NaturalSize
            ?? (placement.Kind == PlacedItemKind.Picture ? PictureImaging.FitToPage(image, page.WidthPoints, page.HeightPoints) : DefaultSignatureSize(image));
        if (placement.EveryPage)
        {
            // The same spot (as a share of the page) on every page; one Undo takes them all off.
            var all = Pages.Select(p =>
            {
                var copy = PlacedItemViewModel.CreateImage(p.Index, placement.Kind, p.WidthPoints, p.HeightPoints, image, placement.Art, placement.Stamp);
                copy.Bounds = copy.ToBounds((x * p.WidthPoints) - (width / 2), (y * p.HeightPoints) - (height / 2), width, height);
                return copy;
            }).ToList();
            all.ForEach(AttachItem);
            _undo.Push(() => all.ForEach(DetachItem), () => all.ForEach(AttachItem));
            SelectItem(all[page.Index]);
            return;
        }

        var placed = PlacedItemViewModel.CreateImage(page.Index, placement.Kind, page.WidthPoints, page.HeightPoints, image, placement.Art, placement.Stamp);
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
        ++_savedItemSelectionRequest;
        if (_selectedItem == item)
        {
            return;
        }

        CancelItemOpacityChange();

        if (_selectedItem is not null)
        {
            _selectedItem.IsSelected = false;
            if (_selectedItem.IsSaved && _selectedItem.PageIndex < Pages.Count)
                Pages[_selectedItem.PageIndex].Items.Remove(_selectedItem);
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
        _dragStartRotation = item.Rotation;
        _dragStartOpacity = item.Opacity;
    }

    public void MoveItem(PlacedItemViewModel item, double deltaX, double deltaY)
    {
        if (item.IsSaved && !item.CanTransform || IsBusy) return;
        if (item.IsSaved)
        {
            var b = item.Bounds;
            var x = b.X + deltaX / item.PageWidth;
            var y = b.Y + deltaY / item.PageHeight;
            item.Bounds = b with { X = b.Width <= 1 ? Math.Clamp(x, 0, 1 - b.Width) : x,
                Y = b.Height <= 1 ? Math.Clamp(y, 0, 1 - b.Height) : y };
            return;
        }
        item.Bounds = item.ToBounds(item.Left + deltaX, item.Top + deltaY, item.Width, item.Height);
    }

    public void ResizeItem(PlacedItemViewModel item, double deltaX, double deltaY)
    {
        if (item.IsSaved) return; // Step 6 has no resize API.
        if (item.Rotation == 0)
        {
            var width = item.Width + deltaX;
            var height = item.AspectRatio > 0 ? width / item.AspectRatio : item.Height + deltaY;
            item.Bounds = item.ToBounds(item.Left, item.Top, width, height);
            return;
        }

        var angle = item.Rotation * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var localX = cos * deltaX + sin * deltaY;
        var localY = -sin * deltaX + cos * deltaY;
        var ratio = item.AspectRatio;
        var minWidth = ratio > 0 ? Math.Max(PlacedItemViewModel.MinimumSize, PlacedItemViewModel.MinimumSize * ratio) : PlacedItemViewModel.MinimumSize;
        var widthAfter = Math.Max(minWidth, item.Width + localX);
        var heightAfter = ratio > 0 ? widthAfter / ratio : Math.Max(PlacedItemViewModel.MinimumSize, item.Height + localY);
        // The screen-space top-left corner is the anchor. Moving the centre by half the
        // rotated size change leaves that corner fixed, even when the bounds cross a page edge.
        var changeX = (cos * (widthAfter - item.Width) - sin * (heightAfter - item.Height)) / 2;
        var changeY = (sin * (widthAfter - item.Width) + cos * (heightAfter - item.Height)) / 2;
        var left = item.Left + item.Width / 2 + changeX - widthAfter / 2;
        var top = item.Top + item.Height / 2 + changeY - heightAfter / 2;
        item.Bounds = item.ToBounds(left, top, widthAfter, heightAfter);
    }

    public void RotateItem90(PlacedItemViewModel item)
    {
        if (item.IsSaved && !item.CanTransform || IsBusy) return;
        BeginItemDrag(item);
        item.Rotation += 90;
        if (!item.IsSaved) KeepOnPage(item);
        EndItemDrag(item);
    }

    private static void KeepOnPage(PlacedItemViewModel item) =>
        item.Bounds = item.ToBounds(item.Left, item.Top, item.Width, item.Height);

    /// <summary>Updates a rotation gesture; its caller records the whole gesture with Begin/EndItemDrag.</summary>
    public void RotateItem(PlacedItemViewModel item, double degrees, bool snap)
    {
        if (item.IsSaved && !item.CanTransform || IsBusy) return;
        item.Rotation = snap ? Math.Round(degrees / 15, MidpointRounding.AwayFromZero) * 15 : degrees;
        // Fit from the gesture's starting box, so a temporary shrink at 45° is undone back at 0°.
        item.Bounds = _dragStartBounds;
        if (!item.IsSaved) KeepOnPage(item);
    }

    public void ChangeItemOpacity(PlacedItemViewModel item, double opacity)
    {
        if (!double.IsFinite(opacity) || IsBusy || !item.CanAdjustOpacity) return;
        if (_itemOpacityChange is { } change && change.Frame == item)
        {
            item.Opacity = opacity;
            if (item.IsSaved) RequestItemOpacityPreview(change);
            return;
        }
        if (item.IsSaved)
        {
            var requested = Math.Clamp(opacity, 0.1, 1);
            if (item.Opacity != requested) SavedItemEditTask = EditSavedItemAsync(item, opacity: requested);
            return;
        }
        var before = item.Opacity;
        item.Opacity = opacity;
        var after = item.Opacity;
        if (before != after) _undo.Push(() => item.Opacity = before, () => item.Opacity = after);
    }

    public void EndItemDrag(PlacedItemViewModel item)
    {
        if (item.IsSaved)
        {
            var delta = new NormalizedPoint(item.Bounds.X - _dragStartBounds.X, item.Bounds.Y - _dragStartBounds.Y);
            var rotation = item.Rotation - _dragStartRotation;
            var opacity = item.Opacity != _dragStartOpacity ? item.Opacity : (double?)null;
            item.Bounds = _dragStartBounds;
            item.Rotation = _dragStartRotation;
            item.Opacity = _dragStartOpacity;
            if (delta != default || rotation != 0 || opacity is not null)
                SavedItemEditTask = EditSavedItemAsync(item, delta, rotation, opacity);
            return;
        }
        var before = _dragStartBounds;
        var after = item.Bounds;
        var rotationBefore = _dragStartRotation;
        var rotationAfter = item.Rotation;
        var opacityBefore = _dragStartOpacity;
        var opacityAfter = item.Opacity;
        if (before != after || rotationBefore != rotationAfter || opacityBefore != opacityAfter)
        {
            _undo.Push(
                () => { item.Bounds = before; item.Rotation = rotationBefore; item.Opacity = opacityBefore; },
                () => { item.Bounds = after; item.Rotation = rotationAfter; item.Opacity = opacityAfter; });
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

    /// <summary>Changes the words of added text that is not being edited, as one step for undo.</summary>
    public void ReplaceText(PlacedItemViewModel item, string text)
    {
        item.IsEditing = true;
        CommitText(item, text);
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
        StampCommand = new RelayCommand(Stamp);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        UndoCommand = new RelayCommand(_undo.Undo, () => !IsBusy && _undo.CanUndo);
        RedoCommand = new RelayCommand(_undo.Redo, () => !IsBusy && _undo.CanRedo);
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
            PlacedItemKind.Stamp, stamp.Image, (stamp.Size.Width, stamp.Size.Height), "Click where the stamp should go. Press Esc to cancel.", Stamp: stamp));
    }

    private void Stamp()
    {
        if (_document is null || _dialogs.AskStamp(StampSettings.Load()) is not { } request)
        {
            return;
        }

        if (request.ManyFiles)
        {
            _ = BatchAsync(BatchAction.Stamp);
            return;
        }

        var stamp = RubberStamp.Render(request.Design, request.Date);
        BeginPlacement(new Placement(
            PlacedItemKind.Stamp,
            stamp.Image,
            (stamp.Size.Width, stamp.Size.Height),
            request.EveryPage
                ? "Click where the stamp should go: it goes in the same place on every page. Press Esc to cancel."
                : "Click where the stamp should go. Press Esc to cancel.",
            Stamp: stamp,
            EveryPage: request.EveryPage));
    }

    private void DeleteSelected()
    {
        if (DeleteSelectedMeasurement())
        {
            return;
        }

        if (_selectedItem is null && SelectedComment is { } comment)
        {
            _ = DeleteCommentAsync(comment);
            return;
        }

        if (_selectedItem is not { IsEditing: false } item)
        {
            return;
        }

        if (item.IsSaved)
        {
            SavedItemEditTask = EditSavedItemAsync(item, delete: true);
            return;
        }
        DetachItem(item);
        _undo.Push(() => AttachItem(item), () => DetachItem(item));
    }

    private void BeginPlacement(Placement placement)
    {
        if (_document is null || !ConfirmFirstEdit())
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
        if (!_keepVersions) _savedItemSignatureAcknowledgedFor = null;
        ++_savedItemSelectionRequest;
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
        ClearVersions();
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
        // A private version candidate can still fail to save, copy or reopen. Its overlays
        // stay pending in the open document until OpenVersionAsync successfully replaces it.
        var commitToOpenDocument = ReferenceEquals(document, _document);
        foreach (var item in _placedItems.ToList())
        {
            if (keepText && item.IsText)
            {
                continue;
            }

            if (keepText && item.HasStampText)
            {
                // The stamp's frame goes into the page now; its words are written as real text over it (ApplyPlacedText).
                if (!item.FrameCommitted)
                {
                    await document.AddImageAsync(item.PageIndex, ImageConversion.ToPdfImage(item.Stamp!.Frame!), item.Bounds, style: PlacementStyleOf(item));
                    if (commitToOpenDocument)
                    {
                        item.FrameCommitted = true;
                        _documentModified = true;
                    }
                }

                continue;
            }

            if (item.Art is { } art)
            {
                await WriteSignatureAsync(document, item, art);
            }
            else
            {
                await document.AddImageAsync(item.PageIndex, ItemRenderer.Render(item), item.Bounds, style: PlacementStyleOf(item));
            }

            if (commitToOpenDocument)
            {
                DetachItem(item); // Now part of the open page: never add it twice if a later step fails.
                _documentModified = true;
            }
        }

        if (_fieldEdits.Count > 0)
        {
            await document.ApplyFormEditsAsync(_fieldEdits.Values.ToList());
            if (commitToOpenDocument)
            {
                _fieldEdits.Clear();
                _documentModified = true;
            }
        }

        if (!commitToOpenDocument) return;

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

        if (IsDirty && !ConfirmPassword(SavePermission, "save changes to it").Confirmed)
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
        var reopening = false;
        try
        {
            try
            {
                await WriteChangesAsync(document, tempPath);
            }
            catch (Exception ex)
            {
                // Nothing was replaced. The changes stay in the open document, so saving can be retried.
                TryDelete(tempPath);
                _dialogs.ShowError(Loc.F("Couldn't save \"{0}\".\n\n{1}", targetName, ex.Message));
                return false;
            }

            // From here the complete new file exists, so the user's work can no longer be lost.
            reopening = true;
            BeginDocumentReopen();
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
            if (reopening) EndDocumentReopen();
            IsBusy = false;
            if (StatusMessage == Loc.T("Saving…"))
            {
                StatusMessage = string.Empty;
            }
        }
    }

    /// <summary>Writes the document with every change not saved yet to <paramref name="path"/>.</summary>
    private async Task WriteChangesAsync(IPdfDocument document, string path)
    {
        if (document.IsEncrypted)
        {
            await PdfTemporaryFiles.WriteDurableAsync(path, await WriteChangesToBytesAsync(document));
            return;
        }

        var realText = CanWriteRealText;
        var fieldLooks = BuildFieldLooks(document); // Before the fields are committed and reloaded.
        await CommitOverlaysAsync(document, keepText: realText);
        fieldLooks = await PurgeDeletedCommentsAsync(document, fieldLooks);
        await document.SaveAsync(path);

        // PDFium writes every object it has loaded, changed or not (about 1 MB more per save of a filled
        // form); only the objects that really changed are kept.
        var saved = await TrimSaveAsync(document.FilePath, await File.ReadAllBytesAsync(path));
        if (HasTextEdits || _layerDefaultsChanged || _newFields.Count > 0 || _fieldChanges.Count > 0 || (realText && HasPlacedText) || fieldLooks.Count > 0)
        {
            // Text edits, added text, field looks and layer defaults work on the file itself (PDFium cannot shape Thai).
            // Field looks come first: they find widgets by their place in /Annots, which new fields extend.
            saved = ApplyNewFields(ApplyLayerDefaults(ApplyTextEdits(ApplyPlacedText(ApplyFieldLooks(saved, fieldLooks)))));
        }

        await PdfTemporaryFiles.WriteDurableAsync(path, saved);
    }

    // The same pipeline as the file path, with no intermediate writes for protected documents.
    private async Task<byte[]> WriteChangesToBytesAsync(IPdfDocument document)
    {
        var realText = CanWriteRealText;
        var fieldLooks = BuildFieldLooks(document);
        await CommitOverlaysAsync(document, keepText: realText);
        fieldLooks = await PurgeDeletedCommentsAsync(document, fieldLooks);
        var saved = await TrimSaveAsync(document.FilePath, await document.SaveToBytesAsync());
        if (HasTextEdits || _layerDefaultsChanged || _newFields.Count > 0 || _fieldChanges.Count > 0 || (realText && HasPlacedText) || fieldLooks.Count > 0)
            saved = ApplyNewFields(ApplyLayerDefaults(ApplyTextEdits(ApplyPlacedText(ApplyFieldLooks(saved, fieldLooks)))));
        return saved;
    }

    /// <summary>Makes the new form fields real now, so they can be filled before saving (see <see cref="ApplyNowAsync"/>).</summary>
    private Task ApplyNewFieldsAsync() =>
        ApplyNowAsync("Adding the fields…", "Fields ready to fill", "Couldn't add the fields.\n\n{0}", () => _newFields.Count > 0);

    private ViewState CaptureViewState()
    {
        // Where the view is, as a place on a page: while the document reopens the sidebar is gone for a moment, so
        // the fitted zoom (and every offset in pixels) changes before the view comes back.
        var top = _pageTops.Length == 0 ? 0 : PageIndexAt(_scrollOffset);
        var fraction = top < Pages.Count ? (_scrollOffset - _pageTops[top]) / Math.Max(1, Pages[top].DisplayHeight) : 0;
        return new(_zoomMode, _zoom, _scrollOffset, _currentPageIndex, top, fraction);
    }

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
        catch (Exception)
        {
        }
    }

    private sealed record Placement(
        PlacedItemKind Kind, BitmapSource? Image, (double Width, double Height)? NaturalSize, string Hint, SignatureArt? Art = null, bool LockAfterwards = false,
        RenderedStamp? Stamp = null, bool EveryPage = false, string? Words = null);

    private sealed record ViewState(ZoomMode ZoomMode, double Zoom, double ScrollOffset, int PageIndex, int TopPage, double TopFraction);
}
