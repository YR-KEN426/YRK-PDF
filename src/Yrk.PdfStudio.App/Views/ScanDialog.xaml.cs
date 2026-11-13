using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Scan;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The cancellation source is disposed in WorkAsync before the window closes.")]
public partial class ScanDialog : Window
{
    private readonly ScanViewModel _model;
    private readonly IScannerService _scanner;
    private readonly AppSettings _saved = AppSettings.Load();
    private bool _ownsSession, _working, _closePending, _settingsReady;
    private CancellationTokenSource? _cancel;
    private Point _dragStart;
    private ScanPageEntry? _dragEntry;
    private bool _fit = true, _cropping, _drawingCrop;
    private Point _cropStart;
    private int _previewVersion;
    private int _adjustmentVersion;
    private bool _showingAdjustments;
    private ScanPageEntry? _adjustmentEntry;
    public ScanRequest? Request { get; private set; }

    public ScanDialog() : this(new ScanSession(), new FakeScannerService()) { _ownsSession = true; }

    public ScanDialog(ScanSession session, IScannerService scanner)
    {
        InitializeComponent();
        _scanner = scanner;
        _model = new(session, scanner, Dispatcher);
        PageList.ItemsSource = _model.Pages;
        Quality.Value = Math.Clamp(_saved.ScanJpegQuality, 50, 95);
        Ocr.IsChecked = _saved.ScanOcr;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { ++_adjustmentVersion; ++_previewVersion; _model.ClearGlassPreview(); _model.CancelCard(); if (_ownsSession) session.Dispose(); };
        UpdateButtons();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await WorkAsync(async () => { await FindAsync(); await _model.LoadPagesAsync(); });
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
        // Keep requested values available: the scanner reports any negotiated fallback as a notice.
        Colors.ItemsSource = Enum.GetValues<ScanColorMode>().Select(c => new Choice<ScanColorMode>(c, ColorText(c))).ToArray();
        Sources.SelectedValue = Enum.TryParse<ScanSource>(_saved.ScanSource, out var source) && caps.Sources.Contains(source) ? source : caps.Sources.Count > 0 ? caps.Sources[0] : null;
        Colors.SelectedValue = Enum.TryParse<ScanColorMode>(_saved.ScanColorMode, out var color) && caps.ColorModes.Contains(color) ? color : caps.ColorModes.Count > 0 ? caps.ColorModes[0] : null;
        var dpi = caps.Dpi.Concat([150, 200, 300, 600]).Distinct().Order().ToArray();
        Dpi.ItemsSource = dpi;
        Dpi.SelectedItem = dpi.Length == 0 ? null : dpi.MinBy(d => Math.Abs((long)d - _saved.ScanDpi));
        ReadSettings();
        _model.ApplyPreset(Enum.TryParse<ScanPreset>(_saved.ScanPreset, out var preset) ? preset : ScanPreset.ColorDocument);
        ShowPreset();
        _settingsReady = true;
    }

    private static string SourceText(ScanSource source) => source switch
    {
        ScanSource.Feeder => Loc.T("Feeder"), ScanSource.Duplex => Loc.T("Duplex"), _ => Loc.T("Flatbed"),
    };
    private static string ColorText(ScanColorMode color) => color switch
    {
        ScanColorMode.Grayscale => Loc.T("Grayscale"), ScanColorMode.BlackAndWhite => Loc.T("Black and white"), _ => Loc.T("Color"),
    };
    private void ReadSettings()
    {
        var settings = new ScanSettings(Dpi.SelectedItem is int dpi ? dpi : 300,
        Colors.SelectedValue is ScanColorMode color ? color : ScanColorMode.Color,
        Sources.SelectedValue is ScanSource source ? source : ScanSource.Flatbed, (int)Quality.Value);
        if (_settingsReady && (settings.Dpi != _model.Settings.Dpi || settings.ColorMode != _model.Settings.ColorMode || settings.JpegQuality != _model.Settings.JpegQuality))
        {
            _model.ApplyPreset(ScanPreset.Custom);
            PresetLabel.Text = PresetText(ScanPreset.Custom);
        }
        _model.Settings = settings with { Region = settings.Source == ScanSource.Flatbed ? _model.Settings.Region : null };
        _model.PaperSize = PaperSize.SelectedValue as string ?? "Original";
    }
    private static string PresetText(ScanPreset preset) => preset switch
    {
        ScanPreset.BlackAndWhiteDocument => Loc.T("B&W document"), ScanPreset.ColorDocument => Loc.T("Color document"),
        ScanPreset.Receipt => Loc.T("Receipt"), ScanPreset.TwoSidedCard => Loc.T("Two-sided card"),
        ScanPreset.Photo => Loc.T("Photo"), _ => Loc.T("Custom"),
    };
    private void ShowPreset()
    {
        Sources.SelectedValue = _model.Settings.Source;
        Colors.SelectedValue = _model.Settings.ColorMode;
        Dpi.SelectedItem = _model.Settings.Dpi;
        Quality.Value = _model.Settings.JpegQuality;
        PresetLabel.Text = PresetText(_model.Preset);
        MoreSettings.IsExpanded = _model.Preset == ScanPreset.Custom;
    }
    private async void OnPreset(object sender, RoutedEventArgs e)
    {
        if (_working || sender is not Button { Tag: string name } || !Enum.TryParse<ScanPreset>(name, out var preset)) return;
        ReadSettings();
        _model.CancelCard();
        _model.ClearGlassPreview();
        _model.ApplyPreset(preset);
        ShowPreset();
        SavePreferences();
        await WorkAsync(_model.RefreshAllAsync);
    }
    private async void OnApplySettings(object sender, RoutedEventArgs e)
    {
        ReadSettings(); _model.ApplyPreset(ScanPreset.Custom); ShowPreset();
        SavePreferences();
        await WorkAsync(_model.RefreshAllAsync);
    }

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
        await StartScanAsync();
    }
    private async void OnAppend(object sender, RoutedEventArgs e) => await StartScanAsync();
    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null) return;
        _model.ClearGlassPreview();
        _model.Settings = _model.Settings with { Region = null };
        UpdateButtons();
    }
    private async void OnGlassPreview(object sender, RoutedEventArgs e)
    {
        if (_working || Devices.SelectedItem is not ScanDevice device) return;
        ReadSettings();
        _model.CancelCard();
        await WorkAsync(async () => { await _model.PreviewGlassAsync(device, _cancel!.Token); });
        if (_model.GlassPreview is not null && !_closePending)
        {
            _cropping = true;
            Message.Text = Loc.T("Drag a rectangle to select the scan area.");
        }
    }
    private async void OnClearArea(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _model.Settings = _model.Settings with { Region = null };
        _model.ClearGlassPreview(); _cropping = false;
        UpdateButtons(); await RefreshPreviewAsync();
    }
    private async void OnCancelCard(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _model.CancelCard(); _cropping = false;
        UpdateButtons(); await RefreshPreviewAsync();
    }
    private ScanOutputFormat SelectedOutput => Enum.TryParse<ScanOutputFormat>(OutputFormat?.SelectedValue as string, out var format) ? format : ScanOutputFormat.Pdf;
    private void OnOutputFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Ocr is null || CreateButton is null) return;
        if (SelectedOutput != ScanOutputFormat.Pdf) Ocr.IsChecked = false;
        Ocr.IsEnabled = !_working && SelectedOutput == ScanOutputFormat.Pdf;
        CreateButton.Content = Loc.T(SelectedOutput == ScanOutputFormat.Pdf ? "Save PDF" : "Save images");
    }
    private async Task StartScanAsync()
    {
        if (_working) return;
        if (Devices.SelectedItem is not ScanDevice device) { Message.Text = Loc.T("No scanner found. Connect your scanner and try again."); return; }
        ReadSettings();
        SavePreferences();
        await WorkAsync(async () =>
        {
            await _model.RefreshAllAsync();
            if (_model.Preset == ScanPreset.TwoSidedCard)
            {
                await _model.ScanCardAsync(device, _cancel!.Token);
                _cropping = _model.TemporaryPreview is not null;
                Message.Text = _model.CardBack is not null ? Loc.T("Check the back crop, then press Combine card.")
                    : _model.CardFront is not null ? Loc.T("Check the front crop, flip the card, then press Continue.") : string.Empty;
                if (_model.AdjustmentMessage.Length > 0 && _model.TemporaryPreview is not null) Message.Text += " " + _model.AdjustmentMessage;
            }
            else
            {
                _model.ClearGlassPreview(); _cropping = false;
                await _model.ScanAsync(device, UpdateButtons, _cancel!.Token);
            }
        });
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
        catch (Exception ex) { Message.Text = Loc.T(ex.Message); }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            await RefreshPreviewAsync();
            _working = false;
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
        PageTools.IsEnabled = _model.TemporaryPreview is null;
        PageList.IsEnabled = _model.TemporaryPreview is null;
        ScanButton.IsEnabled = !_working;
        Ocr.IsEnabled = !_working && SelectedOutput == ScanOutputFormat.Pdf;
        OutputFormat.IsEnabled = !_working;
        GlassButton.IsEnabled = !_working && Sources.SelectedValue is ScanSource.Flatbed;
        ScanButton.Content = Loc.T(_model.CardBack is not null ? "Combine card" : _model.CardFront is not null ? "Continue" : _model.Pages.Count == 0 ? "Scan" : "Scan next page");
        CancelCardButton.Visibility = _model.CardFront is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelCardButton.IsEnabled = !_working;
        SizeText.Text = _model.SizeSummary;
        EmptyHint.Visibility = _model.Pages.Count == 0 && _model.TemporaryPreview is null ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = _model.CanUndo;
        RedoButton.IsEnabled = _model.CanRedo;
        CreateButton.IsEnabled = !_working && _model.Pages.Count > 0 && _model.CardFront is null;
        CloseButton.IsEnabled = !_working;
        ProgressArea.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = string.Join(Environment.NewLine, _model.Notices);
        NoticeBand.Visibility = _model.Notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowAdjustments();
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
        if (_working || _model.Pages.Count == 0 || _model.CardFront is not null) return;
        ReadSettings();
        SavePreferences();
        _model.CommitPages();
        Request = new(_model.Session, _model.Settings, SelectedOutput == ScanOutputFormat.Pdf && _saved.ScanOcr, ["tha", "eng"], SelectedOutput);
        DialogResult = true;
    }
    private void SavePreferences()
    {
        _saved.ScanDpi = _model.Settings.Dpi;
        _saved.ScanColorMode = _model.Settings.ColorMode.ToString();
        _saved.ScanSource = _model.Settings.Source.ToString();
        _saved.ScanJpegQuality = _model.Settings.JpegQuality;
        _saved.ScanOcr = Ocr.IsChecked == true;
        _saved.ScanDeviceName = (Devices.SelectedItem as ScanDevice)?.Name ?? string.Empty;
        _saved.ScanPreset = _model.Preset.ToString();
        _saved.Save();
    }
    private async Task RotateAsync(int degrees)
    {
        ReadSettings();
        await WorkAsync(() => _model.RotateSelectedAsync(degrees));
    }
    private async void OnLeft(object sender, RoutedEventArgs e) => await RotateAsync(-90);
    private async void OnRight(object sender, RoutedEventArgs e) => await RotateAsync(90);
    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _model.RemoveSelected();
        UpdateButtons();
        _ = RefreshPreviewAsync();
    }
    private async void OnAutoCrop(object sender, RoutedEventArgs e)
    {
        ReadSettings();
        await WorkAsync(async () => { await _model.AutoCropAsync(); Message.Text = _model.AdjustmentMessage; });
    }

    private ScanPageAdjustments ReadAdjustments() => new((int)Brightness.Value, (int)Sharpness.Value,
        WhiteBackground.IsChecked == true, Straighten.IsChecked == true);

    private void ShowAdjustments()
    {
        var selected = _model.SelectedPages;
        var entry = _model.Current ?? (selected.Count > 0 ? selected[0] : null);
        _showingAdjustments = true;
        try
        {
            _adjustmentEntry = entry;
            AdjustmentPanel.IsEnabled = entry is not null && _model.TemporaryPreview is null;
            var edits = entry?.Page.Adjustments ?? new();
            Brightness.Value = edits.Brightness;
            Sharpness.Value = edits.Sharpness;
            WhiteBackground.IsChecked = edits.WhiteBackground;
            Straighten.IsChecked = edits.Straighten ?? _model.Settings.AutoStraighten;
        }
        finally { _showingAdjustments = false; }
    }

    private async void OnAdjustmentChanged(object sender, RoutedEventArgs e)
    {
        if (_showingAdjustments || _working || _adjustmentEntry is not { } entry) return;
        _model.AdjustCurrent(entry, ReadAdjustments());
        var version = ++_adjustmentVersion;
        await Task.Delay(120);
        if (version != _adjustmentVersion || _working || _adjustmentEntry != entry) return;
        await RefreshPreviewAsync(); // Always render the cached screen-sized image first.
        // The estimate is a separate encoding pass after the controls have been idle.
        await Task.Delay(600);
        if (version != _adjustmentVersion || _working || _adjustmentEntry != entry) return;
        try { await _model.RefreshAsync(entry); if (version == _adjustmentVersion) UpdateButtons(); }
        catch (Exception ex) { Message.Text = ex.Message; }
    }

    private async void OnApplyAll(object sender, RoutedEventArgs e)
    {
        var edits = ReadAdjustments();
        await WorkAsync(() => _model.ApplyAdjustmentsAsync(edits, true));
    }
    private async void OnApplySelected(object sender, RoutedEventArgs e)
    {
        var edits = ReadAdjustments();
        await WorkAsync(() => _model.ApplyAdjustmentsAsync(edits, false));
    }
    private async void OnCrop(object sender, RoutedEventArgs e)
    {
        if (_working || (_model.Current is null && _model.TemporaryPreview is null)) return;
        ReadSettings();
        _cropping = !_cropping;
        await RefreshPreviewAsync();
        if (_cropping) Message.Text = Loc.T("Drag a rectangle on the image to crop selected pages.");
    }
    private async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        var entry = _model.TemporaryPreview ?? _model.Current ?? _model.Pages.FirstOrDefault();
        if (entry is null) { PageImage.Source = null; CropFrame.Visibility = Visibility.Collapsed; return; }
        try
        {
            var temporary = _model.TemporaryPreview is not null;
            var image = _cropping || temporary ? await _model.CropPreviewAsync(entry) : await _model.PreviewAsync(entry);
            if (version != _previewVersion) return;
            PageCanvas.Width = PageImage.Width = image.PixelWidth;
            PageCanvas.Height = PageImage.Height = image.PixelHeight;
            PageImage.Source = image;
            CropFrame.Visibility = Visibility.Collapsed;
            if ((_cropping || temporary) && (entry == _model.GlassPreview ? _model.Settings.Region : entry.Page.Crop) is { } r)
                DrawCrop(new Rect(r.X * PageCanvas.Width, r.Y * PageCanvas.Height, r.Width * PageCanvas.Width, r.Height * PageCanvas.Height));
            SetZoom();
        }
        catch (Exception ex) { Message.Text = ex.Message; }
    }
    private void DrawCrop(Rect rect)
    {
        Canvas.SetLeft(CropFrame, rect.Left); Canvas.SetTop(CropFrame, rect.Top);
        CropFrame.Width = rect.Width; CropFrame.Height = rect.Height;
        CropFrame.Visibility = Visibility.Visible;
    }
    private Point CropPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(PageCanvas);
        return new(Math.Clamp(p.X, 0, PageCanvas.Width), Math.Clamp(p.Y, 0, PageCanvas.Height));
    }
    private void OnCropDown(object sender, MouseButtonEventArgs e)
    {
        if ((!_cropping && _model.TemporaryPreview is null) || _working || PageImage.Source is null) return;
        _cropStart = CropPoint(e); _drawingCrop = true; PageCanvas.CaptureMouse(); e.Handled = true;
    }
    private void OnCropMove(object sender, MouseEventArgs e)
    {
        if (_drawingCrop) DrawCrop(new Rect(_cropStart, CropPoint(e)));
    }
    private async void OnCropUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawingCrop) return;
        _drawingCrop = false; PageCanvas.ReleaseMouseCapture();
        var rect = new Rect(_cropStart, CropPoint(e));
        if (rect.Width < 3 || rect.Height < 3) return;
        var crop = new NormalizedRect(rect.X / PageCanvas.Width, rect.Y / PageCanvas.Height, rect.Width / PageCanvas.Width, rect.Height / PageCanvas.Height);
        if (_model.TemporaryPreview is { } temporary)
        {
            if (temporary == _model.GlassPreview) _model.Settings = _model.Settings with { Region = crop };
            else temporary.Page.Crop = crop;
            DrawCrop(rect);
            return;
        }
        _cropping = false;
        await WorkAsync(() => _model.CropSelectedAsync(crop));
    }
    private async void OnUndo(object sender, RoutedEventArgs e) => await WorkAsync(_model.UndoAsync);
    private async void OnRedo(object sender, RoutedEventArgs e) => await WorkAsync(_model.RedoAsync);
    private void OnFit(object sender, RoutedEventArgs e) { _fit = true; SetZoom(); }
    private void OnActualSize(object sender, RoutedEventArgs e) { _fit = false; SetZoom(); }
    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => SetZoom();
    private void SetZoom()
    {
        if (PreviewBox is null || PageCanvas is null) return;
        PreviewBox.Width = _fit ? Math.Max(1, PreviewScroll.ActualWidth - 24) : PageCanvas.Width;
        PreviewBox.Height = _fit ? Math.Max(1, PreviewScroll.ActualHeight - 24) : PageCanvas.Height;
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _working) return;
        Dispatcher.BeginInvoke(async () => { UpdateButtons(); await RefreshPreviewAsync(); });
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_working || Keyboard.FocusedElement is TextBox or ComboBox or Slider) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_model.TemporaryPreview is not null && e.Key != Key.Space && !(ctrl && e.Key == Key.S)) return;
        e.Handled = true;
        if (ctrl && e.Key == Key.S) OnCreate(sender, e);
        else if (ctrl && e.Key == Key.Z) await WorkAsync(_model.UndoAsync);
        else if (ctrl && e.Key == Key.Y) await WorkAsync(_model.RedoAsync);
        else if (e.Key == Key.Space) await StartScanAsync();
        else if (e.Key == Key.Delete) OnRemove(sender, e);
        else if (e.Key == Key.R) await RotateAsync(shift ? -90 : 90);
        else if (e.Key is Key.Left or Key.Up or Key.Right or Key.Down)
        await WorkAsync(async () =>
        {
            _model.Navigate(e.Key is Key.Left or Key.Up ? -1 : 1, shift);
            if (_model.Current is { } entry) PageList.ScrollIntoView(entry);
            await Task.CompletedTask;
        });
        else e.Handled = false;
    }
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PageList);
        _dragEntry = EntryAt(e.OriginalSource as DependencyObject);
        if (_working || _dragEntry is null) return;
        _model.Select(_model.Pages.IndexOf(_dragEntry), Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
        _ = RefreshPreviewAsync();
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
        UpdateButtons();
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
