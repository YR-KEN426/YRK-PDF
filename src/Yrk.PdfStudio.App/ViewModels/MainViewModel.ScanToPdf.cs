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
            while (true)
            {
                output = _dialogs.PickSavePdfFile($"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf",
                    _document is null ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(_document.FilePath), "Save PDF");
                if (output is null) break;
                if (!CheckNotOpenFile(output)) continue;
                if (File.Exists(output) && !_dialogs.Confirm(Loc.F("\"{0}\" already exists. Replace it?", Path.GetFileName(output)))) continue;
                break;
            }
            if (output is null) continue;
            var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"~{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.yrk-writing");
            var count = session.Pages.Count;
            try
            {
                var done = await RunBusyAsync("Creating PDF…", async () =>
                {
                    await new ScanPdfBuilder(_engine, new ScanPageProcessor(new WpfImageCodec())).BuildAsync(session, request.Settings, temp);
                    File.Move(temp, output, overwrite: true);
                });
                if (!done) return;
                // STEP6: OCR here
                await OpenFileAsync(output);
                StatusMessage = Loc.F("Created a PDF with {0} page(s)", count);
                return;
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(ex.Message);
                return;
            }
            finally { TryDelete(temp); }
        }
    }
}
