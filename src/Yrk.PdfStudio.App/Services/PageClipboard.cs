using System.IO;
using System.Text.Json;
using System.Windows;

namespace Yrk.PdfStudio.App.Services;

public sealed record PageClipboardData(string? Pages, IReadOnlyList<string> Files);

/// <summary>The OS clipboard is replaceable in tests, including failures while publishing a cut.</summary>
public interface IPageClipboard
{
    PageClipboardData Read();
    void WritePages(string payload);
}

public sealed class WindowsPageClipboard : IPageClipboard
{
    public PageClipboardData Read()
    {
        var data = Clipboard.GetDataObject();
        return new(data?.GetData(PageClipboard.Format, false) as string,
            data?.GetData(DataFormats.FileDrop, false) is string[] files ? files : []);
    }

    public void WritePages(string payload)
    {
        var data = new DataObject();
        data.SetData(PageClipboard.Format, payload, false);
        Clipboard.SetDataObject(data, true);
    }
}

/// <summary>Owns only files allocated by this instance. Clipboard paths are never deletion candidates.</summary>
public sealed class PageClipboard : IDisposable
{
    public const string Format = "YRK.PdfPages";
    private const string FilePrefix = "yrk-pages-";
    public static PageClipboard Shared { get; } = new(new WindowsPageClipboard());
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "YRK PDF", "clipboard");
    public string Folder { get; }
    private readonly IPageClipboard _clipboard;
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EventWaitHandle> _cuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingCuts = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Payload(string Path, int PageCount);
    public sealed record Source(string Path, bool IsPageCopy);

    public PageClipboard(IPageClipboard clipboard, string? folder = null)
    {
        _clipboard = clipboard;
        Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder ?? DefaultFolder));
    }

    public bool HasContent
    {
        get
        {
            try
            {
                var data = _clipboard.Read();
                return data.Pages is not null || data.Files.Any(IsExistingPdf);
            }
            catch (Exception) { return false; } // A busy Windows clipboard must not break command queries.
        }
    }

    // A named event acknowledges a successful paste even in another process. No path from the
    // clipboard is written to or deleted to record this acknowledgement.
    private static string EventName(string path) => @"Local\YRK.PdfPages." + Path.GetFileNameWithoutExtension(path);

    public bool HasUnpastedCut => _pendingCuts.Any(path => !_cuts[path].WaitOne(0));

    public bool ConfirmReplace(IDialogService dialogs) => ConfirmReplace(dialogs.Confirm);

    public bool ConfirmReplace(Func<string, bool> confirm) => !HasUnpastedCut || confirm(Loc.T(
        "Cut pages have not been pasted yet. Copying again will lose them.\n\nContinue?"));

    public bool ConfirmClose(IDialogService dialogs) => ConfirmClose(dialogs.Confirm);

    public bool ConfirmClose(Func<string, bool> confirm) => !HasUnpastedCut || confirm(Loc.T(
        "Cut pages have not been pasted yet. Closing the app will lose them.\n\nContinue closing?"));

    public string Allocate()
    {
        if (!HasSafeAncestors(Folder)) throw new IOException("Unsafe page clipboard folder.");
        Directory.CreateDirectory(Folder);
        if (!HasSafeAncestors(Folder)) throw new IOException("Unsafe page clipboard folder.");
        var path = Path.Combine(Folder, FilePrefix + Guid.NewGuid().ToString("N") + ".pdf");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        _owned.Add(path);
        return path;
    }

    public void Publish(string path, int pageCount, bool cut)
    {
        if (!_owned.Contains(path) || !IsSafePagePath(path) || !IsExistingPdf(path) || pageCount < 1)
            throw new IOException("Invalid page clipboard file.");
        EventWaitHandle? acknowledgement = cut ? new(false, EventResetMode.ManualReset, EventName(path)) : null;
        try { _clipboard.WritePages(JsonSerializer.Serialize(new Payload(path, pageCount))); }
        catch { acknowledgement?.Dispose(); throw; }
        if (acknowledgement is not null) _cuts.Add(path, acknowledgement);
        // Publish first: if Windows rejects the new data, the old clipboard remains usable.
        foreach (var previous in _owned.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            // The caller confirmed abandoning any local unpasted cut before starting the copy.
            _pendingCuts.Remove(previous);
            if (_cuts.Remove(previous, out var oldAcknowledgement)) oldAcknowledgement.Dispose();
            DeleteOwned(previous);
        }
    }

    public void MarkCut(string path)
    {
        if (_cuts.ContainsKey(path)) _pendingCuts.Add(path);
    }

    public void CancelCut(string path) => _pendingCuts.Remove(path);

    public void MarkPasted(Source source)
    {
        if (!source.IsPageCopy || !IsSafePagePath(source.Path) || !IsGeneratedName(source.Path)) return;
        try
        {
            using var acknowledgement = EventWaitHandle.OpenExisting(EventName(source.Path));
            acknowledgement.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { } // An ordinary copy has no pending cut.
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Null means malformed, missing or unsafe page data. Only existing PDFs may reach PDFium.</summary>
    public Source? ReadSource()
    {
        var data = _clipboard.Read();
        if (data.Pages is { } json)
        {
            try
            {
                if (json.Length > 8192) return null;
                var payload = JsonSerializer.Deserialize<Payload>(json);
                return payload is { PageCount: > 0 } && IsSafePagePath(payload.Path) && IsExistingPdf(payload.Path)
                    ? new(Path.GetFullPath(payload.Path), true) : null;
            }
            catch (JsonException) { return null; }
        }
        var file = data.Files.FirstOrDefault(IsExistingPdf);
        return file is null ? null : new(Path.GetFullPath(file), false);
    }

    private static bool IsExistingPdf(string? path) => !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private static bool IsGeneratedName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith(FilePrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[FilePrefix.Length..], "N", out _);
    }

    public bool IsSafePagePath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "." or "..")) return false;
            var full = Path.GetFullPath(path);
            return full.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(full), ".pdf", StringComparison.OrdinalIgnoreCase)
                && HasSafeAncestors(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static bool HasSafeAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }

    public void DeleteOwned(string path)
    {
        // Check the original spelling as well as its full path, before checking ownership.
        if (!IsSafePagePath(path) || !_owned.Contains(path)) return;
        try { File.Delete(path); _owned.Remove(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void SweepStale(DateTime utcNow)
    {
        try
        {
            if (!Directory.Exists(Folder) || !HasSafeAncestors(Folder)) return;
            foreach (var path in Directory.EnumerateFiles(Folder, FilePrefix + "*.pdf", SearchOption.TopDirectoryOnly))
            {
                if (!IsGeneratedName(path) || !IsSafePagePath(path)
                    || File.GetLastWriteTimeUtc(path) >= utcNow.AddDays(-1)) continue;
                // Sweeping generated names in our own directory is the sole exception to in-memory ownership.
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public void Dispose()
    {
        foreach (var path in _owned.ToArray()) DeleteOwned(path);
        foreach (var acknowledgement in _cuts.Values) acknowledgement.Dispose();
        _cuts.Clear();
        _pendingCuts.Clear();
    }
}
