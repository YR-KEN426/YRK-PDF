namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Files the app reads from its own assembly must really be embedded there.</summary>
public sealed class EmbeddedResourceTests
{
    // 1.1.5 shipped without the privacy policy: MSBuild read ".th." / ".en." in the file name as a culture
    // and moved the files into satellite assemblies, where GetManifestResourceStream cannot see them.
    [Theory]
    [InlineData("Yrk.PdfStudio.App.Privacy.privacy.th.txt")]
    [InlineData("Yrk.PdfStudio.App.Privacy.privacy.en.txt")]
    [InlineData("Yrk.PdfStudio.App.Localization.th.json")]
    public void Resource_is_in_the_main_app_assembly(string name)
    {
        using var stream = typeof(Yrk.PdfStudio.App.Views.PrivacyPolicyDialog).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }
}
