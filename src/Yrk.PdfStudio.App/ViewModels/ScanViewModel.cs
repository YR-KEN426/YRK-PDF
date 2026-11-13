using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed class ScanViewModel(ScanSession session, IScannerService scanner, Dispatcher dispatcher) : ObservableObject
{
    public ScanSession Session { get; } = session;
    public ObservableCollection<ScanPageEntry> Pages { get; } = [];
    public HashSet<string> Notices { get; } = [];
    public ScanSettings Settings { get; set; } = new();
    public string PaperSize { get; set; } = "Original";
    public ScanPreset Preset { get; private set; } = ScanPreset.ColorDocument;
    public IReadOnlyList<ScanPageEntry> SelectedPages => Pages.Where(p => p.IsSelected).ToArray();
    private ScanPageEntry? _current;
    public ScanPageEntry? Current { get => _current; private set => SetProperty(ref _current, value); }
    private int _anchor;
    private readonly Stack<PageState[]> _undo = new(), _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public long EstimatedBytes => Pages.Sum(p => p.EncodedBytes);
    public string SizeSummary => Loc.F("{0} pages · approximately {1:0.00} MB", Pages.Count, EstimatedBytes / 1_000_000.0);
    public string AdjustmentMessage { get; private set; } = string.Empty;
    internal ScanPageProcessor Processor { get; set; } = new(new WpfImageCodec());

    public void ApplyPreset(ScanPreset preset)
    {
        Preset = preset;
        Settings = preset switch
        {
            ScanPreset.BlackAndWhiteDocument => Settings with { Dpi = 300, ColorMode = ScanColorMode.BlackAndWhite, JpegQuality = 80 },
            ScanPreset.ColorDocument => Settings with { Dpi = 200, ColorMode = ScanColorMode.Color, JpegQuality = 80 },
            ScanPreset.Receipt => Settings with { Dpi = 300, ColorMode = ScanColorMode.Grayscale, JpegQuality = 80 },
            ScanPreset.Photo => Settings with { Dpi = 300, ColorMode = ScanColorMode.Color, JpegQuality = 90 },
            _ => Settings,
        };
    }

    public void Select(int index, bool control = false, bool shift = false)
    {
        if ((uint)index >= Pages.Count) return;
        if (!control) foreach (var page in Pages) page.IsSelected = false;
        if (shift)
            for (var i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index) && i < Pages.Count; i++) Pages[i].IsSelected = true;
        else { Pages[index].IsSelected = !control || !Pages[index].IsSelected; _anchor = index; }
        Current = Pages[index];
    }

    public void Navigate(int delta, bool extend = false) => Select(Math.Clamp(Pages.IndexOf(Current!) + delta, 0, Math.Max(0, Pages.Count - 1)), shift: extend);
    private sealed record PageState(ScanPageEntry Entry, int Rotation, NormalizedRect? Crop, bool Selected, ScanPageAdjustments Adjustments);
    private PageState[] Snapshot() => Pages.Select(p => new PageState(p, p.Page.Rotation, p.Page.Crop, p.IsSelected, p.Page.Adjustments)).ToArray();
    private void Remember() { _undo.Push(Snapshot()); _redo.Clear(); }
    private async Task RestoreAsync(PageState[] state)
    {
        Pages.Clear();
        foreach (var s in state)
        {
            s.Entry.Page.Rotation = s.Rotation; s.Entry.Page.Crop = s.Crop; s.Entry.IsSelected = s.Selected;
            s.Entry.Page.Adjustments = s.Adjustments;
            Pages.Add(s.Entry);
            await RefreshAsync(s.Entry);
        }
        var selected = SelectedPages;
        Current = selected.Count > 0 ? selected[0] : Pages.Count > 0 ? Pages[0] : null;
        _anchor = Math.Max(0, Pages.IndexOf(Current!));
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
            entry.Page.Rotation = (entry.Page.Rotation + degrees + 360) % 360; entry.Page.Crop = null;
            await RefreshAsync(entry);
        }
    }
    public async Task CropSelectedAsync(NormalizedRect? crop)
    {
        if (SelectedPages.Count == 0) return;
        Remember();
        foreach (var entry in SelectedPages) { entry.Page.Crop = crop; await RefreshAsync(entry); }
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
        Remember();
        var pending = new List<Task>();
        try
        {
            await scanner.ScanAsync(device, Settings, new PageSink(page =>
            {
                var saved = Session.Add(page, CancellationToken.None);
                dispatcher.Invoke(() =>
                {
                    var entry = new ScanPageEntry(saved);
                    if (Preset == ScanPreset.Receipt) saved.WhiteBackground = true;
                    if (PaperSize != "Original")
                    {
                        var raw = page.Bitmap;
                        var width = PaperSize == "Letter" ? 215.9 : 210;
                        var height = PaperSize == "Letter" ? 279.4 : 297;
                        saved.Crop = new(0, 0, Math.Min(1, width / 25.4 * saved.DpiX / raw.Width), Math.Min(1, height / 25.4 * saved.DpiY / raw.Height));
                    }
                    Pages.Add(entry);
                    Select(Pages.Count - 1);
                    foreach (var notice in saved.Notices) Notices.Add(TranslateNotice(notice));
                    Renumber();
                    pending.Add(RefreshAsync(entry));
                    changed();
                });
            }), ct);
        }
        finally { await Task.WhenAll(pending); }
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
            if (settings.ColorMode == ScanColorMode.BlackAndWhite)
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
    public Task<BitmapSource> PreviewAsync(ScanPageEntry entry) => RenderPreviewAsync(entry, true);

    public Task<BitmapSource> CropPreviewAsync(ScanPageEntry entry) => RenderPreviewAsync(entry, false);

    private Task<BitmapSource> RenderPreviewAsync(ScanPageEntry entry, bool crop)
    {
        var settings = Settings;
        var rotation = entry.Page.Rotation;
        var edits = entry.Page.Adjustments;
        var rectangle = entry.Page.Crop;
        return Task.Run<BitmapSource>(() =>
        {
            var small = entry.PreviewPage ??= ScanPageProcessor.CreatePreviewPage(Session.Read(entry.Page), 1400);
            var preview = Processor.RenderPreview(small with { Adjustments = edits }, settings, rotation, 1400,
                crop: rectangle, applyCrop: crop);
            var image = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, preview.Pixels, preview.Stride);
            image.Freeze();
            return image;
        });
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
            var rotation = await Task.Run(() => Processor.DetectOrientation(page, entry.Page.Rotation));
            entry.Page.Rotation = (entry.Page.Rotation + rotation) % 360;
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

public sealed class ScanPageEntry(ScanSessionPage page) : ObservableObject
{
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
