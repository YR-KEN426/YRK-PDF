using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yrk.PdfStudio.App.Services.Backup;
using Yrk.PdfStudio.App.Imaging;

namespace Yrk.PdfStudio.App.Services;

/// <summary>The user's own rubber stamps (a company stamp, "รับเงินแล้ว โดย ...") and the one used last.</summary>
public sealed class StampSettings
{
    private static volatile bool s_saveSuspended;
    internal static void SuspendSavingUntilRestart() => s_saveSuspended = true;
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "stamps.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All), // Thai readable in the file.
    };

    public List<StampDesign> MyStamps { get; set; } = [];

    public StampDesign? Last { get; set; }

    /// <summary>The language of the ready-made stamps; null until chosen (then the language of the app).</summary>
    public StampLanguage? Language { get; set; }

    internal const string ShapesFileName = "stamp-shapes.json";
    private string? _path;

    // The complete legacy design plus its ordinal is the key, including null versus empty Detail.
    private sealed record ShapeEntry(StampDesign Design, int Ordinal, int Count, StampShape Shape);
    private sealed record LastShape(StampDesign Design, StampShape Shape);
    private sealed record ShapeSettings(List<ShapeEntry> Stamps, LastShape? Last);

    internal static string ShapesPath(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ShapesFileName);
    private static bool IsNew(StampShape shape) => shape is StampShape.Ribbon or StampShape.Seal;
    internal bool HasNewShapes => MyStamps.Any(s => IsNew(s.Shape)) || Last is not null && IsNew(Last.Shape);
    private static StampDesign Legacy(StampDesign design) => IsNew(design.Shape) ? design with { Shape = StampShape.Box } : design;

    internal byte[] SerializeLegacy() => JsonSerializer.SerializeToUtf8Bytes(new StampSettings
    {
        MyStamps = MyStamps.Select(Legacy).ToList(), Last = Last is null ? null : Legacy(Last), Language = Language,
    }, Json);

    internal byte[] SerializeShapes()
    {
        var entries = new List<ShapeEntry>();
        foreach (var group in MyStamps.GroupBy(Legacy))
        {
            var designs = group.ToList();
            for (var i = 0; i < designs.Count; i++)
                if (IsNew(designs[i].Shape)) entries.Add(new(group.Key, i, designs.Count, designs[i].Shape));
        }
        return JsonSerializer.SerializeToUtf8Bytes(new ShapeSettings(entries,
            Last is not null && IsNew(Last.Shape) ? new(Legacy(Last), Last.Shape) : null), Json);
    }

    internal static StampSettings Deserialize(byte[] bytes, byte[]? shapes = null)
    {
        var settings = JsonSerializer.Deserialize<StampSettings>(bytes, Json) ?? new();
        settings.MyStamps ??= [];
        if (shapes is null) return settings;
        try
        {
            var extra = JsonSerializer.Deserialize<ShapeSettings>(shapes, Json);
            if (extra is null) return settings;
            // Validate all matches before changing any design.
            var groups = settings.MyStamps.Select((design, index) => (design, index))
                .GroupBy(item => item.design).ToDictionary(g => g.Key, g => g.Select(item => item.index).ToArray());
            var matches = new List<(int Index, StampShape Shape)>();
            foreach (var group in (extra.Stamps ?? []).Where(e => e is not null).GroupBy(e => e.Design))
            {
                if (group.Key is null || !groups.TryGetValue(group.Key, out var indices)) continue;
                var entries = group.ToList();
                if (entries.Any(e => e.Count != indices.Length || e.Ordinal < 0 || e.Ordinal >= indices.Length || !IsNew(e.Shape))
                    || entries.Select(e => e.Ordinal).Distinct().Count() != entries.Count) continue;
                matches.AddRange(entries.Select(e => (indices[e.Ordinal], e.Shape)));
            }
            foreach (var match in matches)
                settings.MyStamps[match.Index] = settings.MyStamps[match.Index] with { Shape = match.Shape };
            if (extra.Last is { } last && IsNew(last.Shape) && settings.Last == last.Design && settings.Last is not null)
                settings.Last = settings.Last with { Shape = last.Shape };
        }
        catch (JsonException) { /* A damaged sidecar must never discard stamps. */ }
        return settings;
    }

    public static StampSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var settings = File.Exists(path) ? Deserialize(File.ReadAllBytes(path), ReadShapes(path)) : new();
            settings._path = path;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new() { _path = path };
        }
    }

    private static byte[]? ReadShapes(string path)
    {
        try { return File.Exists(ShapesPath(path)) ? File.ReadAllBytes(ShapesPath(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Save(string? path = null)
    {
        if (s_saveSuspended) return;
        path = Path.GetFullPath(path ?? _path ?? DefaultPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            BackupFormat.AtomicWrite(path, SerializeLegacy(), overwrite: true);
            if (HasNewShapes) BackupFormat.AtomicWrite(ShapesPath(path), SerializeShapes(), overwrite: true);
            else
            {
                BackupFormat.CheckNoLinks(ShapesPath(path));
                File.Delete(ShapesPath(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }
}
