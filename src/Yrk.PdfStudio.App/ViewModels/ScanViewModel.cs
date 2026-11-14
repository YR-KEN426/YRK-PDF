using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.ViewModels;

public enum ScanAdjustmentScope { ThisPage, Selected, AllPages }

public sealed class ScanViewModel(ScanSession session, IScannerService scanner, Dispatcher dispatcher) : ObservableObject
{
    public ScanSession Session { get; } = session;
    public ObservableCollection<ScanPageEntry> Pages { get; } = [];
    public HashSet<string> Notices { get; } = [];
    public ScanSettings Settings { get; set; } = new();
    public string PaperSize { get; set; } = "Original";
    public ScanPaperSize? CustomPaperSize { get; set; }
    public ScanPaperSize? SelectedPaper => PaperSize == "Custom" ? CustomPaperSize : ScanPaperSizes.Standard.FirstOrDefault(p => p.Id == PaperSize);
    private PageState[] _savedPages = [];
    private ScanSettings? _savedSettings;
    private int PendingPageCount => Pages.Count + (CardFront is null ? 0 : 1) + (CardBack is null ? 0 : 1);
    public int UnsavedPageCount => HasUnsavedPages ? PendingPageCount : 0;
    public bool HasUnsavedPages => PendingPageCount > 0 && (CardFront is not null || CardBack is not null ||
        _savedSettings != Settings || !_savedPages.SequenceEqual(Snapshot().Pages.Select(p => p with { Selected = false, OrientationCheckedRotation = null })));
    public void MarkSaved()
    {
        _savedPages = Snapshot().Pages.Select(p => p with { Selected = false, OrientationCheckedRotation = null }).ToArray();
        _savedSettings = Settings;
    }
    public ScanPreset Preset { get; private set; } = ScanPreset.ColorDocument;
    public IReadOnlyList<ScanPageEntry> SelectedPages => Pages.Where(p => p.IsSelected).ToArray();
    private ScanPageEntry? _current;
    public ScanPageEntry? Current { get => _current; private set => SetProperty(ref _current, value); }
    public ScanAdjustmentScope AdjustmentScope { get; private set; }
    public bool CanAdjustSelected => SelectedPages.Count >= 2;
    private ScanPageAdjustments? _allPageAdjustments;
    private bool _adjustmentGesture, _gestureRemembered;
    public void SetAdjustmentScope(ScanAdjustmentScope scope)
    {
        EndAdjustmentGesture();
        AdjustmentScope = scope == ScanAdjustmentScope.Selected && !CanAdjustSelected ? ScanAdjustmentScope.ThisPage : scope;
        _allPageAdjustments = AdjustmentScope == ScanAdjustmentScope.AllPages ? Current?.Page.Adjustments ?? new() : null;
    }
    private void UpdateSelectionScope()
    {
        if (CanAdjustSelected) SetAdjustmentScope(ScanAdjustmentScope.Selected);
        else if (!CanAdjustSelected && AdjustmentScope == ScanAdjustmentScope.Selected) SetAdjustmentScope(ScanAdjustmentScope.ThisPage);
    }
    public void BeginAdjustmentGesture() { _adjustmentGesture = true; _gestureRemembered = false; }
    public void EndAdjustmentGesture() { _adjustmentGesture = false; _gestureRemembered = false; }
    public IReadOnlyList<ScanPageEntry> Adjust(Func<ScanPageAdjustments, ScanPageAdjustments> change)
    {
        var targets = AdjustmentScope switch
        {
            ScanAdjustmentScope.AllPages => Pages.ToArray(),
            ScanAdjustmentScope.Selected => SelectedPages.ToArray(),
            _ => Current is { } current ? new[] { current } : [],
        };
        var edits = targets.Select(p => change(p.Page.Adjustments)).ToArray();
        foreach (var edit in edits) edit.Validate();
        var inherited = _allPageAdjustments is { } all ? change(all) : null;
        if (inherited is not null) inherited.Validate();
        if (!targets.Where((p, i) => p.Page.Adjustments != edits[i]).Any() && inherited == _allPageAdjustments) return targets;
        if (!_adjustmentGesture || !_gestureRemembered) { Remember(); _gestureRemembered = _adjustmentGesture; }
        for (var i = 0; i < targets.Length; i++) targets[i].Page.Adjustments = edits[i];
        _allPageAdjustments = inherited;
        return targets;
    }
    private int _anchor;
    private readonly Stack<EditorState> _undo = new(), _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public long EstimatedBytes => Pages.Sum(p => p.EncodedBytes);
    public string SizeSummary => Pages.Count == 1
        ? Loc.F("{0} page · approximately {1:0.00} MB", Pages.Count, EstimatedBytes / 1_000_000.0)
        : Loc.F("{0} pages · approximately {1:0.00} MB", Pages.Count, EstimatedBytes / 1_000_000.0);
    public bool AutoOrientationEnabled { get; private set; }
    private bool _orientationConsentAsked;
    private bool _orientationConsented;

    public async Task SetAutoOrientationAsync(bool enabled, OcrLanguageData data, Func<Task<bool>> consent, CancellationToken ct = default)
    {
        AdjustmentMessage = string.Empty;
        AutoOrientationEnabled = false;
        if (!enabled) return;
        AutoOrientationEnabled = await data.EnsureOrientationDataAsync(async () =>
        {
            if (_orientationConsentAsked) return _orientationConsented;
            _orientationConsentAsked = true;
            return _orientationConsented = await consent();
        }, ct);
        if (AutoOrientationEnabled)
        {
            var targets = AdjustmentScope switch
            {
                ScanAdjustmentScope.AllPages => Pages.ToArray(),
                ScanAdjustmentScope.Selected => SelectedPages.ToArray(),
                _ => Current is { } current ? new[] { current } : [],
            };
            var remembered = false;
            foreach (var entry in targets)
            {
                await AutoOrientAsync(entry, () => { if (!remembered) { Remember(); remembered = true; } }, ct);
                await RefreshAsync(entry);
            }
        }
    }
    public string AdjustmentMessage { get; private set; } = string.Empty;
    internal ScanPageProcessor Processor { get; set; } = new(new WpfImageCodec(), log: App.LogMessage);
    public ScanPageEntry? GlassPreview { get; private set; }
    public ScanPageEntry? CardFront { get; private set; }
    public ScanPageEntry? CardBack { get; private set; }
    private ScanPageEntry? _cardCropRequired;
    public ScanPageEntry? TemporaryPreview => _cardCropRequired ?? CardBack ?? CardFront ?? GlassPreview;

    public void ClearGlassPreview()
    {
        if (GlassPreview is { } entry) Session.DeleteTemporary(entry.Page);
        GlassPreview = null;
    }

    public void CancelCard()
    {
        if (CardFront is { } front) Session.DeleteTemporary(front.Page);
        if (CardBack is { } back) Session.DeleteTemporary(back.Page);
        CardFront = CardBack = null;
        _cardCropRequired = null;
    }

    private async Task<ScanPageEntry> CaptureTemporaryAsync(ScanDevice device, ScanSettings settings, CancellationToken ct)
    {
        ScanSessionPage? saved = null;
        try
        {
            await scanner.ScanAsync(device, settings, new PageSink(page =>
            {
                saved ??= Session.AddTemporary(page with { ColorMode = settings.ColorMode }, ct);
            }), ct);
            ct.ThrowIfCancellationRequested();
            if (saved is null) throw new InvalidOperationException(Loc.T("No scanned page was returned."));
            foreach (var notice in saved.Notices) Notices.Add(TranslateNotice(notice));
            return new(saved);
        }
        catch (ScannerException ex)
        {
            if (saved is not null) Session.DeleteTemporary(saved);
            throw await ScannerFailure.ResolveAsync(scanner, device, ex, ct);
        }
        catch { if (saved is not null) Session.DeleteTemporary(saved); throw; }
    }

    public async Task PreviewGlassAsync(ScanDevice device, CancellationToken ct = default)
    {
        ClearGlassPreview();
        Settings = Settings with { Region = null };
        GlassPreview = await CaptureTemporaryAsync(device, Settings with
        { Dpi = 75, Source = ScanSource.Flatbed, Region = null, AutoCrop = false, AutoStraighten = false }, ct);
    }

    public async Task ScanCardAsync(ScanDevice device, CancellationToken ct = default)
    {
        ClearGlassPreview();
        try
        {
            var settings = Settings with { Source = ScanSource.Flatbed, Region = null, AutoStraighten = false, AutoCrop = true };
            if (CardBack is not null)
            {
                var front = Session.Read(CardFront!.Page);
                var back = Session.Read(CardBack.Page);
                _cardCropRequired = !ScanPageProcessor.CanComposeCardSide(front, CardFront.Page.Crop, settings.Dpi) ? CardFront
                    : !ScanPageProcessor.CanComposeCardSide(back, CardBack.Page.Crop, settings.Dpi) ? CardBack : null;
                if (_cardCropRequired is not null)
                {
                    AdjustmentMessage = _cardCropRequired == CardFront
                        ? Loc.T("Front card edges were not found. Adjust the crop to fit the card, then press Continue.")
                        : Loc.T("Back card edges were not found. Adjust the crop to fit the card, then press Continue.");
                    return;
                }
                var composed = await Task.Run(() => ScanPageProcessor.ComposeCard(front, CardFront.Page.Crop,
                    back, CardBack.Page.Crop, settings.Dpi, ct), ct);
                Remember();
                var saved = Session.Add(composed with { ColorMode = settings.ColorMode }, ct);
                saved.Crop = new(0, 0, 1, 1); // Keep the A4 canvas through the normal processing pipeline.
                if (AdjustmentScope == ScanAdjustmentScope.AllPages && _allPageAdjustments is { } all) saved.Adjustments = all;
                var entry = new ScanPageEntry(saved);
                Pages.Add(entry); Select(Pages.Count - 1); Renumber();
                CancelCard();
                await RefreshAsync(entry);
                AdjustmentMessage = string.Empty;
                return;
            }
            var side = await CaptureTemporaryAsync(device, settings, ct);
            if (CardFront is null) CardFront = side; else CardBack = side;
            side.Page.Adjustments = new(Straighten: false);
            side.Page.Crop = (await Task.Run(() => Processor.Analyze(Session.Read(side.Page), settings, ct: ct), ct)).Keep;
            AdjustmentMessage = side.Page.Crop is null ? Loc.T("No edges to crop were found") : string.Empty;
        }
        catch (InvalidOperationException) when (CardBack is not null && !ct.IsCancellationRequested) { throw; }
        catch { CancelCard(); throw; }
    }

    public void ApplyPreset(ScanPreset preset)
    {
        Preset = preset;
        Settings = preset switch
        {
            ScanPreset.BlackAndWhiteDocument => Settings with { Dpi = 300, ColorMode = ScanColorMode.BlackAndWhite, JpegQuality = 80 },
            ScanPreset.ColorDocument => Settings with { Dpi = 200, ColorMode = ScanColorMode.Color, JpegQuality = 80 },
            ScanPreset.Receipt => Settings with { Dpi = 300, ColorMode = ScanColorMode.Grayscale, JpegQuality = 80 },
            ScanPreset.Photo => Settings with { Dpi = 300, ColorMode = ScanColorMode.Color, JpegQuality = 90 },
            ScanPreset.TwoSidedCard => Settings with { Dpi = 300, ColorMode = ScanColorMode.Color, Source = ScanSource.Flatbed, Region = null },
            _ => Settings,
        };
    }

    public void Select(int index, bool control = false, bool shift = false)
    {
        if ((uint)index >= Pages.Count) return;
        EndAdjustmentGesture();
        if (!control) foreach (var page in Pages) page.IsSelected = false;
        if (shift)
            for (var i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index) && i < Pages.Count; i++) Pages[i].IsSelected = true;
        else { Pages[index].IsSelected = !control || !Pages[index].IsSelected; _anchor = index; }
        Current = Pages[index];
        UpdateSelectionScope();
    }

    public void Navigate(int delta, bool extend = false) => Select(Math.Clamp(Pages.IndexOf(Current!) + delta, 0, Math.Max(0, Pages.Count - 1)), shift: extend);
    private sealed record PageState(ScanPageEntry Entry, int Rotation, NormalizedRect? Crop, bool Selected, ScanPageAdjustments Adjustments, int? OrientationCheckedRotation);
    private sealed record EditorState(PageState[] Pages, ScanPageAdjustments? AllPageAdjustments);
    private EditorState Snapshot() => new(Pages.Select(p => new PageState(p, p.Page.Rotation, p.Page.Crop, p.IsSelected, p.Page.Adjustments, p.OrientationCheckedRotation)).ToArray(), _allPageAdjustments);
    private void Remember() { _undo.Push(Snapshot()); _redo.Clear(); }
    private async Task RestoreAsync(EditorState state)
    {
        EndAdjustmentGesture();
        _allPageAdjustments = state.AllPageAdjustments;
        Pages.Clear();
        foreach (var s in state.Pages)
        {
            s.Entry.Page.Rotation = s.Rotation; s.Entry.Page.Crop = s.Crop; s.Entry.IsSelected = s.Selected;
            s.Entry.Page.Adjustments = s.Adjustments;
            s.Entry.OrientationCheckedRotation = s.OrientationCheckedRotation;
            Pages.Add(s.Entry);
            await RefreshAsync(s.Entry);
        }
        var selected = SelectedPages;
        Current = selected.Count > 0 ? selected[0] : Pages.Count > 0 ? Pages[0] : null;
        _anchor = Math.Max(0, Pages.IndexOf(Current!));
        if (!CanAdjustSelected && AdjustmentScope == ScanAdjustmentScope.Selected) SetAdjustmentScope(ScanAdjustmentScope.ThisPage);
        Renumber();
    }
    public async Task UndoAsync() { if (!CanUndo) return; _redo.Push(Snapshot()); await RestoreAsync(_undo.Pop()); }
    public async Task RedoAsync() { if (!CanRedo) return; _undo.Push(Snapshot()); await RestoreAsync(_redo.Pop()); }
    public async Task RotateSelectedAsync(int degrees)
    {
        if (SelectedPages.Count == 0) return;
        Remember();
        foreach (var entry in SelectedPages)
        {
            RotateEntry(entry, degrees);
            await RefreshAsync(entry);
        }
    }
    public async Task CropSelectedAsync(NormalizedRect? crop)
    {
        var targets = AdjustmentScope switch
        {
            ScanAdjustmentScope.AllPages => Pages.ToArray(),
            ScanAdjustmentScope.Selected => SelectedPages.ToArray(),
            _ => Current is { } current ? new[] { current } : [],
        };
        if (targets.Length == 0) return;
        EndAdjustmentGesture();
        Remember();
        foreach (var entry in targets) { entry.Page.Crop = crop; await RefreshAsync(entry); }
    }

    public Task<(NormalizedRect? Paper, NormalizedRect? Content)> DetectCropBoundsAsync(ScanPageEntry entry, CancellationToken ct = default)
    {
        var settings = TemporaryPreview == entry ? Settings with { AutoStraighten = false } : Settings;
        var rotation = entry.Page.Rotation; var edits = entry.Page.Adjustments;
        return Task.Run(() =>
        {
            var proxy = ScanPageProcessor.CreatePreviewPage(entry.PreviewPage ?? Session.Read(entry.Page), 1000, ct);
            return Processor.DetectCropBounds(proxy with { Adjustments = edits }, settings, rotation, ct);
        }, ct);
    }

    public async Task<NormalizedRect?> DetectCropFrameAsync(ScanPageEntry entry, CancellationToken ct = default)
    {
        var settings = Settings with { AutoCrop = true };
        var rotation = entry.Page.Rotation;
        var edits = entry.Page.Adjustments;
        return await Task.Run(() =>
        {
            var proxy = ScanPageProcessor.CreatePreviewPage(entry.PreviewPage ?? Session.Read(entry.Page), 1000, ct);
            return Processor.Analyze(proxy with { Adjustments = edits }, settings, rotation, ct).Keep;
        }, ct);
    }
    public void RemoveSelected()
    {
        if (SelectedPages.Count == 0) return;
        Remember();
        var index = Pages.IndexOf(Current!);
        foreach (var entry in SelectedPages) Pages.Remove(entry);
        Select(Math.Clamp(index, 0, Math.Max(0, Pages.Count - 1)));
        if (Pages.Count == 0) Current = null;
        Renumber();
    }
    // Deleted pages remain owned by the locked session for undo, until output is requested.
    public void CommitPages()
    {
        for (var i = Session.Pages.Count - 1; i >= 0; i--)
            if (!Pages.Any(p => p.Page == Session.Pages[i])) Session.RemoveAt(i);
        for (var i = 0; i < Pages.Count; i++) Session.Move(Session.Pages.ToList().IndexOf(Pages[i].Page), i);
        _undo.Clear(); _redo.Clear();
    }
    public async Task RefreshAllAsync() { foreach (var entry in Pages) await RefreshAsync(entry); }

    public async Task LoadPagesAsync()
    {
        foreach (var page in Session.Pages)
        {
            var entry = new ScanPageEntry(page);
            Pages.Add(entry);
            foreach (var notice in page.Notices) Notices.Add(TranslateNotice(notice));
            await RefreshAsync(entry);
        }
        Renumber();
        if (Pages.Count > 0) Select(0);
    }

    public async Task ScanAsync(ScanDevice device, Action changed, CancellationToken ct = default)
    {
        AdjustmentMessage = string.Empty;
        Remember();
        var pending = new List<Task>();
        var settings = Settings;
        try
        {
            await scanner.ScanAsync(device, settings, new PageSink(page =>
            {
                var saved = Session.Add(page with { ColorMode = settings.ColorMode }, CancellationToken.None);
                dispatcher.Invoke(() =>
                {
                    var entry = new ScanPageEntry(saved);
                    if (Preset == ScanPreset.Receipt) saved.WhiteBackground = true;
                    if (AdjustmentScope == ScanAdjustmentScope.AllPages && _allPageAdjustments is { } all) saved.Adjustments = all;
                    if (SelectedPaper is { } paper)
                        saved.Crop = paper.Crop(page.Bitmap.Width, page.Bitmap.Height, saved.DpiX, saved.DpiY);
                    Pages.Add(entry);
                    Select(Pages.Count - 1);
                    foreach (var notice in saved.Notices) Notices.Add(TranslateNotice(notice));
                    Renumber();
                    pending.Add(FinishScannedPageAsync(entry, changed));
                    changed();
                });
            }), ct);
        }
        catch (ScannerException ex) { throw await ScannerFailure.ResolveAsync(scanner, device, ex, ct); }
        finally { await Task.WhenAll(pending); }
    }

    private async Task FinishScannedPageAsync(ScanPageEntry entry, Action changed)
    {
        // Finish received pages even when acquisition is cancelled or the feeder reports an error.
        if (AutoOrientationEnabled) await AutoOrientAsync(entry, () => { }); // ScanAsync already remembered the batch.
        await RefreshAsync(entry);
        changed();
    }

    private static void RotateEntry(ScanPageEntry entry, int degrees)
    {
        var turn = (degrees % 360 + 360) % 360;
        entry.Page.Rotation = (entry.Page.Rotation + turn) % 360;
        if (entry.Page.Crop is { } crop)
            entry.Page.Crop = turn switch
            {
                90 => new(1 - crop.Y - crop.Height, crop.X, crop.Height, crop.Width),
                180 => new(1 - crop.X - crop.Width, 1 - crop.Y - crop.Height, crop.Width, crop.Height),
                270 => new(crop.Y, 1 - crop.X - crop.Width, crop.Height, crop.Width),
                _ => crop,
            };
    }

    private async Task AutoOrientAsync(ScanPageEntry entry, Action remember, CancellationToken ct = default)
    {
        var rotation = entry.Page.Rotation;
        if (entry.OrientationCheckedRotation == rotation) return;
        var correction = await Task.Run(() => Processor.DetectOrientation(Session.Read(entry.Page), rotation, ct), ct);
        ct.ThrowIfCancellationRequested();
        if (correction != 0)
        {
            remember();
            RotateEntry(entry, correction);
            AdjustmentMessage = Loc.F("Automatically rotated page {0} by {1}°", entry.Number, correction);
        }
        entry.OrientationCheckedRotation = entry.Page.Rotation;
    }

    public async Task RefreshAsync(ScanPageEntry entry)
    {
        var settings = Settings;
        var rotation = entry.Page.Rotation;
        var crop = entry.Page.Crop;
        var edits = entry.Page.Adjustments;
        var result = await Task.Run(() =>
        {
            var page = Session.Read(entry.Page) with { Adjustments = edits };
            var processed = Processor.Process(page, settings, rotation, crop);
            var previewPage = entry.PreviewPage ?? ScanPageProcessor.CreatePreviewPage(page, 1400);
            var full = Decode(processed.Data);
            var scale = Math.Min(1, 160.0 / Math.Max(full.PixelWidth, full.PixelHeight));
            var visual = new DrawingVisual();
            var width = Math.Max(1, (int)(full.PixelWidth * scale));
            var height = Math.Max(1, (int)(full.PixelHeight * scale));
            using (var drawing = visual.RenderOpen()) drawing.DrawImage(full, new System.Windows.Rect(0, 0, width, height));
            var source = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            source.Render(visual);
            source.Freeze();
            var bytes = processed.Data.LongLength;
            if ((page.ColorMode ?? settings.ColorMode) == ScanColorMode.BlackAndWhite)
            {
                using var document = new PdfSharp.Pdf.PdfDocument();
                document.Options.EnableCcittCompressionForBilevelImages = true;
                var target = document.AddPage();
                using var input = new System.IO.MemoryStream(processed.Data);
                using var image = PdfSharp.Drawing.XImage.FromStream(input);
                using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(target)) graphics.DrawImage(image, 0, 0);
                using var output = new System.IO.MemoryStream();
                document.Save(output, false);
                bytes = output.Length;
            }
            return (processed.IsBlank, source, bytes, previewPage);
        });
        if (entry.Page.Adjustments != edits || entry.Page.Rotation != rotation || entry.Page.Crop != crop || Settings != settings) return;
        entry.PreviewPage = result.previewPage;
        entry.Page.IsBlank = result.IsBlank;
        entry.Blank = result.IsBlank ? Loc.T("Blank") : string.Empty;
        entry.Thumbnail = result.source;
        entry.EncodedBytes = result.bytes;
    }

    private static BitmapFrame Decode(byte[] data)
    {
        using var stream = new System.IO.MemoryStream(data);
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }
    private readonly SemaphoreSlim _previewGate = new(1);
    private sealed record GeometryKey(ScanPageEntry Entry, ScanSettings Settings, int Rotation, NormalizedRect? Crop,
        bool ApplyCrop, bool? Straighten, bool White);
    private GeometryKey? _proxyKey;
    private PdfBitmap? _proxyGeometry;
    private (GeometryKey Geometry, ScanPageAdjustments Edits, ScanPreviewFrame Frame)? _proxyRender;
    private System.Windows.Size _sourceSize;
    private sealed record RenderKey(GeometryKey Geometry, ScanPageAdjustments Edits, ScanRenderPlan Plan);
    private (RenderKey Key, ScanPreviewFrame Frame)? _fullRender, _regionRender;

    public async Task<BitmapSource> PreviewAsync(ScanPageEntry entry) => (await PreviewForViewAsync(entry, true)).Image;

    public async Task<BitmapSource> CropPreviewAsync(ScanPageEntry entry) => (await PreviewForViewAsync(entry, false)).Image;

    public Task<ScanPreviewFrame> PreviewForViewAsync(ScanPageEntry entry, bool crop,
        ScanRenderPlan? plan = null, CancellationToken ct = default)
    {
        var settings = TemporaryPreview == entry ? Settings with { AutoStraighten = false, AutoCrop = false } : Settings;
        var rotation = entry.Page.Rotation;
        var edits = entry.Page.Adjustments;
        var rectangle = entry.Page.Crop;
        var key = new GeometryKey(entry, settings, rotation, rectangle, crop, edits.Straighten, edits.WhiteBackground);
        return Task.Run(async () =>
        {
            await _previewGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (plan is not null)
                {
                    var renderKey = new RenderKey(key, edits, plan);
                    var cached = plan.IsRegion ? _regionRender : _fullRender;
                    if (cached is { } hit && hit.Key == renderKey) return hit.Frame;
                }
                else if (_proxyRender is { } proxyHit && proxyHit.Geometry == key && proxyHit.Edits == edits)
                    return proxyHit.Frame;
                if (_proxyKey != key)
                {
                    var source = entry.PreviewPage ?? Session.Read(entry.Page);
                    var original = source.Bitmap;
                    // The old thumbnail cache is 1400px. Read original dimensions only on a geometry miss.
                    if (entry.PreviewPage is not null) original = Session.Read(entry.Page).Bitmap;
                    var proxy = ScanPageProcessor.CreatePreviewPage(source, 1000, ct) with { Adjustments = edits };
                    var geometry = Processor.PreparePreview(proxy, settings, rotation, 1000, rectangle, crop, ct);
                    var ratio = Math.Max(original.Width, original.Height) / (double)Math.Max(proxy.Bitmap.Width, proxy.Bitmap.Height);
                    _sourceSize = new(geometry.Width * ratio, geometry.Height * ratio);
                    _proxyGeometry = geometry;
                    _proxyKey = key;
                    _fullRender = _regionRender = null;
                }
                var bitmap = _proxyGeometry!;
                var region = new System.Windows.Rect(0, 0, 1, 1);
                if (plan is not null)
                {
                    var source = Session.Read(entry.Page) with { Adjustments = edits };
                    // Bound the geometry working set, including unusually large scanner pages.
                    var capScale = Math.Min(1, Math.Sqrt(ScanPreviewSizing.PixelCap / ((double)source.Bitmap.Width * source.Bitmap.Height)));
                    var maxSide = Math.Max(1, (int)(Math.Max(source.Bitmap.Width, source.Bitmap.Height) * capScale));
                    if (!plan.IsRegion)
                        maxSide = Math.Min(maxSide, (int)Math.Ceiling(plan.MaxSide / Math.Max(.001,
                            Math.Max(_sourceSize.Width, _sourceSize.Height) / Math.Max(source.Bitmap.Width, source.Bitmap.Height))));
                    bitmap = Processor.PreparePreview(source, settings, rotation, maxSide, rectangle, crop, ct);
                    region = plan.Region;
                    if (plan.IsRegion)
                        bitmap = ScanRegion.Crop(bitmap, new(region.X, region.Y, region.Width, region.Height), ct);
                    bitmap = ScanPageProcessor.CreatePreviewPage(new(bitmap, 96, 96), plan.MaxSide, ct).Bitmap;
                }
                var preview = ScanPageProcessor.RenderPreparedPreview(bitmap, edits, entry.Page.ColorMode ?? settings.ColorMode,
                    toneReference: plan is null ? null : _proxyGeometry, ct: ct);
                ct.ThrowIfCancellationRequested();
                var image = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, preview.Pixels, preview.Stride);
                image.Freeze();
                var frame = new ScanPreviewFrame(image, _sourceSize, region);
                if (plan is not null)
                {
                    var cached = (new RenderKey(key, edits, plan), frame);
                    if (plan.IsRegion) _regionRender = cached; else _fullRender = cached;
                }
                else _proxyRender = (key, edits, frame);
                return frame;
            }
            finally { _previewGate.Release(); }
        }, ct);
    }

    public void AdjustCurrent(ScanPageEntry entry, ScanPageAdjustments edits)
    {
        edits.Validate();
        if (entry.Page.Adjustments == edits) return;
        Remember();
        entry.Page.Adjustments = edits;
    }

    public async Task ApplyAdjustmentsAsync(ScanPageAdjustments edits, bool allPages)
    {
        edits.Validate();
        var targets = allPages ? Pages.ToArray() : SelectedPages.ToArray();
        if (targets.Length == 0) return;
        Remember();
        foreach (var entry in targets) entry.Page.Adjustments = edits;
        foreach (var entry in targets) await RefreshAsync(entry);
    }

    public async Task AutoCropAsync()
    {
        if (SelectedPages.Count == 0) return;
        Remember();
        AdjustmentMessage = string.Empty;
        foreach (var entry in SelectedPages)
        {
            entry.Page.Straighten = true;
            entry.Page.WhiteBackground = true;
            var page = entry.PreviewPage ??= await Task.Run(() => ScanPageProcessor.CreatePreviewPage(Session.Read(entry.Page), 1400));
            if (AutoOrientationEnabled) await AutoOrientAsync(entry, () => { });
            entry.Page.Crop = (await Task.Run(() => Processor.Analyze(page with { Adjustments = entry.Page.Adjustments },
                Settings with { AutoCrop = true, AutoStraighten = true }, entry.Page.Rotation))).Keep;
            if (entry.Page.Crop is null) AdjustmentMessage = Loc.T("No edges to crop were found");
            await RefreshAsync(entry);
        }
    }

    public void Move(int from, int to)
    {
        Remember();
        Pages.Move(from, to);
        Renumber();
    }

    public void Remove(int index)
    {
        Select(index);
        RemoveSelected();
    }

    private void Renumber()
    {
        for (var i = 0; i < Pages.Count; i++) Pages[i].Number = i + 1;
    }

    internal static string TranslateNotice(string notice)
    {
        var adjusted = Regex.Match(notice, @"^(.+?) adjusted from (.+?) to (.+?)\.$");
        if (adjusted.Success)
            return Loc.F("{0} adjusted from {1} to {2}.", NoticeLabel(adjusted.Groups[1].Value), Loc.T(adjusted.Groups[2].Value), Loc.T(adjusted.Groups[3].Value));
        var unsupported = Regex.Match(notice, @"^(.+?) (\d+) is unsupported; using device value (\d+)\.$");
        if (unsupported.Success)
            return Loc.F("{0} {1} is unsupported; using device value {2}.", NoticeLabel(unsupported.Groups[1].Value), unsupported.Groups[2].Value, unsupported.Groups[3].Value);
        var actual = Regex.Match(notice, @"^Actual (horizontal|vertical) DPI: ([\d.]+) \(requested (\d+)\)\.$");
        if (actual.Success)
            return Loc.F("Actual {0} DPI: {1} (requested {2}).", Loc.T(actual.Groups[1].Value), actual.Groups[2].Value, actual.Groups[3].Value);
        return Loc.T(notice);
    }

    private static string NoticeLabel(string label)
    {
        var dpi = Regex.Match(label, @"^DPI \((\d+)\)$");
        return dpi.Success ? Loc.F("DPI ({0})", dpi.Groups[1].Value) : Loc.T(label);
    }

    private sealed class PageSink(Action<ScannedPage> receive) : IProgress<ScannedPage>
    {
        public void Report(ScannedPage value) => receive(value);
    }
}

public enum ScanPreset { BlackAndWhiteDocument, ColorDocument, Receipt, TwoSidedCard, Photo, Custom }

public sealed record ScanPreviewFrame(BitmapSource Image, System.Windows.Size ImageSize, System.Windows.Rect Region);

public sealed class ScanPageEntry(ScanSessionPage page) : ObservableObject
{
    internal int? OrientationCheckedRotation { get; set; }
    public ScanSessionPage Page { get; } = page;
    internal ScannedPage? PreviewPage { get; set; }
    public long EncodedBytes { get; set; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
    private string _blank = string.Empty;
    public string Blank { get => _blank; set => SetProperty(ref _blank, value); }
    private int _number;
    public int Number { get => _number; set => SetProperty(ref _number, value); }
}
