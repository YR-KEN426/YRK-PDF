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

/// <summary>The file opened with its user password, and its restrictions forbid the change: the owner password is needed.</summary>
public sealed class PdfRestrictedException : PdfException
{
    public PdfRestrictedException()
        : base("The file's restrictions do not allow this change. The owner password is needed.")
    {
    }
}
