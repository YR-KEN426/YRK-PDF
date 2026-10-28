using System.IO;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// Changes that are written into the document right away (edited lines, new form fields) instead of on Save:
/// the document with every change so far is written to a working copy, which is opened in place of the file.
/// Each such step keeps a copy of the document before and after it, so Undo and Redo reopen those copies.
/// The file itself only changes on Save.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The document after each step; [0] is before the first one (null: the file itself, unchanged).</summary>
    private readonly List<string?> _versions = [];
    private int _version;
    private bool _keepVersions;

    private static string WorkingFolder => Path.Combine(Path.GetTempPath(), "YRK", "Working");

    /// <summary>Writes every change not saved yet into a working copy and opens it.</summary>
    /// <param name="busy">Shown while it works.</param>
    /// <param name="done">Shown when it worked.</param>
    /// <param name="failed">The error message, with {0} for the reason.</param>
    /// <param name="needed">Whether there is anything to apply, asked after the open editors were finished.</param>
    private async Task ApplyNowAsync(string busy, string done, string failed, Func<bool> needed)
    {
        if (_document is not { } document || IsBusy)
        {
            return;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty); // Finishes a line being typed.
        EndPlacement();
        if (!needed())
        {
            return;
        }

        IsBusy = true;
        StatusMessage = busy;
        var restore = CaptureViewState();
        var copy = Path.Combine(WorkingFolder, $"{Guid.NewGuid():N}.pdf");
        string? before = null;
        string? after = null;
        try
        {
            try
            {
                Directory.CreateDirectory(WorkingFolder);
                if (_versions.Count == 0)
                {
                    // The document as it is now, for Undo: the file itself, or its changes so far (pages, earlier steps).
                    before = _workingCopy is null && !_documentModified ? null : NewVersionPath();
                    if (before is not null)
                    {
                        await document.SaveAsync(before);
                    }
                }

                await WriteChangesAsync(document, copy);
                after = NewVersionPath();
                File.Copy(copy, after);
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                TryDelete(copy);
                DeleteVersionFile(before);
                DeleteVersionFile(after);
                _dialogs.ShowError(Loc.F(failed, ex.Message));
                return;
            }

            if (_versions.Count == 0)
            {
                _versions.Add(before);
            }

            // A new step after Undo drops the steps that were undone.
            for (var i = _versions.Count - 1; i > _version; i--)
            {
                DeleteVersionFile(_versions[i]);
                _versions.RemoveAt(i);
            }

            _versions.Add(after);
            if (await OpenVersionAsync(document.FilePath, copy, restore))
            {
                _version = _versions.Count - 1;
                RebuildVersionSteps();
                StatusMessage = done;
            }
        }
        finally
        {
            IsBusy = false;
            if (StatusMessage == Loc.T(busy))
            {
                StatusMessage = string.Empty;
            }
        }
    }

    /// <summary>Opens version <paramref name="index"/> again (Undo and Redo).</summary>
    private async Task RestoreVersionAsync(int index)
    {
        if (_document is not { } document || IsBusy || index < 0 || index >= _versions.Count)
        {
            return;
        }

        IsBusy = true;
        try
        {
            string? copy = null;
            if (_versions[index] is { } version)
            {
                copy = Path.Combine(WorkingFolder, $"{Guid.NewGuid():N}.pdf");
                try
                {
                    File.Copy(version, copy);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _dialogs.ShowError(Loc.F("Couldn't undo this step.\n\n{0}", ex.Message));
                    RebuildVersionSteps();
                    return;
                }
            }

            if (await OpenVersionAsync(document.FilePath, copy, CaptureViewState()))
            {
                _version = index;
                StatusMessage = string.Empty; // The message of the step no longer fits.
            }

            RebuildVersionSteps();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens a working copy (or the file itself, for null) without forgetting the versions.</summary>
    private async Task<bool> OpenVersionAsync(string path, string? copy, ViewState restore)
    {
        _keepVersions = true;
        try
        {
            if (!await OpenDocumentAsync(path, _password, restore, keepEdits: false, copyPath: copy))
            {
                if (copy is not null)
                {
                    TryDelete(copy);
                }

                return false;
            }
        }
        finally
        {
            _keepVersions = false;
        }

        _documentModified = copy is not null; // The file on disk does not have these changes yet.
        OnEditsChanged();
        return true;
    }

    /// <summary>Undo and Redo steps between the versions (reopening clears the history, so it is made again).</summary>
    private void RebuildVersionSteps()
    {
        _undo.Clear();
        for (var i = 1; i <= _version; i++)
        {
            var step = i;
            _undo.Push(() => _ = RestoreVersionAsync(step - 1), () => _ = RestoreVersionAsync(step));
        }

        for (var i = _versions.Count - 1; i > _version; i--)
        {
            var step = i;
            _undo.PushRedo(() => _ = RestoreVersionAsync(step - 1), () => _ = RestoreVersionAsync(step));
        }
    }

    /// <summary>Forgets the versions (the document was saved or closed).</summary>
    private void ClearVersions()
    {
        if (_keepVersions)
        {
            return;
        }

        foreach (var version in _versions)
        {
            DeleteVersionFile(version);
        }

        _versions.Clear();
        _version = 0;
    }

    private static string NewVersionPath() => Path.Combine(WorkingFolder, $"version-{Guid.NewGuid():N}.pdf");

    private static void DeleteVersionFile(string? path)
    {
        if (path is not null)
        {
            TryDelete(path);
        }
    }
}
