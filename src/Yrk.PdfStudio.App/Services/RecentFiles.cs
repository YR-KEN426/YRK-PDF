using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;


namespace Yrk.PdfStudio.App.Services;

/// <summary>A file in the recent list: its path, name and folder for display.</summary>
public sealed record RecentFile(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>
/// Recently opened files, newest first, shared by all tabs. Kept only on this computer, in
/// %LOCALAPPDATA%\YRK\EasyPdf\recent.json (paths only, never file contents), and can be cleared.
/// </summary>
public static class RecentFiles
{
    public const int MaxCount = 12;

    private static readonly string DefaultFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "recent.json");

    // Async-local overrides keep independent app harnesses from sharing either the user's list or each other's list.
    private sealed class TestStorage(string path, Func<DateTimeOffset> utcNow)
    {
        public string Path { get; } = path;
        public Func<DateTimeOffset> UtcNow { get; } = utcNow;
        public ObservableCollection<RecentFile>? Items { get; set; }
        public TestStorage? Parent { get; init; }
        public bool Disposed { get; set; }
    }

    private static readonly AsyncLocal<TestStorage?> Storage = new();
    private static TestStorage? CurrentStorage
    {
        get
        {
            var storage = Storage.Value;
            while (storage?.Disposed == true) storage = storage.Parent;
            return storage;
        }
    }
    private static string FilePath => CurrentStorage?.Path ?? DefaultFilePath;

    // The current paths-only format has no timestamps. Keep this clock available for the Home history work
    // without adding timestamps or changing ordering/serialization in this preparation step.
    internal static DateTimeOffset UtcNow => CurrentStorage?.UtcNow() ?? DateTimeOffset.UtcNow;

    internal static IDisposable UseStorage(string path, Func<DateTimeOffset>? utcNow = null)
    {
        var previous = CurrentStorage;
        var storage = new TestStorage(path, utcNow ?? (() => DateTimeOffset.UtcNow)) { Parent = previous };
        Storage.Value = storage;
        // Disposal can run after an await in a child execution context; invalidate the shared frame too.
        return new StorageScope(() => { storage.Disposed = true; Storage.Value = previous; });
    }

    private sealed class StorageScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }

    private static ObservableCollection<RecentFile>? s_items;

    /// <summary>The list for binding (loaded on first use). Change it only on the UI thread.</summary>
    public static ObservableCollection<RecentFile> Items => CurrentStorage is { } storage
        ? storage.Items ??= new ObservableCollection<RecentFile>(Load().Select(p => new RecentFile(p)))
        : s_items ??= new ObservableCollection<RecentFile>(Load().Select(p => new RecentFile(p)));

    /// <summary>Puts a file at the top of the list.</summary>
    public static void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsTemporary(path))
        {
            return;
        }

        var full = System.IO.Path.GetFullPath(path);
        Remove(full, save: false);
        Items.Insert(0, new RecentFile(full));
        while (Items.Count > MaxCount)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        Save();
    }

    public static void Remove(string path) => Remove(path, save: true);

    public static void Clear()
    {
        ReadingPositions.Clear();
        ReaderBookmarks.Clear();
        Items.Clear();
        Save();
    }

    private static void Remove(string path, bool save)
    {
        foreach (var item in Items.Where(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Items.Remove(item);
        }

        if (save)
        {
            Save();
        }
    }

    /// <summary>Converted drawings and other working copies live in the temp folder: not worth listing.</summary>
    private static bool IsTemporary(string path) =>
        System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);

    private static List<string> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.Select(i => i.Path).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }
}
