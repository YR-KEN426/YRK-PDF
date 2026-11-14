using System.IO;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    internal Func<IScannerService> ScannerFactory = () =>
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("YRK_FAKE_SCANNER") == "1") return new FakeScannerService();
#endif
        return new WiaScannerService();
    };

    private IAsyncRelayCommand? _scanToPdfCommand;
    public IAsyncRelayCommand ScanToPdfCommand => _scanToPdfCommand ??= new AsyncRelayCommand(ScanToPdfAsync);

    private async Task ScanToPdfAsync()
    {
        if (IsBusy) return;
        using var session = new ScanSession();
        var scanner = ScannerFactory();
        while (true)
        {
            var request = _dialogs.AskScan(session, scanner);
            if (request is null) return;
            try
            {
                if (request.Format != ScanOutputFormat.Pdf)
                {
                    if (await SaveScanImagesAsync(request)) return;
                    continue;
                }
                string? output;
                ScanOutputStamp? outputStamp = null;
                while (true)
                {
                    output = _dialogs.PickSavePdfFile($"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf",
                        ScanPreferences.SaveFolder(AppSettings.Load().ScanSaveFolder, _document?.FilePath, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "Save PDF");
                    if (output is null) break;
                    if (!CheckNotOpenFile(output)) continue;
                    outputStamp = ScanOutputStamp.Read(output);
                    if (outputStamp is not null && !_dialogs.Confirm(Loc.F("\"{0}\" already exists. Replace it?", Path.GetFileName(output)))) continue;
                    break;
                }
                if (output is null) continue;
                var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"~{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.yrk-writing");
                var count = session.Pages.Count;
                var keepTemp = false;
                var createdTemp = false;
                try
                {
                    var done = await RunBusyAsync("Creating PDF…", async () =>
                    {
                        await new ScanPdfBuilder(_engine, new ScanPageProcessor(new WpfImageCodec())).BuildAsync(session, request.Settings, temp);
                        createdTemp = true;
                        keepTemp = true;
                        PublishScanOutput(temp, output, outputStamp);
                        keepTemp = false;
                    });
                    if (!done) continue;
                    RememberScanFolder(output);
                    var ocrStatus = request.Ocr ? await OcrScannedFileAsync(output, request.OcrLanguages) : null;
                    await OpenFileAsync(output);
                    StatusMessage = ocrStatus ?? Loc.F("Created a PDF with {0} page(s)", count);
                    return;
                }
                finally { if (createdTemp && !keepTemp) TryDelete(temp); }
            }
            catch (Exception ex) { _dialogs.ShowError(ex.Message); }
        }
    }

    private static void RememberScanFolder(string output)
    {
        var settings = AppSettings.Load();
        settings.ScanSaveFolder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        settings.Save();
    }

    internal static string ScanImagePath(string chosenPath, int page, int pageCount, ScanOutputFormat format)
    {
        if (page < 1 || page > pageCount) throw new ArgumentOutOfRangeException(nameof(page));
        var digits = Math.Max(3, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        var extension = format == ScanOutputFormat.Jpeg ? ".jpg" : ".png";
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(chosenPath))!,
            Path.GetFileNameWithoutExtension(chosenPath) + "-" + page.ToString("D" + digits, CultureInfo.InvariantCulture) + extension);
    }

    private async Task<bool> SaveScanImagesAsync(Services.ScanRequest request)
    {
        var extension = request.Format == ScanOutputFormat.Jpeg ? ".jpg" : ".png";
        var pages = request.Session.Pages;
        string[] outputs;
        ScanOutputStamp?[] stamps;
        while (true)
        {
            var chosen = _dialogs.PickSaveFile($"Scan {DateTime.Now:yyyy-MM-dd HHmm}{extension}",
                ScanPreferences.SaveFolder(AppSettings.Load().ScanSaveFolder, _document?.FilePath, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                "Save images", request.Format == ScanOutputFormat.Jpeg ? "JPEG image|*.jpg" : "PNG image|*.png", extension);
            if (chosen is null) return false;
            outputs = Enumerable.Range(1, pages.Count).Select(n => ScanImagePath(chosen, n, pages.Count, request.Format)).ToArray();
            stamps = new ScanOutputStamp?[outputs.Length];
            var approved = true;
            for (var i = 0; i < outputs.Length; i++)
            {
                if (!CheckNotOpenFile(outputs[i])) { approved = false; break; }
                stamps[i] = ScanOutputStamp.Read(outputs[i]);
                if (stamps[i] is not null && !_dialogs.Confirm(Loc.F("\"{0}\" already exists. Replace it?", Path.GetFileName(outputs[i]))))
                { approved = false; break; }
            }
            if (approved) break;
        }
        var done = await RunBusyAsync("Saving images…", async () =>
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var temp = Path.Combine(Path.GetDirectoryName(outputs[i])!, $"~{Guid.NewGuid():N}.yrk-writing");
                var keepTemp = false;
                var createdTemp = false;
                try
                {
                    RequireScanOutputNoLinks(temp);
                    var bytes = await Task.Run(() => RenderScanImage(request.Session, page, request.Settings, request.Format));
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        createdTemp = true;
                        await stream.WriteAsync(bytes);
                    }
                    keepTemp = true;
                    PublishScanOutput(temp, outputs[i], stamps[i]);
                    keepTemp = false;
                }
                finally { if (createdTemp && !keepTemp) TryDelete(temp); }
            }
        });
        if (done) RememberScanFolder(outputs[0]);
        if (done) StatusMessage = Loc.F("Saved {0} image(s)", pages.Count);
        return done;
    }

    internal static byte[] RenderScanImage(ScanSession session, ScanSessionPage page, ScanSettings settings, ScanOutputFormat format)
    {
        var raw = session.Read(page);
        var processor = new ScanPageProcessor(new WpfImageCodec());
        // Full-resolution preparation shares rotation, deskew, crop, adjustments and per-page mode with PDF output.
        var bitmap = processor.RenderPreview(raw, settings, page.Rotation, int.MaxValue, page.Crop, true);
        var swapped = page.Rotation % 180 != 0;
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, swapped ? raw.DpiY : raw.DpiX,
            swapped ? raw.DpiX : raw.DpiY, PixelFormats.Bgra32, null, bitmap.Pixels, bitmap.Stride);
        BitmapEncoder encoder = format == ScanOutputFormat.Jpeg
            ? new JpegBitmapEncoder { QualityLevel = settings.JpegQuality } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    private static void RequireScanOutputNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(Loc.T("Scan output paths must not use symbolic links or junctions."));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private readonly record struct ScanOutputStamp(long Length, DateTime LastWriteTimeUtc)
    {
        public static ScanOutputStamp? Read(string path)
        {
            RequireScanOutputNoLinks(path);
            var file = new FileInfo(path);
            return file.Exists ? new(file.Length, file.LastWriteTimeUtc) : null;
        }
    }

    private static void PublishScanOutput(string temp, string output, ScanOutputStamp? expected)
    {
        try
        {
            RequireScanOutputNoLinks(temp);
            RequireScanOutputNoLinks(output);
            if (expected is not null && ScanOutputStamp.Read(output) != expected)
                throw new IOException(Loc.F("The destination changed. The result is kept at:\n{0}", temp));
            // An absent destination was never approved for replacement. Move must enforce this
            // even if another process creates the file after the save dialog closes.
            File.Move(temp, output, overwrite: expected is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(Loc.F("The file could not be saved. The result is kept at:\n{0}\n\n{1}", temp, ex.Message), ex);
        }
    }
}
