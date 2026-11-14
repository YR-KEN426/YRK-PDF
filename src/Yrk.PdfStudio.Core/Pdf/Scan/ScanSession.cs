namespace Yrk.PdfStudio.Core.Pdf.Scan;

public sealed class ScanSessionPage
{
    internal ScanSessionPage(string path, ScannedPage page)
    {
        FilePath = path;
        DpiX = page.DpiX;
        DpiY = page.DpiY;
        ColorMode = page.ColorMode;
        Notices = page.Notices?.ToArray() ?? [];
        Adjustments = page.Adjustments ?? new();
    }

    public string FilePath { get; }
    public double DpiX { get; }
    public double DpiY { get; }
    /// <summary>Acquisition mode; null preserves the settings fallback for callers adding legacy raw pages.</summary>
    public ScanColorMode? ColorMode { get; }
    public IReadOnlyList<string> Notices { get; }
    public int Rotation { get; set; }
    public NormalizedRect? Crop { get; set; }
    public bool IsBlank { get; set; }
    private ScanPageAdjustments _adjustments = new();
    public ScanPageAdjustments Adjustments
    {
        get => _adjustments;
        set { ArgumentNullException.ThrowIfNull(value); value.Validate(); _adjustments = value; }
    }
    public int Brightness { get => Adjustments.Brightness; set => Adjustments = Adjustments with { Brightness = value }; }
    public int Contrast { get => Adjustments.Contrast; set => Adjustments = Adjustments with { Contrast = value }; }
    public int Sharpness { get => Adjustments.Sharpness; set => Adjustments = Adjustments with { Sharpness = value }; }
    public bool WhiteBackground { get => Adjustments.WhiteBackground; set => Adjustments = Adjustments with { WhiteBackground = value }; }
    public bool Straighten { get => Adjustments.Straighten ?? true; set => Adjustments = Adjustments with { Straighten = value }; }
}

/// <summary>Owns only its freshly created directory. No caller-supplied file is ever deleted.</summary>
public sealed class ScanSession : IDisposable
{
    private readonly List<ScanSessionPage> _pages = [];
    private readonly HashSet<ScanSessionPage> _temporaryPages = [];
    private readonly object _gate = new();
    private readonly FileStream _sessionLock;
    private bool _disposed;

    public ScanSession()
    {
        DirectoryPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YRK PDF", "scan", Guid.NewGuid().ToString("N")));
        RequireNoLinks(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        RequireNoLinks(DirectoryPath);
        _sessionLock = new FileStream(Path.Combine(DirectoryPath, ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    public string DirectoryPath { get; }
    public IReadOnlyList<ScanSessionPage> Pages { get { lock (_gate) return _pages.ToArray(); } }

    /// <summary>Session-owned input for glass previews and card sides; never a document page.</summary>
    public ScanSessionPage AddTemporary(ScannedPage page, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var saved = Add(page, ct);
            _pages.Remove(saved);
            _temporaryPages.Add(saved);
            return saved;
        }
    }

    public void DeleteTemporary(ScanSessionPage page)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_temporaryPages.Contains(page)) throw new ArgumentException("Not a temporary page.", nameof(page));
            DeleteOwnedPath(page.FilePath);
            _temporaryPages.Remove(page);
        }
    }

    public ScanSessionPage Add(ScannedPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? path = null;
            var created = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanPageProcessor.Validate(page);
                path = CreateTemporaryPath(".bgra");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    created = true;
                    writer.Write(page.Bitmap.Width);
                    writer.Write(page.Bitmap.Height);
                    writer.Write(page.Bitmap.Stride);
                    writer.Write(page.Bitmap.Pixels);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var item = new ScanSessionPage(path, page);
                _pages.Add(item);
                return item;
            }
            catch
            {
                // A failed page must not invalidate pages already shown in the scan dialog.
                if (created)
                {
                    try { DeleteOwnedPath(path!); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
        }
    }

    /// <summary>Reports synchronously so saving finishes before the scanner completes or throws.</summary>
    public async Task ScanAsync(IScannerService scanner, ScanDevice device, ScanSettings settings,
        IProgress<ScanSessionPage>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        try
        {
            await scanner.ScanAsync(device, settings, new PageSink(page =>
            {
                var saved = Add(page with { ColorMode = settings.ColorMode }, cancellationToken);
                progress?.Report(saved);
            }), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch { Dispose(); throw; }
    }

    private sealed class PageSink(Action<ScannedPage> receive) : IProgress<ScannedPage>
    {
        public void Report(ScannedPage value) => receive(value);
    }

    public ScannedPage Read(ScanSessionPage page)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pages.Contains(page) && !_temporaryPages.Contains(page)) throw new ArgumentException("Page belongs to another session.", nameof(page));
            RequireOwned(page.FilePath);
            RequireNoLinks(page.FilePath);
            using var stream = File.OpenRead(page.FilePath);
            using var reader = new BinaryReader(stream);
            var w = reader.ReadInt32();
            var h = reader.ReadInt32();
            var stride = reader.ReadInt32();
            var count = checked(stride * h);
            if (w <= 0 || h <= 0 || stride < checked(w * 4) || stream.Length - stream.Position != count)
                throw new InvalidDataException("Invalid scan page file.");
            return new(new(w, h, stride, reader.ReadBytes(count)), page.DpiX, page.DpiY, page.Notices, page.Adjustments, page.ColorMode);
        }
    }

    public void Move(int from, int to)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((uint)from >= _pages.Count || (uint)to >= _pages.Count) throw new ArgumentOutOfRangeException(nameof(from));
            var page = _pages[from];
            _pages.RemoveAt(from);
            _pages.Insert(to, page);
        }
    }

    public void RemoveAt(int index)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DeleteOwnedPath(_pages[index].FilePath);
            _pages.RemoveAt(index);
        }
    }

    internal string CreateTemporaryPath(string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireNoLinks(DirectoryPath);
        return Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + extension);
    }

    /// <summary>Rejects traversal and sibling-prefix paths. Removes links themselves, never their targets.</summary>
    internal void DeleteOwnedPath(string path)
        => DeleteOwnedPath(DirectoryPath, path, TempRoot);

    private static void DeleteOwnedPath(string root, string path, string tempRoot)
    {
        var full = RequireOwned(root, path);
        // Check ancestors, but allow deletion of the final link itself.
        RequireNoLinks(Path.GetDirectoryName(full)!, tempRoot);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(full); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.Directory) == 0) File.Delete(full);
        else
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(full)) DeleteOwnedPath(root, child, tempRoot);
            Directory.Delete(full, recursive: false);
        }
    }

    private string RequireOwned(string path) => RequireOwned(DirectoryPath, path);

    private static string RequireOwned(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path is outside this scan session.", nameof(path));
        return full;
    }

    // Only the folders YRK creates (TEMP\YRK PDF\...) are checked: a user's TEMP or profile may itself
    // sit behind a junction (profile moved to another drive), and that must not block scanning.
    private static readonly string TempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

    public static int SweepStale(DateTime nowUtc, TimeSpan maxAge)
        => SweepStale(nowUtc, maxAge, Path.Combine(TempRoot, "YRK PDF", "scan"), TempRoot);

    internal static int SweepStale(DateTime nowUtc, TimeSpan maxAge, string scanRoot, string tempRoot)
    {
        var removed = 0;
        try
        {
            tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempRoot));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scanRoot));
            if (!string.Equals(root, Path.Combine(tempRoot, "YRK PDF", "scan"), StringComparison.OrdinalIgnoreCase)) return 0;
            RequireNoLinks(root, tempRoot);
            if (!Directory.Exists(root)) return 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    var created = File.GetCreationTimeUtc(path);
                    var written = File.GetLastWriteTimeUtc(path);
                    if (nowUtc - (created > written ? created : written) <= maxAge) continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                    {
                        var lockPath = Path.Combine(path, ".lock");
                        RequireNoLinks(lockPath, tempRoot);
                        // Keep exclusive ownership while removing contents; active sessions keep this
                        // handle open even when they have not added a page for more than maxAge.
                        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        {
                            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                                if (!string.Equals(child, lockPath, StringComparison.OrdinalIgnoreCase))
                                    DeleteOwnedPath(root, child, tempRoot);
                        }
                        DeleteOwnedPath(root, lockPath, tempRoot);
                        Directory.Delete(path, recursive: false);
                    }
                    else DeleteOwnedPath(root, path, tempRoot);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception) { /* Startup maintenance must never prevent startup. */ }
        return removed;
    }

    private static void RequireNoLinks(string path) => RequireNoLinks(path, TempRoot);

    private static void RequireNoLinks(string path, string tempRoot)
    {
        for (var current = Path.GetFullPath(path);
             current is not null && current.Length > tempRoot.Length
                 && current.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scan temporary paths must not traverse junctions or symbolic links.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pages.Clear();
            _temporaryPages.Clear();
            _sessionLock.Dispose();
            // A file held open (e.g. by antivirus) must not make Dispose throw; leftovers are removed
            // by the stale-folder sweep at the next start.
            try { DeleteOwnedPath(DirectoryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
