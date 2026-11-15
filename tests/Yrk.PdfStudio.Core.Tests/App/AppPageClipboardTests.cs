using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppPageClipboardTests
{
    internal sealed class MemoryClipboard : IPageClipboard
    {
        public PageClipboardData Data { get; set; } = new(null, []);
        public bool FailWrite { get; set; }
        public PageClipboardData Read() => Data;
        public void WritePages(string payload)
        {
            if (FailWrite) throw new COMException("Simulated clipboard contention.");
            Data = new(payload, []);
        }
    }

    private static string Sample(AppTestHarness app, string name = "source.pdf")
    {
        var path = app.PathFor(name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"), path);
        return path;
    }

    private static IPdfDocument Document(AppTestHarness app) => (IPdfDocument)typeof(MainViewModel)
        .GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;

    private static async Task AssertOrder(AppTestHarness app, params int[] chapters)
    {
        Assert.Equal(chapters.Length, app.Model.Pages.Count);
        for (var i = 0; i < chapters.Length; i++)
            Assert.Contains("Chapter " + chapters[i], await Document(app).GetPageTextAsync(i));
    }

    private static async Task Idle(AppTestHarness app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (app.Model.IsBusy) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task Copies_pages_2_3_between_models_and_paste_undo_redo_preserve_order_and_source()
    {
        await using var a = new AppTestHarness();
        await using var b = new AppTestHarness();
        var os = new MemoryClipboard();
        using var origin = new PageClipboard(os, a.PathFor("clipboard"));
        using var destination = new PageClipboard(os, origin.Folder); // Separate instance, as in another window/process.
        a.Model.PageClipboard = origin;
        b.Model.PageClipboard = destination;
        var source = Sample(a);
        var before = File.ReadAllBytes(source);
        Assert.True(await a.OpenAsync(source));
        Assert.True(await b.OpenAsync(Sample(b)));

        Assert.True(await a.Model.CopyPagesAsync([1, 2]));
        Assert.False(a.Model.IsDirty);
        await b.Model.PastePagesAsync(1);

        await AssertOrder(b, 1, 2, 3, 2, 3);
        Assert.Equal(Loc.F("Pasted {0} pages", 2), b.Model.StatusMessage);
        b.Model.UndoCommand.Execute(null);
        await Idle(b);
        await AssertOrder(b, 1, 2, 3);
        // Redo must survive the originating app deleting its clipboard file.
        origin.Dispose();
        b.Model.RedoCommand.Execute(null);
        await Idle(b);
        await AssertOrder(b, 1, 2, 3, 2, 3);
        await AssertOrder(a, 1, 2, 3);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(a.Dialogs.Named("ShowError"));
        Assert.Empty(b.Dialogs.Named("ShowError"));
    }

    [Theory]
    [InlineData(PdfPermissions.Copy)]
    [InlineData(PdfPermissions.Assemble)]
    public async Task Restrictions_disable_copy_cut_and_assembly_disables_paste(PdfPermissions denied)
    {
        await using var app = new AppTestHarness();
        var os = new MemoryClipboard();
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        var source = Sample(app);
        var protectedPath = app.PathFor("restricted.pdf");
        File.WriteAllBytes(protectedPath, PdfSecurity.Protect(File.ReadAllBytes(source), new(null, "owner", PdfPermissions.All & ~denied)));
        Assert.True(await app.OpenAsync(protectedPath));

        Assert.False(app.Model.CanCopyPages);
        Assert.False(app.Model.CanCutPages([1]));
        Assert.Equal(denied != PdfPermissions.Assemble, app.Model.CanPastePages);
        Assert.False(await app.Model.CopyPagesAsync([1], cut: true));
        Assert.Null(os.Data.Pages);
        Assert.False(app.Model.IsDirty);
        Assert.Empty(app.Dialogs.Named("AskOwnerPassword"));
    }

    [Fact]
    public async Task Missing_temporary_pdf_reports_PAGE_006_without_changing_target()
    {
        await using var app = new AppTestHarness();
        using var clipboard = new PageClipboard(new MemoryClipboard(), app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(Sample(app)));
        Assert.True(await app.Model.CopyPagesAsync([1, 2]));
        File.Delete(clipboard.ReadSource()!.Path);
        Assert.True(clipboard.HasContent); // Keep Paste enabled so it can explain a missing copy.

        await app.Model.PastePagesAsync(1);

        Assert.Equal("YRK-PAGE-006", Assert.IsType<ErrorInfo>(Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]).Code);
        await AssertOrder(app, 1, 2, 3);
        Assert.False(app.Model.IsDirty);
    }

    [Fact]
    public async Task Cut_removes_page_2_with_undo_and_tracks_the_close_question()
    {
        await using var app = new AppTestHarness();
        using var clipboard = new PageClipboard(new MemoryClipboard(), app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        var source = Sample(app);
        var before = File.ReadAllBytes(source);
        Assert.True(await app.OpenAsync(source));

        Assert.True(await app.Model.CopyPagesAsync([1], cut: true));

        await AssertOrder(app, 1, 3);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.True(clipboard.HasUnpastedCut);
        app.Dialogs.Answer("Confirm", false, true);
        Assert.False(clipboard.ConfirmClose(app.Dialogs.Service));
        Assert.True(clipboard.ConfirmClose(app.Dialogs.Service));
        Assert.Equal(2, app.Dialogs.Named("Confirm").Length);
        app.Model.UndoCommand.Execute(null);
        await Idle(app);
        await AssertOrder(app, 1, 2, 3);
        Assert.False(clipboard.HasUnpastedCut);
        app.Model.RedoCommand.Execute(null);
        await Idle(app);
        await AssertOrder(app, 1, 3);
        Assert.True(clipboard.HasUnpastedCut);
    }

    [Fact]
    public async Task Failed_clipboard_write_never_deletes_pages_or_invalidates_previous_copy()
    {
        await using var app = new AppTestHarness();
        var os = new MemoryClipboard();
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(Sample(app)));
        Assert.True(await app.Model.CopyPagesAsync([0]));
        var previous = clipboard.ReadSource()!.Path;
        os.FailWrite = true;

        Assert.False(await app.Model.CopyPagesAsync([1], cut: true));

        await AssertOrder(app, 1, 2, 3);
        Assert.False(app.Model.IsDirty);
        Assert.False(clipboard.HasUnpastedCut);
        Assert.Equal("YRK-PAGE-007", Assert.IsType<ErrorInfo>(Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]).Code);
        Assert.Equal([previous], Directory.GetFiles(clipboard.Folder));
    }

    [Fact]
    public async Task Cutting_every_page_is_disabled_and_does_not_touch_clipboard()
    {
        await using var app = new AppTestHarness();
        var os = new MemoryClipboard();
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(Sample(app)));
        Assert.False(app.Model.CanCutPages([0, 1, 2]));
        Assert.False(await app.Model.CopyPagesAsync([0, 1, 2], cut: true));
        Assert.Null(os.Data.Pages);
        await AssertOrder(app, 1, 2, 3);
    }

    [Fact]
    public async Task Successful_paste_acknowledges_cut_to_another_clipboard_instance()
    {
        await using var a = new AppTestHarness();
        await using var b = new AppTestHarness();
        var os = new MemoryClipboard();
        using var origin = new PageClipboard(os, a.PathFor("clipboard"));
        using var destination = new PageClipboard(os, origin.Folder);
        a.Model.PageClipboard = origin;
        b.Model.PageClipboard = destination;
        Assert.True(await a.OpenAsync(Sample(a)));
        Assert.True(await b.OpenAsync(Sample(b)));
        Assert.True(await a.Model.CopyPagesAsync([1], cut: true));
        Assert.True(origin.HasUnpastedCut);

        await b.Model.PastePagesAsync(1);

        Assert.False(origin.HasUnpastedCut);
        Assert.True(origin.ConfirmClose(a.Dialogs.Service));
        Assert.Empty(a.Dialogs.Named("Confirm"));
        await AssertOrder(b, 1, 2, 2, 3);
    }

    [Fact]
    public async Task Explorer_pdf_pastes_all_pages_with_undo()
    {
        await using var app = new AppTestHarness();
        var source = Sample(app);
        var os = new MemoryClipboard { Data = new(null, [source]) };
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(source));
        Assert.True(clipboard.HasContent);

        await app.Model.PastePagesAsync(1);

        await AssertOrder(app, 1, 1, 2, 3, 2, 3);
        app.Model.UndoCommand.Execute(null);
        await Idle(app);
        await AssertOrder(app, 1, 2, 3);
    }

    [Fact]
    public async Task Signed_destination_uses_existing_warning_and_cancel_keeps_cut_pending()
    {
        await using var a = new AppTestHarness();
        await using var b = new AppTestHarness();
        var os = new MemoryClipboard();
        using var clipboard = new PageClipboard(os, a.PathFor("clipboard"));
        a.Model.PageClipboard = clipboard;
        b.Model.PageClipboard = clipboard;
        Assert.True(await a.OpenAsync(Sample(a)));
        Assert.True(await a.Model.CopyPagesAsync([1], cut: true));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Page clipboard test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var signed = b.PathFor("signed.pdf");
        File.WriteAllBytes(signed, await PdfSigner.SignAsync(File.ReadAllBytes(Sample(b)), new(certificate)));
        Assert.True(await b.OpenAsync(signed));
        b.Dialogs.Answer("Confirm", false, true);

        await b.Model.PastePagesAsync(1);

        await AssertOrder(b, 1, 2, 3);
        Assert.True(clipboard.HasUnpastedCut);
        Assert.False(b.Model.IsDirty);
        Assert.Equal(Loc.T("Editing this item will make digital signatures show that the document was changed after signing. Continue?"),
            Assert.Single(b.Dialogs.Named("Confirm")).Arguments[0]);
        await b.Model.PastePagesAsync(1);
        Assert.False(clipboard.HasUnpastedCut);
        await AssertOrder(b, 1, 2, 2, 3);
    }

    [Fact]
    public async Task Cleanup_only_deletes_owned_safe_paths_and_never_paths_from_clipboard()
    {
        await using var app = new AppTestHarness();
        var os = new MemoryClipboard();
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        var outside = Sample(app);
        var owned = clipboard.Allocate();
        var foreign = Path.Combine(clipboard.Folder, "foreign.pdf");
        File.Copy(outside, foreign);
        var sibling = Directory.CreateDirectory(clipboard.Folder + "-other").FullName;
        var siblingPdf = Path.Combine(sibling, "foreign.pdf");
        File.Copy(outside, siblingPdf);
        os.Data = new(JsonSerializer.Serialize(new { Path = outside, PageCount = 3 }), []);

        Assert.Null(clipboard.ReadSource());
        clipboard.DeleteOwned(outside);
        clipboard.DeleteOwned(foreign);
        clipboard.DeleteOwned(siblingPdf);
        var traversal = Path.Combine(clipboard.Folder, "..", "clipboard", Path.GetFileName(owned));
        Assert.False(clipboard.IsSafePagePath(traversal));
        clipboard.DeleteOwned(traversal);
        Assert.True(File.Exists(owned));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(siblingPdf));
        clipboard.Dispose();
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(siblingPdf));
    }

    [Fact]
    public async Task New_copy_cleans_previous_owned_file_and_startup_only_sweeps_old_generated_names()
    {
        await using var app = new AppTestHarness();
        using var clipboard = new PageClipboard(new MemoryClipboard(), app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(Sample(app)));
        Assert.True(await app.Model.CopyPagesAsync([0]));
        var previous = clipboard.ReadSource()!.Path;
        Assert.True(await app.Model.CopyPagesAsync([1]));
        Assert.False(File.Exists(previous));
        var current = clipboard.ReadSource()!.Path;
        var old = Path.Combine(clipboard.Folder, "yrk-pages-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(old, "old generated file");
        var foreign = Path.Combine(clipboard.Folder, "foreign.pdf");
        File.WriteAllText(foreign, "foreign file");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));

        clipboard.SweepStale(DateTime.UtcNow);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Custom_format_rejects_non_PDF_missing_traversal_and_outside_paths()
    {
        await using var app = new AppTestHarness();
        var os = new MemoryClipboard();
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        Directory.CreateDirectory(clipboard.Folder);
        var text = Path.Combine(clipboard.Folder, "note.txt");
        File.WriteAllText(text, "text");
        foreach (var path in new[] { text, Sample(app), Path.Combine(clipboard.Folder, "missing.pdf"),
                     Path.Combine(clipboard.Folder, "..", "source.pdf") })
        {
            os.Data = new(JsonSerializer.Serialize(new { Path = path, PageCount = 1 }), []);
            Assert.Null(clipboard.ReadSource());
        }
        os.Data = new("not json", []);
        Assert.Null(clipboard.ReadSource());
        os.Data = new(null, [text]);
        Assert.False(clipboard.HasContent);
        Assert.Null(clipboard.ReadSource());
    }

    [Fact]
    public async Task Junction_in_clipboard_folder_is_never_followed_for_reads_creation_or_cleanup()
    {
        await using var app = new AppTestHarness();
        var os = new MemoryClipboard();
        using var clipboard = new PageClipboard(os, app.PathFor("clipboard"));
        var owned = clipboard.Allocate();
        File.Delete(owned);
        Directory.Move(clipboard.Folder, app.PathFor("original-clipboard"));
        var outside = Directory.CreateDirectory(app.PathFor("outside")).FullName;
        var target = Path.Combine(outside, Path.GetFileName(owned));
        File.WriteAllText(target, "must remain untouched");
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-2));
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{clipboard.Folder}\" \"{outside}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        try
        {
            Assert.False(clipboard.IsSafePagePath(owned));
            os.Data = new(JsonSerializer.Serialize(new { Path = owned, PageCount = 1 }), []);
            Assert.Null(clipboard.ReadSource());
            Assert.Throws<IOException>(() => clipboard.Allocate());
            clipboard.DeleteOwned(owned);
            clipboard.SweepStale(DateTime.UtcNow);
            clipboard.Dispose();
            Assert.Equal("must remain untouched", File.ReadAllText(target));
        }
        finally { Directory.Delete(clipboard.Folder); } // Remove only the junction itself, never recurse.
    }
}
