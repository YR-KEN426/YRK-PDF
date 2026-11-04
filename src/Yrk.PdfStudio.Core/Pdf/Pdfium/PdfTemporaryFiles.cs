namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Session-owned temporary files. Shared read leases prove a session is still in use.</summary>
public sealed class PdfTemporaryFiles : IDisposable
{
    private const string Marker = ".session-lock";
    private readonly string _temp;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, FileStream> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Mutex _gate;
    private bool _disposed;

    public static PdfTemporaryFiles Shared { get; } = new(Path.GetTempPath());

    public PdfTemporaryFiles(string tempRoot)
    {
        _temp = Path.GetFullPath(tempRoot);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_temp.ToUpperInvariant())));
        _gate = new Mutex(false, "Local\\YRK-PDF-Temp-" + hash);
    }

    private string Root(string area) => area == "Working"
        ? Path.Combine(_temp, "YRK", "Working") : Path.Combine(_temp, "YRK PDF", area);

    public string GetFolder(string area)
    {
        if (area is not ("undo" or "drawings" or "revisions" or "signatures" or "redact" or "Working"))
            throw new ArgumentException("Unknown temporary area.", nameof(area));
        return Guard(() =>
        {
            var folder = Path.Combine(Root(area), _session);
            if (!_leases.ContainsKey(folder))
            {
                RequireSafe(folder);
                Directory.CreateDirectory(folder);
                var marker = Path.Combine(folder, Marker);
                File.WriteAllText(marker, _session);
                _leases.Add(folder, new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read));
                _owned.Add(folder);
            }
            return folder;
        });
    }

    /// <summary>A preview can be opened by another process; that process retains the originating session too.</summary>
    public void RetainForPath(string path) => Guard(() =>
    {
        var full = Path.GetFullPath(path);
        foreach (var root in Roots())
        {
            if (!IsUnder(full, root)) continue;
            var relative = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar);
            if (relative.Length < 2 || !Guid.TryParseExact(relative[0], "N", out _)) continue;
            var folder = Path.Combine(root, relative[0]);
            if (!_leases.ContainsKey(folder) && Safe(folder) && File.Exists(Path.Combine(folder, Marker)))
                _leases.Add(folder, new FileStream(Path.Combine(folder, Marker), FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        return true;
    });

    private IEnumerable<string> Roots() => new[] { "undo", "drawings", "revisions", "signatures", "redact", "Working" }.Select(Root);

    /// <param name="includeLegacy">Only true when no older, unmarked instance can still own the legacy files.</param>
    public void Cleanup(bool includeLegacy = false) => Guard(() =>
    {
        foreach (var root in Roots())
        {
            if (!Safe(root) || !Directory.Exists(root)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (!Safe(entry)) continue;
                if (Directory.Exists(entry) && File.Exists(Path.Combine(entry, Marker))) CleanupSession(entry);
                else if (includeLegacy) DeleteTree(entry);
            }
        }

        if (includeLegacy)
        {
            // Pre-session versions put signed revision previews directly in YRK PDF.
            var old = Path.Combine(_temp, "YRK PDF");
            if (Safe(old) && Directory.Exists(old))
                foreach (var file in Directory.EnumerateFiles(old)) DeleteTree(file);
            if (Directory.Exists(_temp))
                foreach (var entry in Directory.EnumerateFileSystemEntries(_temp, "yrk-redact-*")) DeleteTree(entry);
        }
        return true;
    });

    private void CleanupSession(string folder)
    {
        var marker = Path.Combine(folder, Marker);
        if (!Safe(marker)) return;
        try
        {
            // PDFium's document share flags cannot prove liveness. This exclusive open can.
            using (var lease = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                    if (!string.Equals(entry, marker, StringComparison.OrdinalIgnoreCase)) DeleteTree(entry);
            }
            File.Delete(marker);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes only a subtree owned by this session, without following reparse points.</summary>
    public void DeleteOwnedDirectory(string path) => Guard(() =>
    {
        var full = Path.GetFullPath(path);
        if (!_owned.Any(root => IsUnder(full, root))) throw new ArgumentException("Not owned by this session.", nameof(path));
        DeleteTree(full);
        return true;
    });

    private void DeleteTree(string path)
    {
        if (!Safe(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path)) DeleteTree(entry);
                if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            else File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsUnder(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Check every existing ancestor below the temp folder. Never recurse through a junction. The temp folder itself
    // and what is above it are the user's choice (a moved or redirected profile), so they are not checked.
    private bool Safe(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsUnder(full, _temp)) return false;
        try
        {
            for (var current = full; current is not null && IsUnder(current, _temp); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void RequireSafe(string path)
    {
        if (!Safe(path)) throw new IOException("The temporary folder contains a reparse point.");
    }

    private T Guard<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { _gate.WaitOne(); }
        catch (AbandonedMutexException) { }
        try { return work(); }
        finally { _gate.ReleaseMutex(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Guard(() =>
        {
            foreach (var lease in _leases.Values) lease.Dispose();
            _leases.Clear();
            foreach (var folder in _owned) CleanupSession(folder);
            return true;
        });
        _disposed = true;
        _gate.Dispose();
    }

    public static async Task WriteDurableAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }
}
