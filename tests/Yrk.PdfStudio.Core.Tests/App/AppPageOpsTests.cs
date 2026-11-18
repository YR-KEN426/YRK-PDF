using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppPageOpsTests
{
    [Theory]
    [InlineData("rotate")]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("insert")]
    [InlineData("extract")]
    public async Task Page_operation_save_and_reopen_preserves_count_order_and_rotation(string operation)
    {
        await using var app = new AppTestHarness();
        var source = app.PathFor("source.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"), source);
        Assert.True(await app.OpenAsync(source));
        int[] expected = [1, 2, 3];
        var output = app.PathFor("result.pdf");
        switch (operation)
        {
            case "rotate": await app.Model.RotatePagesAsync([1], 1); break;
            case "delete":
                app.Dialogs.Answer("Confirm", true);
                await app.Model.DeletePagesAsync([1]); expected = [1, 3]; break;
            case "move": await app.Model.MovePagesAsync([2], 0); expected = [3, 1, 2]; break;
            case "insert":
                var extra = app.PathFor("extra.pdf"); File.Copy(source, extra);
                app.Dialogs.Answer("PickPdfFile", extra);
                await app.Model.InsertPagesFromFileAsync(1); expected = [1, 1, 2, 3, 2, 3]; break;
            case "extract":
                app.Dialogs.Answer("PickSavePdfFile", output);
                await app.Model.ExtractPagesAsync([0, 2]); expected = [1, 3]; break;
        }
        if (operation != "extract") Assert.True(await app.SaveToAsync(output));
        else
        {
            // Extract writes a separate PDF; save/reopen the source too to prove it retained all pages.
            var savedSource = app.PathFor("unchanged-source.pdf");
            Assert.True(await app.SaveToAsync(savedSource));
            Assert.True(await app.OpenAsync(savedSource));
            Assert.Equal(3, app.Model.Pages.Count);
        }
        Assert.True(await app.OpenAsync(output));
        Assert.Equal(expected.Length, app.Model.Pages.Count);
        var document = AppTextEditTests.Document(app);
        var parsed = CosDocument.Load(File.ReadAllBytes(output));
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Contains("Chapter " + expected[i], await document.GetPageTextAsync(i));
            Assert.Equal(operation == "rotate" && i == 1 ? 90 : 0, parsed.GetPages()[i].Rotate);
        }
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }
}


