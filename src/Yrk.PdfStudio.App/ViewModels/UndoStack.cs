namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>A simple undo/redo history of reversible actions.</summary>
public sealed class UndoStack
{
    private const int Capacity = 200;

    private readonly LinkedList<UndoEntry> _undo = new();
    private readonly Stack<UndoEntry> _redo = new();

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records an action that has already been performed.</summary>
    /// <param name="pageStep">Changes the pages themselves (rotate, delete, insert, move); see <see cref="KeepOnlyPageSteps"/>.</param>
    public void Push(Action undo, Action redo, bool pageStep = false)
    {
        _undo.AddLast(new UndoEntry(undo, redo, pageStep));
        if (_undo.Count > Capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Last is not { } node)
        {
            return;
        }

        _undo.RemoveLast();
        node.Value.Undo();
        _redo.Push(node.Value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var entry))
        {
            return;
        }

        entry.Redo();
        _undo.AddLast(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Before pages are renumbered: other steps refer to page numbers and items of the old page list, so they
    /// go. Page steps stay, because undoing them in reverse order brings back the numbering each one expects.
    /// </summary>
    public void KeepOnlyPageSteps()
    {
        for (var node = _undo.First; node is not null;)
        {
            var next = node.Next;
            if (!node.Value.PageStep)
            {
                _undo.Remove(node);
            }

            node = next;
        }

        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record UndoEntry(Action Undo, Action Redo, bool PageStep);
}
