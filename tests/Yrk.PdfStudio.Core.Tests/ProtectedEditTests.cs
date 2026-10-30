using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Redaction;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Signatures;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Signing and redacting password-protected files: the result keeps the same passwords and restrictions.</summary>
public sealed class ProtectedEditTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private static readonly PdfVerifyOptions Offline = new(CheckRevocation: false);
    private static readonly Lazy<X509Certificate2> Signer = new(() =>
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Protected Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    });

    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-protected-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Signs_a_file_with_an_open_password_and_keeps_both_passwords()
    {
        var input = PdfSecurity.Protect(File.ReadAllBytes(SamplePath), new PdfProtection("เปิด123", "owner", PdfPermissions.FillForms | PdfPermissions.Print));

        var signed = await PdfSigner.SignAsync(input, new PdfSignRequest(Signer.Value, new PdfSignatureAppearance(0, new NormalizedRect(0.1, 0.8, 0.3, 0.1), Solid()), Reason: "อนุมัติ", Password: "เปิด123"));

        Assert.True(signed.AsSpan(0, input.Length).SequenceEqual(input)); // An incremental update.
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(signed, Offline)).IsIntact);
        var path = Write("signed.pdf", signed);
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(path));
        await using (var asUser = await _engine.OpenAsync(path, "เปิด123"))
        {
            Assert.Contains("invoice", await asUser.GetPageTextAsync(0));
            Assert.False(asUser.Permissions.HasFlag(PdfPermissions.Modify));
        }

        await using var asOwner = await _engine.OpenAsync(path, "owner");
        Assert.Equal(PdfPermissions.All, asOwner.Permissions);
    }

    [Fact]
    public async Task Restrictions_need_the_owner_password()
    {
        var input = PdfSecurity.Protect(File.ReadAllBytes(SamplePath), new PdfProtection(null, "owner", PdfPermissions.Print));

        await Assert.ThrowsAsync<PdfRestrictedException>(() => PdfSigner.SignAsync(input, new PdfSignRequest(Signer.Value)));
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => PdfSigner.SignAsync(input, new PdfSignRequest(Signer.Value, Password: "wrong")));
        Assert.Equal(PdfUnlockResult.Restricted, PdfSecurity.CheckPassword(input, null, PdfPermissions.Modify));
        Assert.Equal(PdfUnlockResult.Unlocked, PdfSecurity.CheckPassword(input, "owner", PdfPermissions.Modify));

        var signed = await PdfSigner.SignAsync(input, new PdfSignRequest(Signer.Value, Password: "owner"));
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(signed, Offline)).IsIntact);
        await using var opened = await _engine.OpenAsync(Write("signed.pdf", signed));
        Assert.False(opened.Permissions.HasFlag(PdfPermissions.Copy)); // Still restricted.
    }

    [Fact]
    public async Task Redacts_a_protected_file_and_protects_the_result_the_same_way()
    {
        var input = PdfSecurity.Protect(File.ReadAllBytes(SamplePath), new PdfProtection("abc", "own", PdfPermissions.Copy));
        NormalizedRect area;
        await using (var document = await _engine.OpenAsync(Write("in.pdf", input), "abc"))
        {
            area = (await document.FindTextAsync(0, "sample document", PdfSearchOptions.None))[0].Rects[0];
        }

        await Assert.ThrowsAsync<PdfRestrictedException>(() => PdfRedactor.RedactAsync(input, [new PdfRedactionArea(0, area)], _engine, password: "abc"));
        var result = await PdfRedactor.RedactAsync(input, [new PdfRedactionArea(0, area)], _engine, password: "own");

        var path = Write("redacted.pdf", result.Output);
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(path));
        await using (var asUser = await _engine.OpenAsync(path, "abc"))
        {
            var text = await asUser.GetPageTextAsync(0);
            Assert.DoesNotContain("sample document", text, StringComparison.Ordinal);
            Assert.Contains("invoice", text, StringComparison.Ordinal);
            Assert.True(asUser.Permissions.HasFlag(PdfPermissions.Copy));
            Assert.False(asUser.Permissions.HasFlag(PdfPermissions.Print));
        }

        Assert.DoesNotContain("invoice", Encoding.Latin1.GetString(result.Output));
    }

    [Fact]
    public async Task Edits_the_text_of_a_protected_file_and_it_stays_protected()
    {
        var input = PdfSecurity.Protect(File.ReadAllBytes(SamplePath), new PdfProtection("abc", "own", PdfPermissions.All));
        NormalizedRect area;
        await using (var document = await _engine.OpenAsync(Write("in.pdf", input), "abc"))
        {
            area = (await document.FindTextAsync(0, "sample document", PdfSearchOptions.None))[0].Rects[0];
        }

        var edited = PdfTextEditor.Apply(input, [new PdfTextEdit(0, area, null)], "abc");

        Assert.True(edited.AsSpan(0, input.Length).SequenceEqual(input)); // An incremental update.
        var path = Write("edited.pdf", edited);
        await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => _engine.OpenAsync(path));
        await using var opened = await _engine.OpenAsync(path, "abc");
        var text = await opened.GetPageTextAsync(0);
        Assert.DoesNotContain("sample document", text, StringComparison.Ordinal);
        Assert.Contains("invoice", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, "V2")]    // RC4 128-bit (Acrobat 5–6).
    [InlineData(4, "AESV2")] // AES 128-bit (Acrobat 7–8).
    public async Task Signs_and_redacts_files_with_older_encryption(int revision, string method)
    {
        var input = Legacy(File.ReadAllBytes(SamplePath), revision, method, "user", "owner");
        await using (var check = await _engine.OpenAsync(Write("legacy.pdf", input), "user"))
        {
            Assert.Contains("invoice", await check.GetPageTextAsync(0)); // The test file itself is right.
        }

        var signed = await PdfSigner.SignAsync(input, new PdfSignRequest(Signer.Value, new PdfSignatureAppearance(0, new NormalizedRect(0.1, 0.8, 0.3, 0.1), Solid()), Password: "owner"));
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(signed, Offline)).IsIntact);
        await using (var opened = await _engine.OpenAsync(Write("signed.pdf", signed), "user"))
        {
            Assert.Contains("invoice", await opened.GetPageTextAsync(0));
        }

        NormalizedRect area;
        await using (var document = await _engine.OpenAsync(Write("in.pdf", input), "user"))
        {
            area = (await document.FindTextAsync(0, "sample document", PdfSearchOptions.None))[0].Rects[0];
        }

        var redacted = await PdfRedactor.RedactAsync(input, [new PdfRedactionArea(0, area)], _engine, password: "owner");
        await using var result = await _engine.OpenAsync(Write("redacted.pdf", redacted.Output), "user");
        var text = await result.GetPageTextAsync(0);
        Assert.DoesNotContain("sample document", text, StringComparison.Ordinal);
        Assert.Contains("invoice", text, StringComparison.Ordinal);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder.FullName, $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static PdfImage Solid() => new(4, 4, Enumerable.Repeat((byte)0x80, 4 * 4 * 4).ToArray());

    private static readonly byte[] Padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    /// <summary>Encrypts with the standard handler, revision 3 or 4 (algorithms 2, 3 and 5), all permissions but copying.</summary>
    private static byte[] Legacy(byte[] pdf, int revision, string method, string user, string owner)
    {
        var p = -1 & ~(1 << 4);
        var id = RandomNumberGenerator.GetBytes(16);
        static byte[] Pad(string password) => [.. Encoding.Latin1.GetBytes(password).Take(32), .. Padding.Take(32 - Math.Min(32, password.Length))];

        var ownerKey = MD5.HashData(Pad(owner));
        for (var i = 0; i < 50; i++)
        {
            ownerKey = MD5.HashData(ownerKey);
        }

        var o = CosCrypt.Rc4(ownerKey, Pad(user));
        for (var i = 1; i <= 19; i++)
        {
            o = CosCrypt.Rc4(ownerKey.Select(b => (byte)(b ^ i)).ToArray(), o);
        }

        var key = MD5.HashData([.. Pad(user), .. o, (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24), .. id]);
        for (var i = 0; i < 50; i++)
        {
            key = MD5.HashData(key);
        }

        var u = CosCrypt.Rc4(key, MD5.HashData([.. Padding, .. id]));
        for (var i = 1; i <= 19; i++)
        {
            u = CosCrypt.Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), u);
        }

        var encrypt = new CosDictionary()
            .Set("Filter", new CosName("Standard"))
            .Set("V", new CosInteger(revision == 4 ? 4 : 2))
            .Set("R", new CosInteger(revision))
            .Set("Length", new CosInteger(128))
            .Set("O", new CosString(o, isHex: true))
            .Set("U", new CosString([.. u, .. new byte[16]], isHex: true))
            .Set("P", new CosInteger(p));
        if (revision == 4)
        {
            encrypt
                .Set("CF", new CosDictionary().Set("StdCF", new CosDictionary().Set("CFM", new CosName(method)).Set("Length", new CosInteger(16))))
                .Set("StmF", new CosName("StdCF"))
                .Set("StrF", new CosName("StdCF"));
        }

        var fileId = new CosString(id, isHex: true);
        var trailer = new CosDictionary().Set("ID", new CosArray([fileId, fileId]));
        var crypt = CosCrypt.Open(encrypt, trailer, user)!;
        var document = CosDocument.Load(pdf);
        document.Trailer["ID"] = new CosArray([fileId, fileId]);
        return CosCompactWriter.Write(document, crypt);
    }
}
