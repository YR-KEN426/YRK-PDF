namespace Yrk.PdfStudio.App.Scanning;

/// <summary>Single consumer, latest request wins. Delay is injectable for virtual-time tests.</summary>
public sealed class ScanPreviewQueue : IDisposable
{
    private readonly Func<bool, CancellationToken, Task> _render;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<bool> _busy;
    private CancellationTokenSource? _request, _active;
    private Task _tail = Task.CompletedTask;
    private Task? _coalescing;
    private bool _disposed;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public ScanPreviewQueue(Func<bool, CancellationToken, Task> render, Action<bool> busy,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _render = render; _busy = busy; _delay = delay ?? Task.Delay; }

    public void Request(bool adjustment = false, bool endGesture = false)
    {
        if (_disposed) return;
        _request?.Cancel();
        _active?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        Completion = ScheduleAsync(request, adjustment, endGesture);
    }
    private async Task ScheduleAsync(CancellationTokenSource request, bool adjustment, bool end)
    {
        try
        {
            var idle = adjustment && !end ? _delay(TimeSpan.FromMilliseconds(200), request.Token) : Task.CompletedTask;
            if (!end)
            {
                // A shared window throttles a continuous stream too; resetting a debounce on
                // every mouse move would prevent any preview while the user is dragging.
                if (_coalescing is null || _coalescing.IsCompleted)
                    _coalescing = _delay(TimeSpan.FromMilliseconds(60), CancellationToken.None);
                await _coalescing.WaitAsync(request.Token);
            }
            await EnqueueAsync(!adjustment || end, request.Token);
            if (adjustment && !end)
            {
                await idle;
                await EnqueueAsync(true, request.Token);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (_request == request) _request = null; request.Dispose(); }
    }
    private Task EnqueueAsync(bool sharp, CancellationToken token)
    {
        var previous = _tail;
        return _tail = RunAsync(previous, sharp, token);
    }
    private async Task RunAsync(Task previous, bool sharp, CancellationToken token)
    {
        try { await previous; } catch (Exception) { /* A failed request must not poison later requests. */ }
        token.ThrowIfCancellationRequested();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        _active = active;
        var busy = BusyAsync(active.Token);
        try { await _render(sharp, active.Token); }
        finally
        {
            active.Cancel();
            await busy;
            _busy(false);
            if (_active == active) _active = null;
        }
    }
    private async Task BusyAsync(CancellationToken token)
    {
        try { await _delay(TimeSpan.FromMilliseconds(300), token); token.ThrowIfCancellationRequested(); _busy(true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel(); _active?.Cancel();
    }
}
