namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Memory operations for documents and engines backed by PDFium.</summary>
public static class PdfiumMemory
{
    public static Task<IPdfDocument> OpenBytesAsync(this IPdfEngine engine, byte[] bytes, string filePath,
        string? password = null, CancellationToken cancellationToken = default) =>
        engine is PdfiumEngine pdfium ? pdfium.OpenBytesAsync(bytes, filePath, password, cancellationToken)
            : throw new NotSupportedException("Memory documents require PDFium.");

    public static Task<byte[]> ExtractPagesToBytesAsync(this IPdfDocument document, IReadOnlyList<int> pages,
        CancellationToken cancellationToken = default) =>
        document is PdfiumDocument pdfium ? pdfium.ExtractPagesToBytesAsync(pages, cancellationToken)
            : throw new NotSupportedException("Memory extraction requires PDFium.");

    public static Task<byte[]> SaveToBytesAsync(this IPdfDocument document, CancellationToken cancellationToken = default) =>
        document is PdfiumDocument pdfium ? pdfium.SaveToBytesAsync(cancellationToken)
            : throw new NotSupportedException("Memory saves require PDFium.");
}
