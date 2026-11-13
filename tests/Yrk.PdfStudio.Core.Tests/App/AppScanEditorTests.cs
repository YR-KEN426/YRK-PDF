using System.Windows.Threading;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppScanEditorTests
{
    [Fact]
    public void Unsaved_state_tracks_successful_save_edits_undo_and_selection() => Run(async () =>
    {
        using var session = CreateSession();
        var model = CreateModel(session);
        Assert.False(model.HasUnsavedPages);
        await model.LoadPagesAsync();
        Assert.True(model.HasUnsavedPages);
        Assert.Equal(3, model.UnsavedPageCount);
        model.MarkSaved();
        Assert.False(model.HasUnsavedPages);
        Assert.Equal(0, model.UnsavedPageCount);
        model.Select(1);
        Assert.False(model.HasUnsavedPages);
        await model.RotateSelectedAsync(90);
        Assert.True(model.HasUnsavedPages);
        await model.UndoAsync();
        Assert.False(model.HasUnsavedPages);
        model.Move(0, 2);
        Assert.True(model.HasUnsavedPages);
        model.MarkSaved();
        Assert.False(model.HasUnsavedPages);
        model.Settings = model.Settings with { JpegQuality = 90 };
        Assert.True(model.HasUnsavedPages);
        model.MarkSaved();
        model.RemoveSelected();
        Assert.True(model.HasUnsavedPages);
    });

    [Theory]
    [InlineData(ScanAdjustmentScope.ThisPage)]
    [InlineData(ScanAdjustmentScope.Selected)]
    [InlineData(ScanAdjustmentScope.AllPages)]
    public void Live_scope_uses_absolute_values_and_one_undo_per_drag(ScanAdjustmentScope scope) => Run(async () =>
    {
        using var session = CreateSession();
        var model = CreateModel(session);
        await model.LoadPagesAsync();
        Assert.Equal(ScanAdjustmentScope.ThisPage, model.AdjustmentScope);
        Assert.False(model.CanAdjustSelected);
        model.SetAdjustmentScope(ScanAdjustmentScope.Selected);
        Assert.Equal(ScanAdjustmentScope.ThisPage, model.AdjustmentScope);
        model.Pages[0].Page.Adjustments = new(Brightness: -10, Contrast: 15);
        model.Pages[1].Page.Adjustments = new(Brightness: 30, Sharpness: 20);
        model.Select(2, control: true);
        Assert.Equal(ScanAdjustmentScope.Selected, model.AdjustmentScope);
        model.SetAdjustmentScope(scope);
        var original = model.Pages.Select(p => p.Page.Adjustments).ToArray();
        model.BeginAdjustmentGesture();
        model.Adjust(a => a with { Brightness = 5 });
        model.Adjust(a => a with { Brightness = 18 });
        model.Adjust(a => a with { Brightness = -25 });
        model.EndAdjustmentGesture();
        for (var i = 0; i < 3; i++)
        {
            var affected = scope == ScanAdjustmentScope.AllPages || i == 2 || scope == ScanAdjustmentScope.Selected && i == 0;
            Assert.Equal(affected ? original[i] with { Brightness = -25 } : original[i], model.Pages[i].Page.Adjustments);
        }
        await model.UndoAsync();
        Assert.Equal(original, model.Pages.Select(p => p.Page.Adjustments));
        Assert.False(model.CanUndo);
        await model.RedoAsync();
        Assert.Equal(-25, model.Pages[2].Page.Brightness);
        model.Adjust(a => a with { WhiteBackground = true });
        await model.UndoAsync();
        Assert.False(model.Pages[2].Page.WhiteBackground);
        Assert.Equal(-25, model.Pages[2].Page.Brightness);
        model.Select(1);
        if (scope == ScanAdjustmentScope.Selected) Assert.Equal(ScanAdjustmentScope.ThisPage, model.AdjustmentScope);
    });

    [Fact]
    public void All_scope_new_scans_inherit_adjustments_and_undo_restores_inheritance() => Run(async () =>
    {
        using var session = CreateSession();
        var scanner = new FakeScannerService { PageCount = 1 };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 75) };
        await model.LoadPagesAsync();
        model.SetAdjustmentScope(ScanAdjustmentScope.AllPages);
        var edits = new ScanPageAdjustments(12, 22, true, false, 32);
        model.BeginAdjustmentGesture();
        model.Adjust(_ => edits);
        model.EndAdjustmentGesture();
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.Equal(ScanAdjustmentScope.AllPages, model.AdjustmentScope);
        Assert.All(model.Pages, p => Assert.Equal(edits, p.Page.Adjustments));
        await model.UndoAsync();
        await model.UndoAsync();
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.Equal(new ScanPageAdjustments(), model.Pages[^1].Page.Adjustments);
        model.SetAdjustmentScope(ScanAdjustmentScope.ThisPage);
        model.Adjust(a => a with { Brightness = 42 });
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.Equal(0, model.Pages[^1].Page.Brightness);
    });

    [Fact]
    public void Per_page_adjustments_apply_to_selected_or_all_and_support_undo_redo() => Run(async () =>
    {
        using var session = CreateSession();
        var model = CreateModel(session);
        await model.LoadPagesAsync();
        model.Select(0); model.Select(2, control: true);
        var edits = new ScanPageAdjustments(20, -30, true, false, Contrast: 25);
        model.AdjustCurrent(model.Current!, edits);
        Assert.Equal(new ScanPageAdjustments(), model.Pages[0].Page.Adjustments);
        Assert.Equal(edits, model.Pages[2].Page.Adjustments);
        await model.ApplyAdjustmentsAsync(edits, false);
        Assert.Equal(edits, model.Pages[0].Page.Adjustments);
        Assert.Equal(new ScanPageAdjustments(), model.Pages[1].Page.Adjustments);
        await model.ApplyAdjustmentsAsync(edits, true);
        Assert.All(model.Pages, entry => Assert.Equal(edits, entry.Page.Adjustments));
        await model.UndoAsync();
        Assert.Equal(new ScanPageAdjustments(), model.Pages[1].Page.Adjustments);
        await model.RedoAsync();
        Assert.All(model.Pages, entry => Assert.Equal(edits, entry.Page.Adjustments));
        var entry = model.Pages[1];
        var before = await model.PreviewAsync(entry);
        var cache = entry.PreviewPage;
        model.AdjustCurrent(entry, new(-50, 50, false, false));
        var after = await model.PreviewAsync(entry);
        Assert.Same(cache, entry.PreviewPage);
        var a = new byte[before.PixelWidth * before.PixelHeight * 4];
        var b = new byte[after.PixelWidth * after.PixelHeight * 4];
        before.CopyPixels(a, before.PixelWidth * 4, 0);
        after.CopyPixels(b, after.PixelWidth * 4, 0);
        Assert.NotEqual(a, b);
    });

    [Fact]
    public void Auto_adjust_reports_missing_crop_edges_and_turns_on_whitening_and_straightening() => Run(async () =>
    {
        using var session = new ScanSession();
        session.Add(ScanSessionTests.Page(300, 400));
        var model = CreateModel(session);
        await model.LoadPagesAsync();
        await model.AutoCropAsync();
        Assert.Equal(Loc.T("No edges to crop were found"), model.AdjustmentMessage);
        Assert.True(model.Current!.Page.WhiteBackground);
        Assert.True(model.Current.Page.Straighten);
        await model.UndoAsync();
        Assert.False(model.Current!.Page.WhiteBackground);
    });

    [Fact]
    public void Auto_adjust_crops_fake_card_on_white_glass_and_receipt_preset_whitens_new_pages() => Run(async () =>
    {
        using var session = new ScanSession();
        var scanner = new FakeScannerService { PageCount = 1, PageStyle = FakeScanPageStyle.CardOnWhiteGlass };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 100) };
        await model.ScanAsync(FakeScannerService.Device, () => { });
        await model.AutoCropAsync();
        Assert.NotNull(model.Current!.Page.Crop);
        Assert.Empty(model.AdjustmentMessage);
        Assert.True(model.Current.Page.WhiteBackground);
        model.ApplyPreset(ScanPreset.Receipt);
        model.Settings = model.Settings with { Dpi = 100 };
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.True(model.Current!.Page.WhiteBackground);
    });

    [Fact]
    public void Auto_adjust_applies_optional_osd_rotation_only_to_selected_page_and_undo_restores_it() => Run(async () =>
    {
        using var session = CreateSession();
        var folder = Path.Combine(session.DirectoryPath, "tessdata");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "osd.traineddata"), [1]);
        var detector = new TestOrientationDetector();
        var model = CreateModel(session);
        model.Processor = new(new WpfImageCodec(), detector, folder);
        await model.SetAutoOrientationAsync(true, new(folder), () => throw new InvalidOperationException("Installed"));
        await model.LoadPagesAsync();
        model.Select(1);
        await model.AutoCropAsync();
        Assert.Equal(90, model.Pages[1].Page.Rotation);
        Assert.Equal(0, model.Pages[0].Page.Rotation);
        Assert.Equal(0, model.Pages[2].Page.Rotation);
        Assert.Equal(1, detector.Calls);
        await model.UndoAsync();
        Assert.Equal(0, model.Pages[1].Page.Rotation);
        Assert.False(model.Pages[1].Page.WhiteBackground);
    });

    private sealed class TestOrientationDetector : IScanOrientationDetector
    {
        public int Calls { get; private set; }
        public int? Detect(PdfBitmap bitmap, string tessdataFolder, CancellationToken cancellationToken)
        { Calls++; return 90; }
    }

    [Theory]
    [InlineData(ScanPreset.BlackAndWhiteDocument, 300, ScanColorMode.BlackAndWhite, 80)]
    [InlineData(ScanPreset.ColorDocument, 200, ScanColorMode.Color, 80)]
    [InlineData(ScanPreset.Receipt, 300, ScanColorMode.Grayscale, 80)]
    [InlineData(ScanPreset.Photo, 300, ScanColorMode.Color, 90)]
    public void Presets_set_scan_settings_and_preserve_paper_source(ScanPreset preset, int dpi, ScanColorMode color, int quality)
    {
        using var session = new ScanSession();
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
        { Settings = new(Source: ScanSource.Duplex) };
        model.ApplyPreset(preset);
        Assert.Equal(dpi, model.Settings.Dpi);
        Assert.Equal(color, model.Settings.ColorMode);
        Assert.Equal(quality, model.Settings.JpegQuality);
        Assert.Equal(ScanSource.Duplex, model.Settings.Source);
    }

    [Theory]
    [InlineData(ScanPreset.Custom)]
    public void Custom_keeps_normal_settings(ScanPreset preset)
    {
        using var session = new ScanSession();
        var model = new ScanViewModel(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 150, JpegQuality: 65) };
        var original = model.Settings;
        model.ApplyPreset(preset);
        Assert.Equal(original, model.Settings);
        Assert.Equal(preset, model.Preset);
    }

    [Fact]
    public void Selection_supports_control_toggle_shift_range_and_navigation() => Run(async () =>
    {
        using var session = CreateSession();
        var model = CreateModel(session);
        await model.LoadPagesAsync();
        model.Select(0);
        model.Select(2, control: true);
        Assert.Equal(2, model.SelectedPages.Count);
        model.Select(2, control: true);
        Assert.Single(model.SelectedPages);
        model.Select(0);
        model.Select(2, shift: true);
        Assert.Equal(3, model.SelectedPages.Count);
        model.Navigate(-1);
        Assert.Same(model.Pages[1], Assert.Single(model.SelectedPages));
        model.Navigate(-1, extend: true);
        Assert.Equal(2, model.SelectedPages.Count);
    });

    [Fact]
    public void Batch_rotation_crop_delete_and_reorder_can_be_undone_and_redone() => Run(async () =>
    {
        using var session = CreateSession();
        var model = CreateModel(session);
        await model.LoadPagesAsync();
        var first = model.Pages[0];
        model.Select(0); model.Select(1, control: true);
        await model.RotateSelectedAsync(90);
        Assert.Equal(90, model.Pages[0].Page.Rotation);
        Assert.Equal(90, model.Pages[1].Page.Rotation);
        Assert.Equal(0, model.Pages[2].Page.Rotation);
        var crop = new NormalizedRect(0.1, 0.1, 0.5, 0.6);
        await model.CropSelectedAsync(crop);
        Assert.All(model.SelectedPages, p => Assert.Equal(crop, p.Page.Crop));
        await model.UndoAsync();
        Assert.All(model.SelectedPages, p => Assert.Null(p.Page.Crop));
        await model.RedoAsync();
        model.RemoveSelected();
        Assert.Single(model.Pages);
        Assert.Equal(3, session.Pages.Count); // Retain raw pages under the session lock for undo.
        await model.UndoAsync();
        Assert.Equal(3, model.Pages.Count);
        Assert.All(model.SelectedPages, p => Assert.Equal(crop, p.Page.Crop));
        model.Move(0, 2);
        Assert.Same(first, model.Pages[2]);
        await model.UndoAsync();
        Assert.Same(first, model.Pages[0]);
        await model.RedoAsync();
        model.CommitPages();
        Assert.Equal(model.Pages.Select(p => p.Page), session.Pages);
        Assert.False(model.CanUndo);
    });

    [Fact]
    public void New_edit_clears_redo_and_commit_deletes_only_removed_pages() => Run(async () =>
    {
        using var session = CreateSession();
        var model = CreateModel(session);
        await model.LoadPagesAsync();
        model.Select(1);
        var removedPath = model.Current!.Page.FilePath;
        await model.RotateSelectedAsync(-90);
        await model.UndoAsync();
        model.RemoveSelected();
        Assert.False(model.CanRedo);
        Assert.True(File.Exists(removedPath));
        model.CommitPages();
        Assert.False(File.Exists(removedPath));
        Assert.Equal(2, session.Pages.Count);
        Assert.All(session.Pages, p => Assert.True(File.Exists(p.FilePath)));
    });

    [Fact]
    public void Estimate_matches_real_jpeg_encoding_and_updates_after_crop_delete_undo() => Run(async () =>
    {
        using var session = CreateSession();
        var model = CreateModel(session);
        await model.LoadPagesAsync();
        var processor = new ScanPageProcessor(new WpfImageCodec());
        var expected = session.Pages.Sum(p => (long)processor.Process(session.Read(p), model.Settings, p.Rotation, p.Crop).Data.Length);
        Assert.Equal(expected, model.EstimatedBytes);
        model.Select(0);
        await model.CropSelectedAsync(new(0, 0, 0.25, 0.25));
        Assert.True(model.EstimatedBytes < expected);
        await model.UndoAsync();
        Assert.Equal(expected, model.EstimatedBytes);
        model.RemoveSelected();
        Assert.True(model.EstimatedBytes < expected);
        await model.UndoAsync();
        Assert.Equal(expected, model.EstimatedBytes);
        model.Settings = model.Settings with { JpegQuality = 50 };
        await model.RefreshAllAsync();
        Assert.True(model.EstimatedBytes < expected);
    });

    [Theory]
    [InlineData(FakeScanPageStyle.GrayFaintText)]
    [InlineData(FakeScanPageStyle.CardOnWhiteGlass)]
    public void Fake_scanner_exposes_extra_test_pages_without_changing_default(FakeScanPageStyle style) => Run(async () =>
    {
        Assert.Equal(FakeScanPageStyle.Default, new FakeScannerService().PageStyle);
        using var session = new ScanSession();
        var scanner = new FakeScannerService { PageCount = 1, PageStyle = style };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher) { Settings = new(Dpi: 100) };
        await model.ScanAsync(FakeScannerService.Device, () => { });
        var page = session.Read(Assert.Single(session.Pages));
        var corner = page.Bitmap.Pixels[0];
        Assert.Equal(style == FakeScanPageStyle.GrayFaintText ? 211 : 255, corner);
        Assert.True(model.EstimatedBytes > 0);
    });

    [Fact]
    public void Bilevel_estimate_uses_compressed_pdf_bytes_instead_of_raw_bmp() => Run(async () =>
    {
        using var session = new ScanSession();
        var pixels = new byte[1000 * 1400 * 4];
        Array.Fill(pixels, (byte)255);
        session.Add(new(new(1000, 1400, 4000, pixels), 300, 300));
        var model = CreateModel(session);
        model.Settings = model.Settings with { ColorMode = ScanColorMode.BlackAndWhite };
        await model.LoadPagesAsync();
        Assert.InRange(model.EstimatedBytes, 1, 20_000);
    });

    [Fact]
    public void Appending_keeps_previous_pages_and_paper_size_crops_only_new_pages() => Run(async () =>
    {
        using var session = CreateSession();
        var scanner = new FakeScannerService { PageCount = 1 };
        var model = new ScanViewModel(session, scanner, Dispatcher.CurrentDispatcher)
        { Settings = new(Dpi: 100, AutoStraighten: false, AutoCrop: false), PaperSize = "Letter" };
        await model.LoadPagesAsync();
        var original = model.Pages[0];
        await model.ScanAsync(FakeScannerService.Device, () => { });
        Assert.Equal(4, model.Pages.Count);
        Assert.Same(original, model.Pages[0]);
        Assert.NotNull(model.Pages[3].Page.Crop);
        Assert.True(model.Pages[3].Page.Crop!.Value.Height < 1);
        Assert.Same(model.Pages[3], Assert.Single(model.SelectedPages));
        await model.UndoAsync();
        Assert.Equal(3, model.Pages.Count);
        await model.RedoAsync();
        Assert.Equal(4, model.Pages.Count);
    });

    private static ScanViewModel CreateModel(ScanSession session) => new(session, new FakeScannerService(), Dispatcher.CurrentDispatcher)
    { Settings = new(AutoStraighten: false, AutoCrop: false) };

    private static ScanSession CreateSession()
    {
        var session = new ScanSession();
        for (var n = 0; n < 3; n++)
        {
            var pixels = new byte[180 * 240 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var value = (byte)((i / 4 * 37 + n * 17) % 256);
                pixels[i] = value; pixels[i + 1] = (byte)(255 - value); pixels[i + 2] = value; pixels[i + 3] = 255;
            }
            session.Add(new(new(180, 240, 180 * 4, pixels), 300, 300));
        }
        return session;
    }

    private static void Run(Func<Task> action) => Sta.Run(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = action();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        return true;
    });
}
