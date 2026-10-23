namespace Yrk.PdfStudio.Core.Pdf;

public class PdfException : Exception
{
    public PdfException(string message)
        : base(message)
    {
    }

    public PdfException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class PdfPasswordRequiredException : PdfException
{
    public PdfPasswordRequiredException(bool passwordWasWrong)
        : base(passwordWasWrong ? "The password is incorrect." : "This document is password protected.")
    {
        PasswordWasWrong = passwordWasWrong;
    }

    public bool PasswordWasWrong { get; }
}
