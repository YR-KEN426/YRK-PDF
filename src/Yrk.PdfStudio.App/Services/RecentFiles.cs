using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yrk.PdfStudio.App.Services.Backup;


namespace Yrk.PdfStudio.App.Services;

/// <summary>A file in the recent list: its path, name and folder for display, and when it was last opened (if known).</summary>
public sealed record RecentFile(string Path, DateTimeOffset? LastOpened = null)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>
/// Recently opened files, newest first, shared by all tabs. Kept only on this computer, never file contents, and can be cleared.
/// <para>
/// recent.json (in %LOCALAPPDATA%\YRK\EasyPdf) is the main file and keeps its original format, a plain list of paths, so an older
/// version of the app can still read it. The time each file was last opened lives beside it in recent-times.json (path to time).
/// That file is only a decoration: times for paths that are not in recent.json are ignored and dropped on the next write, and a
/// missing or damaged file just means no times are shown. Both files are written through a temporary file and replaced in one
/// step, recent.json first, so a stop half way never brings back a path that was cleared. Neither is part of a backup.
/// </para>
/// </summary>
public static class RecentFiles
{
    public const int MaxCount = 12;
    private static readonly Regex TemporaryFileName = new(@"\Arecent(?:-times)?\.json\.[0-9a-fA-F]{32}\.tmp\z", RegexOptions.CultureInvariant);

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

    /// <summary>recent-times.json for recent.json (for any other name, x.json gives x-times.json), in the same folder.</summary>
    private static string TimesFilePath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(FilePath) ?? string.Empty, System.IO.Path.GetFileNameWithoutExtension(FilePath) + "-times.json");

    // The clock for the time a file was opened (tests set their own).
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
        ? storage.Items ??= new ObservableCollection<RecentFile>(LoadItems())
        : s_items ??= new ObservableCollection<RecentFile>(LoadItems());

    /// <summary>Puts a file at the top of the list.</summary>
    public static void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsTemporary(path))
        {
            return;
        }

        var full = System.IO.Path.GetFullPath(path);
        Remove(full, save: false);
        Items.Insert(0, new RecentFile(full, UtcNow));
        while (Items.Count > MaxCount)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        Save();
    }

    public static void Remove(string path) => Remove(path, save: true);

    /// <summary>Clears the list and its leftover temporary files; false if any recent-file data could not be cleared.</summary>
    public static bool Clear()
    {
        ReadingPositions.Clear();
        ReaderBookmarks.Clear();
        Items.Clear();
        var saved = Save();
        var cleaned = ClearTemporaryFiles();
        return saved && cleaned;
    }

    private static bool ClearTemporaryFiles()
    {
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath))!;
        var cleared = true;
        try
        {
            BackupFormat.CheckNoLinks(folder);
            if (!Directory.Exists(folder)) return true;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (TemporaryFileName.IsMatch(System.IO.Path.GetFileName(path)))
                    cleared &= TryWithRetry(() =>
                    {
                        BackupFormat.CheckNoLinks(path);
                        File.Delete(path);
                    });
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
        return cleared;
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

    /// <summary>The paths of recent.json, each with its time from recent-times.json when there is one.</summary>
    private static List<RecentFile> LoadItems()
    {
        var times = LoadTimes();
        return Load().Select(p => new RecentFile(p, times.TryGetValue(p, out var time) ? time : null)).ToList();
    }

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

    /// <summary>Times by path. A file that cannot be read, or is not what we wrote, gives no times at all.</summary>
    private static Dictionary<string, DateTimeOffset> LoadTimes()
    {
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(TimesFilePath) && JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(TimesFilePath)) is { } stored)
            {
                foreach (var (path, text) in stored)
                {
                    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
                    {
                        times[path] = time;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            times.Clear();
        }

        return times;
    }

    /// <summary>Writes recent.json, then recent-times.json (only for paths still in the list). The second is skipped if the first fails.</summary>
    private static bool Save()
    {
        if (!WriteAtomically(FilePath, JsonSerializer.Serialize(Items.Select(i => i.Path).ToList())))
        {
            return false;
        }

        var times = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items.Where(i => i.LastOpened is not null))
        {
            times[item.Path] = item.LastOpened!.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        if (times.Count == 0)
        {
            if (TryWithRetry(() =>
            {
                BackupFormat.CheckNoLinks(TimesFilePath);
                File.Delete(TimesFilePath);
            })) return true;
            // Could not delete it: write it empty instead, so no path is left in it.
        }

        return WriteAtomically(TimesFilePath, JsonSerializer.Serialize(times));
    }

    private static bool TryWithRetry(Action action)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { action(); return true; }
            catch (InvalidDataException) { return false; } // A link is unsafe, rather than temporarily busy.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 2) Thread.Sleep(50);
            }
        }
        return false;
    }

    /// <summary>Writes a temporary file beside the target and replaces the target with it. False when that failed (only a convenience).</summary>
    private static bool WriteAtomically(string path, string content) => TryWithRetry(() =>
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            BackupFormat.CheckNoLinks(path);
            BackupFormat.CheckNoLinks(temporary);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { BackupFormat.CheckNoLinks(temporary); File.Delete(temporary); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
    });
}
