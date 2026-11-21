using System.IO;
using System.Text.Json;
using Yrk.PdfStudio.App.Services.Backup;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Local reading history shared by tabs. Stores paths, zero-based pages and eviction times only.</summary>
internal static class ReadingPositions
{
    internal const int MaxCount = 200;
    internal static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "reading-positions.json");

    private static readonly object Gate = new();
    private sealed record Position(int PageIndex, DateTimeOffset LastUsed);

    public static int GetPageIndex(string path)
    {
        lock (Gate)
        {
            var full = Normalize(path);
            return full is not null && Load().TryGetValue(full, out var position) ? position.PageIndex : 0;
        }
    }

    public static void Save(string path, int pageIndex)
    {
        lock (Gate)
        {
            var full = Normalize(path);
            if (full is null || pageIndex < 0) return;
            var positions = Load();
            positions[full] = new(pageIndex, DateTimeOffset.UtcNow);
            Write(positions.OrderByDescending(pair => pair.Value.LastUsed).Take(MaxCount)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        }
    }

    public static bool Clear()
    {
        lock (Gate) return Write([]);
    }

    private static string? Normalize(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<string, Position> Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, Position?>>(File.ReadAllText(FilePath));
            var positions = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);
            if (stored is not null)
                foreach (var pair in stored.OrderBy(pair => pair.Value?.LastUsed))
                    if (pair.Value is { PageIndex: >= 0 } position && Normalize(pair.Key) is { } full)
                        positions[full] = position;
            return positions;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool Write(Dictionary<string, Position> positions)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var cleanTemporary = false;
        try
        {
            BackupFormat.CheckNoLinks(FilePath);
            BackupFormat.CheckNoLinks(temporary);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            cleanTemporary = true;
            File.WriteAllText(temporary, JsonSerializer.Serialize(positions));
            BackupFormat.CheckNoLinks(FilePath);
            BackupFormat.CheckNoLinks(temporary);
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (cleanTemporary)
                {
                    BackupFormat.CheckNoLinks(temporary);
                    File.Delete(temporary);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
    }
}
