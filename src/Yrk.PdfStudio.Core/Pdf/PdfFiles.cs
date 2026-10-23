using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf;

/// <summary>Whole-file helpers.</summary>
public static class PdfFiles
{
    /// <summary>
    /// Rewrites a PDF as one clean revision with only the objects in use (earlier versions and unused
    /// objects are dropped). Digital signatures do not survive this.
    /// </summary>
    public static byte[] Compact(byte[] pdf) => CosCompactWriter.Write(CosDocument.Load(pdf));

    /// <summary>
    /// Keeps only the objects that really changed in an incremental save of <paramref name="original"/>
    /// (PDFium writes every object it has loaded). Returns <paramref name="saved"/> when it cannot do better.
    /// </summary>
    public static byte[] TrimIncrementalSave(byte[] original, byte[] saved) => CosUpdateTrimmer.Trim(original, saved);

    /// <summary>Whether the file is encrypted (including owner-password-only files that open without a password).</summary>
    public static bool IsEncrypted(byte[] pdf) => CosDocument.Load(pdf).IsEncrypted;
}
