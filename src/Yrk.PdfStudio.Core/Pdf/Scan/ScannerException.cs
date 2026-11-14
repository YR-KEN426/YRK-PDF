namespace Yrk.PdfStudio.Core.Pdf.Scan;

public enum ScannerErrorKind { NoDevice, DeviceBusy, PaperJam, FeederEmpty, Cancelled, Unavailable, Other, Disconnected }

public sealed class ScannerException : Exception
{
    public ScannerErrorKind Kind { get; }
    public bool TransferStarted { get; init; }

    public ScannerException(ScannerErrorKind kind, string message, Exception? innerException = null, int? hresult = null)
        : base(message, innerException)
    {
        Kind = kind;
        if (hresult is { } code) HResult = code;
        else if (innerException is not null) HResult = innerException.HResult;
    }
}
