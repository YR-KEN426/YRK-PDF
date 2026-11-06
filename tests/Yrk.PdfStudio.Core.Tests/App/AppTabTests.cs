using Yrk.PdfStudio.App.Localization;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppTabTests
{
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
