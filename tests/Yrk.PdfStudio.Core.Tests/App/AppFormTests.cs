using System.Reflection;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppFormTests
{
    [Theory]
    [InlineData("Example form value")]
    [InlineData("ค่าฟอร์มภาษาไทย")]
    public async Task Fill_form_through_viewmodel_save_and_reopen_keeps_value(string value)
    {
        await using var app = new AppTestHarness();
        var source = app.PathFor("form.pdf");
        File.WriteAllBytes(source, PdfFormBuilder.AddFields(File.ReadAllBytes(app.CreatePdf()),
            [new(0, new(.1, .4, .5, .04), PdfNewFieldKind.Text, "Example")]));
        Assert.True(await app.OpenAsync(source));
        // Load through the app's field pipeline, including its edit callback.
        await (Task)typeof(MainViewModel).GetMethod("LoadFormFieldsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [AppTextEditTests.Document(app), app.Model.Pages[0]])!;
        var field = Assert.Single(app.Model.Pages[0].Fields.Where(f => f.Field.Name == "Example"));
        field.Text = value;
        Assert.True(app.Model.IsDirty);
        var output = app.PathFor("filled.pdf");
        Assert.True(await app.SaveToAsync(output));
        Assert.True(await app.OpenAsync(output));
        Assert.Equal(value, Assert.Single(await AppTextEditTests.Document(app).GetFormFieldsAsync(0)).Value);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Theory]
    [InlineData("Example field")]
    [InlineData("ช่องใหม่ภาษาไทย")]
    public async Task New_field_gesture_uses_MainViewModel_Forms_and_saves_real_widget(string name)
    {
        await using var app = new AppTestHarness();
        Assert.True(await app.OpenAsync(app.CreatePdf()));
        app.Model.CurrentTool = AnnotationTool.FieldText;
        app.Dialogs.Answer("AskFormField", new FormFieldAnswer(name, null, false));
        // Exercise the Forms partial's gesture endpoint, rather than calling Core.AddFields directly.
        typeof(MainViewModel).GetMethod("EndFieldGesture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app.Model, [app.Model.Pages[0], new NormalizedPoint(.1, .5), new NormalizedPoint(.5, .55), false]);
        Assert.True(app.Model.HasNewFields);
        var output = app.PathFor("new-field.pdf");
        Assert.True(await app.SaveToAsync(output));
        Assert.True(await app.OpenAsync(output));
        var field = Assert.Single(await AppTextEditTests.Document(app).GetFormFieldsAsync(0));
        Assert.Equal(name, field.Name);
        Assert.Equal(PdfFormFieldType.Text, field.Type);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }

    [Fact]
    public void RecentFiles_storage_and_clock_scopes_are_isolated_and_preserve_paths_only_format()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-recent-seam-");
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            using var outer = RecentFiles.UseStorage(Path.Combine(folder.FullName, "recent.json"), () => now);
            RecentFiles.Add(@"C:\Example\first.pdf");
            var list = RecentFiles.Items;
            Assert.Equal(now, RecentFiles.UtcNow);
            using (RecentFiles.UseStorage(Path.Combine(folder.FullName, "other.json"), () => now.AddDays(1)))
            {
                Assert.Empty(RecentFiles.Items);
                RecentFiles.Add(@"C:\Example\second.pdf");
                Assert.Equal(now.AddDays(1), RecentFiles.UtcNow);
            }
            Assert.Same(list, RecentFiles.Items);
            Assert.Equal(@"C:\Example\first.pdf", Assert.Single(list).Path);
            Assert.Equal(new[] { @"C:\Example\first.pdf" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(Path.Combine(folder.FullName, "recent.json"))));
            using var reload = RecentFiles.UseStorage(Path.Combine(folder.FullName, "recent.json"));
            Assert.Equal(@"C:\Example\first.pdf", Assert.Single(RecentFiles.Items).Path);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task RecentFiles_scope_restores_parent_when_disposed_after_await()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-recent-async-");
        try
        {
            using var parent = RecentFiles.UseStorage(Path.Combine(folder.FullName, "parent.json"));
            RecentFiles.Add(@"C:\Example\parent.pdf");
            var child = RecentFiles.UseStorage(Path.Combine(folder.FullName, "child.json"));
            RecentFiles.Add(@"C:\Example\child.pdf");
            await DisposeInChildContext();
            Assert.Equal(@"C:\Example\parent.pdf", Assert.Single(RecentFiles.Items).Path);
            async Task DisposeInChildContext() { await Task.Yield(); child.Dispose(); }
        }
        finally { folder.Delete(true); }
    }
}
