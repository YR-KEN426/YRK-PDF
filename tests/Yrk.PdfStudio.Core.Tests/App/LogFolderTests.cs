namespace Yrk.PdfStudio.Core.Tests;

public sealed class LogFolderTests
{
    [Fact]
    public void Default_log_folder_uses_nonempty_environment_value()
    {
        const string logFolder = "C:\\test-logs";

        Assert.Equal(logFolder, Yrk.PdfStudio.App.App.GetDefaultLogFolder(logFolder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Default_log_folder_falls_back_when_environment_value_is_empty(string? logFolder)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "logs");

        Assert.Equal(expected, Yrk.PdfStudio.App.App.GetDefaultLogFolder(logFolder));
    }
}
