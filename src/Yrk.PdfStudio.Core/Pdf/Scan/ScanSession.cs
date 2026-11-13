namespace Yrk.PdfStudio.Core.Pdf.Scan;

public sealed class ScanSessionPage
{
    internal ScanSessionPage(string path, ScannedPage page)
    {
        FilePath = path;
        DpiX = page.DpiX;
        DpiY = page.DpiY;
        Notices = page.Notices?.ToArray() ?? [];
    }

    public string FilePath { get; }
    public double DpiX { get; }
    public double DpiY { get; }
    public IReadOnlyList<string> Notices { get; }
    public int Rotation { get; set; }
    public NormalizedRect? Crop { get; set; }
    public bool IsBlank { get; set; }
}

/// <summary>Owns only its freshly created directory. No caller-supplied file is ever deleted.</summary>
public sealed class ScanSession : IDisposable
{
    private readonly List<ScanSessionPage> _pages = [];
    private readonly object _gate = new();
    private bool _disposed;

    public ScanSession()
    {
        DirectoryPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "YRK PDF", "scan", Guid.NewGuid().ToString("N")));
        RequireNoLinks(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        RequireNoLinks(DirectoryPath);
    }

    public string DirectoryPath { get; }
    public IReadOnlyList<ScanSessionPage> Pages { get { lock (_gate) return _pages.ToArray(); } }

    public ScanSessionPage Add(ScannedPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanPageProcessor.Validate(page);
                var path = CreateTemporaryPath(".bgra");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
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
            catch { Dispose(); throw; }
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
                var saved = Add(page, cancellationToken);
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
            if (!_pages.Contains(page)) throw new ArgumentException("Page belongs to another session.", nameof(page));
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
            return new(new(w, h, stride, reader.ReadBytes(count)), page.DpiX, page.DpiY, page.Notices);
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
    {
        var full = RequireOwned(path);
        // Check ancestors, but allow deletion of the final link itself.
        RequireNoLinks(Path.GetDirectoryName(full)!);
        if (!File.Exists(full) && !Directory.Exists(full)) return;
        var attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.Directory) == 0) File.Delete(full);
        else
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(full)) DeleteOwnedPath(child);
            Directory.Delete(full, recursive: false);
        }
    }

    private string RequireOwned(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(full, DirectoryPath, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path is outside this scan session.", nameof(path));
        return full;
    }

    private static void RequireNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scan temporary paths must not traverse junctions or symbolic links.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            DeleteOwnedPath(DirectoryPath);
            _pages.Clear();
            _disposed = true;
        }
    }
}
