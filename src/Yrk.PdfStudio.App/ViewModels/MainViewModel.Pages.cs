using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Compression;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>Page tools (rotate, delete, move, insert, extract, split) and file tools (combine, images, compress).</summary>
public sealed partial class MainViewModel
{
    /// <summary>The open document has changes in memory (page tools or committed overlays) not yet saved.</summary>
    private bool _documentModified;

    private IAsyncRelayCommand? _combineFilesCommand;
    private IAsyncRelayCommand? _imagesToPdfCommand;
    private IAsyncRelayCommand? _compressCommand;
    private IAsyncRelayCommand? _splitCommand;

    public IAsyncRelayCommand CombineFilesCommand => _combineFilesCommand ??= new AsyncRelayCommand(CombineFilesAsync);

    public IAsyncRelayCommand ImagesToPdfCommand => _imagesToPdfCommand ??= new AsyncRelayCommand(ImagesToPdfAsync);

    public IAsyncRelayCommand CompressCommand => _compressCommand ??= new AsyncRelayCommand(CompressAsync);

    public IAsyncRelayCommand SplitCommand => _splitCommand ??= new AsyncRelayCommand(SplitAsync);

    // --- Page tools (called by the view with the selected pages) ----------------------------------

    public PageClipboard PageClipboard { get; set; } = Services.PageClipboard.Shared;

    public bool CanCopyPages => HasDocument && !IsBusy && _document is { } document
        && (document.Permissions & (Core.Pdf.Security.PdfPermissions.Copy | Core.Pdf.Security.PdfPermissions.Assemble))
            == (Core.Pdf.Security.PdfPermissions.Copy | Core.Pdf.Security.PdfPermissions.Assemble);

    public bool CanPastePages => HasDocument && !IsBusy && _document is { } document
        && (document.Permissions & Core.Pdf.Security.PdfPermissions.Assemble) != 0;

    public bool CanCutPages(IReadOnlyList<int> pages) => CanCopyPages && CanPastePages
        && pages.Count > 0 && pages.Distinct().Count() < Pages.Count;

    public async Task<bool> CopyPagesAsync(IReadOnlyList<int> pages, bool cut = false)
    {
        if (!CanCopyPages || (cut && !CanCutPages(pages)) || _document is not { } document
            || pages.Count == 0 || pages.Any(p => p < 0 || p >= document.PageCount)
            || !await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Copy | Core.Pdf.Security.PdfPermissions.Assemble, "extract its pages")
            || BlockedByTextEdits()) return false;

        var sorted = pages.Distinct().Order().ToArray();
        string? path = null;
        var published = false;
        var done = await RunBusyAsync("Extracting pages…", async () =>
        {
            path = PageClipboard.Allocate();
            if (_placedItems.Count > 0 || _fieldEdits.Count > 0)
            {
                // Flatten pending items into a private document, preserving the source and its undo history.
                await using var copy = await _engine.OpenBytesAsync(await document.SaveToBytesAsync(), document.FilePath, document.OpenPassword);
                await CommitOverlaysAsync(copy);
                await copy.ExtractPagesAsync(sorted, path);
            }
            else await document.ExtractPagesAsync(sorted, path);
            try
            {
                PageClipboard.Publish(path, sorted.Length, cut);
                published = true;
            }
            catch (Exception ex) { _dialogs.ShowError(ErrorCatalog.PageClipboardWrite, ex); }
        });
        if (!done || !published)
        {
            if (path is not null) PageClipboard.DeleteOwned(path);
            return false;
        }
        if (cut && await DeletePagesCoreAsync(sorted, confirm: false, cutPath: path)) PageClipboard.MarkCut(path!);
        return true;
    }

    public async Task PastePagesAsync(int insertIndex)
    {
        if (!CanPastePages) return;
        PageClipboard.Source? source;
        try { source = PageClipboard.ReadSource(); }
        catch (Exception ex) { _dialogs.ShowError(ErrorCatalog.PageClipboardWrite, ex); return; }
        if (source is null)
        {
            _dialogs.ShowError(ErrorCatalog.PageClipboardMissing);
            return;
        }
        if (await InsertPagesAsync(source.Path, insertIndex, pasted: true)) PageClipboard.MarkPasted(source);
    }

    public Task RotatePagesAsync(IReadOnlyList<int> pages, int quarterTurns) =>
        RunPageToolAsync("Rotating…", async document =>
        {
            await document.RotatePagesAsync(pages, quarterTurns);
            _undo.Push(
                () => _ = RunPageToolAsync("Rotating…", d => d.RotatePagesAsync(pages, -quarterTurns), pages[0]),
                () => _ = RunPageToolAsync("Rotating…", d => d.RotatePagesAsync(pages, quarterTurns), pages[0]),
                pageStep: true);
        }, pages[0], commitFirst: true);

    public Task DeletePagesAsync(IReadOnlyList<int> pages) => DeletePagesCoreAsync(pages);

    private async Task<bool> DeletePagesCoreAsync(IReadOnlyList<int> pages, bool confirm = true, string? cutPath = null)
    {
        if (_document is null)
        {
            return false;
        }

        if (pages.Count >= _document.PageCount)
        {
            _dialogs.ShowError(ErrorCatalog.PageKeepOne);
            return false;
        }

        var label = pages.Count == 1 ? Loc.F("page {0}", pages[0] + 1) : Loc.F("{0} pages ({1})", pages.Count, FormatPageList(pages));
        if (confirm && !_dialogs.Confirm(Loc.F("Delete {0}?\n\nUndo (Ctrl+Z) brings the pages back. Nothing changes on disk until you save.", label)))
        {
            return false;
        }

        var sorted = pages.Distinct().Order().ToArray();
        var focus = Math.Max(0, sorted[0] - 1);
        var clipboard = PageClipboard;
        return await RunPageToolAsync("Deleting pages…", async document =>
        {
            var removed = await SnapshotPagesAsync(document, sorted);
            var removedMarks = await DeletePagesWithReadingMarksAsync(document, sorted);
            _undo.KeepOnlyPageSteps();
            _undo.Push(
                () => _ = RunPageToolAsync("Restoring pages…", async d =>
                {
                    await RestorePagesAsync(d, removed, sorted);
                    RestoreReadingMarks(d, removedMarks);
                    if (cutPath is not null) clipboard.CancelCut(cutPath);
                }, sorted[0]),
                () => _ = RunPageToolAsync("Deleting pages…", async d =>
                {
                    removedMarks = await DeletePagesWithReadingMarksAsync(d, sorted);
                    if (cutPath is not null) clipboard.MarkCut(cutPath);
                }, focus),
                pageStep: true);
        }, focus, commitFirst: true);
    }

    /// <summary>Moves <paramref name="pages"/> so they start at <paramref name="destination"/> (index after removal).</summary>
    public Task MovePagesAsync(IReadOnlyList<int> pages, int destination)
    {
        if (_document is null)
        {
            return Task.CompletedTask;
        }

        var sorted = pages.Distinct().Order().ToArray();
        var before = Enumerable.Range(0, _document.PageCount).ToList();
        var after = SimulateMove(before, sorted, destination);
        if (after.SequenceEqual(before))
        {
            return Task.CompletedTask;
        }

        return RunPageToolAsync("Moving pages…", async document =>
        {
            await MovePagesWithReadingMarksAsync(document, sorted, destination);
            _undo.KeepOnlyPageSteps(); // Other steps (e.g. annotations) refer to the old page numbers.
            _undo.Push(
                () => _ = RunPageToolAsync("Moving pages…", d => ReorderAsync(d, after, before), sorted[0]),
                () => _ = RunPageToolAsync("Moving pages…", d => ReorderAsync(d, before, after), destination),
                pageStep: true);
        }, destination, commitFirst: true);
    }

    public async Task InsertPagesFromFileAsync(int insertIndex)
    {
        if (_document is null || _dialogs.PickPdfFile() is not { } path)
        {
            return;
        }

        await InsertPagesAsync(path, insertIndex);
    }

    private async Task<bool> InsertPagesAsync(string path, int insertIndex, bool pasted = false)
    {
        var source = await OpenSourceAsync(path);
        if (source is null)
        {
            return false;
        }

        await using (source)
        {
            var count = source.PageCount;
            return await RunPageToolAsync("Inserting pages…", async document =>
            {
                await InsertPagesWithReadingMarksAsync(document, source, insertIndex);
                var inserted = Enumerable.Range(insertIndex, count).ToArray();
                PageSnapshot? copy = null; // Taken when first undone, for redo (the source file may change).
                IReadOnlyList<ReaderBookmark> removedMarks = [];
                _undo.KeepOnlyPageSteps();
                _undo.Push(
                    () => _ = RunPageToolAsync("Removing inserted pages…", async d =>
                    {
                        copy ??= await SnapshotPagesAsync(d, inserted);
                        removedMarks = await DeletePagesWithReadingMarksAsync(d, inserted);
                    }, Math.Max(0, insertIndex - 1)),
                    () => _ = RunPageToolAsync("Inserting pages…", async d =>
                    {
                        if (copy is null) return;
                        await InsertPagesWithReadingMarksAsync(d, copy.Document, insertIndex);
                        RestoreReadingMarks(d, removedMarks);
                    }, insertIndex),
                    pageStep: true);
                StatusMessage = pasted ? Loc.F("Pasted {0} pages", count) : count == 1
                    ? Loc.F("Inserted 1 page from {0}", Path.GetFileName(path))
                    : Loc.F("Inserted {0} pages from {1}", count, Path.GetFileName(path));
            }, insertIndex, commitFirst: true);
        }
    }

    public async Task ExtractPagesAsync(IReadOnlyList<int> pages)
    {
        if (!await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Assemble, "extract its pages") || _document is not { } document)
        {
            return;
        }

        var name = $"{Path.GetFileNameWithoutExtension(document.FilePath)} (pages {FormatPageList(pages)}).pdf";
        if (_dialogs.PickSavePdfFile(name, Path.GetDirectoryName(document.FilePath), "Extract pages") is not { } path
            || !CheckNotOpenFile(path))
        {
            return;
        }

        if (BlockedByTextEdits())
        {
            return;
        }

        await RunBusyAsync("Extracting pages…", async () =>
        {
            await CommitOverlaysAsync(document);
            await document.ExtractPagesAsync(pages, path);
            StatusMessage = Loc.F("Saved {0}", Path.GetFileName(path));
        });
    }

    // --- File tools ------------------------------------------------------------------------------

    /// <summary>A bookmark title cut to a length that keeps the full path well under Windows' limit.</summary>
    private static string Shorten(string title)
    {
        title = title.Replace(": ", " - ", StringComparison.Ordinal); // A colon cannot be in a file name.
        return title.Length <= 80 ? title : title[..80].TrimEnd();
    }

    private async Task SplitAsync()
    {
        if (!await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Assemble, "split it"))
        {
            return;
        }

        if (_document is not { } document
            || _dialogs.AskSplit(document.PageCount, Path.GetDirectoryName(document.FilePath)!, await document.GetBookmarksAsync()) is not { } plan)
        {
            return;
        }

        // Split at bookmarks: "01 - Chapter title.pdf", numbered so the files sort in the document's order.
        var baseName = Path.GetFileNameWithoutExtension(document.FilePath);
        var digits = Math.Max(2, plan.Parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        var outputs = plan.Parts
            .Select((part, i) => Path.Combine(plan.Folder, plan.Titles is { } titles
                ? $"{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0')} - {Sanitize(Shorten(titles[i] is { Length: > 0 } t ? t : baseName))}.pdf"
                : $"{baseName} (part {i + 1}).pdf"))
            .ToList();
        var existing = outputs.Count(File.Exists);
        if (existing > 0 && !_dialogs.Confirm(Loc.F("{0} of the {1} files already exist in this folder. Replace them?", existing, outputs.Count)))
        {
            return;
        }

        if (outputs.Any(path => !CheckNotOpenFile(path)))
        {
            return;
        }

        if (BlockedByTextEdits())
        {
            return;
        }

        await RunBusyAsync("Splitting…", async () =>
        {
            await CommitOverlaysAsync(document);
            for (var i = 0; i < plan.Parts.Count; i++)
            {
                StatusMessage = Loc.T("Splitting…") + $" {i + 1}/{plan.Parts.Count}";
                await document.ExtractPagesAsync(plan.Parts[i], outputs[i]);
            }

            StatusMessage = Loc.F("Saved {0} files to {1}", outputs.Count, plan.Folder);
        });
    }

    private async Task CombineFilesAsync()
    {
        var initial = _document is null ? [] : new[] { _document.FilePath };
        if (_dialogs.AskCombine(initial) is not { Count: > 0 } files)
        {
            return;
        }

        var suggested = Path.GetFileNameWithoutExtension(files[0]) + " (combined).pdf";
        if (_dialogs.PickSavePdfFile(suggested, Path.GetDirectoryName(files[0]), "Save combined PDF") is not { } output)
        {
            return;
        }

        if (files.Any(file => string.Equals(Path.GetFullPath(file), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.ShowError(ErrorCatalog.PageCombineName);
            return;
        }

        var sources = new List<IPdfDocument>();
        try
        {
            foreach (var file in files)
            {
                if (await OpenSourceAsync(file) is not { } source)
                {
                    return;
                }

                sources.Add(source);
            }

            var done = await RunBusyAsync("Combining…", () => _engine.CombineAsync(sources, output));
            if (!done)
            {
                return;
            }
        }
        finally
        {
            foreach (var source in sources)
            {
                await source.DisposeAsync();
            }
        }

        await OpenFileAsync(output);
    }

    private async Task ImagesToPdfAsync()
    {
        if (_dialogs.AskImagesToPdf() is not { } request)
        {
            return;
        }

        var suggested = Path.GetFileNameWithoutExtension(request.Files[0]) + (request.Files.Count > 1 ? " and more" : string.Empty) + ".pdf";
        if (_dialogs.PickSavePdfFile(suggested, Path.GetDirectoryName(request.Files[0]), "Save PDF") is not { } output
            || !CheckNotOpenFile(output))
        {
            return;
        }

        var done = await RunBusyAsync("Creating PDF…", async () =>
        {
            var inputs = new List<PdfImageInput>();
            foreach (var file in request.Files)
            {
                inputs.Add(await Task.Run(() => ImageInputLoader.Load(file)));
            }

            await _engine.CreateFromImagesAsync(inputs, request.Options, output);
        });

        if (done)
        {
            await OpenFileAsync(output);
        }
    }

    private async Task CompressAsync()
    {
        if (!await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Modify, "change it") || _document is not { } document)
        {
            return;
        }

        // Compression reads the file on disk, so unsaved changes must be saved (or left out) first.
        if (!await ConfirmDiscardOrSaveAsync() || _document is null)
        {
            return;
        }

        if (_dialogs.AskCompression() is not { } options)
        {
            return;
        }

        var source = _document.FilePath;
        var suggested = Path.GetFileNameWithoutExtension(source) + " (compressed).pdf";
        if (_dialogs.PickSavePdfFile(suggested, Path.GetDirectoryName(source), "Save compressed copy") is not { } output
            || !CheckNotOpenFile(output))
        {
            return;
        }

        PdfCompressionResult? result = null;
        var done = await RunBusyAsync("Compressing…", async () =>
            result = await PdfCompressor.CompressAsync(source, _password, output, options, new WpfImageCodec()));
        if (!done || result is null)
        {
            return;
        }

        var summary = $"{FormatSize(result.OriginalSize)} → {FormatSize(result.CompressedSize)}";
        var message = result.CompressedSize < result.OriginalSize
            ? Loc.F("Saved \"{0}\": {1} ({2:P0} smaller, {3} image(s) recompressed).\n\nOpen the compressed copy?", Path.GetFileName(output), summary, result.Reduction, result.ImagesRecompressed)
            : Loc.F("Saved \"{0}\", but it is not smaller ({1}). This PDF has little to compress.\n\nOpen it anyway?", Path.GetFileName(output), summary);
        if (_dialogs.AskYesNo(message))
        {
            await OpenFileAsync(output);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Runs a page-structure change on the open document, then rebuilds the page list around
    /// <paramref name="focusPage"/>. Overlays are committed first because page numbers change.
    /// </summary>
    private async Task<bool> RunPageToolAsync(string busyText, Func<IPdfDocument, Task> change, int focusPage, bool commitFirst = false)
    {
        if (!ConfirmFirstEdit() || !await EnsureAllowedAsync(Core.Pdf.Security.PdfPermissions.Assemble, "rearrange its pages") || _document is not { } document)
        {
            return false;
        }

        if (BlockedByTextEdits())
        {
            return false;
        }

        if (_fieldLooks.Count > 0)
        {
            // Field fonts and pictures are written when saving, by each widget's place on its page.
            _dialogs.ShowInfo("Save the filled-in form first (Ctrl+S), then use the page tools.");
            return false;
        }

        if (commitFirst)
        {
            await _signatureFirstPassTask;
            if (document != _document) return false;
            if (HasSignatures && !_dialogs.Confirm(Loc.T("Editing this item will make digital signatures show that the document was changed after signing. Continue?")))
                return false;
        }

        return await RunBusyAsync(busyText, async () =>
        {
            try
            {
                if (commitFirst)
                {
                    await CommitOverlaysAsync(document);
                }

                await change(document);
                _documentModified = true;
            }
            finally
            {
                if (document == _document)
                {
                    RebuildPages(focusPage); // Also after a failure: PDFium may have changed part of the structure.
                }
            }
        });
    }

    // --- Copies of removed pages, for undo -------------------------------------------------------

    /// <summary>Pages copied into a temporary PDF so a delete can be undone. Removed with the edits.</summary>
    private sealed record PageSnapshot(IPdfDocument Document, string? Path);

    private readonly List<PageSnapshot> _pageSnapshots = [];

    /// <summary>Where page copies live while the app runs; left-overs from a crash are removed at the next start.</summary>
    public static string PageSnapshotFolder => PdfTemporaryFiles.Shared.GetFolder("undo");

    private async Task<PageSnapshot> SnapshotPagesAsync(IPdfDocument document, IReadOnlyList<int> sortedPages)
    {
        if (document.IsEncrypted)
        {
            var bytes = await document.ExtractPagesToBytesAsync(sortedPages);
            var memory = new PageSnapshot(await _engine.OpenBytesAsync(bytes, document.FilePath), null);
            _pageSnapshots.Add(memory);
            return memory;
        }

        Directory.CreateDirectory(PageSnapshotFolder);
        var path = Path.Combine(PageSnapshotFolder, Guid.NewGuid().ToString("N") + ".pdf");
        await document.ExtractPagesAsync(sortedPages, path);
        var snapshot = new PageSnapshot(await _engine.OpenAsync(path), path);
        _pageSnapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>Puts copied pages back where they were: page i of the copy goes to <paramref name="sortedPages"/>[i].</summary>
    private async Task RestorePagesAsync(IPdfDocument document, PageSnapshot snapshot, IReadOnlyList<int> sortedPages)
    {
        for (var i = 0; i < sortedPages.Count; i++)
        {
            await InsertPagesWithReadingMarksAsync(document, snapshot.Document, sortedPages[i], [i]);
        }
    }

    private void ReleasePageSnapshots()
    {
        foreach (var snapshot in _pageSnapshots)
        {
            _ = ReleaseAsync(snapshot);
        }

        _pageSnapshots.Clear();

        static async Task ReleaseAsync(PageSnapshot snapshot)
        {
            await snapshot.Document.DisposeAsync();
            if (snapshot.Path is { } path) TryDelete(path);
        }
    }

    private void RebuildPages(int focusPage)
    {
        ClearSavedItemSelection();
        if (_document is not { } document)
        {
            return;
        }

        ClearSearch();
        CancelPageGesture();
        ClearTextSelection();
        SelectedComment = null;
        foreach (var page in Pages)
        {
            page.CancelPendingRender();
            page.PendingThumbnail?.Cancel();
        }

        var pages = document.PageSizes.Select((size, index) => new PageViewModel(index, size)).ToList();
        _maxPageWidthPoints = pages.Count == 0 ? 0 : pages.Max(page => page.WidthPoints);
        Pages = new ObservableCollection<PageViewModel>(pages);
        RefreshReadingMarks();
        // Page tools discard text editors. Their old page numbers/coordinates must go too.
        _textEditFrames.Clear();
        var focus = Math.Clamp(focusPage, 0, Math.Max(0, pages.Count - 1));
        _currentPageIndex = focus;
        ApplyZoom(_zoomMode == ZoomMode.Custom ? _zoom : ComputeFitZoom(_zoomMode), keepAnchor: false);
        if (pages.Count > 0)
        {
            RequestScroll(_pageTops[focus] - PageGap, navigationTarget: focus);
        }

        RaiseDocumentPropertiesChanged();
        OnEditsChanged();
        _ = LoadBookmarksAsync(document);
        _ = LoadCommentsAsync(document);
    }

    /// <summary>Runs work with the UI disabled; reports PDF and file errors. Returns false on failure.</summary>
    private async Task<bool> RunBusyAsync(string busyText, Func<Task> work)
    {
        busyText = Loc.T(busyText);
        if (IsBusy)
        {
            return false;
        }

        CommitEditsRequested?.Invoke(this, EventArgs.Empty);
        IsBusy = true;
        StatusMessage = busyText;
        try
        {
            await work();
            return true;
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _dialogs.ShowError(ErrorCatalog.PageOperation, ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            if (StatusMessage == busyText || StatusMessage.EndsWith('…'))
            {
                StatusMessage = string.Empty;
            }
        }
    }

    /// <summary>Opens another PDF for reading (asking for its password if needed); null if cancelled or unreadable.</summary>
    private async Task<IPdfDocument?> OpenSourceAsync(string path)
    {
        string? password = null;
        while (true)
        {
            try
            {
                return await _engine.OpenAsync(path, password);
            }
            catch (PdfPasswordRequiredException ex)
            {
                password = _dialogs.PromptPassword(Path.GetFileName(path), ex.PasswordWasWrong);
                if (password is null)
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError(ErrorCatalog.FileOpen, ex);
                return null;
            }
        }
    }

    /// <summary>Writing over the open document would fail (it is being read), so stop early with a clear message.</summary>
    private bool CheckNotOpenFile(string path)
    {
        if (_document is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_document.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowError(ErrorCatalog.PageOutputName);
            return false;
        }

        return true;
    }

    /// <summary>Applies the moves that turn page order <paramref name="from"/> into <paramref name="to"/>.</summary>
    private async Task ReorderAsync(IPdfDocument document, IReadOnlyList<int> from, IReadOnlyList<int> to)
    {
        var current = from.ToList();
        for (var position = 0; position < to.Count; position++)
        {
            var at = current.IndexOf(to[position]);
            if (at != position)
            {
                await MovePagesWithReadingMarksAsync(document, [at], position);
                current = SimulateMove(current, [at], position);
            }
        }
    }

    /// <summary>Mirrors FPDF_MovePages: take the pages out (keeping their order), insert them at <paramref name="destination"/>.</summary>
    private static List<int> SimulateMove(IReadOnlyList<int> order, IReadOnlyList<int> sortedPositions, int destination)
    {
        var moving = sortedPositions.Select(position => order[position]).ToList();
        var rest = order.Where((_, position) => !sortedPositions.Contains(position)).ToList();
        rest.InsertRange(Math.Clamp(destination, 0, rest.Count), moving);
        return rest;
    }

    /// <summary>"2-4, 7" for pages 1, 2, 3, 6 (0-based in, 1-based out).</summary>
    internal static string FormatPageList(IReadOnlyList<int> pages)
    {
        var sorted = pages.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1)
            {
                i++;
            }

            parts.Add(start == sorted[i] ? $"{start + 1}" : $"{start + 1}-{sorted[i] + 1}");
        }

        return string.Join(", ", parts);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => Loc.F("{0} bytes", bytes),
    };

    private static string UniqueSiblingPath(string path, string label)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = Path.GetFileNameWithoutExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(folder, i == 1 ? $"{name} ({label}).pdf" : $"{name} ({label} {i}).pdf");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
