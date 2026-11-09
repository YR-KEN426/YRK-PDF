using System.Globalization;
using Yrk.PdfStudio.App.Localization;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppToolbarHintTests
{
    [Fact]
    public async Task Leaving_the_Edit_tab_clears_the_text_edit_hint_only()
    {
        await using var app = new AppTestHarness();

        app.Model.StatusMessage = "Click on a line of text to edit it";
        app.Model.ClearTextEditStatus();
        Assert.Equal(string.Empty, app.Model.StatusMessage);

        app.Model.StatusMessage = "Saved";
        app.Model.ClearTextEditStatus();
        Assert.Equal("Saved", app.Model.StatusMessage);
    }

    [Fact]
    public void English_texts_use_the_Western_year_even_on_a_Thai_region()
    {
        var originalLanguage = Loc.Language;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            Loc.Language = Loc.English;
            Assert.Equal("1/1/2036", Loc.F("{0:d}", new DateTime(2036, 1, 1)).Replace("01/01/2036", "1/1/2036"));
            Loc.Language = Loc.Thai;
            Assert.Contains("2579", Loc.F("{0:d}", new DateTime(2036, 1, 1)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            Loc.Language = originalLanguage;
        }
    }
}
