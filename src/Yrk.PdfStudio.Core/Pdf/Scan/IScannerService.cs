namespace Yrk.PdfStudio.Core.Pdf.Scan;

public interface IScannerService
{
    Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task<ScanCapabilities> GetCapabilitiesAsync(ScanDevice device, CancellationToken cancellationToken = default);
    Task ScanAsync(ScanDevice device, ScanSettings settings, IProgress<ScannedPage> progress, CancellationToken cancellationToken = default);
}
