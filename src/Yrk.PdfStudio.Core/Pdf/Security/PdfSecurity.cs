using System.Security.Cryptography;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Security;

/// <summary>What a reader may do without the owner password (ISO 32000-2, table 22).</summary>
[Flags]
public enum PdfPermissions : uint
{
    None = 0,
    Print = 1 << 2,
    Modify = 1 << 3,
    Copy = 1 << 4,
    Annotate = 1 << 5,
    FillForms = 1 << 8,
    Accessibility = 1 << 9,
    Assemble = 1 << 10,
    PrintHighQuality = 1 << 11,
    All = Print | Modify | Copy | Annotate | FillForms | Accessibility | Assemble | PrintHighQuality,
}

/// <param name="UserPassword">Needed to open the file; null or empty opens without a password.</param>
/// <param name="OwnerPassword">Needed to change the restrictions; null makes a random one (nobody can lift them).</param>
/// <param name="Allowed">What anyone who can open the file may do.</param>
public sealed record PdfProtection(string? UserPassword, string? OwnerPassword, PdfPermissions Allowed);

/// <summary>
/// Password protection with AES-256 (standard security handler, revision 6, as in PDF 2.0 and Acrobat X
/// and later). The file is rewritten as one clean revision; every string and stream is encrypted with
/// a random file key, which the passwords unlock.
/// </summary>
public static class PdfSecurity
{
    private const int KeyLength = 32;

    /// <summary>Encrypts an unencrypted PDF.</summary>
    /// <exception cref="PdfException">The PDF is already encrypted or cannot be read.</exception>
    public static byte[] Protect(byte[] pdf, PdfProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
        {
            throw new PdfException("Remove the existing protection first.");
        }

        var fileKey = RandomNumberGenerator.GetBytes(KeyLength);
        var user = Password(protection.UserPassword);
        var owner = string.IsNullOrEmpty(protection.OwnerPassword)
            ? RandomNumberGenerator.GetBytes(32)
            : Password(protection.OwnerPassword);

        // U and UE: the user password checks itself and unlocks the file key.
        var userValidationSalt = RandomNumberGenerator.GetBytes(8);
        var userKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] u = [.. Hash(user, userValidationSalt, []), .. userValidationSalt, .. userKeySalt];
        var ue = AesNoPadding(Hash(user, userKeySalt, []), fileKey);

        // O and OE: the same for the owner password, bound to U.
        var ownerValidationSalt = RandomNumberGenerator.GetBytes(8);
        var ownerKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] o = [.. Hash(owner, ownerValidationSalt, u), .. ownerValidationSalt, .. ownerKeySalt];
        var oe = AesNoPadding(Hash(owner, ownerKeySalt, u), fileKey);

        // Unused bits 7, 8 and 13–32 must be set; bits 1–2 must be clear.
        var p = unchecked((int)(0xFFFFF0C0u | (uint)protection.Allowed));
        var perms = new byte[16];
        BitConverter.GetBytes(p).CopyTo(perms, 0);
        perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
        perms[8] = (byte)'T'; // Metadata is encrypted too.
        perms[9] = (byte)'a';
        perms[10] = (byte)'d';
        perms[11] = (byte)'b';
        RandomNumberGenerator.Fill(perms.AsSpan(12));
        using (var aes = Aes.Create())
        {
            aes.Key = fileKey;
            perms = aes.EncryptEcb(perms, PaddingMode.None);
        }

        var cryptFilter = new CosDictionary()
            .Set("Type", new CosName("CryptFilter"))
            .Set("CFM", new CosName("AESV3"))
            .Set("AuthEvent", new CosName("DocOpen"))
            .Set("Length", new CosInteger(32));
        var encrypt = new CosDictionary()
            .Set("Filter", new CosName("Standard"))
            .Set("V", new CosInteger(5))
            .Set("R", new CosInteger(6))
            .Set("Length", new CosInteger(256))
            .Set("CF", new CosDictionary().Set("StdCF", cryptFilter))
            .Set("StmF", new CosName("StdCF"))
            .Set("StrF", new CosName("StdCF"))
            .Set("O", new CosString(o, isHex: true))
            .Set("U", new CosString(u, isHex: true))
            .Set("OE", new CosString(oe, isHex: true))
            .Set("UE", new CosString(ue, isHex: true))
            .Set("Perms", new CosString(perms, isHex: true))
            .Set("P", new CosInteger(p))
            .Set("EncryptMetadata", new CosBoolean(true));
        return CosCompactWriter.Write(document, new CosEncryption(fileKey, encrypt));
    }

    /// <summary>A password as UTF-8, at most 127 bytes (§7.6.4.3.3; SASLprep is not applied).</summary>
    private static byte[] Password(string? password)
    {
        var bytes = Encoding.UTF8.GetBytes((password ?? string.Empty).Normalize(NormalizationForm.FormKC));
        return bytes.Length > 127 ? bytes[..127] : bytes;
    }

    /// <summary>Algorithm 2.B: the hardened SHA-2 hash of revision 6.</summary>
    internal static byte[] Hash(byte[] password, byte[] salt, byte[] userKey)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userKey]);
        using var aes = Aes.Create();
        for (var round = 0; ; round++)
        {
            var k1 = new byte[64 * (password.Length + k.Length + userKey.Length)];
            var block = k1.AsSpan();
            for (var i = 0; i < 64; i++)
            {
                password.CopyTo(block);
                k.AsSpan().CopyTo(block[password.Length..]);
                userKey.CopyTo(block[(password.Length + k.Length)..]);
                block = block[(password.Length + k.Length + userKey.Length)..];
            }

            aes.Key = k[..16];
            var e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            var sum = 0;
            for (var i = 0; i < 16; i++)
            {
                sum += e[i];
            }

            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            if (round >= 63 && e[^1] <= round + 1 - 32)
            {
                return k[..32];
            }
        }
    }

    private static byte[] AesNoPadding(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key[..32];
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }
}

/// <summary>Encrypts strings and streams for the compact writer (AESV3: random IV in front, PKCS#7 padding).</summary>
internal sealed class CosEncryption(byte[] fileKey, CosDictionary encryptDictionary)
{
    public CosDictionary Dictionary { get; } = encryptDictionary;

    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = fileKey;
        var iv = RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
    }
}
