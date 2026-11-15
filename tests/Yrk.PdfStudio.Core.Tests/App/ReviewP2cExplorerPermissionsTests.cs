using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.Core.Pdf.Security;
using static Yrk.PdfStudio.Core.Tests.PageReviewTestSupport;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ReviewP2cExplorerPermissionsTests
{
    [Theory]
    [InlineData(PdfPermissions.Copy)]
    [InlineData(PdfPermissions.Assemble)]
    [InlineData(PdfPermissions.Copy | PdfPermissions.Assemble)]
    public async Task Explorer_source_missing_copy_or_assemble_is_rejected_with_PAGE_008(PdfPermissions denied)
    {
        await using var app = new AppTestHarness();
        var source = Sample(app);
        var restricted = app.PathFor("restricted.pdf");
        File.WriteAllBytes(restricted, PdfSecurity.Protect(File.ReadAllBytes(source),
            new(null, "owner", PdfPermissions.All & ~denied)));
        var before = File.ReadAllBytes(restricted);
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard
            { Data = new(null, [restricted]) }, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(source));

        await app.Model.PastePagesAsync(1);

        Assert.Equal("YRK-PAGE-008", Assert.IsType<ErrorInfo>(Assert.Single(app.Dialogs.Named("ShowError")).Arguments[0]).Code);
        Assert.Equal(3, app.Model.Pages.Count);
        Assert.False(app.Model.IsDirty);
        Assert.False(app.Model.UndoCommand.CanExecute(null));
        Assert.Empty(app.Dialogs.Named("AskOwnerPassword"));
        Assert.Equal(before, File.ReadAllBytes(restricted));
    }

    [Fact]
    public async Task Encrypted_Explorer_source_with_copy_and_assemble_permissions_pastes_successfully()
    {
        await using var app = new AppTestHarness();
        var source = Sample(app);
        var allowed = app.PathFor("allowed.pdf");
        File.WriteAllBytes(allowed, PdfSecurity.Protect(File.ReadAllBytes(source),
            new(null, "owner", PdfPermissions.Copy | PdfPermissions.Assemble)));
        using var clipboard = new PageClipboard(new AppPageClipboardTests.MemoryClipboard
            { Data = new(null, [allowed]) }, app.PathFor("clipboard"));
        app.Model.PageClipboard = clipboard;
        Assert.True(await app.OpenAsync(source));
        await app.Model.PastePagesAsync(1);
        Assert.Equal(6, app.Model.Pages.Count);
        Assert.Empty(app.Dialogs.Named("ShowError"));
        await UndoRedo(app);
        Assert.Equal(3, app.Model.Pages.Count);
    }
}
