namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>
/// Opens documents with PDFium. All PDFium calls run on the process-wide <see cref="PdfiumWorker"/>
/// thread, so several engine instances can coexist safely. Disposing an engine closes the
/// documents it opened.
/// </summary>
public sealed partial class PdfiumEngine : IPdfEngine
{
    private readonly PdfiumWorker _worker = PdfiumWorker.Shared;
    private readonly HashSet<PdfiumDocument> _openDocuments = []; // Touched on the worker thread only.
    private volatile bool _disposed;

    public Task<IPdfDocument> OpenAsync(string filePath, string? password = null, CancellationToken cancellationToken = default) =>
        OpenCopyAsync(filePath, filePath, password, cancellationToken);

    public Task<IPdfDocument> OpenCopyAsync(string filePath, string copyPath, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(copyPath);
        return InvokeAsync<IPdfDocument>(
            () =>
            {
                var document = PdfiumDocument.Open(this, filePath, password, copyPath);
                _openDocuments.Add(document);
                return document;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    /// <summary>Retains an owned copy of the bytes until the document is disposed.</summary>
    public Task<IPdfDocument> OpenBytesAsync(byte[] bytes, string filePath, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var buffer = bytes.ToArray();
        return InvokeAsync<IPdfDocument>(() =>
        {
            var document = PdfiumDocument.Open(this, filePath, password, new MemoryStream(buffer, writable: false));
            _openDocuments.Add(document);
            return document;
        }, PdfWorkPriority.Interactive, cancellationToken);
    }

    /// <summary>Reads from the current position. The caller retains ownership of the input stream.</summary>
    public async Task<IPdfDocument> OpenStreamAsync(Stream stream, string filePath, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await OpenBytesAsync(buffer.ToArray(), filePath, password, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _worker.InvokeAsync(
            () =>
            {
                foreach (var document in _openDocuments.ToArray())
                {
                    document.ReleaseNative();
                }

                return true;
            },
            PdfWorkPriority.Interactive,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Called on the worker thread when a document releases its native handles.</summary>
    internal void OnDocumentClosed(PdfiumDocument document) => _openDocuments.Remove(document);

    internal Task<T> InvokeAsync<T>(Func<T> work, PdfWorkPriority priority, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _worker.InvokeAsync(work, priority, cancellationToken);
    }
}
