using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Services.Backup;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class StampCompatibilityTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-stamp-compat-");
    private string FilePath => Path.Combine(_folder.FullName, "stamps.json");
    private string Shapes => StampSettings.ShapesPath(FilePath);
    public void Dispose() => _folder.Delete(true);
    private static StampDesign Design(StampShape shape) => new("อนุมัติ", "บริษัท ผู้ซื้อ จำกัด", StampInk.Blue, shape, StampDateStyle.ThaiLong);
    private StampSettings SaveTwins()
    {
        var settings = new StampSettings
        {
            MyStamps = [Design(StampShape.Ribbon), Design(StampShape.Seal), Design(StampShape.Oval) with { Text = "Old" }],
            Last = Design(StampShape.Seal), Language = StampLanguage.Both,
        };
        settings.Save(FilePath);
        return settings;
    }

    // Deliberately independent of StampShape/StampSettings. This is the old string-enum schema,
    // including its whole-file fallback when a future enum name is encountered.
    private enum OldShape { Box, DoubleBox, Oval }
    private sealed record OldDesign(string Text, string? Detail, StampInk Ink, OldShape Shape, StampDateStyle Date);
    private sealed class OldSettings
    {
        public List<OldDesign> MyStamps { get; set; } = [];
        public OldDesign? Last { get; set; }
        public StampLanguage? Language { get; set; }
    }
    private static readonly JsonSerializerOptions OldJson = new() { Converters = { new JsonStringEnumConverter() } };
    private OldSettings OldLoad()
    {
        try { return JsonSerializer.Deserialize<OldSettings>(File.ReadAllText(FilePath), OldJson) ?? new(); }
        catch (JsonException) { return new(); }
    }
    private void OldSave(OldSettings settings) => File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, OldJson));

    private static ZipArchive OpenBackup(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var header = bytes.AsSpan(0, BackupFormat.HeaderBytes);
        var plain = new byte[bytes.Length - BackupFormat.HeaderBytes - BackupFormat.TagBytes];
        var key = BackupFormat.Key("test-password".AsSpan(), header);
        try
        {
            using var aes = new AesGcm(key, BackupFormat.TagBytes);
            aes.Decrypt(header.Slice(7 + BackupFormat.SaltBytes, BackupFormat.NonceBytes),
                bytes.AsSpan(BackupFormat.HeaderBytes, plain.Length), bytes.AsSpan(bytes.Length - BackupFormat.TagBytes), plain, header);
            return new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // The old CheckPath root-file branch, with its independent pre-1.2.4 settings list.
    // Nested-file validation is unchanged; this fixture exports only root settings files.
    private static void OldCheckPath(string path, bool manifest = false)
    {
        string[] settings = ["settings.json", "stamps.json", "etax.json", "reader-bookmarks.json"];
        if (manifest && path == "manifest.json" || settings.Contains(path, StringComparer.Ordinal)) return;
        if (path.Split('/').Length != 2) throw new InvalidDataException();
        BackupFormat.CheckPath(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Backup_embeds_shapes_and_is_readable_by_old_path_checker_and_stamp_schema(bool lastOnly)
    {
        var source = SaveTwins();
        if (lastOnly)
        {
            source.MyStamps = [Design(StampShape.Oval)];
            source.Save(FilePath);
        }
        var main = File.ReadAllBytes(FilePath);
        var shapes = File.ReadAllBytes(Shapes);
        var archive = Path.Combine(_folder.FullName, "test.yrkbackup");
        var manifest = new BackupWriter().Write(archive, _folder.FullName, "test-password".AsSpan(), "test", BackupSelection.Stamps);
        Assert.Equal("stamps.json", Assert.Single(manifest.Files).Path);
        Assert.Throws<InvalidDataException>(() => OldCheckPath(StampSettings.ShapesFileName));
        using var zip = OpenBackup(archive);
        Assert.Equal(2, zip.Entries.Count);
        Assert.All(zip.Entries, entry => OldCheckPath(entry.FullName, manifest: true));
        Assert.Null(zip.GetEntry(StampSettings.ShapesFileName));
        using var stream = zip.GetEntry("stamps.json")!.Open();
        using var json = JsonDocument.Parse(stream);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(source.SerializeShapes()), JsonNode.Parse(json.RootElement.GetProperty("YrkShapes").GetRawText())));
        var old = JsonSerializer.Deserialize<OldSettings>(json.RootElement, OldJson)!;
        Assert.Equal(source.MyStamps.Count, old.MyStamps.Count);
        Assert.Equal(lastOnly ? [OldShape.Oval] : new[] { OldShape.Box, OldShape.Box, OldShape.Oval }, old.MyStamps.Select(s => s.Shape));
        Assert.Equal(OldShape.Box, old.Last!.Shape);
        Assert.Equal(main, File.ReadAllBytes(FilePath));
        Assert.Equal(shapes, File.ReadAllBytes(Shapes));
        var target = Path.Combine(_folder.FullName, "target", "stamps.json");
        new BackupReader().Import(archive, Path.GetDirectoryName(target)!, Path.Combine(_folder.FullName, "staging"), "test-password".AsSpan());
        Assert.Equal(source.MyStamps, StampSettings.Load(target).MyStamps);
        Assert.Equal(source.Last, StampSettings.Load(target).Last);
        Assert.False(JsonNode.Parse(File.ReadAllBytes(target))!.AsObject().ContainsKey("YrkShapes"));
    }

    [Fact]
    public void Saving_without_new_shapes_removes_sidecar_and_backup_omits_embedded_shapes()
    {
        SaveTwins();
        var source = new StampSettings { MyStamps = [Design(StampShape.Box)], Last = Design(StampShape.Oval) };
        source.Save(FilePath);
        Assert.False(File.Exists(Shapes));
        var archive = Path.Combine(_folder.FullName, "test.yrkbackup");
        var manifest = new BackupWriter().Write(archive, _folder.FullName, "test-password".AsSpan(), "test", BackupSelection.Stamps);
        Assert.Equal("stamps.json", Assert.Single(manifest.Files).Path);
        using var zip = OpenBackup(archive);
        Assert.Null(zip.GetEntry(StampSettings.ShapesFileName));
        using var stream = zip.GetEntry("stamps.json")!.Open();
        using var json = JsonDocument.Parse(stream);
        Assert.False(json.RootElement.TryGetProperty("YrkShapes", out _));
        Assert.Equal(source.MyStamps, StampSettings.Load(FilePath).MyStamps);
    }

    [Fact]
    public void Load_ignores_leftover_YrkShapes_with_future_enum_names()
    {
        var source = SaveTwins();
        var json = JsonNode.Parse(File.ReadAllBytes(FilePath))!.AsObject();
        json["YrkShapes"] = JsonNode.Parse(source.SerializeShapes());
        File.WriteAllText(FilePath, json.ToJsonString());
        Assert.Equal(source.MyStamps, StampSettings.Load(FilePath).MyStamps);
        Assert.Equal(source.Last, StampSettings.Load(FilePath).Last);
        File.Delete(Shapes);
        var loaded = StampSettings.Load(FilePath);
        Assert.Equal([StampShape.Box, StampShape.Box, StampShape.Oval], loaded.MyStamps.Select(s => s.Shape));
        Assert.Equal(StampShape.Box, loaded.Last!.Shape);
    }

    [Theory]
    [InlineData("Ribbon")]
    [InlineData("Seal")]
    public void Old_reader_really_rejects_unknown_string_enum_names(string name)
    {
        SaveTwins();
        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("\"Box\"", "\"" + name + "\"", StringComparison.Ordinal));
        Assert.Empty(OldLoad().MyStamps);
    }

    [Fact]
    public void Old_string_enum_roundtrip_preserves_every_stamp_and_independent_Last_shape()
    {
        var expected = SaveTwins();
        var sidecar = File.ReadAllBytes(Shapes);
        var old = OldLoad();
        Assert.Equal(3, old.MyStamps.Count);
        Assert.Equal(OldShape.Box, old.MyStamps[0].Shape);
        OldSave(old);
        Assert.Equal(sidecar, File.ReadAllBytes(Shapes));
        var actual = StampSettings.Load(FilePath);
        Assert.Equal(expected.MyStamps, actual.MyStamps);
        Assert.Equal(expected.Last, actual.Last);
        Assert.Equal(expected.Language, actual.Language);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Deleting_either_twin_in_old_version_drops_the_entire_ambiguous_group(int index)
    {
        SaveTwins();
        var old = OldLoad(); old.MyStamps.RemoveAt(index); OldSave(old);
        var loaded = StampSettings.Load(FilePath);
        Assert.Equal(2, loaded.MyStamps.Count);
        Assert.Equal(StampShape.Box, loaded.MyStamps[0].Shape);
        Assert.Equal(StampShape.Oval, loaded.MyStamps[1].Shape);
        Assert.Equal(StampShape.Seal, loaded.Last!.Shape);
    }

    [Fact]
    public void Changing_only_Last_in_old_version_does_not_change_any_saved_shape()
    {
        var expected = SaveTwins();
        var old = OldLoad(); old.Last = old.Last! with { Detail = "Changed last" }; OldSave(old);
        var loaded = StampSettings.Load(FilePath);
        Assert.Equal(expected.MyStamps, loaded.MyStamps);
        Assert.Equal(StampShape.Box, loaded.Last!.Shape);
        Assert.Equal("Changed last", loaded.Last.Detail);
    }

    [Fact]
    public void Ordinals_count_plain_boxes_too_and_Last_is_independent_of_the_list()
    {
        var design = Design(StampShape.Box);
        var expected = new StampSettings { MyStamps = [design, design with { Shape = StampShape.Seal }], Last = design with { Shape = StampShape.Ribbon } };
        expected.Save(FilePath); OldSave(OldLoad());
        var actual = StampSettings.Load(FilePath);
        Assert.Equal(expected.MyStamps, actual.MyStamps); Assert.Equal(expected.Last, actual.Last);
        var old = OldLoad(); old.MyStamps.Add(old.MyStamps[0]); OldSave(old);
        Assert.All(StampSettings.Load(FilePath).MyStamps, s => Assert.Equal(StampShape.Box, s.Shape));
    }

    [Fact]
    public void Import_without_replace_skips_both_stamp_files_and_never_overwrites_local_shapes()
    {
        SaveTwins();
        var archive = Path.Combine(_folder.FullName, "test.yrkbackup");
        new BackupWriter().Write(archive, _folder.FullName, "test-password".AsSpan(), "test", BackupSelection.Stamps);
        var target = Path.Combine(_folder.FullName, "target", "stamps.json");
        new StampSettings { MyStamps = [Design(StampShape.Seal)] }.Save(target);
        var main = File.ReadAllBytes(target); var shapes = File.ReadAllBytes(StampSettings.ShapesPath(target));
        Assert.Empty(new BackupReader().Import(archive, Path.GetDirectoryName(target)!, Path.Combine(_folder.FullName, "staging"), "test-password".AsSpan()).ImportedPaths);
        Assert.Equal(main, File.ReadAllBytes(target)); Assert.Equal(shapes, File.ReadAllBytes(StampSettings.ShapesPath(target)));
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Detail")]
    [InlineData("Ink")]
    [InlineData("Date")]
    [InlineData("Shape")]
    public void Editing_any_part_of_legacy_key_or_Last_never_reuses_a_stale_shape(string field)
    {
        SaveTwins();
        var old = OldLoad();
        var edited = field switch
        {
            "Text" => old.MyStamps[0] with { Text = "Changed" },
            "Detail" => old.MyStamps[0] with { Detail = "Changed" },
            "Ink" => old.MyStamps[0] with { Ink = StampInk.Red },
            "Date" => old.MyStamps[0] with { Date = StampDateStyle.None },
            _ => old.MyStamps[0] with { Shape = OldShape.Oval },
        };
        old.MyStamps[0] = edited; old.Last = edited; OldSave(old);
        var loaded = StampSettings.Load(FilePath);
        Assert.Equal(field == "Shape" ? StampShape.Oval : StampShape.Box, loaded.MyStamps[0].Shape);
        Assert.Equal(StampShape.Box, loaded.MyStamps[1].Shape);
        Assert.Equal(loaded.MyStamps[0], loaded.Last);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("json")]
    [InlineData("enum")]
    [InlineData("locked")]
    public void Missing_damaged_or_unreadable_sidecar_keeps_all_legacy_data(string fault)
    {
        SaveTwins();
        FileStream? locked = null;
        try
        {
            if (fault == "missing") File.Delete(Shapes);
            if (fault == "json") File.WriteAllText(Shapes, "{broken");
            if (fault == "enum") File.WriteAllText(Shapes, File.ReadAllText(Shapes).Replace("Ribbon", "FutureShape", StringComparison.Ordinal));
            if (fault == "locked") locked = new FileStream(Shapes, FileMode.Open, FileAccess.Read, FileShare.None);
            var loaded = StampSettings.Load(FilePath);
            Assert.Equal([StampShape.Box, StampShape.Box, StampShape.Oval], loaded.MyStamps.Select(s => s.Shape));
            Assert.Equal(StampShape.Box, loaded.Last!.Shape);
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public void Legacy_only_data_loads_unchanged_and_failed_atomic_replace_keeps_original_bytes()
    {
        SaveTwins();
        var old = OldLoad(); old.MyStamps[0] = old.MyStamps[0] with { Shape = OldShape.DoubleBox }; OldSave(old);
        File.Delete(Shapes);
        Assert.Equal(StampShape.DoubleBox, StampSettings.Load(FilePath).MyStamps[0].Shape);
        var bytes = File.ReadAllBytes(FilePath);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            new StampSettings { MyStamps = [Design(StampShape.Ribbon)] }.Save(FilePath);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.EnumerateFiles(_folder.FullName, "*.tmp"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Backup_import_keeps_shapes_in_empty_or_colliding_store_and_rekeys_after_rename(bool collision, bool legacyArchive)
    {
        var source = SaveTwins();
        if (legacyArchive) File.Delete(Shapes);
        var archive = Path.Combine(_folder.FullName, "test.yrkbackup");
        var manifest = new BackupWriter().Write(archive, _folder.FullName, "test-password".AsSpan(), "test", BackupSelection.Stamps);
        Assert.Equal("stamps.json", Assert.Single(manifest.Files).Path);
        var target = Path.Combine(_folder.FullName, "target", "stamps.json");
        var existing = new StampSettings { MyStamps = [Design(StampShape.Seal)] };
        if (collision) existing.Save(target);
        new BackupReader().Import(archive, Path.GetDirectoryName(target)!, Path.Combine(_folder.FullName, "staging"), "test-password".AsSpan(), true);
        Assert.False(JsonNode.Parse(File.ReadAllBytes(target))!.AsObject().ContainsKey("YrkShapes"));
        var loaded = StampSettings.Load(target);
        Assert.Equal(collision ? 4 : 3, loaded.MyStamps.Count);
        if (collision) Assert.Equal(existing.MyStamps[0], loaded.MyStamps[0]);
        var imported = loaded.MyStamps.Skip(collision ? 1 : 0).ToArray();
        Assert.Equal(legacyArchive ? [StampShape.Box, StampShape.Box, StampShape.Oval] : source.MyStamps.Select(s => s.Shape), imported.Select(s => s.Shape));
        if (collision) Assert.Equal(["อนุมัติ (นำเข้า)", "อนุมัติ (นำเข้า) (2)", "Old"], imported.Select(s => s.Text));
        Assert.Equal(legacyArchive ? source.Last! with { Shape = StampShape.Box } : source.Last, loaded.Last);
        // A second downgrade roundtrip also proves the merged keys are based on the renamed data.
        var old = JsonSerializer.Deserialize<OldSettings>(File.ReadAllText(target), OldJson)!;
        File.WriteAllText(target, JsonSerializer.Serialize(old, OldJson));
        Assert.Equal(loaded.MyStamps, StampSettings.Load(target).MyStamps);
    }
}
