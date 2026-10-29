using System.IO;
using System.Text.Json;

namespace Yrk.PdfStudio.App.Services;

/// <summary>A copy of a document with its unsaved changes, kept in case the program closes without saving.</summary>
/// <param name="OriginalPath">The file the changes belong to.</param>
public sealed record RecoveryEntry(string Id, string OriginalPath, string Name, DateTime SavedAt)
{
    public string PdfPath => Path.Combine(RecoveryStore.Folder, Id + ".pdf");
}

/// <summary>
/// Recovery copies in %LOCALAPPDATA%\YRK\EasyPdf\Recovery: a PDF and a small .json about it per open document with
/// unsaved changes. They are deleted when the changes are saved or discarded, and when the program closes normally;
/// any left at the next start were cut off, and are offered back.
/// </summary>
public static class RecoveryStore
{
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "Recovery");

    /// <summary>The copies this run of the program wrote, deleted when it closes normally.</summary>
    private static readonly HashSet<string> Written = [];

    public static IReadOnlyList<RecoveryEntry> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var entries = new List<RecoveryEntry>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllText(file)) is { } entry && File.Exists(entry.PdfPath)
                    && !Written.Contains(entry.Id))
                {
                    entries.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A damaged note is skipped; DeleteAllButWritten removes it with the rest.
            }
        }

        return entries.OrderByDescending(e => e.SavedAt).ToList();
    }

    /// <summary>Keeps <paramref name="pdf"/> (a finished file in the folder) as the recovery copy for <paramref name="id"/>.</summary>
    public static void Keep(string id, string pdf, string originalPath, string name)
    {
        var entry = new RecoveryEntry(id, originalPath, name, DateTime.Now);
        File.Move(pdf, entry.PdfPath, overwrite: true);
        File.WriteAllText(Path.Combine(Folder, id + ".json"), JsonSerializer.Serialize(entry));
        Written.Add(id);
    }

    public static void Delete(string id)
    {
        TryDelete(Path.Combine(Folder, id + ".json"));
        TryDelete(Path.Combine(Folder, id + ".pdf"));
        TryDelete(Path.Combine(Folder, id + ".tmp"));
        Written.Remove(id);
    }

    /// <summary>Deletes the copies of this run (the program is closing normally: every change was saved or discarded).</summary>
    public static void DeleteWritten()
    {
        foreach (var id in Written.ToList())
        {
            Delete(id);
        }
    }

    /// <summary>Removes copies offered at start and not wanted, and any leftovers.</summary>
    public static void DeleteAllButWritten()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            if (!Written.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>
    /// Puts a recovered copy next to its original as "name (recovered).pdf" (or in Documents when that folder cannot be
    /// written) and forgets it here.
    /// </summary>
    /// <returns>Where the copy now is.</returns>
    public static string Restore(RecoveryEntry entry)
    {
        var name = Path.GetFileNameWithoutExtension(entry.Name);
        foreach (var folder in new[] { Path.GetDirectoryName(entry.OriginalPath), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            var target = Path.Combine(folder, Localization.Loc.F("{0} (recovered)", name) + ".pdf");
            for (var n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(folder, Localization.Loc.F("{0} (recovered {1})", name, n) + ".pdf");
            }

            try
            {
                File.Copy(entry.PdfPath, target);
                Delete(entry.Id);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try Documents.
            }
        }

        throw new IOException(Localization.Loc.T("No folder could take the recovered file."));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
