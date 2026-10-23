using System.IO.Compression;
using System.Security.Cryptography;
using Yrk.PdfStudio.Core.Security;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class UpdateSignatureTests
{
    private const string Id = "YRK.EasyPdf";
    private const string Version = "1.0.2";
    private const string File = "YRK.EasyPdf-1.0.2-full.nupkg";
    private static readonly string Sha = UpdateSignature.Sha256Hex(new MemoryStream([1, 2, 3, 4]));

    [Fact]
    public void A_signed_update_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = UpdateSignature.Sign(key, Id, Version, File, Sha);

        Assert.True(UpdateSignature.Verify(key.ExportSubjectPublicKeyInfoPem(), Id, Version, File, Sha, signature));
        Assert.Equal("YRK.EasyPdf-1.0.2.sig", UpdateSignature.FileName(Id, Version));
    }

    [Fact]
    public void A_changed_package_version_or_name_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var signature = UpdateSignature.Sign(key, Id, Version, File, Sha);
        var otherSha = UpdateSignature.Sha256Hex(new MemoryStream([1, 2, 3, 5]));

        Assert.False(UpdateSignature.Verify(publicKey, Id, Version, File, otherSha, signature));        // Different bytes.
        Assert.False(UpdateSignature.Verify(publicKey, Id, "1.0.3", File, Sha, signature));             // Replayed as another version.
        Assert.False(UpdateSignature.Verify(publicKey, Id, Version, "Other-1.0.2-full.nupkg", Sha, signature));
        Assert.False(UpdateSignature.Verify(publicKey, "Other.App", Version, File, Sha, signature));
    }

    [Fact]
    public void Another_key_or_a_broken_signature_is_rejected()
    {
        using var ours = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var theirs = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = UpdateSignature.Sign(theirs, Id, Version, File, Sha);
        var publicKey = ours.ExportSubjectPublicKeyInfoPem();

        Assert.False(UpdateSignature.Verify(publicKey, Id, Version, File, Sha, forged));
        Assert.False(UpdateSignature.Verify(publicKey, Id, Version, File, Sha, "not base64 !"));
        Assert.False(UpdateSignature.Verify(publicKey, Id, Version, File, Sha, string.Empty));
        Assert.False(UpdateSignature.Verify("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----", Id, Version, File, Sha, forged));
    }

    [Fact]
    public void Content_digest_ignores_how_the_zip_was_written_but_not_what_is_in_it()
    {
        // Like a package the updater rebuilt from a delta: same files, other order, compression and metadata.
        var folder = Directory.CreateTempSubdirectory("yrk-sig-");
        try
        {
            var original = MakePackage(folder, "a.nupkg", [("lib/app/A.dll", "aaa"), ("lib/app/B.dll", "bbb"), ("x.psmdcp", "1")], CompressionLevel.Optimal);
            var rebuilt = MakePackage(folder, "b.nupkg", [("lib/app/B.dll", "bbb"), ("lib/app/A.dll", "aaa"), ("y.psmdcp", "2")], CompressionLevel.NoCompression);
            var changed = MakePackage(folder, "c.nupkg", [("lib/app/A.dll", "aaa"), ("lib/app/B.dll", "bbX")], CompressionLevel.Optimal);
            var added = MakePackage(folder, "d.nupkg", [("lib/app/A.dll", "aaa"), ("lib/app/B.dll", "bbb"), ("lib/app/C.dll", "c")], CompressionLevel.Optimal);

            Assert.NotEqual(UpdateSignature.Sha256Hex(original), UpdateSignature.Sha256Hex(rebuilt));
            Assert.Equal(UpdateSignature.ContentDigest(original), UpdateSignature.ContentDigest(rebuilt));
            Assert.NotEqual(UpdateSignature.ContentDigest(original), UpdateSignature.ContentDigest(changed));
            Assert.NotEqual(UpdateSignature.ContentDigest(original), UpdateSignature.ContentDigest(added));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_content_signature_is_accepted_only_for_its_files_version_and_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var theirs = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var digest = Sha;
        var signature = UpdateSignature.SignContent(key, Id, Version, digest);

        Assert.True(UpdateSignature.VerifyContent(publicKey, Id, Version, digest, signature));
        Assert.False(UpdateSignature.VerifyContent(publicKey, Id, "1.0.3", digest, signature));
        Assert.False(UpdateSignature.VerifyContent(publicKey, Id, Version, UpdateSignature.Sha256Hex(new MemoryStream([9])), signature));
        Assert.False(UpdateSignature.VerifyContent(publicKey, Id, Version, digest, UpdateSignature.SignContent(theirs, Id, Version, digest)));

        // A file signature can't stand in for a content signature, or the other way round.
        Assert.False(UpdateSignature.Verify(publicKey, Id, Version, File, digest, signature));
        Assert.False(UpdateSignature.VerifyContent(publicKey, Id, Version, digest, UpdateSignature.Sign(key, Id, Version, File, digest)));
        Assert.Equal("YRK.EasyPdf-1.0.2.content.sig", UpdateSignature.ContentFileName(Id, Version));
    }

    private static string MakePackage(DirectoryInfo folder, string name, (string Path, string Text)[] entries, CompressionLevel level)
    {
        var path = Path.Combine(folder.FullName, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryPath, level).Open());
            writer.Write(text);
        }

        return path;
    }

    [Fact]
    public void Fingerprint_is_stable_and_short()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        Assert.Equal(UpdateSignature.Fingerprint(pem), UpdateSignature.Fingerprint(pem));
        Assert.Matches("^([0-9A-F]{4} ){7}[0-9A-F]{4}$", UpdateSignature.Fingerprint(pem));
    }
}
