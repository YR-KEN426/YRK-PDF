using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ReportRedactorTests
{
    private static string Redact(string text) => ReportRedactor.Redact(text, "ken", "WORK-PC", @"C:\Users\ken");

    [Theory]
    [InlineData(@"C:\Users\ken\Desktop\ลับ.pdf")]
    [InlineData(@"C:/Users/ken/Desktop/ลับ.pdf")]
    [InlineData(@"C:\Program Files\My App\a b.pdf")]
    [InlineData(@"C:/Program Files/My App/a b.pdf")]
    [InlineData(@"D:\เอกสาร ลับ\ไฟล์ ใหม่.pdf")]
    [InlineData(@"D:/เอกสาร ลับ/ไฟล์ ใหม่.pdf")]
    [InlineData(@"\\server\share\private.pdf")]
    [InlineData(@"\server\share\private.pdf")]
    [InlineData(@"\\server\shared files\เอกสาร ลับ\ไฟล์ ใหม่.pdf")]
    [InlineData(@"\\server/share/เอกสาร ลับ/ไฟล์ ใหม่.pdf")]
    [InlineData(@"%USERPROFILE%\Desktop\ลับ.pdf")]
    [InlineData(@"%userprofile%/My Documents/a b.pdf")]
    [InlineData(@"C:\Users\ken")]
    [InlineData(@"%USERPROFILE%")]
    [InlineData(@"C:\private\file.customextension")]
    [InlineData(@"C:\private\archive.tar.gz")]
    [InlineData(@"C:\private\a.b.pdf")]
    [InlineData(@"C:\private\report.pdf copy.pdf")]
    [InlineData(@"C:\archive.pdf files\private.pdf")]
    [InlineData(@"//server/share/private.pdf")]
    [InlineData(@"C:\private\report.2026 draft.pdf")]
    [InlineData(@"C:\Program Files (x86)\My App\a (1).pdf")]
    [InlineData(@"C:\folder.with.dots\nested\private.pdf")]
    [InlineData(@"C:\private\folder without extension")]
    [InlineData(@"\\server\shared files\folder without extension")]
    public void Paths_include_the_entire_final_file_name(string path)
    {
        Assert.Equal("<path>", Redact(path));
    }

    [Theory]
    [InlineData("Could not open \"D:\\เอกสาร ลับ\\ไฟล์ ใหม่.pdf\". Retry.", "Could not open \"<path>\". Retry.")]
    [InlineData("Open 'C:\\Program Files\\My App\\a b.pdf' failed.", "Open '<path>' failed.")]
    [InlineData("Open C:\\private\\file.pdf failed.", "Open <path> failed.")]
    [InlineData("Open C:\\private\\file.pdf. Retry.", "Open <path>. Retry.")]
    [InlineData("Open C:\\private\\folder. Retry.", "Open <path>. Retry.")]
    [InlineData("C:\\private\\file.pdf; retry", "<path>; retry")]
    [InlineData("C:\\private\\file.pdf, retry", "<path>, retry")]
    [InlineData("C:\\private\\file.pdf! Retry", "<path>! Retry")]
    [InlineData("(C:\\private\\file.pdf)", "(<path>)")]
    [InlineData("C:\\private\\file.pdf\r\nNext line", "<path>\r\nNext line")]
    [InlineData("\"C:\\private\\one.pdf\" -> \"D:\\secret\\two.pdf\"", "\"<path>\" -> \"<path>\"")]
    public void Paths_stop_at_quotes_newlines_sentence_terminators_or_file_extensions(string text, string expected)
    {
        Assert.Equal(expected, Redact(text));
    }

    [Fact]
    public void User_and_machine_names_are_redacted_case_insensitively_inside_text()
    {
        Assert.Equal("prefix<user>suffix on pre<pc>post; <user> <pc>",
            Redact("prefixKENsuffix on prework-pcpost; KeN WORK-PC"));
    }

    [Theory]
    [InlineData("a", "b", "ZAZ ZbZ \"C:\\secret\\file.pdf\" a@b.test")]
    [InlineData("p", "e", "ZPZ ZeZ \"C:\\secret\\file.pdf\" p@e.test")]
    public void Very_short_names_are_redacted_without_damaging_generated_markers(string user, string pc, string text)
    {
        Assert.Equal("Z<user>Z Z<pc>Z \"<path>\" <email>", ReportRedactor.Redact(text, user, pc, ""));
    }

    [Fact]
    public void Longer_overlapping_identities_are_matched_first()
    {
        Assert.Equal("<pc> <user>", ReportRedactor.Redact("KEN-PC Ken", "ken", "ken-pc", ""));
    }

    [Fact]
    public void Injected_profile_is_redacted_even_without_a_drive_letter()
    {
        Assert.Equal("Profile <path>; file \"<path>\"",
            ReportRedactor.Redact("Profile /home/PRIVATE USER; file \"/home/private user/เอกสาร ลับ/ใหม่.pdf\"",
                "different-user", "different-pc", "/home/private user/"));
    }

    [Theory]
    [InlineData("Mail: first.last+tag@example.com", "Mail: <email>")]
    [InlineData("Contact KEN@example.co.th, or other@host.test.", "Contact <email>, or <email>.")]
    [InlineData("ที่อยู่: คนไทย@ตัวอย่าง.ไทย", "ที่อยู่: <email>")]
    public void Email_addresses_are_redacted(string text, string expected)
    {
        Assert.Equal(expected, Redact(text));
    }

    [Fact]
    public void Default_overload_uses_the_real_environment()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"\"{profile}\"\nZ{Environment.UserName.ToUpperInvariant()}Z\nZ{Environment.MachineName.ToLowerInvariant()}Z";
        Assert.Equal(ReportRedactor.Redact(text, Environment.UserName, Environment.MachineName, profile),
            ReportRedactor.Redact(text));
        Assert.StartsWith("\"<path>\"", ReportRedactor.Redact(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("The document could not be opened. Try again.")]
    [InlineData("เอกสารมี 5 หน้า กรุณาลองใหม่")]
    [InlineData("YRK-APP-001 HRESULT: 0x80004005")]
    public void Plain_text_is_untouched(string text)
    {
        Assert.Equal(text, Redact(text));
    }

    [Fact]
    public void Long_unbroken_log_text_does_not_require_backtracking()
    {
        var text = new string('x', 100_000);
        Assert.Equal(text, Redact(text));
    }
}
