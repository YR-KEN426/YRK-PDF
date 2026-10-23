using System.Diagnostics.CodeAnalysis;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>
/// The one thread in the process that calls PDFium. PDFium keeps global state and is not
/// thread-safe, so every <see cref="PdfiumEngine"/> instance shares this worker. Interactive work
/// runs before background work. The library stays initialized for the life of the process.
/// </summary>
internal sealed class PdfiumWorker
{
    private static readonly Lazy<PdfiumWorker> SharedInstance = new(() => new PdfiumWorker());

    private readonly object _gate = new();
    private readonly Queue<Action> _interactive = new();
    private readonly Queue<Action> _background = new();
    private Exception? _startupError;

    private PdfiumWorker()
    {
        new Thread(Run) { IsBackground = true, Name = "PDFium worker" }.Start();
    }

    public static PdfiumWorker Shared => SharedInstance.Value;

    public Task<T> InvokeAsync<T>(Func<T> work, PdfWorkPriority priority, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.SetCanceled(cancellationToken);
            return completion.Task;
        }

        void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            if (_startupError is not null)
            {
                completion.TrySetException(new PdfException("The PDF engine failed to start.", _startupError));
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        lock (_gate)
        {
            (priority == PdfWorkPriority.Interactive ? _interactive : _background).Enqueue(Execute);
            Monitor.Pulse(_gate);
        }

        return completion.Task;
    }

    private void Run()
    {
        try
        {
            PdfiumNative.FPDF_InitLibrary();
        }
        catch (Exception ex)
        {
            // Typically DllNotFoundException when pdfium.dll is missing; queued work reports it.
            _startupError = ex;
        }

        while (true)
        {
            Dequeue()();
        }
    }

    private Action Dequeue()
    {
        lock (_gate)
        {
            while (true)
            {
                if (TryDequeue(out var work))
                {
                    return work;
                }

                Monitor.Wait(_gate);
            }
        }
    }

    private bool TryDequeue([NotNullWhen(true)] out Action? work) =>
        _interactive.TryDequeue(out work) || _background.TryDequeue(out work);
}
