using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Yrk.PdfStudio.Core.Security;

/// <summary>
/// Signs and checks update packages with YRK's own key (ECDSA P-256, SHA-256), independent of where the
/// packages are hosted: an update is installed only when its signature matches the public key built into
/// the app, so even someone who takes over the download site cannot ship a fake update.
/// </summary>
/// <remarks>
/// Two kinds of signature are published with each release. The file signature (v1) covers the bytes of the
/// full package; it matches only when the app downloaded that very file. The content signature (v2) covers
/// the program files inside the package, so it also matches a full package that the updater rebuilt from a
/// small delta update, whose zip bytes differ even though every file in it is the same.
/// </remarks>
public static class UpdateSignature
{
    /// <summary>The file signature (v1) published next to each release: "YRK.EasyPdf-1.0.2.sig".</summary>
    public static string FileName(string packageId, string version) => $"{packageId}-{version}.sig";

    /// <summary>The content signature (v2): "YRK.EasyPdf-1.0.5.content.sig".</summary>
    public static string ContentFileName(string packageId, string version) => $"{packageId}-{version}.content.sig";

    /// <summary>What v1 signs: the package, its version and file name, and the SHA-256 of its bytes.</summary>
    public static byte[] Statement(string packageId, string version, string fileName, string sha256Hex) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "YRK update v1",
            packageId,
            version,
            fileName,
            sha256Hex.ToLowerInvariant()));

    /// <summary>What v2 signs: the package, its version and <see cref="ContentDigest"/>.</summary>
    public static byte[] ContentStatement(string packageId, string version, string contentDigestHex) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "YRK update v2",
            packageId,
            version,
            contentDigestHex.ToLowerInvariant()));

    public static string Sha256Hex(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256Hex(stream);
    }

    /// <summary>
    /// SHA-256 over the program files of a package (every entry under "lib/", in name order, as
    /// "name\nsha256\n"). Zip details such as compression, order and timestamps don't change it.
    /// </summary>
    public static string ContentDigest(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var files = zip.Entries
            .Where(entry => entry.FullName.Replace('\\', '/').StartsWith("lib/", StringComparison.Ordinal) && !entry.FullName.EndsWith('/'))
            .OrderBy(entry => entry.FullName.Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidDataException("The package has no program files.");
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in files)
        {
            string fileHash;
            using (var content = entry.Open())
            {
                fileHash = Sha256Hex(content);
            }

            digest.AppendData(Encoding.UTF8.GetBytes(entry.FullName.Replace('\\', '/') + "\n" + fileHash + "\n"));
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    /// <summary>Returns the v1 signature file's text (base64 of the IEEE P1363 signature).</summary>
    public static string Sign(ECDsa privateKey, string packageId, string version, string fileName, string sha256Hex) =>
        SignStatement(privateKey, Statement(packageId, version, fileName, sha256Hex));

    /// <summary>Returns the v2 signature file's text.</summary>
    public static string SignContent(ECDsa privateKey, string packageId, string version, string contentDigestHex) =>
        SignStatement(privateKey, ContentStatement(packageId, version, contentDigestHex));

    /// <summary>True when the v1 <paramref name="signatureText"/> was made by the key matching <paramref name="publicKeyPem"/>.</summary>
    public static bool Verify(string publicKeyPem, string packageId, string version, string fileName, string sha256Hex, string signatureText) =>
        VerifyStatement(publicKeyPem, Statement(packageId, version, fileName, sha256Hex), signatureText);

    /// <summary>True when the v2 <paramref name="signatureText"/> was made by the key matching <paramref name="publicKeyPem"/>.</summary>
    public static bool VerifyContent(string publicKeyPem, string packageId, string version, string contentDigestHex, string signatureText) =>
        VerifyStatement(publicKeyPem, ContentStatement(packageId, version, contentDigestHex), signatureText);

    private static string SignStatement(ECDsa privateKey, byte[] statement) =>
        Convert.ToBase64String(privateKey.SignData(statement, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static bool VerifyStatement(string publicKeyPem, byte[] statement, string signatureText)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(publicKeyPem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }

        if (key.KeySize != 256)
        {
            return false; // Only the P-256 key this app was built with.
        }

        return key.VerifyData(statement, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>A short fingerprint of a public key, to compare by eye ("3F2A 9C…").</summary>
    public static string Fingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var hash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        return string.Join(' ', Enumerable.Range(0, 8).Select(i => hash[i * 2].ToString("X2", CultureInfo.InvariantCulture) + hash[(i * 2) + 1].ToString("X2", CultureInfo.InvariantCulture)));
    }
}
