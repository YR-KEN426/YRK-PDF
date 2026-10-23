using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SecurityTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-security-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task An_open_password_is_required_and_the_owner_gets_everything()
    {
        var path = Write("protected.pdf", PdfSecurity.Protect(
            await File.ReadAllBytesAsync(SamplePath),
            new PdfProtection("เปิด123", "owner-secret", PdfPermissions.Copy | PdfPermissions.Accessibility)));

        var noPassword = await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(path));
        Assert.False(noPassword.PasswordWasWrong);
        var wrong = await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(path, "nope"));
        Assert.True(wrong.PasswordWasWrong);

        await using (var asUser = await _engine.OpenAsync(path, "เปิด123"))
        {
            Assert.True(asUser.IsEncrypted);
            Assert.True(asUser.Permissions.HasFlag(PdfPermissions.Copy));
            Assert.False(asUser.Permissions.HasFlag(PdfPermissions.Print));
            Assert.False(asUser.Permissions.HasFlag(PdfPermissions.Modify));
            Assert.Contains("invoice", await asUser.GetPageTextAsync(0)); // Strings and streams decrypt.
            Assert.Equal(3, asUser.PageCount);
        }

        await using var asOwner = await _engine.OpenAsync(path, "owner-secret");
        Assert.Equal(PdfPermissions.All, asOwner.Permissions);
    }

    [Fact]
    public async Task Restrictions_alone_open_without_a_password()
    {
        var path = Write("restricted.pdf", PdfSecurity.Protect(
            await File.ReadAllBytesAsync(SamplePath),
            new PdfProtection(null, "owner", PdfPermissions.Print | PdfPermissions.PrintHighQuality)));

        await using var document = await _engine.OpenAsync(path);
        Assert.True(document.IsEncrypted);
        Assert.True(document.Permissions.HasFlag(PdfPermissions.Print));
        Assert.False(document.Permissions.HasFlag(PdfPermissions.Copy));
        Assert.DoesNotContain("invoice", Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path))); // Nothing readable in the file.
    }

    [Fact]
    public async Task Protection_can_be_removed_with_the_password()
    {
        var path = Write("protected.pdf", PdfSecurity.Protect(
            await File.ReadAllBytesAsync(SamplePath),
            new PdfProtection("abc", null, PdfPermissions.All)));

        byte[] plain;
        await using (var document = await _engine.OpenAsync(path, "abc"))
        {
            plain = await document.SaveDecryptedAsync();
        }

        Assert.False(PdfFiles.IsEncrypted(plain));
        await using var reopened = await _engine.OpenAsync(Write("plain.pdf", plain));
        Assert.False(reopened.IsEncrypted);
        Assert.Contains("invoice", await reopened.GetPageTextAsync(0));
    }

    [Fact]
    public void An_already_encrypted_file_is_not_encrypted_twice()
    {
        var once = PdfSecurity.Protect(File.ReadAllBytes(SamplePath), new PdfProtection("a", "b", PdfPermissions.All));
        Assert.Throws<PdfException>(() => PdfSecurity.Protect(once, new PdfProtection("c", "d", PdfPermissions.All)));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
