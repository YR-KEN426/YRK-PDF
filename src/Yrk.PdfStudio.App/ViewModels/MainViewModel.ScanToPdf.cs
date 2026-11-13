using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Scanning;
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
            string? output;
            ScanOutputStamp? outputStamp = null;
            while (true)
            {
                output = _dialogs.PickSavePdfFile($"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf",
                    _document is null ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(_document.FilePath), "Save PDF");
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
            try
            {
                var done = await RunBusyAsync("Creating PDF…", async () =>
                {
                    await new ScanPdfBuilder(_engine, new ScanPageProcessor(new WpfImageCodec())).BuildAsync(session, request.Settings, temp);
                    keepTemp = true;
                    PublishScanOutput(temp, output, outputStamp);
                    keepTemp = false;
                });
                if (!done) return;
                var ocrStatus = request.Ocr ? await OcrScannedFileAsync(output, request.OcrLanguages) : null;
                await OpenFileAsync(output);
                StatusMessage = ocrStatus ?? Loc.F("Created a PDF with {0} page(s)", count);
                return;
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(ex.Message);
                return;
            }
            finally { if (!keepTemp) TryDelete(temp); }
        }
    }

    private readonly record struct ScanOutputStamp(long Length, DateTime LastWriteTimeUtc)
    {
        public static ScanOutputStamp? Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(file.Length, file.LastWriteTimeUtc) : null;
        }
    }

    private static void PublishScanOutput(string temp, string output, ScanOutputStamp? expected)
    {
        if (expected is not null && ScanOutputStamp.Read(output) != expected)
            throw new IOException(Loc.F("The destination changed. The result is kept at:\n{0}", temp));
        try
        {
            // An absent destination was never approved for replacement. Move must enforce this
            // even if another process creates the file after the save dialog closes.
            File.Move(temp, output, overwrite: expected is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(Loc.F("The PDF could not be saved. The result is kept at:\n{0}\n\n{1}", temp, ex.Message), ex);
        }
    }
}
