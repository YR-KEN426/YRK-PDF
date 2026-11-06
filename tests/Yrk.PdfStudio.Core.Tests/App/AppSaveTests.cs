using System.Runtime.InteropServices;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppSaveTests
{
    [Fact]
    public async Task Save_overwrites_source_with_the_edited_document()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        Assert.True(await app.OpenAsync(source));
        await app.RotateAsync();

        await app.Model.SaveCommand.ExecuteAsync(null);

        Assert.False(before.AsSpan().SequenceEqual(File.ReadAllBytes(source)));
        Assert.False(app.Model.IsDirty);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        await AssertRotatedAsync(app, source);
        Assert.Equal([source], Directory.GetFiles(app.Folder));
    }

    [Fact]
    public async Task Save_as_writes_edits_to_new_file_and_preserves_source()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        var target = app.PathFor("saved.pdf");
        Assert.True(await app.OpenAsync(source));
        await app.RotateAsync();
        app.Dialogs.Answer("PickSavePdfFile", target);

        await app.Model.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(before, File.ReadAllBytes(source));
        await AssertRotatedAsync(app, target);
        Assert.False(app.Model.IsDirty);
        Assert.Equal(source, Assert.Single(app.Dialogs.Named("PickSavePdfFile")).Arguments[0]);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Equal(2, Directory.GetFiles(app.Folder).Length);
    }

    [Fact]
    public async Task Cancelling_save_as_writes_nothing_and_keeps_unsaved_edits()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        Assert.True(await app.OpenAsync(source));
        await app.RotateAsync();
        app.Dialogs.Answer("PickSavePdfFile", (object?)null);

        await app.Model.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal([source], Directory.GetFiles(app.Folder));
        Assert.True(app.Model.IsDirty);
        Assert.Single(app.Dialogs.Named("PickSavePdfFile"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    // Save As onto a hard link of the open file is an ordinary overwrite of that name, which the
    // user confirmed in the save dialog. The new file is written to a temporary file and then moved
    // over the alias, so only the alias's directory entry is replaced: the source name keeps the
    // original bytes and the two names stop sharing one file. (Signing refuses aliases instead,
    // because signing must never touch the source.)
    [Fact]
    public async Task Save_as_onto_a_hard_link_replaces_only_that_name()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        var alias = app.PathFor("alias.pdf");
        Assert.True(CreateHardLink(alias, source, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        Assert.True(Yrk.PdfStudio.App.Services.FileIdentity.AreSameFile(source, alias));
        Assert.True(await app.OpenAsync(source));
        await app.RotateAsync();
        app.Dialogs.Answer("PickSavePdfFile", alias);

        await app.Model.SaveAsCommand.ExecuteAsync(null);

        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.False(app.Model.IsDirty);
        Assert.Equal(before, File.ReadAllBytes(source));
        await AssertRotatedAsync(app, alias);
        Assert.False(Yrk.PdfStudio.App.Services.FileIdentity.AreSameFile(source, alias));
        Assert.Equal(2, Directory.GetFiles(app.Folder).Length);
    }

    [Theory]
    [InlineData("RC4", false)]
    [InlineData("AES-128", false)]
    [InlineData("AES-256", false)]
    [InlineData("RC4", true)]
    [InlineData("AES-128", true)]
    [InlineData("AES-256", true)]
    public async Task Protected_save_keeps_original_passwords_and_the_edit(string encryption, bool saveAs)
    {
        await using var app = new AppTestHarness();
        var source = CreateProtected(app, encryption);
        var before = File.ReadAllBytes(source);
        app.Dialogs.Answer("PromptPassword", "user");
        Assert.True(await app.OpenAsync(source));
        app.Dialogs.Answer("ConfirmPassword", "user", "user"); // First edit, then save.
        await app.RotateAsync();
        var target = saveAs ? app.PathFor("saved.pdf") : source;

        Assert.True(await app.SaveToAsync(target));

        Assert.Equal(2, app.Dialogs.Named("ConfirmPassword").Length);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.False(app.Model.IsDirty);
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => app.Engine.OpenAsync(target));
        await AssertRotatedAsync(app, target, "user");
        await AssertRotatedAsync(app, target, "owner");
        if (saveAs) Assert.Equal(before, File.ReadAllBytes(source));
        else Assert.False(before.AsSpan().SequenceEqual(File.ReadAllBytes(source)));
        Assert.Equal(saveAs ? 2 : 1, Directory.GetFiles(app.Folder).Length);
    }

    [Theory]
    [InlineData("RC4", false)]
    [InlineData("AES-128", false)]
    [InlineData("AES-256", false)]
    [InlineData("RC4", true)]
    [InlineData("AES-128", true)]
    [InlineData("AES-256", true)]
    public async Task Wrong_or_cancelled_save_password_writes_nothing(string encryption, bool wrongFirst)
    {
        await using var app = new AppTestHarness();
        var source = CreateProtected(app, encryption);
        var before = File.ReadAllBytes(source);
        app.Dialogs.Answer("PromptPassword", "user");
        Assert.True(await app.OpenAsync(source));
        app.Dialogs.Answer("ConfirmPassword", "user");
        await app.RotateAsync();
        app.Dialogs.Calls.Clear();
        if (wrongFirst) app.Dialogs.Answer("ConfirmPassword", "wrong");
        app.Dialogs.Answer("ConfirmPassword", (object?)null);

        Assert.False(await app.SaveToAsync(app.PathFor("saved.pdf")));

        var prompts = app.Dialogs.Named("ConfirmPassword");
        Assert.Equal(wrongFirst ? 2 : 1, prompts.Length);
        Assert.Equal(false, prompts[0].Arguments[2]);
        if (wrongFirst) Assert.Equal(true, prompts[1].Arguments[2]);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal([source], Directory.GetFiles(app.Folder));
        Assert.True(app.Model.IsDirty);
        Assert.False(app.Model.IsBusy);
    }

    [Theory]
    [InlineData("RC4")]
    [InlineData("AES-128")]
    [InlineData("AES-256")]
    public async Task Wrong_open_password_then_cancel_writes_nothing(string encryption)
    {
        await using var app = new AppTestHarness();
        var source = CreateProtected(app, encryption);
        var before = File.ReadAllBytes(source);
        app.Dialogs.Answer("PromptPassword", "wrong", null);
        Assert.False(await app.OpenAsync(source));

        Assert.False(await app.SaveToAsync(app.PathFor("saved.pdf")));

        var prompts = app.Dialogs.Named("PromptPassword");
        Assert.Equal(2, prompts.Length);
        Assert.Equal(true, prompts[1].Arguments[1]);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal([source], Directory.GetFiles(app.Folder));
    }

    [Fact]
    public async Task Save_to_an_unusable_folder_shows_an_error_and_preserves_source()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        Assert.True(await app.OpenAsync(source));
        await app.RotateAsync();
        // A regular file cannot be used as a directory, even under an elevated Windows account.
        var blocker = app.PathFor("not-a-directory");
        File.WriteAllText(blocker, "blocker");
        var target = Path.Combine(blocker, "saved.pdf");

        Assert.False(await app.SaveToAsync(target));

        Assert.Single(app.Dialogs.Named("ShowError"));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal("blocker", File.ReadAllText(blocker));
        Assert.Equal(new[] { blocker, source }.Order(), Directory.GetFiles(app.Folder, "*", SearchOption.AllDirectories).Order());
        Assert.True(app.Model.IsDirty);
        Assert.False(app.Model.IsBusy);
    }

    [Fact]
    public async Task Failed_write_midway_removes_partial_temporary_file_and_preserves_source()
    {
        await using var app = new AppTestHarness(failWriteMidway: true);
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        var target = app.PathFor("saved.pdf");
        Assert.True(await app.OpenAsync(source));
        await app.RotateAsync();

        Assert.False(await app.SaveToAsync(target));

        Assert.True(app.PartialFileExistedAtFailure);
        Assert.NotNull(app.FailedWritePath);
        Assert.EndsWith(".yrk-saving", app.FailedWritePath);
        Assert.Equal(app.Folder, Path.GetDirectoryName(app.FailedWritePath));
        Assert.Single(app.Dialogs.Named("ShowError"));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(app.Folder, "*.yrk-saving"));
        Assert.False(File.Exists(target));
        Assert.True(app.Model.IsDirty);
        Assert.False(app.Model.IsBusy);
    }

    private static string CreateProtected(AppTestHarness app, string encryption)
    {
        var path = app.CreatePdf();
        var plain = File.ReadAllBytes(path);
        var encrypted = encryption switch
        {
            "RC4" => ProtectedPdfFactory.Legacy(plain, 3, "V2", "user", "owner"),
            "AES-128" => ProtectedPdfFactory.Legacy(plain, 4, "AESV2", "user", "owner"),
            "AES-256" => PdfSecurity.Protect(plain, new PdfProtection("user", "owner", PdfPermissions.All)),
            _ => throw new ArgumentOutOfRangeException(nameof(encryption)),
        };
        File.WriteAllBytes(path, encrypted);
        return path;
    }

    private static async Task AssertRotatedAsync(AppTestHarness app, string path, string? password = null)
    {
        await using var document = await app.Engine.OpenAsync(path, password);
        var size = Assert.Single(document.PageSizes);
        Assert.Equal(842, size.Width);
        Assert.Equal(595, size.Height);
        Assert.Contains("Monthly report", await document.GetPageTextAsync(0));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
