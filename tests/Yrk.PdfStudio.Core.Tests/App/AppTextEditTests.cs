using System.Reflection;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppTextEditTests
{
    [Theory]
    [InlineData("Example added text")]
    [InlineData("ข้อความภาษาไทยทดสอบ")]
    public async Task AddTextCommand_save_and_reopen_keeps_extractable_text(string text)
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(app.CreatePdf()));
        Add(app, text);
        var output = app.PathFor("added.pdf");
        Assert.True(await app.SaveToAsync(output));
        Assert.True(await app.OpenAsync(output));
        Assert.Contains(text, await Document(app).GetPageTextAsync(0));
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Theory]
    [InlineData("Old example words", "New replacement words")]
    [InlineData("ข้อความเก่าตัวอย่าง", "ข้อความใหม่แทนที่")]
    public async Task BeginEditText_replaces_existing_added_text_in_saved_pdf(string oldText, string newText)
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(app.CreatePdf()));
        var item = Add(app, oldText);
        app.Model.BeginEditText(item);
        Assert.True(item.IsEditing);
        app.Model.CommitText(item, newText);
        var output = app.PathFor("edited.pdf");
        Assert.True(await app.SaveToAsync(output));
        Assert.True(await app.OpenAsync(output));
        var extracted = await Document(app).GetPageTextAsync(0);
        Assert.Contains(newText, extracted);
        Assert.DoesNotContain(oldText, extracted);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public async Task Undo_added_text_removes_it_from_page_and_saved_pdf()
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(app.CreatePdf()));
        var item = Add(app, "UNDO-EXAMPLE");
        app.Model.UndoCommand.Execute(null);
        Assert.DoesNotContain(item, app.Model.Pages[0].Items);
        var output = app.PathFor("undo.pdf");
        Assert.True(await app.SaveToAsync(output));
        Assert.True(await app.OpenAsync(output));
        Assert.DoesNotContain("UNDO-EXAMPLE", await Document(app).GetPageTextAsync(0));
    }

    private static PlacedItemViewModel Add(AppTestHarness app, string text)
    {
        app.Model.AddTextCommand.Execute(null);
        app.Model.OnPageClicked(app.Model.Pages[0], .2, .6);
        var item = Assert.Single(app.Model.Pages[0].Items);
        app.Model.CommitText(item, text);
        return item;
    }

    internal static IPdfDocument Document(AppTestHarness app) => (IPdfDocument)typeof(MainViewModel)
        .GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;
}
