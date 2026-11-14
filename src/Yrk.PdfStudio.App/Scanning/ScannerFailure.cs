using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.Scanning;

public static class ScannerFailure
{
    public static async Task<ScannerException> ResolveAsync(IScannerService scanner, ScanDevice device,
        ScannerException error, CancellationToken ct = default)
    {
        if (!error.TransferStarted || error.HResult != unchecked((int)0x80210001)) return error;
        try
        {
            var devices = await scanner.ListDevicesAsync(ct);
            if (!devices.Any(d => d.Id == device.Id))
                return new(ScannerErrorKind.Disconnected, "Scanner disconnected during transfer.", error, error.HResult) { TransferStarted = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { App.LogMessage($"Scanner inventory check: 0x{ex.HResult:X8} {ex}"); }
        return error;
    }
}
