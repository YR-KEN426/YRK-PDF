using System.IO;
using System.Text.Json;

namespace Yrk.PdfStudio.App.Services;

internal sealed record ReaderBookmark(int PageIndex, string Name, string Color, DateTimeOffset CreatedUtc);
internal sealed record ReaderBookmarkChange(IReadOnlyList<ReaderBookmark> Bookmarks, IReadOnlyList<ReaderBookmark> Removed);

/// <summary>Local reader bookmarks only. Page indices are zero-based; no PDF is opened or written.</summary>
internal static class ReaderBookmarks
{
    internal const int MaxCount = 200;
    internal const int MaxBookmarksPerFile = 100;
    internal static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "reader-bookmarks.json");

    private static readonly object Gate = new();
    private sealed record Document(IReadOnlyList<ReaderBookmark> Bookmarks, DateTimeOffset LastUsed);

    public static IReadOnlyList<ReaderBookmark> Get(string path)
    {
        lock (Gate)
        {
            var full = Normalize(path);
            var documents = Load();
            if (full is null || !documents.TryGetValue(full, out var document)) return [];
            documents[full] = document with { LastUsed = DateTimeOffset.UtcNow };
            Write(documents);
            return document.Bookmarks.ToArray();
        }
    }

    /// <summary>Adds or edits a page, preserving its creation time. False means invalid input or the file is full.</summary>
    public static bool Add(string path, int pageIndex, string name, string color)
    {
        lock (Gate)
        {
            var full = Normalize(path);
            if (full is null || pageIndex < 0 || !IsColor(color)) return false;
            var documents = Load();
            var bookmarks = documents.TryGetValue(full, out var document) ? document.Bookmarks.ToList() : [];
            var index = bookmarks.FindIndex(b => b.PageIndex == pageIndex);
            if (index < 0 && bookmarks.Count >= MaxBookmarksPerFile) return false;
            var bookmark = new ReaderBookmark(pageIndex, ShortName(name), color,
                index >= 0 ? bookmarks[index].CreatedUtc : DateTimeOffset.UtcNow);
            if (index >= 0) bookmarks[index] = bookmark;
            else bookmarks.Add(bookmark);
            Store(documents, full, bookmarks);
            return true;
        }
    }

    public static void Remove(string path, int pageIndex)
    {
        lock (Gate)
        {
            var full = Normalize(path);
            var documents = Load();
            if (full is null || !documents.TryGetValue(full, out var document)) return;
            Store(documents, full, document.Bookmarks.Where(b => b.PageIndex != pageIndex));
        }
    }

    /// <summary>Replaces the local list, including original creation times, for page edits and undo.</summary>
    public static void Save(string path, IEnumerable<ReaderBookmark> bookmarks)
    {
        lock (Gate)
        {
            if (Normalize(path) is { } full) Store(Load(), full, bookmarks);
        }
    }

    /// <summary>Save As transfers the source list, replacing the destination list.</summary>
    public static void Move(string oldPath, string newPath)
    {
        lock (Gate)
        {
            var oldFull = Normalize(oldPath);
            var newFull = Normalize(newPath);
            if (newFull is null) return;
            var documents = Load();
            if (oldFull is not null && documents.Remove(oldFull, out var document))
                documents[newFull] = document with { LastUsed = DateTimeOffset.UtcNow };
            else documents.Remove(newFull);
            Write(documents);
        }
    }

    public static void Clear()
    {
        lock (Gate) Write(new(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Pure page replacement/reordering: absent old indices are deleted. Mapping must be one-to-one.</summary>
    public static ReaderBookmarkChange RemapPages(IEnumerable<ReaderBookmark> bookmarks, IReadOnlyDictionary<int, int> oldToNew)
    {
        if (oldToNew.Any(p => p.Key < 0 || p.Value < 0) || oldToNew.Values.Distinct().Count() != oldToNew.Count)
            throw new ArgumentException("Page mapping must have nonnegative, unique destinations.", nameof(oldToNew));
        var remaining = new List<ReaderBookmark>();
        var removed = new List<ReaderBookmark>();
        foreach (var bookmark in bookmarks)
            if (oldToNew.TryGetValue(bookmark.PageIndex, out var page)) remaining.Add(bookmark with { PageIndex = page });
            else removed.Add(bookmark);
        return new(remaining.OrderBy(b => b.PageIndex).ToArray(), removed.ToArray());
    }

    public static ReaderBookmarkChange DeletePages(IEnumerable<ReaderBookmark> bookmarks, IEnumerable<int> pageIndices)
    {
        var deleted = pageIndices.ToHashSet();
        if (deleted.Any(p => p < 0)) throw new ArgumentOutOfRangeException(nameof(pageIndices));
        var source = bookmarks.ToArray();
        return RemapPages(source, source.Select(b => b.PageIndex).Distinct().Where(p => !deleted.Contains(p))
            .ToDictionary(p => p, p => p - deleted.Count(d => d < p)));
    }

    /// <summary>Inserts pageCount pages before insertionIndex; rotation uses an unchanged list.</summary>
    public static ReaderBookmarkChange InsertPages(IEnumerable<ReaderBookmark> bookmarks, int insertionIndex, int pageCount)
    {
        if (insertionIndex < 0) throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        if (pageCount < 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var source = bookmarks.ToArray();
        return RemapPages(source, source.Select(b => b.PageIndex).Distinct()
            .ToDictionary(p => p, p => p < insertionIndex ? p : checked(p + pageCount)));
    }

    /// <summary>newPageOrder lists old page indices in their new order; omitted pages are removed.</summary>
    public static ReaderBookmarkChange ReorderPages(IEnumerable<ReaderBookmark> bookmarks, IReadOnlyList<int> newPageOrder)
    {
        if (newPageOrder.Any(p => p < 0) || newPageOrder.Distinct().Count() != newPageOrder.Count)
            throw new ArgumentException("Page order must contain unique nonnegative indices.", nameof(newPageOrder));
        return RemapPages(bookmarks, newPageOrder.Select((old, index) => (old, index)).ToDictionary(p => p.old, p => p.index));
    }

    private static bool IsColor(string? color) => color is "red" or "orange" or "yellow" or "green" or "blue" or "purple";
    private static string ShortName(string? name) => string.IsNullOrWhiteSpace(name) ? "" : name.Length <= 40 ? name :
        name[..(char.IsHighSurrogate(name[39]) && char.IsLowSurrogate(name[40]) ? 39 : 40)];

    internal static IReadOnlyList<ReaderBookmark> Clean(IEnumerable<ReaderBookmark?> bookmarks) => bookmarks
        .Where(b => b is { PageIndex: >= 0 } && IsColor(b.Color))
        .Select(b => b! with { Name = ShortName(b!.Name) }).GroupBy(b => b.PageIndex).Select(g => g.Last())
        .OrderBy(b => b.PageIndex).Take(MaxBookmarksPerFile).ToArray();

    private static void Store(Dictionary<string, Document> documents, string full, IEnumerable<ReaderBookmark> bookmarks)
    {
        var clean = Clean(bookmarks);
        if (clean.Count == 0) documents.Remove(full);
        else documents[full] = new(clean, DateTimeOffset.UtcNow);
        Write(documents);
    }

    private static string? Normalize(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = Path.GetFullPath(path);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            return full.Equals(temp, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<string, Document> Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, Document?>>(File.ReadAllText(FilePath));
            var documents = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
            if (stored is not null)
                foreach (var pair in stored.OrderBy(p => p.Value?.LastUsed))
                    if (pair.Value is { Bookmarks: not null } document && Normalize(pair.Key) is { } full)
                    {
                        var clean = Clean(document.Bookmarks);
                        if (clean.Count > 0) documents[full] = document with { Bookmarks = clean };
                    }
            return documents.OrderByDescending(p => p.Value.LastUsed).Take(MaxCount)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Write(Dictionary<string, Document> documents)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var limited = documents.OrderByDescending(p => p.Value.LastUsed).Take(MaxCount)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(temporary, JsonSerializer.Serialize(limited));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Local bookmarks must never prevent opening or closing a document.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
