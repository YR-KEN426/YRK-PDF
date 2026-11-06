using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignedFileNameTests
{
    [Theory]
    [InlineData("report.pdf", "report (signed).pdf")]
    [InlineData("report (signed).pdf", "report (signed).pdf")]
    [InlineData("report (SIGNED).pdf", "report (SIGNED).pdf")]
    [InlineData("report (signed) draft.pdf", "report (signed) draft (signed).pdf")]
    [InlineData("C:/documents/report.pdf", "report (signed).pdf")]
    [InlineData("x (signed) (signed).pdf", "x (signed) (signed).pdf")]
    public void SuggestAddsSuffixOnlyWhenMissing(string path, string expected)
    {
        Assert.Equal(expected, SignedFileName.Suggest(path));
    }

    [Theory]
    [InlineData("report.pdf", "report (before signing).pdf")]
    [InlineData("report (signed).pdf", "report (before signing).pdf")]
    [InlineData("report (SIGNED).pdf", "report (before signing).pdf")]
    [InlineData("report (signed) draft.pdf", "report (signed) draft (before signing).pdf")]
    [InlineData("C:/documents/report.pdf", "report (before signing).pdf")]
    public void SuggestBeforeSigningRemovesOnlyTrailingSignedSuffix(string path, string expected)
    {
        Assert.Equal(expected, SignedFileName.SuggestBeforeSigning(path));
    }
}
