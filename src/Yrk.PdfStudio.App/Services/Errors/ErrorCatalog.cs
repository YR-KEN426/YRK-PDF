using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Stable error codes and English keys for the existing translation dictionary.</summary>
public static class ErrorCatalog
{
    public static ErrorInfo AppDispatcherUnhandled { get; } = new("YRK-APP-001",
        "Something went wrong while using the app.",
        "Try again. If the problem continues, copy the problem report and contact support.");

    public static ErrorInfo AppDomainUnhandled { get; } = new("YRK-APP-002",
        "The app encountered a problem and needs to close.",
        "Restart the app. If recovery is offered, recover your unsaved work.");

    public static ErrorInfo AppTaskUnobserved { get; } = new("YRK-APP-003",
        "A background task could not be completed.",
        "Try the operation again. If the problem continues, copy the problem report and contact support.");

    public static ErrorInfo ScannerNoDevice { get; } = new("YRK-SCN-001",
        "No scanner found.", "Connect your scanner and try again.");

    public static ErrorInfo ScannerDeviceBusy { get; } = new("YRK-SCN-002",
        "The scanner is busy. Scanned pages have been kept.", "Wait and try again.");

    public static ErrorInfo ScannerPaperJam { get; } = new("YRK-SCN-003",
        "Paper is jammed. Scanned pages have been kept.", "Clear the paper and try Scan more.");

    public static ErrorInfo ScannerFeederEmpty { get; } = new("YRK-SCN-004",
        "The feeder is empty.", "Put paper in the feeder and try again.");

    public static ErrorInfo ScannerFeederEmptyWithPages { get; } = new("YRK-SCN-005",
        "The feeder is empty. Scanned pages have been kept.",
        "Add more paper and try Scan more, or create a file from the scanned pages.");

    public static ErrorInfo ScannerCancelled { get; } = new("YRK-SCN-006",
        "Scanning cancelled. Scanned pages have been kept.",
        "Try Scan more, or create a file from the scanned pages.");

    public static ErrorInfo ScannerUnavailable { get; } = new("YRK-SCN-007",
        "The scanner is not ready.", "Check its power and connection, then find scanners again.");

    public static ErrorInfo ScannerDisconnected { get; } = new("YRK-SCN-008",
        "The scanner connection was lost.",
        "Check the USB cable and turn on the scanner, then press Find scanners again.");

    public static ErrorInfo ScannerOther { get; } = new("YRK-SCN-009",
        "The scanner reported an error.", "Try scanning again.");

    public static IReadOnlyList<ErrorInfo> All { get; } = Array.AsReadOnly<ErrorInfo>([
        AppDispatcherUnhandled, AppDomainUnhandled, AppTaskUnobserved,
        ScannerNoDevice, ScannerDeviceBusy, ScannerPaperJam, ScannerFeederEmpty,
        ScannerFeederEmptyWithPages, ScannerCancelled, ScannerUnavailable,
        ScannerDisconnected, ScannerOther,
    ]);

    public static ErrorInfo ForScanner(ScannerErrorKind kind, bool hasPages = false) => kind switch
    {
        ScannerErrorKind.NoDevice => ScannerNoDevice,
        ScannerErrorKind.DeviceBusy => ScannerDeviceBusy,
        ScannerErrorKind.PaperJam => ScannerPaperJam,
        ScannerErrorKind.FeederEmpty => hasPages ? ScannerFeederEmptyWithPages : ScannerFeederEmpty,
        ScannerErrorKind.Cancelled => ScannerCancelled,
        ScannerErrorKind.Unavailable => ScannerUnavailable,
        ScannerErrorKind.Disconnected => ScannerDisconnected,
        _ => ScannerOther,
    };
}
