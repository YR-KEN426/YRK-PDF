using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Redaction;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.Core.Tests;

[CollectionDefinition("Temporary file security", DisableParallelization = true)]
public sealed class TemporaryFileSecurityCollection;

[Collection("Temporary file security")]
public sealed class TemporaryFileSecurityTests : IDisposable
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-temp-security-");
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(true);
    }

    [Fact]
    public void Shared_and_process_temp_paths_are_inside_the_private_directory()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PrivateTempDirectory.Path)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root, Path.GetFullPath(PdfTemporaryFiles.Shared.GetFolder("revisions")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Temp_watcher_detects_writes_in_the_shared_temporary_folder()
    {
        var file = Path.Combine(PdfTemporaryFiles.Shared.GetFolder("revisions"), Guid.NewGuid().ToString("N") + ".pdf");
        using var writes = new TempWrites();
        try
        {
            File.WriteAllText(file, "watcher guard");
            await Assert.ThrowsAnyAsync<Exception>(() => writes.AssertEmptyAsync());
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("user")]
    [InlineData(null)] // Owner-only protection opens without an open password, but is still encrypted.
    public async Task Actual_page_snapshot_and_undo_write_nothing_to_temp(string? user)
    {
        var bytes = PdfSecurity.Protect(File.ReadAllBytes(Sample), new PdfProtection(user, "owner", PdfPermissions.Assemble | PdfPermissions.Copy));
        await using var document = await _engine.OpenBytesAsync(bytes, Path.Combine(_folder.FullName, "original.pdf"), "owner");
        var model = BareModel(); // No constructor, settings, signatures or Digital ID stores are used.
        var snapshots = typeof(MainViewModel).GetField("_pageSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!;
        snapshots.SetValue(model, Activator.CreateInstance(snapshots.FieldType));

        using var writes = new TempWrites();
        var task = (Task)Invoke(model, "SnapshotPagesAsync", document, new[] { 0, 2 })!;
        await task;
        var snapshot = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var snapshotDocument = (IPdfDocument)snapshot.GetType().GetProperty("Document")!.GetValue(snapshot)!;
        try
        {
            Assert.Null(snapshot.GetType().GetProperty("Path")!.GetValue(snapshot));
            await document.DeletePagesAsync([0, 2]);
            Assert.Equal(1, document.PageCount);
            var restore = typeof(MainViewModel).GetMethod("RestorePagesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task)restore.Invoke(null, [document, snapshot, new[] { 0, 2 }])!;
            Assert.Equal(3, document.PageCount);
            Assert.Contains("Chapter 1", await document.GetPageTextAsync(0));
            Assert.Contains("Chapter 3", await document.GetPageTextAsync(2));
            await writes.AssertEmptyAsync();
        }
        finally { await snapshotDocument.DisposeAsync(); }
    }

    [Theory]
    [InlineData("user", false)]
    [InlineData(null, false)]
    [InlineData("user", true)]
    public async Task Protected_redaction_has_no_plaintext_intermediate(string? user, bool raster)
    {
        var plain = File.ReadAllBytes(Sample);
        if (raster)
        {
            var images = Path.Combine(_folder.FullName, "scan.pdf");
            var pixels = Enumerable.Repeat((byte)200, 200 * 100 * 4).ToArray();
            await _engine.CreateFromImagesAsync([new PdfBitmapInput(new PdfImage(200, 100, pixels))], new ImagesToPdfOptions(), images);
            plain = File.ReadAllBytes(images);
        }
        var input = PdfSecurity.Protect(plain, new PdfProtection(user, "owner", PdfPermissions.Copy));
        await using var source = await _engine.OpenBytesAsync(input, "source.pdf", "owner");
        var bounds = new NormalizedRect(0.3, 0.3, 0.2, 0.1);
        if (!raster)
        {
            var rects = (await source.FindTextAsync(0, "sample document", PdfSearchOptions.None))[0].Rects;
            var x = rects.Min(r => r.X);
            var y = rects.Min(r => r.Y);
            bounds = new NormalizedRect(x, y, rects.Max(r => r.X + r.Width) - x, rects.Max(r => r.Y + r.Height) - y);
        }
        using var writes = new TempWrites();
        var result = await PdfRedactor.RedactAsync(input, [new PdfRedactionArea(0, bounds)], _engine, password: "owner");
        Assert.Equal(raster, result.ImagePages.Contains(0));
        await using var reopened = await _engine.OpenBytesAsync(result.Output, "redacted.pdf", user);
        Assert.True(reopened.IsEncrypted);
        Assert.Equal(PdfPermissions.Copy, reopened.Permissions);
        Assert.DoesNotContain("sample document", await reopened.GetPageTextAsync(0));
        if (user is not null) await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenBytesAsync(result.Output, "redacted.pdf"));
        await writes.AssertEmptyAsync();
    }

    [Theory]
    [InlineData("user")]
    [InlineData(null)]
    public async Task Memory_version_save_preserves_passwords_permissions_and_file_identity(string? user)
    {
        var path = Path.Combine(_folder.FullName, "protected.pdf");
        var input = PdfSecurity.Protect(File.ReadAllBytes(Sample), new PdfProtection(user, "owner", PdfPermissions.Assemble | PdfPermissions.Copy));
        await using var original = await _engine.OpenBytesAsync(input, path, "owner");
        await original.DeletePagesAsync([1]);
        var version = await original.SaveToBytesAsync();
        await using var reopened = await _engine.OpenBytesAsync(version, path, "owner");
        Assert.Equal(path, reopened.FilePath);
        Assert.Equal(2, reopened.PageCount);
        Assert.True(reopened.IsEncrypted);
        var saved = Path.Combine(_folder.FullName, "saved.pdf");
        await reopened.SaveAsync(saved);
        await using var asUser = await _engine.OpenAsync(saved, user);
        Assert.True(asUser.IsEncrypted);
        Assert.Equal(PdfPermissions.Assemble | PdfPermissions.Copy, asUser.Permissions);
        Assert.Equal(2, asUser.PageCount);
        await using var asOwner = await _engine.OpenAsync(saved, "owner");
        Assert.Equal(PdfPermissions.All, asOwner.Permissions);
        if (user is not null) await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(saved));
    }

    [Fact]
    public async Task Stream_and_byte_buffers_live_until_document_disposal()
    {
        var bytes = File.ReadAllBytes(Sample);
        await using var fromBytes = await _engine.OpenBytesAsync(bytes, "bytes.pdf");
        Array.Clear(bytes);
        IPdfDocument fromStream;
        using (var stream = new MemoryStream(File.ReadAllBytes(Sample)))
            fromStream = await _engine.OpenStreamAsync(stream, "stream.pdf");
        await using (fromStream)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Contains("Chapter 3", await fromBytes.GetPageTextAsync(2));
            Assert.Contains("Chapter 3", await fromStream.GetPageTextAsync(2));
        }
    }

    [Theory]
    [InlineData("user")]
    [InlineData(null)]
    public async Task Actual_save_pipeline_and_recovery_of_a_memory_version_remain_encrypted(string? user)
    {
        var input = PdfSecurity.Protect(File.ReadAllBytes(Sample), new PdfProtection(user, "owner", PdfPermissions.Assemble));
        var original = Path.Combine(_folder.FullName, "original.pdf");
        File.WriteAllBytes(original, input);
        await using var document = await _engine.OpenBytesAsync(input, original, "owner");
        await document.DeletePagesAsync([1]);
        var model = BareModel();
        foreach (var name in new[] { "_placedItems", "_fieldEdits", "_fieldLooks", "_textEdits", "_newFields", "_fieldChanges" })
        {
            var field = typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(model, Activator.CreateInstance(field.FieldType));
        }
        Set(model, "_document", document);
        Set(model, "_password", "owner");
        Set(model, "_documentModified", true);
        Set(model, "_editsVersion", 1);
        Set(model, "_recoveredVersion", -1);
        Set(model, "_recoveryId", Guid.NewGuid().ToString("N"));

        using (var writes = new TempWrites())
        {
            var bytes = await (Task<byte[]>)Invoke(model, "WriteChangesToBytesAsync", document)!;
            await using var version = await _engine.OpenBytesAsync(bytes, original, user);
            Assert.True(version.IsEncrypted);
            Assert.Equal(2, version.PageCount);
            await writes.AssertEmptyAsync();
        }

        var saved = Path.Combine(_folder.FullName, "saved.pdf");
        await (Task)Invoke(model, "WriteChangesAsync", document, saved)!;
        await using (var asUser = await _engine.OpenAsync(saved, user))
        {
            Assert.True(asUser.IsEncrypted);
            Assert.Equal(PdfPermissions.Assemble, asUser.Permissions);
            Assert.Equal(2, asUser.PageCount);
        }

        var oldFolder = RecoveryStore.Folder;
        RecoveryStore.Folder = Path.Combine(_folder.FullName, "Recovery");
        try
        {
            await (Task)Invoke(model, "WriteRecoveryAsync")!;
            var recovery = Assert.Single(Directory.GetFiles(RecoveryStore.Folder, "*.pdf"));
            await using var recovered = await _engine.OpenAsync(recovery, user);
            Assert.True(recovered.IsEncrypted);
            Assert.Equal(PdfPermissions.Assemble, recovered.Permissions);
            Assert.Equal(2, recovered.PageCount);
        }
        finally
        {
            RecoveryStore.DeleteWritten();
            RecoveryStore.Folder = oldFolder;
        }
    }

    [Fact]
    public async Task Cleanup_respects_a_lock_held_by_another_process()
    {
        using var cleaner = new PdfTemporaryFiles(_folder.FullName);
        var session = Directory.CreateDirectory(Path.Combine(_folder.FullName, "YRK", "Working", Guid.NewGuid().ToString("N"))).FullName;
        var marker = Path.Combine(session, ".session-lock");
        var content = Path.Combine(session, "live.pdf");
        File.WriteAllText(marker, "child");
        File.WriteAllText(content, "live");
        var ready = Path.Combine(_folder.FullName, "ready");
        var release = Path.Combine(_folder.FullName, "release");
        static string Quote(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = $"$lease = [IO.File]::Open({Quote(marker)}, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read); " +
            $"try {{ [IO.File]::WriteAllText({Quote(ready)}, 'ready'); while (-not [IO.File]::Exists({Quote(release)})) {{ Start-Sleep -Milliseconds 20 }} }} finally {{ $lease.Dispose() }}";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        using var child = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 150 && !File.Exists(ready) && !child.HasExited; i++) await Task.Delay(20);
            Assert.True(File.Exists(ready), "The test child must hold its lease before cleanup.");
            cleaner.Cleanup(includeLegacy: true);
            Assert.True(File.Exists(content));
        }
        finally
        {
            File.WriteAllText(release, "release");
            if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); }
        }
        cleaner.Cleanup();
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public void Cleanup_skips_live_sessions_and_removes_dead_and_legacy_files()
    {
        using var live = new PdfTemporaryFiles(_folder.FullName);
        using var cleaner = new PdfTemporaryFiles(_folder.FullName);
        foreach (var area in new[] { "undo", "drawings", "revisions", "signatures", "redact", "Working" })
        {
            var liveFolder = live.GetFolder(area);
            File.WriteAllText(Path.Combine(liveFolder, "live.pdf"), "live");
            var dead = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(liveFolder)!, Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(dead, ".session-lock"), "dead");
            File.WriteAllText(Path.Combine(dead, "dead.pdf"), "dead");
            cleaner.Cleanup();
            Assert.True(File.Exists(Path.Combine(liveFolder, "live.pdf")));
            Assert.False(Directory.Exists(dead));
        }

        var legacy = Directory.CreateDirectory(Path.Combine(_folder.FullName, "yrk-redact-old")).FullName;
        File.WriteAllText(Path.Combine(legacy, "1.pdf"), "legacy");
        var outside = Path.Combine(_folder.FullName, "keep.pdf");
        File.WriteAllText(outside, "keep");
        cleaner.Cleanup(includeLegacy: true);
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void A_preview_lease_in_another_session_survives_its_creator_exiting()
    {
        var creator = new PdfTemporaryFiles(_folder.FullName);
        using var preview = new PdfTemporaryFiles(_folder.FullName);
        using var cleaner = new PdfTemporaryFiles(_folder.FullName);
        var file = Path.Combine(creator.GetFolder("revisions"), "preview.pdf");
        File.WriteAllText(file, "preview");
        preview.RetainForPath(file);
        creator.Dispose();
        cleaner.Cleanup();
        Assert.True(File.Exists(file));
        preview.Dispose();
        cleaner.Cleanup();
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Cleanup_never_follows_a_junction_or_reparse_root()
    {
        using var session = new PdfTemporaryFiles(_folder.FullName);
        var undo = session.GetFolder("undo");
        var outside = Directory.CreateDirectory(Path.Combine(_folder.FullName, "outside")).FullName;
        var sentinel = Path.Combine(outside, "keep.pdf");
        File.WriteAllText(sentinel, "keep");
        var junction = Path.Combine(undo, "junction");
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };

        info.Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"";
        using (var process = Process.Start(info)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        var dead = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(undo)!, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(dead, ".session-lock"), "dead");
        var deadJunction = Path.Combine(dead, "junction");
        info.Arguments = $"/c mklink /J \"{deadJunction}\" \"{outside}\"";
        using (var process = Process.Start(info)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        try
        {
            using var cleaner = new PdfTemporaryFiles(_folder.FullName);
            cleaner.Cleanup(includeLegacy: true);
            session.DeleteOwnedDirectory(junction);
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.True(Directory.Exists(junction));
            Assert.Throws<ArgumentException>(() => session.DeleteOwnedDirectory(outside));
        }
        finally
        {
            Directory.Delete(junction);
            Directory.Delete(deadJunction); // Remove junctions themselves, never their targets.
        }
    }

    [Theory]
    [InlineData("YRK PDF")]
    [InlineData("YRK/Working")]
    [InlineData("yrk-redact-legacy")]
    public void Cleanup_skips_roots_that_are_junctions(string root)
    {
        using var cleaner = new PdfTemporaryFiles(_folder.FullName);
        var outside = Directory.CreateDirectory(Path.Combine(_folder.FullName, "outside")).FullName;
        var sentinel = Path.Combine(outside, "keep.pdf");
        File.WriteAllText(sentinel, "keep");
        var junction = Path.Combine(_folder.FullName, root);
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(info)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        try
        {
            cleaner.Cleanup(includeLegacy: true);
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("not-a-guid")]
    [InlineData("00000000000000000000000000000000/../outside")]
    [InlineData("")]
    public void Recovery_rejects_non_GUID_ids(string id)
    {
        var entry = new RecoveryEntry(id, "original.pdf", "name.pdf", DateTime.Now);
        Assert.Throws<ArgumentException>(() => entry.PdfPath);
        Assert.Throws<ArgumentException>(() => RecoveryStore.Keep(id, "unused.tmp", "original.pdf", "name.pdf"));
        Assert.Throws<ArgumentException>(() => RecoveryStore.Delete(id));
        Assert.Throws<ArgumentException>(() => RecoveryStore.Restore(entry));
    }

    private MainViewModel BareModel()
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, _engine);
        return model;
    }

    private static void Set(object model, string field, object value) =>
        typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);

    private static object? Invoke(object model, string method, params object?[] args) =>
        typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args);

    // Observe every temp write, including transient files deleted before the operation returns.
    // The module initializer redirects managed and native temp writes to a private directory, so other
    // programs cannot interfere. Watch its whole tree to catch PDFium and Shared writes without filtering;
    // this collection also runs without other test collections in parallel.
    private sealed class TempWrites : IDisposable
    {
        private readonly ConcurrentQueue<string> _writes = new();
        private readonly ConcurrentQueue<Exception> _errors = new();
        private readonly FileSystemWatcher _watcher = new(Path.GetTempPath())
        {
            IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        public TempWrites()
        {
            _watcher.Created += (_, e) => _writes.Enqueue(e.FullPath);
            _watcher.Changed += (_, e) => _writes.Enqueue(e.FullPath);
            _watcher.Renamed += (_, e) => _writes.Enqueue(e.FullPath);
            _watcher.Error += (_, e) => _errors.Enqueue(e.GetException());
            _watcher.EnableRaisingEvents = true;
        }

        public async Task AssertEmptyAsync()
        {
            await Task.Delay(150); // Let asynchronous watcher events arrive.
            Assert.Empty(_errors);
            Assert.Empty(_writes);
        }

        public void Dispose() => _watcher.Dispose();
    }
}
