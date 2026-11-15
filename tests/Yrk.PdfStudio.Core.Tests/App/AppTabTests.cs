using Yrk.PdfStudio.App.Localization;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppTabTests
{
    [Fact]
    public async Task Single_file_open_on_empty_model_and_cancel_keep_existing_behavior()
    {
        await using var app = new AppTestHarness();
        var path = app.CreatePdf();
        var newTabRequests = new List<string>();
        app.Model.OpenInNewTabRequested += (_, file) => newTabRequests.Add(file);
        app.Model.OpenManyInTabsRequested += _ => throw new InvalidOperationException("Single file used batch opener");
        app.Dialogs.Answer("PickPdfFiles", (object)new[] { path });
        await app.Model.OpenCommand.ExecuteAsync(null);
        Assert.Equal(path, app.Model.DocumentPath);
        Assert.Empty(newTabRequests);

        // Cancellation (the fake defaults to an empty list) changes nothing.
        await app.Model.OpenCommand.ExecuteAsync(null);
        Assert.Equal(path, app.Model.DocumentPath);
        Assert.Empty(newTabRequests);

        app.Dialogs.Answer("PickPdfFiles", (object)new[] { "another.pdf" });
        await app.Model.OpenCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "another.pdf" }, newTabRequests);
        Assert.Equal(path, app.Model.DocumentPath);
    }

    [Fact]
    public async Task Closing_last_tab_keeps_language_notifications_until_the_model_is_closed()
    {
        await using var app = new AppTestHarness();
        var originalLanguage = Loc.Language;
        var otherLanguage = originalLanguage == Loc.English ? Loc.Thai : Loc.English;
        var notifications = new List<string?>();
        app.Model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        try
        {
            await app.Model.CloseTabAsync(keepAlive: true);
            notifications.Clear();

            Loc.Language = otherLanguage;

            Assert.Contains(string.Empty, notifications);

            await app.Model.CloseTabAsync();
            notifications.Clear();

            Loc.Language = originalLanguage;

            Assert.Empty(notifications);
        }
        finally
        {
            Loc.Language = originalLanguage;
        }
    }
}
