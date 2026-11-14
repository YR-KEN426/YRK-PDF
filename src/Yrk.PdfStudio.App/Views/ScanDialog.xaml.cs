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
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The cancellation source is disposed in WorkAsync before the window closes.")]
public partial class ScanDialog : Window
{
    private readonly ScanViewModel _model;
    private readonly IScannerService _scanner;
    private readonly AppSettings _saved = AppSettings.Load();
    private ScanCapabilities? _capabilities;
    private bool _updatingPaper, _saveRequested;
    private bool _ownsSession, _working, _closePending, _settingsReady;
    private CancellationTokenSource? _cancel;
    private Point _dragStart;
    private ScanPageEntry? _dragEntry;
    private bool _cropping, _drawingCrop, _panning;
    private Point _panStart;
    private readonly ScanViewTransform _view = new();
    private readonly ScanPreviewQueue _previewQueue;
    private ScanPageEntry? _viewEntry;
    private bool _viewCrop;
    private ScanPreviewFrame? _basePreview;
    private bool _spaceUsed;
    private Point _cropStart;
    private int _previewVersion;
    private int _adjustmentVersion;
    private bool _showingAdjustments;
    private ScanPageEntry? _adjustmentEntry;
    private readonly HashSet<ScanPageEntry> _pendingAdjustments = [];
    internal OcrLanguageData OrientationData { get; set; } = new();
    internal Func<string, bool> Confirm { get; set; } = message => new DialogService().Confirm(message);
    internal Func<SaveChangesChoice>? CloseChoice { get; set; }
    public ScanRequest? Request { get; private set; }

    public ScanDialog() : this(new ScanSession(), new FakeScannerService()) { _ownsSession = true; }

    public ScanDialog(ScanSession session, IScannerService scanner)
    {
        InitializeComponent();
        _scanner = scanner;
        _model = new(session, scanner, Dispatcher);
        _previewQueue = new(RenderQueuedPreviewAsync, busy => PreviewBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed);
        PreviewKeyUp += OnPreviewKeyUp;
        PageList.ItemsSource = _model.Pages;
        Quality.Value = Math.Clamp(_saved.ScanJpegQuality, 50, 95);
        Ocr.IsChecked = _saved.ScanOcr;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { _previewQueue.Dispose(); ++_adjustmentVersion; ++_previewVersion; _model.ClearGlassPreview(); _model.CancelCard(); if (_ownsSession) session.Dispose(); };
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
        UpdateButtons();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ShowAdjustments();
        ShowPaperSizes();
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
                if (!ReadSettings()) return;
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
    private SaveChangesChoice AskCloseChoice()
    {
        var dialog = new Window { Owner = this, Title = Loc.T("Unsaved scans"), SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 500 };
        panel.Children.Add(new TextBlock { Text = Loc.F("There are {0} unsaved pages. Close anyway?", _model.UnsavedPageCount), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var choice = SaveChangesChoice.Cancel;
        foreach (var (value, label) in new[] { (SaveChangesChoice.Save, Loc.T("Save")), (SaveChangesChoice.Discard, Loc.T("Close without saving")), (SaveChangesChoice.Cancel, Loc.T("Cancel")) })
        {
            var button = new Button { Content = label, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4), IsCancel = value == SaveChangesChoice.Cancel };
            button.Click += (_, _) => { choice = value; dialog.Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); dialog.Content = panel; dialog.ShowDialog();
        return choice;
    }

    private void ShowPaperSizes()
    {
        if (_updatingPaper) return;
        _updatingPaper = true;
        try
        {
            var pref = _saved.ScanPaperByPreset.GetValueOrDefault(_model.Preset.ToString()) ?? new ScanPaperPreference();
            ScanPaperSize? remembered = null;
            try { remembered = pref.Size; } catch (ArgumentOutOfRangeException) { }
            var extent = _capabilities?.Extent(Sources.SelectedValue is ScanSource source ? source : ScanSource.Flatbed);
            var metric = System.Globalization.RegionInfo.CurrentRegion.IsMetric;
            PaperSize.Items.Clear();
            PaperSize.Items.Add(new ComboBoxItem { Tag = "Original", Content = Loc.T("Original (full glass)") });
            string? group = null;
            foreach (var paper in ScanPaperSizes.Visible(extent, pref.Id))
            {
                var fits = paper.Fits(extent);
                if (group != paper.Group)
                {
                    if (group is not null) PaperSize.Items.Add(new Separator());
                    group = paper.Group;
                }
                var dimensions = metric ? $"{paper.WidthMm:0.##} × {paper.HeightMm:0.##} mm" : $"{paper.WidthMm / 25.4:0.##} × {paper.HeightMm / 25.4:0.##} in";
                var item = new ComboBoxItem { Tag = paper.Id, Content = Loc.T(paper.Name) + " (" + dimensions + ")", IsEnabled = fits,
                    ToolTip = fits ? null : Loc.T("This size exceeds the scanner area.") };
                ToolTipService.SetShowOnDisabled(item, true);
                PaperSize.Items.Add(item);
            }
            var customFits = remembered is not { Id: "Custom" } || remembered.Fits(extent);
            var customItem = new ComboBoxItem { Tag = "Custom", Content = Loc.T("Custom…"), IsEnabled = customFits,
                ToolTip = customFits ? null : Loc.T("This size exceeds the scanner area.") };
            ToolTipService.SetShowOnDisabled(customItem, true);
            PaperSize.Items.Add(customItem);
            if (remembered is { Id: "Custom" })
            {
                CustomUnit.SelectedIndex = metric ? 0 : 1;
                CustomWidth.Text = (remembered.WidthMm / (metric ? 1 : 25.4)).ToString("0.###");
                CustomHeight.Text = (remembered.HeightMm / (metric ? 1 : 25.4)).ToString("0.###");
                _model.CustomPaperSize = remembered;
            }
            else
            {
                CustomUnit.SelectedIndex = metric ? 0 : 1;
                CustomWidth.Text = (210 / (metric ? 1 : 25.4)).ToString("0.###");
                CustomHeight.Text = (297 / (metric ? 1 : 25.4)).ToString("0.###");
            }
            PaperSize.SelectedValue = remembered?.Fits(extent) == true ? pref.Id : "Original";
            _model.PaperSize = PaperSize.SelectedValue as string ?? "Original";
            CustomPaperPanel.Visibility = _model.PaperSize == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingPaper = false; }
    }

    private void OnPaperChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPaper || _model is null || CustomPaperPanel is null) return;
        CustomPaperPanel.Visibility = PaperSize.SelectedValue as string == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        _model.PaperSize = PaperSize.SelectedValue as string ?? "Original";
        if (_model.PaperSize != "Custom")
            _saved.ScanPaperByPreset[_model.Preset.ToString()] = new(_model.PaperSize);
    }
    private async Task CapabilitiesAsync(ScanDevice device)
    {
        _settingsReady = false;
        var caps = await _scanner.GetCapabilitiesAsync(device, _cancel!.Token);
        _capabilities = caps;
        Sources.ItemsSource = caps.Sources.Select(s => new Choice<ScanSource>(s, SourceText(s))).ToArray();
        // Keep requested values available: the scanner reports any negotiated fallback as a notice.
        Colors.ItemsSource = Enum.GetValues<ScanColorMode>().Select(c => new Choice<ScanColorMode>(c, ColorText(c))).ToArray();
        Sources.SelectedValue = Enum.TryParse<ScanSource>(_saved.ScanSource, out var source) && caps.Sources.Contains(source) ? source : caps.Sources.Count > 0 ? caps.Sources[0] : null;
        Colors.SelectedValue = Enum.TryParse<ScanColorMode>(_saved.ScanColorMode, out var color) && caps.ColorModes.Contains(color) ? color : caps.ColorModes.Count > 0 ? caps.ColorModes[0] : null;
        var dpi = caps.Dpi.Concat([150, 200, 300, 600]).Distinct().Order().ToArray();
        Dpi.ItemsSource = dpi;
        Dpi.SelectedItem = dpi.Length == 0 ? null : dpi.MinBy(d => Math.Abs((long)d - _saved.ScanDpi));
        ShowPaperSizes();
        if (!ReadSettings()) return;
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
    private bool ReadSettings()
    {
        var settings = new ScanSettings(Dpi.SelectedItem is int dpi ? dpi : 300,
        Colors.SelectedValue is ScanColorMode color ? color : ScanColorMode.Color,
        Sources.SelectedValue is ScanSource source ? source : ScanSource.Flatbed, (int)Quality.Value);
        if (_settingsReady && (settings.Dpi != _model.Settings.Dpi || settings.ColorMode != _model.Settings.ColorMode || settings.JpegQuality != _model.Settings.JpegQuality))
        {
            _model.ApplyPreset(ScanPreset.Custom);
            UpdatePresetSelection();
        }
        _model.Settings = settings with { Region = settings.Source == ScanSource.Flatbed ? _model.Settings.Region : null };
        _model.PaperSize = PaperSize.SelectedValue as string ?? "Original";
        if (_model.PaperSize == "Custom")
        {
            if (!double.TryParse(CustomWidth.Text, out var w) || !double.TryParse(CustomHeight.Text, out var h))
            {
                Message.Text = Loc.T("Enter a valid paper width and height.");
                return false;
            }
            try { _model.CustomPaperSize = ScanPaperSizes.Custom(w, h, CustomUnit.SelectedIndex == 1, _capabilities?.Extent(settings.Source)); }
            catch (ArgumentOutOfRangeException)
            {
                Message.Text = Loc.T("Enter a valid paper width and height.");
                return false;
            }
        }
        return true;
    }
    private void ShowPreset()
    {
        Sources.SelectedValue = _model.Settings.Source;
        Colors.SelectedValue = _model.Settings.ColorMode;
        Dpi.SelectedItem = _model.Settings.Dpi;
        Quality.Value = _model.Settings.JpegQuality;
        ShowPaperSizes();
        UpdatePresetSelection();
        MoreSettings.IsExpanded = _model.Preset == ScanPreset.Custom;
    }
    private void UpdatePresetSelection()
    {
        foreach (var chip in PresetRow.Children.OfType<RadioButton>())
            chip.IsChecked = chip.Tag as string == _model.Preset.ToString();
    }
    private async void OnPreset(object sender, RoutedEventArgs e)
    {
        if (_working || sender is not RadioButton { Tag: string name } || !Enum.TryParse<ScanPreset>(name, out var preset)) return;
        if (!ReadSettings()) return;
        _model.CancelCard();
        _model.ClearGlassPreview();
        SavePreferences();
        _model.ApplyPreset(preset);
        ShowPreset();
        SavePreferences();
        await WorkAsync(_model.RefreshAllAsync);
    }
    private async void OnApplySettings(object sender, RoutedEventArgs e)
    {
        if (!ReadSettings()) return;
        SavePreferences();
        ShowPreset();
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
        if (_settingsReady) ShowPaperSizes();
        UpdateButtons();
    }
    private async void OnGlassPreview(object sender, RoutedEventArgs e)
    {
        if (_working || Devices.SelectedItem is not ScanDevice device) return;
        if (!ReadSettings()) return;
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
        if ((_model.Pages.Count > 0 || _model.TemporaryPreview is not null)
            && !Confirm(Loc.T("There are unsaved scans. Clear the scan area?"))) return;
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
        if (!ReadSettings()) return;
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
        if (!_working)
        {
            _closePending = false;
            if (_saveRequested || !_model.HasUnsavedPages) return;
            var choice = CloseChoice?.Invoke() ?? AskCloseChoice();
            if (choice == SaveChangesChoice.Discard) return;
            e.Cancel = true;
            if (choice == SaveChangesChoice.Save)
                Dispatcher.BeginInvoke(() => OnCreate(this, new RoutedEventArgs()));
            return;
        }
        e.Cancel = true;
        _closePending = true;
        _cancel?.Cancel();
    }
    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_working || _model.Pages.Count == 0 || _model.CardFront is not null) return;
        if (!ReadSettings()) return;
        SavePreferences();
        _model.CommitPages();
        _saveRequested = true;
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
        _saved.ScanPaperByPreset[_saved.ScanPreset] = new(_model.PaperSize,
            _model.CustomPaperSize?.WidthMm ?? 0, _model.CustomPaperSize?.HeightMm ?? 0);
        _saved.Save();
    }
    private async Task RotateAsync(int degrees)
    {
        if (!ReadSettings()) return;
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
        if (!ReadSettings()) return;
        await WorkAsync(async () => { await _model.AutoCropAsync(); Message.Text = _model.AdjustmentMessage; });
    }

    private async void OnOrientationChanged(object sender, RoutedEventArgs e)
    {
        if (_showingAdjustments || _working) return;
        var enabled = AutoOrientation.IsChecked == true;
        await WorkAsync(async () =>
        {
            try
            {
                await _model.SetAutoOrientationAsync(enabled, OrientationData,
                    () => Task.FromResult(Confirm(Loc.T("Auto rotation needs orientation data.\n\nDownload it now from github.com/tesseract-ocr (about 10 MB)? It is downloaded only once."))), _cancel!.Token);
            }
            finally
            {
                _showingAdjustments = true;
                try { AutoOrientation.IsChecked = _model.AutoOrientationEnabled; }
                finally { _showingAdjustments = false; }
            }
        });
    }

    private void ShowAdjustments()
    {
        var selected = _model.SelectedPages;
        var entry = _model.Current ?? (selected.Count > 0 ? selected[0] : null);
        _showingAdjustments = true;
        try
        {
            _adjustmentEntry = entry;
            SelectedScope.Content = Loc.F("Selected ({0})", selected.Count);
            SelectedScope.IsEnabled = _model.CanAdjustSelected;
            foreach (var chip in ScopeRow.Children.OfType<RadioButton>())
                chip.IsChecked = chip.Tag as string == _model.AdjustmentScope.ToString();
            AdjustmentPanel.IsEnabled = entry is not null && _model.TemporaryPreview is null;
            var edits = entry?.Page.Adjustments ?? new();
            Brightness.Value = edits.Brightness;
            Contrast.Value = edits.Contrast;
            Sharpness.Value = edits.Sharpness;
            WhiteBackground.IsChecked = edits.WhiteBackground;
            Straighten.IsChecked = edits.Straighten ?? _model.Settings.AutoStraighten;
        }
        finally { _showingAdjustments = false; }
    }

    private async void OnAdjustmentChanged(object sender, RoutedEventArgs e)
    {
        if (_showingAdjustments || _working || _adjustmentEntry is null) return;
        var targets = _model.Adjust(edits => sender == Brightness ? edits with { Brightness = (int)Brightness.Value }
            : sender == Contrast ? edits with { Contrast = (int)Contrast.Value }
            : sender == Sharpness ? edits with { Sharpness = (int)Sharpness.Value }
            : sender == WhiteBackground ? edits with { WhiteBackground = WhiteBackground.IsChecked == true }
            : edits with { Straighten = Straighten.IsChecked == true });
        _pendingAdjustments.UnionWith(targets);
        UndoButton.IsEnabled = _model.CanUndo;
        RedoButton.IsEnabled = _model.CanRedo;
        var version = ++_adjustmentVersion;
        _previewQueue.Request(adjustment: true);
        // The estimate is a separate encoding pass after the controls have been idle.
        await Task.Delay(600);
        if (version != _adjustmentVersion || _working) return;
        try
        {
            var pending = _pendingAdjustments.ToArray();
            foreach (var target in pending) await _model.RefreshAsync(target);
            if (version == _adjustmentVersion) { _pendingAdjustments.ExceptWith(pending); UpdateButtons(); }
        }
        catch (Exception ex) { Message.Text = ex.Message; }
    }

    private void OnScope(object sender, RoutedEventArgs e)
    {
        if (_working || sender is not RadioButton { Tag: string name } || !Enum.TryParse<ScanAdjustmentScope>(name, out var scope)) return;
        _model.SetAdjustmentScope(scope);
        ShowAdjustments();
    }
    private void OnAdjustmentStart(object sender, MouseButtonEventArgs e) => _model.BeginAdjustmentGesture();
    private void OnAdjustmentEnd(object sender, RoutedEventArgs e)
    {
        _model.EndAdjustmentGesture();
        _previewQueue.Request(endGesture: true);
    }
    private void OnAdjustmentKeyStart(object sender, KeyEventArgs e)
    {
        if (!e.IsRepeat && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            _model.BeginAdjustmentGesture();
    }
    private void OnAdjustmentKeyEnd(object sender, KeyEventArgs e) => OnAdjustmentEnd(sender, e);
    private async void OnCrop(object sender, RoutedEventArgs e)
    {
        if (_working || (_model.Current is null && _model.TemporaryPreview is null)) return;
        if (!ReadSettings()) return;
        _cropping = !_cropping;
        await RefreshPreviewAsync();
        if (_cropping) Message.Text = Loc.T("Drag a rectangle on the image to crop selected pages.");
    }
    private async Task RefreshPreviewAsync()
    {
        _previewQueue.Request(endGesture: true);
        await _previewQueue.Completion;
    }
    private async Task RenderQueuedPreviewAsync(bool sharp, CancellationToken ct)
    {
        var version = ++_previewVersion;
        var entry = _model.TemporaryPreview ?? _model.Current ?? _model.Pages.FirstOrDefault();
        if (entry is null) { PageImage.Source = RegionImage.Source = null; _viewEntry = null; _basePreview = null; CropFrame.Visibility = Visibility.Collapsed; return; }
        try
        {
            var temporary = _model.TemporaryPreview is not null;
            var applyCrop = !_cropping && !temporary;
            // Refresh the cheap base image too: geometry may have changed through Undo, rotation or crop.
            {
                var proxy = await _model.PreviewForViewAsync(entry, applyCrop, ct: ct);
                ct.ThrowIfCancellationRequested();
                var reset = _viewEntry != entry || _viewCrop != applyCrop;
                _viewEntry = entry; _viewCrop = applyCrop;
                PageCanvas.Width = PageImage.Width = proxy.ImageSize.Width;
                PageCanvas.Height = PageImage.Height = proxy.ImageSize.Height;
                if (reset || !ReferenceEquals(_basePreview, proxy))
                {
                    PageImage.Source = proxy.Image;
                    RegionImage.Source = null;
                    _basePreview = proxy;
                }
                _view.Resize(proxy.ImageSize, new(PreviewScroll.ActualWidth, PreviewScroll.ActualHeight));
                if (reset) _view.Fit();
                SetZoom();
            }
            if (sharp)
            {
                var plan = ScanPreviewSizing.Plan(_view, VisualTreeHelper.GetDpi(this).DpiScaleX);
                var frame = await _model.PreviewForViewAsync(entry, applyCrop, plan, ct);
                ct.ThrowIfCancellationRequested();
                if (version != _previewVersion) return;
                if (plan.IsRegion)
                {
                    RegionImage.Source = frame.Image;
                    Canvas.SetLeft(RegionImage, frame.Region.X * PageCanvas.Width);
                    Canvas.SetTop(RegionImage, frame.Region.Y * PageCanvas.Height);
                    RegionImage.Width = frame.Region.Width * PageCanvas.Width;
                    RegionImage.Height = frame.Region.Height * PageCanvas.Height;
                }
                else { PageImage.Source = frame.Image; RegionImage.Source = null; }
            }
            if (_drawingCrop) return;
            CropFrame.Visibility = Visibility.Collapsed;
            if (!applyCrop && (entry == _model.GlassPreview ? _model.Settings.Region : entry.Page.Crop) is { } r)
                DrawCrop(new Rect(r.X * PageCanvas.Width, r.Y * PageCanvas.Height, r.Width * PageCanvas.Width, r.Height * PageCanvas.Height));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
        var p = _view.ViewToImage(e.GetPosition(PreviewScroll));
        return new(Math.Clamp(p.X, 0, PageCanvas.Width), Math.Clamp(p.Y, 0, PageCanvas.Height));
    }
    private void OnCropDown(object sender, MouseButtonEventArgs e)
    {
        if (_working || PageImage.Source is null) return;
        if (e.ClickCount == 2)
        {
            if (_view.IsFit) OnActualSize(sender, e); else OnFit(sender, e);
            e.Handled = true; return;
        }
        if (Keyboard.IsKeyDown(Key.Space) || (!_cropping && _model.TemporaryPreview is null))
        {
            _panning = true; _panStart = e.GetPosition(PreviewScroll);
            _spaceUsed = Keyboard.IsKeyDown(Key.Space);
        }
        else { _cropStart = CropPoint(e); _drawingCrop = true; }
        PreviewScroll.CaptureMouse(); e.Handled = true;
    }
    private void OnCropMove(object sender, MouseEventArgs e)
    {
        if (_drawingCrop) DrawCrop(new Rect(_cropStart, CropPoint(e)));
        if (_panning)
        {
            var point = e.GetPosition(PreviewScroll);
            _view.Pan(point - _panStart); _panStart = point;
            SetZoom(); _previewQueue.Request();
        }
    }
    private void OnPreviewCaptureLost(object sender, MouseEventArgs e) { _panning = false; _drawingCrop = false; }
    private async void OnCropUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) { _panning = false; PreviewScroll.ReleaseMouseCapture(); _previewQueue.Request(); return; }
        if (!_drawingCrop) return;
        _drawingCrop = false; PreviewScroll.ReleaseMouseCapture();
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
    private Point ViewCenter => new(_view.View.Width / 2, _view.View.Height / 2);
    private void OnFit(object sender, RoutedEventArgs e) { _view.Fit(); SetZoom(); _previewQueue.Request(); }
    private void OnActualSize(object sender, RoutedEventArgs e) => ZoomTo(1, ViewCenter);
    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomTo(_view.Zoom * 1.25, ViewCenter);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomTo(_view.Zoom / 1.25, ViewCenter);
    internal void ZoomTo(double zoom, Point anchor)
    {
        _view.ZoomAt(zoom, anchor); SetZoom(); _previewQueue.Request();
    }
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        ZoomTo(_view.Zoom * Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(PreviewScroll)); e.Handled = true;
    }
    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _view.Resize(_view.Image, new(PreviewScroll.ActualWidth, PreviewScroll.ActualHeight));
        SetZoom(); _previewQueue?.Request();
    }
    private void SetZoom()
    {
        if (PageCanvas is null) return;
        PageCanvas.RenderTransform = new MatrixTransform(_view.Zoom, 0, 0, _view.Zoom, _view.Offset.X, _view.Offset.Y);
        CropFrame.StrokeThickness = 2 / _view.Zoom;
        ZoomText.Text = Loc.F("{0:0}%", _view.Zoom * 100);
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _working) return;
        Dispatcher.BeginInvoke(async () => { UpdateButtons(); await RefreshPreviewAsync(); });
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key is Key.D0 or Key.NumPad0) OnFit(sender, e);
            else if (e.Key is Key.D1 or Key.NumPad1) OnActualSize(sender, e);
            else if (e.Key is Key.OemPlus or Key.Add) OnZoomIn(sender, e);
            else if (e.Key is Key.OemMinus or Key.Subtract) OnZoomOut(sender, e);
            else goto OtherKeys;
            e.Handled = true; return;
        }
        if (e.Key == Key.Space && Keyboard.FocusedElement is not (TextBox or ComboBox or Slider or RadioButton or CheckBox))
        { e.Handled = true; return; }
        OtherKeys:
        if (_working || Keyboard.FocusedElement is TextBox or ComboBox or Slider) return;
        if (Keyboard.FocusedElement is RadioButton or CheckBox && e.Key is Key.Space or Key.Left or Key.Right or Key.Up or Key.Down) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_model.TemporaryPreview is not null && e.Key != Key.Space && !(ctrl && e.Key == Key.S)) return;
        e.Handled = true;
        if (ctrl && e.Key == Key.S) OnCreate(sender, e);
        else if (ctrl && e.Key == Key.Z) await WorkAsync(_model.UndoAsync);
        else if (ctrl && e.Key == Key.Y) await WorkAsync(_model.RedoAsync);
        else if (e.Key == Key.Delete) OnRemove(sender, e);
        else if (e.Key == Key.R) await RotateAsync(shift ? -90 : 90);
        else if (e.Key == Key.C && !ctrl) OnCrop(sender, e);
        else if (e.Key == Key.A && !ctrl) OnAutoCrop(sender, e);
        else if (e.Key == Key.F && !ctrl) OnFit(sender, e);
        else if (e.Key == Key.D1 && ctrl) OnActualSize(sender, e);
        else if (e.Key is Key.Left or Key.Up or Key.Right or Key.Down)
        await WorkAsync(async () =>
        {
            _model.Navigate(e.Key is Key.Left or Key.Up ? -1 : 1, shift);
            if (_model.Current is { } entry) PageList.ScrollIntoView(entry);
            await Task.CompletedTask;
        });
        else e.Handled = false;
    }
    private async void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.FocusedElement is TextBox or ComboBox or Slider or RadioButton or CheckBox) return;
        if (!_spaceUsed && !_panning) await StartScanAsync();
        _spaceUsed = false; e.Handled = true;
    }
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PageList);
        _dragEntry = EntryAt(e.OriginalSource as DependencyObject);
        if (_working || _dragEntry is null) return;
        _model.Select(_model.Pages.IndexOf(_dragEntry), Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
        UpdateButtons();
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
