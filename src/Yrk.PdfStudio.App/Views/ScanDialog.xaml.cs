using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The cancellation source is disposed in WorkAsync before the window closes.")]
public partial class ScanDialog : Window
{
    private readonly ScanViewModel _model;
    private readonly IScannerService _scanner;
    private readonly AppSettings _saved = AppSettings.Load();
    private bool _ownsSession, _working, _closePending;
    private CancellationTokenSource? _cancel;
    private Point _dragStart;
    private ScanPageEntry? _dragEntry;
    public ScanRequest? Request { get; private set; }

    public ScanDialog() : this(new ScanSession(), new FakeScannerService()) { _ownsSession = true; }

    public ScanDialog(ScanSession session, IScannerService scanner)
    {
        InitializeComponent();
        _scanner = scanner;
        _model = new(session, scanner, Dispatcher);
        PageList.ItemsSource = _model.Pages;
        Quality.Value = Math.Clamp(_saved.ScanJpegQuality, 1, 100);
        Ocr.IsChecked = _saved.ScanOcr;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { if (_ownsSession) session.Dispose(); };
        UpdateButtons();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await WorkAsync(async () => { await _model.LoadPagesAsync(); await FindAsync(); });
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("YRK_SCAN_DEMO_PAGES"), out var count) && count is >= 1 and <= 6 && !_closePending)
        {
            var fake = new FakeScannerService { PageCount = count };
            await WorkAsync(async () =>
            {
                ReadSettings();
                var demo = new ScanViewModel(_model.Session, fake, Dispatcher) { Settings = _model.Settings };
                await demo.ScanAsync(FakeScannerService.Device, () => { }, _cancel!.Token);
                _model.Pages.Clear();
                await _model.LoadPagesAsync();
                foreach (var notice in demo.Notices) _model.Notices.Add(notice);
            });
        }
#endif
    }

    private async Task FindAsync()
    {
        var devices = await _scanner.ListDevicesAsync(_cancel!.Token);
        Devices.ItemsSource = devices;
        // Capability loading is explicitly awaited, rather than racing SelectionChanged.
        Devices.SelectedItem = devices.FirstOrDefault(d => d.Name == _saved.ScanDeviceName) ?? (devices.Count == 0 ? null : devices[0]);
        if (Devices.SelectedItem is ScanDevice device) await CapabilitiesAsync(device);
        else Message.Text = Loc.T("No scanner found. Connect your scanner and try again.");
    }

    private sealed record Choice<T>(T Value, string Text);
    private async Task CapabilitiesAsync(ScanDevice device)
    {
        var caps = await _scanner.GetCapabilitiesAsync(device, _cancel!.Token);
        Sources.ItemsSource = caps.Sources.Select(s => new Choice<ScanSource>(s, SourceText(s))).ToArray();
        Colors.ItemsSource = caps.ColorModes.Select(c => new Choice<ScanColorMode>(c, ColorText(c))).ToArray();
        Sources.SelectedValue = Enum.TryParse<ScanSource>(_saved.ScanSource, out var source) && caps.Sources.Contains(source) ? source : caps.Sources.Count > 0 ? caps.Sources[0] : null;
        Colors.SelectedValue = Enum.TryParse<ScanColorMode>(_saved.ScanColorMode, out var color) && caps.ColorModes.Contains(color) ? color : caps.ColorModes.Count > 0 ? caps.ColorModes[0] : null;
        var dpi = caps.Dpi.Where(d => d is 150 or 200 or 300 or 600).Distinct().Order().ToArray();
        Dpi.ItemsSource = dpi;
        Dpi.SelectedItem = dpi.Length == 0 ? null : dpi.MinBy(d => Math.Abs((long)d - _saved.ScanDpi));
    }

    private static string SourceText(ScanSource source) => source switch
    {
        ScanSource.Feeder => Loc.T("Feeder"), ScanSource.Duplex => Loc.T("Duplex"), _ => Loc.T("Flatbed"),
    };
    private static string ColorText(ScanColorMode color) => color switch
    {
        ScanColorMode.Grayscale => Loc.T("Grayscale"), ScanColorMode.BlackAndWhite => Loc.T("Black and white"), _ => Loc.T("Color"),
    };
    private void ReadSettings() => _model.Settings = new(Dpi.SelectedItem is int dpi ? dpi : 300,
        Colors.SelectedValue is ScanColorMode color ? color : ScanColorMode.Color,
        Sources.SelectedValue is ScanSource source ? source : ScanSource.Flatbed, (int)Quality.Value);

    private async void OnFind(object sender, RoutedEventArgs e) => await WorkAsync(FindAsync);
    private async void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_working && Devices.SelectedItem is ScanDevice device) await WorkAsync(() => CapabilitiesAsync(device));
    }
    private void OnColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Quality is not null) Quality.IsEnabled = Colors.SelectedValue is not ScanColorMode.BlackAndWhite;
    }
    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_model.Pages.Count > 0 && !new DialogService().Confirm(Loc.T("Clear the scanned pages and scan again?"))) return;
        if (Devices.SelectedItem is not ScanDevice) { Message.Text = Loc.T("No scanner found. Connect your scanner and try again."); return; }
        while (_model.Pages.Count > 0) _model.Remove(_model.Pages.Count - 1);
        _model.Notices.Clear();
        await StartScanAsync();
    }
    private async void OnAppend(object sender, RoutedEventArgs e) => await StartScanAsync();
    private async Task StartScanAsync()
    {
        if (Devices.SelectedItem is not ScanDevice device) { Message.Text = Loc.T("No scanner found. Connect your scanner and try again."); return; }
        ReadSettings();
        await WorkAsync(() => _model.ScanAsync(device, UpdateButtons, _cancel!.Token));
    }

    private async Task WorkAsync(Func<Task> work)
    {
        if (_working) return;
        _working = true;
        _cancel = new();
        Message.Text = string.Empty;
        UpdateButtons();
        try { await work(); }
        catch (ScannerException ex) { Message.Text = ErrorText(ex); }
        catch (OperationCanceledException) { Message.Text = Loc.T("Scanning cancelled. Scanned pages have been kept."); }
        catch (Exception ex) { Message.Text = ex.Message; }
        finally
        {
            _working = false;
            _cancel.Dispose();
            _cancel = null;
            UpdateButtons();
            if (_closePending) Close();
        }
    }

    private string ErrorText(ScannerException ex) => ex.Kind switch
    {
        ScannerErrorKind.NoDevice => Loc.T("No scanner found. Connect your scanner and try again."),
        ScannerErrorKind.DeviceBusy => Loc.T("The scanner is busy. Wait and try again. Scanned pages have been kept."),
        ScannerErrorKind.PaperJam => Loc.T("Paper is jammed. Clear the paper and try Scan more. Scanned pages have been kept."),
        ScannerErrorKind.FeederEmpty => _model.Pages.Count == 0 ? Loc.T("Put paper in the feeder and try again.") : Loc.T("The feeder is empty. Scanned pages have been kept."),
        ScannerErrorKind.Cancelled => Loc.T("Scanning cancelled. Scanned pages have been kept."),
        ScannerErrorKind.Unavailable => Loc.T("The Windows scanning service is unavailable."),
        _ => ex.Message,
    };

    private void UpdateButtons()
    {
        Editor.IsEnabled = !_working;
        CreateButton.IsEnabled = !_working && _model.Pages.Count > 0;
        CloseButton.IsEnabled = !_working;
        ProgressArea.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = string.Join(Environment.NewLine, _model.Notices);
        NoticeBand.Visibility = _model.Notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnCancel(object sender, RoutedEventArgs e) => _cancel?.Cancel();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_working) return;
        e.Cancel = true;
        _closePending = true;
        _cancel?.Cancel();
    }
    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_working || _model.Pages.Count == 0) return;
        ReadSettings();
        _saved.ScanDpi = _model.Settings.Dpi;
        _saved.ScanColorMode = _model.Settings.ColorMode.ToString();
        _saved.ScanSource = _model.Settings.Source.ToString();
        _saved.ScanJpegQuality = _model.Settings.JpegQuality;
        _saved.ScanOcr = Ocr.IsChecked == true;
        _saved.ScanDeviceName = (Devices.SelectedItem as ScanDevice)?.Name ?? string.Empty;
        _saved.Save();
        Request = new(_model.Session, _model.Settings, _saved.ScanOcr, ["tha", "eng"]);
        DialogResult = true;
    }
    private async Task RotateAsync(int degrees)
    {
        if (PageList.SelectedItem is not ScanPageEntry entry) return;
        entry.Page.Rotation = (entry.Page.Rotation + degrees + 360) % 360;
        entry.Page.Crop = null;
        ReadSettings();
        await WorkAsync(() => _model.RefreshAsync(entry));
    }
    private async void OnLeft(object sender, RoutedEventArgs e) => await RotateAsync(-90);
    private async void OnRight(object sender, RoutedEventArgs e) => await RotateAsync(90);
    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedIndex >= 0) _model.Remove(PageList.SelectedIndex);
        UpdateButtons();
    }
    private async void OnAutoCrop(object sender, RoutedEventArgs e) { ReadSettings(); await WorkAsync(_model.AutoCropAsync); }
    private async void OnDeleteBlank(object sender, RoutedEventArgs e)
    {
        ReadSettings();
        await WorkAsync(async () =>
        {
            foreach (var entry in _model.Pages) await _model.RefreshAsync(entry);
            if (_model.Pages.All(p => p.Page.IsBlank)) { Message.Text = Loc.T("All pages are blank. No pages were deleted."); return; }
            for (var i = _model.Pages.Count - 1; i >= 0; i--) if (_model.Pages[i].Page.IsBlank) _model.Remove(i);
        });
    }
    private async void OnCrop(object sender, MouseButtonEventArgs e)
    {
        if (_working || PageList.SelectedItem is not ScanPageEntry entry) return;
        ReadSettings();
        await WorkAsync(async () =>
        {
            var image = await _model.CropPreviewAsync(entry);
            var crop = new ScanCropDialog(image, entry.Page.Crop) { Owner = this };
            if (crop.ShowDialog() == true) entry.Page.Crop = crop.Crop;
        });
    }
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PageList);
        _dragEntry = EntryAt(e.OriginalSource as DependencyObject);
    }
    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_working || e.LeftButton != MouseButtonState.Pressed || _dragEntry is null) return;
        var delta = e.GetPosition(PageList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(PageList, _dragEntry, DragDropEffects.Move);
        _dragEntry = null;
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_working || e.Data.GetData(typeof(ScanPageEntry)) is not ScanPageEntry from || EntryAt(e.OriginalSource as DependencyObject) is not { } to) return;
        _model.Move(_model.Pages.IndexOf(from), _model.Pages.IndexOf(to));
        e.Handled = true;
    }
    private static ScanPageEntry? EntryAt(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ListBoxItem { DataContext: ScanPageEntry entry }) return entry;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
