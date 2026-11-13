using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed class ScanViewModel(ScanSession session, IScannerService scanner, Dispatcher dispatcher)
{
    private readonly ScanPageProcessor _processor = new(new WpfImageCodec());
    public ScanSession Session { get; } = session;
    public ObservableCollection<ScanPageEntry> Pages { get; } = [];
    public HashSet<string> Notices { get; } = [];
    public ScanSettings Settings { get; set; } = new();

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
    }

    public async Task ScanAsync(ScanDevice device, Action changed, CancellationToken ct = default)
    {
        var pending = new List<Task>();
        try
        {
            await scanner.ScanAsync(device, Settings, new PageSink(page =>
            {
                var saved = Session.Add(page, CancellationToken.None);
                dispatcher.Invoke(() =>
                {
                    var entry = new ScanPageEntry(saved);
                    Pages.Add(entry);
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
        var result = await Task.Run(() =>
        {
            var page = Session.Read(entry.Page);
            var analysis = _processor.Analyze(page, settings, rotation);
            var preview = _processor.RenderPreview(page, settings, rotation, 160);
            var source = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, preview.Pixels, preview.Stride);
            source.Freeze();
            return (analysis, source);
        });
        entry.Page.IsBlank = result.analysis.IsBlank;
        entry.Blank = result.analysis.IsBlank ? Loc.T("Blank") : string.Empty;
        entry.Thumbnail = result.source;
    }

    public Task<System.Windows.Media.Imaging.BitmapSource> CropPreviewAsync(ScanPageEntry entry) => Task.Run(() =>
    {
        var preview = _processor.RenderPreview(Session.Read(entry.Page), Settings, entry.Page.Rotation, 1400);
        var image = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, preview.Pixels, preview.Stride);
        image.Freeze();
        return image;
    });

    public async Task AutoCropAsync()
    {
        foreach (var entry in Pages)
            entry.Page.Crop = (await Task.Run(() => _processor.Analyze(Session.Read(entry.Page), Settings with { AutoCrop = true }, entry.Page.Rotation))).Keep;
    }

    public void Move(int from, int to)
    {
        Session.Move(from, to);
        Pages.Move(from, to);
        Renumber();
    }

    public void Remove(int index)
    {
        Session.RemoveAt(index);
        Pages.RemoveAt(index);
        Renumber();
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

public sealed class ScanPageEntry(ScanSessionPage page) : ObservableObject
{
    public ScanSessionPage Page { get; } = page;
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
    private string _blank = string.Empty;
    public string Blank { get => _blank; set => SetProperty(ref _blank, value); }
    private int _number;
    public int Number { get => _number; set => SetProperty(ref _number, value); }
}
