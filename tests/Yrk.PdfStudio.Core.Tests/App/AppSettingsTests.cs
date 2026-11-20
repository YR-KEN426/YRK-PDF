using System.Text.Json.Nodes;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("Import restart")]
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        PrivateTempDirectory.Path, "app-settings-" + Guid.NewGuid().ToString("N"))).FullName;
    private string SettingsPath => Path.Combine(_root, "settings.json");

    public AppSettingsTests() => AppSettings.ResetSavingForTests();

    public void Dispose()
    {
        AppSettings.ResetSavingForTests();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Alternating_stale_copies_merge_only_changes_since_their_last_save()
    {
        File.WriteAllText(SettingsPath, "{\"Language\":\"th\",\"MeasureUnit\":\"cm\"}");
        var first = AppSettings.Load(SettingsPath);
        var second = AppSettings.Load(SettingsPath);
        first.Language = "en";
        first.Save(SettingsPath);
        second.MeasureUnit = "in";
        second.Save(SettingsPath);
        first.CheckForUpdates = false;
        first.Save(SettingsPath);
        second.ScanSaveFolder = "scans";
        second.Save(SettingsPath);
        var saved = AppSettings.Load(SettingsPath);
        Assert.Equal("en", saved.Language);
        Assert.Equal("in", saved.MeasureUnit);
        Assert.False(saved.CheckForUpdates);
        Assert.Equal("scans", saved.ScanSaveFolder);
        AssertOnlySettingsFile();
    }

    [Fact]
    public void Unknown_fields_from_the_latest_file_survive_instance_save_and_update()
    {
        File.WriteAllText(SettingsPath, "{\"Language\":\"th\",\"Future\":{\"items\":[1,null,true]}}");
        var stale = AppSettings.Load(SettingsPath);
        // A newer writer adds data after this copy was loaded.
        var latest = ReadObject();
        latest["Newer"] = "keep";
        File.WriteAllText(SettingsPath, latest.ToJsonString());
        stale.Language = "en";
        stale.Save(SettingsPath);
        AppSettings.Update(s => s.MeasureUnit = "ft", SettingsPath);
        var saved = ReadObject();
        Assert.True(JsonNode.DeepEquals(latest["Future"], saved["Future"]));
        Assert.Equal("keep", saved["Newer"]!.GetValue<string>());
        Assert.Equal("en", saved["Language"]!.GetValue<string>());
        Assert.Equal("ft", saved["MeasureUnit"]!.GetValue<string>());
    }

    [Fact]
    public void Separate_scan_presets_and_unknown_nested_fields_are_merged()
    {
        File.WriteAllText(SettingsPath,
            "{\"ScanPaperByPreset\":{\"ColorDocument\":{\"Id\":\"A4\",\"Future\":7}}}");
        var first = AppSettings.Load(SettingsPath);
        var second = AppSettings.Load(SettingsPath);
        first.ScanPaperByPreset["ColorDocument"] = new("Letter");
        first.Save(SettingsPath);
        second.ScanPaperByPreset["Photo"] = new("A5");
        second.Save(SettingsPath);
        var saved = AppSettings.Load(SettingsPath);
        Assert.Equal(new("Letter"), saved.ScanPaperByPreset["ColorDocument"]);
        Assert.Equal(new("A5"), saved.ScanPaperByPreset["Photo"]);
        Assert.Equal(7, ReadObject()["ScanPaperByPreset"]!["ColorDocument"]!["Future"]!.GetValue<int>());
    }

    [Fact]
    public void Updates_read_latest_state_under_one_lock()
    {
        new AppSettings().Save(SettingsPath);
        Parallel.For(0, 24, _ => AppSettings.Update(s => s.ScanDpi++, SettingsPath));
        Assert.Equal(324, AppSettings.Load(SettingsPath).ScanDpi);
        AssertOnlySettingsFile();
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"ScanDpi\":\"wrong type\"}")]
    public void Corrupt_file_stays_untouched_until_a_change_then_is_backed_up(string corrupt)
    {
        File.WriteAllText(SettingsPath, corrupt);
        var original = File.ReadAllBytes(SettingsPath);
        var settings = AppSettings.Load(SettingsPath);
        Assert.Equal("auto", settings.Language);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        AssertOnlySettingsFile();
        settings.Save(SettingsPath);
        AppSettings.Update(_ => { }, SettingsPath);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        AssertOnlySettingsFile();
        settings.Language = "th";
        settings.Save(SettingsPath);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath + ".bad"));
        Assert.Equal("th", AppSettings.Load(SettingsPath).Language);
        Assert.Equal(300, AppSettings.Load(SettingsPath).ScanDpi);
        AppSettings.Update(s => s.MeasureUnit = "m", SettingsPath);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath + ".bad"));
        Assert.Equal("m", AppSettings.Load(SettingsPath).MeasureUnit);
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void Update_also_backs_up_corrupt_file_before_replacing_it()
    {
        File.WriteAllText(SettingsPath, "broken");
        AppSettings.Update(s => s.Language = "en", SettingsPath);
        Assert.Equal("broken", File.ReadAllText(SettingsPath + ".bad"));
        Assert.Equal("en", AppSettings.Load(SettingsPath).Language);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_bad_backup_or_failed_backup_blocks_both_save_apis(bool backupIsDirectory)
    {
        File.WriteAllText(SettingsPath, "new damaged file");
        if (backupIsDirectory) Directory.CreateDirectory(SettingsPath + ".bad");
        else File.WriteAllText(SettingsPath + ".bad", "earlier damaged file");
        var settings = AppSettings.Load(SettingsPath);
        settings.Language = "th";
        settings.Save(SettingsPath);
        AppSettings.Update(s => s.MeasureUnit = "cm", SettingsPath);
        Assert.Equal("new damaged file", File.ReadAllText(SettingsPath));
        if (backupIsDirectory) Assert.Empty(Directory.GetFileSystemEntries(SettingsPath + ".bad"));
        else Assert.Equal("earlier damaged file", File.ReadAllText(SettingsPath + ".bad"));
        Assert.Equal(2, Directory.GetFileSystemEntries(_root).Length);
    }

    [Fact]
    public void Exclusive_read_lock_never_causes_defaults_to_overwrite_the_file()
    {
        File.WriteAllText(SettingsPath, "{\"Language\":\"th\",\"Future\":true}");
        var original = File.ReadAllBytes(SettingsPath);
        var stale = AppSettings.Load(SettingsPath);
        stale.MeasureUnit = "ft";
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var defaults = AppSettings.Load(SettingsPath);
            Assert.Equal("auto", defaults.Language);
            defaults.Language = "en";
            defaults.Save(SettingsPath);
            stale.Save(SettingsPath);
            var called = false;
            AppSettings.Update(s => { called = true; s.Language = "en"; }, SettingsPath);
            Assert.False(called);
            AssertOnlySettingsFile();
        }
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        // A failed save must retain its pending changes for retry.
        stale.Save(SettingsPath);
        Assert.Equal("th", AppSettings.Load(SettingsPath).Language);
        Assert.Equal("ft", AppSettings.Load(SettingsPath).MeasureUnit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_atomic_replace_is_swallowed_and_leaves_no_temporary_file(bool useUpdate)
    {
        File.WriteAllText(SettingsPath, "{\"Language\":\"th\"}");
        var original = File.ReadAllBytes(SettingsPath);
        var settings = AppSettings.Load(SettingsPath);
        settings.Language = "en";
        // Reads succeed, but the destination cannot be replaced while delete sharing is denied.
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (useUpdate) AppSettings.Update(s => s.Language = "en", SettingsPath);
            else settings.Save(SettingsPath);
            Assert.Equal(original, File.ReadAllBytes(SettingsPath));
            AssertOnlySettingsFile();
        }
        settings.Save(SettingsPath);
        Assert.Equal("en", AppSettings.Load(SettingsPath).Language);
    }

    [Fact]
    public void Unauthorized_write_is_swallowed_without_losing_pending_changes()
    {
        File.WriteAllText(SettingsPath, "{}");
        var settings = AppSettings.Load(SettingsPath);
        settings.Language = "th";
        File.SetAttributes(SettingsPath, FileAttributes.ReadOnly);
        try
        {
            settings.Save(SettingsPath);
            Assert.Equal("{}", File.ReadAllText(SettingsPath));
            AssertOnlySettingsFile();
        }
        finally { File.SetAttributes(SettingsPath, FileAttributes.Normal); }
        settings.Save(SettingsPath);
        Assert.Equal("th", AppSettings.Load(SettingsPath).Language);
    }

    [Fact]
    public void Suspend_blocks_changes_backups_and_even_directory_creation()
    {
        File.WriteAllText(SettingsPath, "broken");
        var settings = AppSettings.Load(SettingsPath);
        settings.Language = "en";
        AppSettings.SuspendSavingUntilRestart();
        settings.Save(SettingsPath);
        AppSettings.Update(s => s.Language = "th", SettingsPath);
        var missing = Path.Combine(_root, "missing", "settings.json");
        settings.Save(missing);
        AppSettings.Update(s => s.Language = "th", missing);
        // Invalid paths also prove the suspend check happens before any file access.
        settings.Save("\0");
        AppSettings.Update(_ => Assert.Fail("Suspended callback ran."), "\0");
        Assert.Equal("broken", File.ReadAllText(SettingsPath));
        AssertOnlySettingsFile();
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Save_waiting_for_the_lock_observes_suspend_before_touching_files()
    {
        // Update holds the same lock used by Save and SuspendSavingUntilRestart.
        Task? pending = null;
        using var started = new ManualResetEventSlim();
        try
        {
            AppSettings.Update(_ =>
            {
                pending = Task.Run(() =>
                {
                    started.Set();
                    new AppSettings { Language = "th" }.Save(SettingsPath);
                });
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
                AppSettings.SuspendSavingUntilRestart();
            }, Path.Combine(_root, "outer.json"));
            await pending!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(SettingsPath));
            Assert.Empty(Directory.GetFileSystemEntries(_root));
        }
        finally { if (pending is not null) await pending; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_file_can_be_created_with_parent_directories(bool useUpdate)
    {
        var path = Path.Combine(_root, "new", "settings.json");
        Assert.Equal("auto", AppSettings.Load(path).Language);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        if (useUpdate) AppSettings.Update(s => s.Language = "th", path);
        else new AppSettings { Language = "th" }.Save(path);
        Assert.Equal("th", AppSettings.Load(path).Language);
        Assert.Equal(300, AppSettings.Load(path).ScanDpi);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void New_default_instance_can_create_a_missing_file()
    {
        new AppSettings().Save(SettingsPath);
        Assert.Equal("auto", AppSettings.Load(SettingsPath).Language);
        AssertOnlySettingsFile();
    }

    private JsonObject ReadObject() => JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
    private void AssertOnlySettingsFile() => Assert.Equal([SettingsPath], Directory.GetFiles(_root));
}
