using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services.Backup;
using Yrk.PdfStudio.App.Views;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class BackupImportTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "transfer-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Source => Path.Combine(_root, "source");
    private string Data => Path.Combine(_root, "data");
    private string Temp => Path.Combine(_root, "temp");
    private string Archive => Path.Combine(_root, "selected.yrkbackup");
    private const string Password = "test-backup-กขค";
    private const string Version = "1.2.0-test";
    private readonly BackupReader _reader = new();
    private readonly BackupWriter _writer = new();

    private static void Put(string root, string name, string text)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private void CreateArchive()
    {
        Put(Source, "settings.json", "{\"Language\":\"th\"}");
        _writer.Write(Archive, Source, Password, Version);
    }
    private BackupImportResult Run(BackupImportCoordinator? coordinator = null, bool replace = true) =>
        (coordinator ?? new()).Import(Archive, Data, Temp, Password, replace, Version);
    private string[] SafetyFiles() => Directory.Exists(Path.Combine(Data, "Backups"))
        ? Directory.GetFiles(Path.Combine(Data, "Backups"), "before-import-*.yrkbackup") : [];

    [Fact]
    public async Task App_harness_export_import_round_trip_preserves_stores()
    {
        await using var app = new AppTestHarness();
        var source = app.PathFor("backup-source");
        var target = app.PathFor("backup-target");
        var archive = app.PathFor("roundtrip.yrkbackup");
        Put(source, "settings.json", "{\"Language\":\"th\"}");
        Put(source, "etax.json", "{\"Seller\":null}");
        Put(source, "stamps.json", "{\"MyStamps\":[],\"Last\":null}");
        Put(source, "DigitalIDs/example.pfx", "opaque-password-protected-test-bytes");
        _writer.Write(archive, source, Password, Version);
        new BackupImportCoordinator().Import(archive, target, app.PathFor("staging"), Password, true, Version);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(target, Path.GetRelativePath(source, file))));
    }

    [Fact]
    public void Before_import_is_valid_and_contains_old_data_before_import_is_called()
    {
        CreateArchive();
        Put(Data, "settings.json", "{\"Language\":\"en\"}");
        var called = false;
        var coordinator = new BackupImportCoordinator(import: (path, data, temp, password, replace) =>
        {
            called = true;
            var safety = Assert.Single(SafetyFiles());
            Assert.True(new FileInfo(safety).Length > BackupFormat.HeaderBytes + BackupFormat.TagBytes);
            Assert.Single(_reader.Inspect(safety, password, checkLinks: true).Files);
            _reader.Import(safety, Path.Combine(_root, "restored"), Temp, password, true);
            Assert.Equal("{\"Language\":\"en\"}", File.ReadAllText(Path.Combine(_root, "restored", "settings.json")));
            Assert.Equal("{\"Language\":\"en\"}", File.ReadAllText(Path.Combine(data, "settings.json")));
            return _reader.Import(path, data, temp, password, replace);
        });
        Run(coordinator);
        Assert.True(called);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "settings.json")), File.ReadAllBytes(Path.Combine(Data, "settings.json")));
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("damaged")]
    [InlineData("oversized")]
    public void Failed_safety_backup_never_calls_import_or_changes_current_stores(string failure)
    {
        CreateArchive();
        Put(Data, "settings.json", "{\"original\":true}");
        var called = false;
        var coordinator = new BackupImportCoordinator(write: (path, data, password, version) =>
        {
            if (failure == "throw") throw new IOException("private/path/must/not/be/shown");
            if (failure != "missing")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = File.Create(path);
                file.SetLength(failure == "short" ? 1 : failure == "oversized" ? BackupFormat.MaxBackupFileBytes + 1L : 200);
            }
            return new(version, DateTimeOffset.UtcNow, []);
        }, import: (path, data, temp, password, replace) => { called = true; throw new Exception("Import must not be called"); });
        var error = Assert.Throws<IOException>(() => Run(coordinator));
        Assert.Equal(Loc.T(BackupImportCoordinator.SafetyError), error.Message);
        Assert.Null(error.InnerException);
        Assert.False(called);
        Assert.Equal("{\"original\":true}", File.ReadAllText(Path.Combine(Data, "settings.json")));
        Assert.False(Directory.Exists(Temp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wrong_password_or_damaged_file_writes_nothing(bool damage)
    {
        CreateArchive();
        Put(Data, "settings.json", "{}");
        if (damage)
        {
            var bytes = File.ReadAllBytes(Archive); bytes[^1] ^= 1; File.WriteAllBytes(Archive, bytes);
        }
        var called = false;
        var coordinator = new BackupImportCoordinator(write: (path, data, password, version) =>
            throw new Exception("Must not attempt backup"), import: (path, data, temp, password, replace) =>
            { called = true; throw new Exception("Must not import"); });
        var error = Assert.Throws<InvalidDataException>(() => coordinator.Import(Archive, Data, Temp,
            damage ? Password : "incorrect-password", true, Version));
        Assert.Equal(Loc.T(BackupFormat.ReadError), error.Message);
        Assert.False(called);
        Assert.Equal("settings.json", Path.GetFileName(Assert.Single(Directory.GetFiles(Data))));
        Assert.Empty(Directory.GetDirectories(Data));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Data, "settings.json")));
        Assert.False(Directory.Exists(Temp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_or_absent_data_skips_backup_and_imports(bool createEmpty)
    {
        CreateArchive();
        if (createEmpty) Directory.CreateDirectory(Data);
        var coordinator = new BackupImportCoordinator(write: (path, data, password, version) =>
            throw new Exception("Empty stores must skip backup"));
        Run(coordinator);
        Assert.Empty(SafetyFiles());
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "settings.json")), File.ReadAllBytes(Path.Combine(Data, "settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replace_settings_option_is_forwarded_and_old_settings_are_backed_up(bool replace)
    {
        CreateArchive();
        Put(Data, "settings.json", "{\"old\":true}");
        Run(replace: replace);
        Assert.Single(SafetyFiles());
        Assert.Equal(replace ? File.ReadAllText(Path.Combine(Source, "settings.json")) : "{\"old\":true}",
            File.ReadAllText(Path.Combine(Data, "settings.json")));
    }

    [Fact]
    public void Inspect_authenticates_all_entries_without_creating_any_files()
    {
        CreateArchive();
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var manifest = _reader.Inspect(Archive, Password);
        Assert.Equal("settings.json", Assert.Single(manifest.Files).Path);
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.False(Directory.Exists(Data)); Assert.False(Directory.Exists(Temp));
    }

    [Theory]
    [InlineData(BackupSelection.Settings, "settings.json")]
    [InlineData(BackupSelection.Stamps, "stamps.json")]
    [InlineData(BackupSelection.ETax, "etax.json")]
    [InlineData(BackupSelection.DigitalIds, "DigitalIDs/example.pfx")]
    public void Export_includes_only_selected_category(BackupSelection selection, string expected)
    {
        Put(Source, "settings.json", "{}"); Put(Source, "stamps.json", "{}"); Put(Source, "etax.json", "{}");
        Put(Source, Path.Combine("DigitalIDs", "example.pfx"), "test");
        _writer.Write(Archive, Source, Password, Version, selection);
        Assert.Equal(expected, Assert.Single(_reader.Inspect(Archive, Password).Files).Path);
    }

    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(); Assert.True(process.ExitCode == 0, output);
    }

    [Fact]
    public void Strict_writer_rejects_linked_destination_while_default_allows_user_choice()
    {
        CreateArchive();
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var link = Path.Combine(_root, "cloud-link"); Junction(link, outside);
        try
        {
            Assert.Throws<IOException>(() => _writer.Write(Path.Combine(link, "strict.yrkbackup"), Source, Password, Version, checkDestinationLinks: true));
            Assert.Empty(Directory.GetFiles(outside));
            _writer.Write(Path.Combine(link, "chosen.yrkbackup"), Source, Password, Version);
            Assert.Single(_reader.Inspect(Path.Combine(link, "chosen.yrkbackup"), Password).Files);
            Assert.Throws<InvalidDataException>(() => _reader.Inspect(Path.Combine(link, "chosen.yrkbackup"), Password, checkLinks: true));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("Backups")]
    [InlineData("data")]
    public void Coordinator_rejects_links_in_internal_data_or_backups_without_importing(string location)
    {
        CreateArchive();
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        string link;
        if (location == "data") { Put(outside, "settings.json", "{}"); link = Data; }
        else { Put(Data, "settings.json", "{}"); link = Path.Combine(Data, "Backups"); }
        Junction(link, outside);
        var called = false;
        try
        {
            var coordinator = new BackupImportCoordinator(import: (path, data, temp, password, replace) =>
                { called = true; throw new Exception("Must not import"); });
            Assert.Throws<IOException>(() => Run(coordinator));
            Assert.False(called);
            Assert.Empty(Directory.GetFiles(outside, "*.yrkbackup"));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(location == "data" ? outside : Data, "settings.json")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Password_worker_uses_span_on_worker_thread_and_propagates_failures()
    {
        using var secret = new SecureString(); foreach (var c in Password) secret.AppendChar(c);
        Assert.True(await BackupDialogSupport.RunAsync(secret, password => password.SequenceEqual(Password)));
        await Assert.ThrowsAsync<IOException>(() => BackupDialogSupport.RunAsync<int>(secret, _ => throw new IOException()));
    }

    [Theory]
    [InlineData("short", "short", false)]
    [InlineData("long-password-123", "long-password-124", false)]
    [InlineData("long-password-123", "long-password-123", true)]
    public void Backup_password_requires_twelve_matching_characters(string first, string second, bool expected)
    {
        using var a = new SecureString();
        using var b = new SecureString();
        foreach (var c in first) a.AppendChar(c);
        foreach (var c in second) b.AppendChar(c);
        Assert.Equal(expected, BackupDialogSupport.PasswordsMatch(a, b));
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
