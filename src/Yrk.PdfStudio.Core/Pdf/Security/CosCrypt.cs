using System.Security.Cryptography;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Security;

/// <summary>
/// The standard security handler of an encrypted file, unlocked with a password (ISO 32000-2, §7.6.4):
/// RC4 and AES-128 (revisions 2–4) and AES-256 (revisions 5–6). Objects are decrypted as they are read and
/// encrypted again with the same key when written, so the file keeps its passwords and restrictions.
/// </summary>
internal sealed class CosCrypt : CosEncryption
{
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private readonly byte[] _key;
    private readonly Method _strings;
    private readonly Method _streams;
    private readonly bool _encryptMetadata;

    private CosCrypt(byte[] key, CosDictionary dictionary, Method strings, Method streams, bool encryptMetadata, bool isOwner, int p)
        : base(key, dictionary)
    {
        _key = key;
        _strings = strings;
        _streams = streams;
        _encryptMetadata = encryptMetadata;
        IsOwner = isOwner;
        Permissions = isOwner ? PdfPermissions.All : (PdfPermissions)((uint)p & (uint)PdfPermissions.All);
    }

    private enum Method
    {
        None,
        Rc4,
        Aes128,
        Aes256,
    }

    /// <summary>Unlocked with the owner password: no restrictions apply.</summary>
    public bool IsOwner { get; }

    /// <summary>What may be done with the password given.</summary>
    public PdfPermissions Permissions { get; }

    public override bool DeclaresAes256 => _strings == Method.Aes256 || _streams == Method.Aes256;

    /// <summary>Unlocks the file key with <paramref name="password"/> (the owner password is tried first).</summary>
    /// <returns>Null when the password fits neither the owner nor the user password.</returns>
    /// <exception cref="PdfException">The file uses a security handler other than the standard one.</exception>
    public static CosCrypt? Open(CosDictionary encrypt, CosDictionary trailer, string? password)
    {
        if ((encrypt["Filter"] as CosName)?.Value != "Standard")
        {
            throw new PdfException("This PDF is protected with a certificate or a plug-in, which is not supported.");
        }

        var v = (int)((encrypt["V"] as CosInteger)?.Value ?? 0);
        var r = (int)((encrypt["R"] as CosInteger)?.Value ?? 2);
        var p = (int)((encrypt["P"] as CosInteger)?.Value ?? -1);
        var o = (encrypt["O"] as CosString)?.Bytes ?? [];
        var u = (encrypt["U"] as CosString)?.Bytes ?? [];
        var encryptMetadata = (encrypt["EncryptMetadata"] as CosBoolean)?.Value ?? true;
        var (strings, streams) = Methods(encrypt, v);
        password ??= string.Empty;

        if (r >= 5)
        {
            var utf8 = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
            if (utf8.Length > 127)
            {
                utf8 = utf8[..127];
            }

            if (o.Length < 48 || u.Length < 48)
            {
                return null;
            }

            var u48 = u[..48];
            if (Hash(r, utf8, o[32..40], u48).AsSpan().SequenceEqual(o.AsSpan(0, 32))
                && (encrypt["OE"] as CosString)?.Bytes is { Length: 32 } oe)
            {
                return new CosCrypt(Unwrap(Hash(r, utf8, o[40..48], u48), oe), encrypt, strings, streams, encryptMetadata, true, p);
            }

            if (Hash(r, utf8, u[32..40], []).AsSpan().SequenceEqual(u.AsSpan(0, 32))
                && (encrypt["UE"] as CosString)?.Bytes is { Length: 32 } ue)
            {
                return new CosCrypt(Unwrap(Hash(r, utf8, u[40..48], []), ue), encrypt, strings, streams, encryptMetadata, false, p);
            }

            return null;
        }

        var length = r == 2 ? 5 : v == 4 ? 16 : (int)Math.Clamp(((encrypt["Length"] as CosInteger)?.Value ?? 40) / 8, 5, 16);
        var id = trailer["ID"] is CosArray { Count: > 0 } ids && ids[0] is CosString first ? first.Bytes : [];
        foreach (var encoding in new[] { Encoding.Latin1, Encoding.UTF8 })
        {
            var bytes = encoding.GetBytes(password);

            // Owner password (algorithm 7): it decrypts O into the padded user password.
            var ownerKey = MD5.HashData(Pad(bytes));
            if (r >= 3)
            {
                for (var i = 0; i < 50; i++)
                {
                    ownerKey = MD5.HashData(ownerKey);
                }
            }

            ownerKey = ownerKey[..length];
            var user = o.Length >= 32 ? o[..32] : [];
            if (r == 2)
            {
                user = Rc4(ownerKey, user);
            }
            else
            {
                for (var i = 19; i >= 0; i--)
                {
                    user = Rc4(ownerKey.Select(b => (byte)(b ^ i)).ToArray(), user);
                }
            }

            var key = FileKey(user, o, p, id, r, length, encryptMetadata);
            if (UserMatches(key, u, id, r))
            {
                return new CosCrypt(key, encrypt, strings, streams, encryptMetadata, true, p);
            }

            key = FileKey(Pad(bytes), o, p, id, r, length, encryptMetadata);
            if (UserMatches(key, u, id, r))
            {
                return new CosCrypt(key, encrypt, strings, streams, encryptMetadata, false, p);
            }
        }

        return null;
    }

    /// <summary>Decrypts the strings and streams inside object <paramref name="id"/> as read from the file.</summary>
    public CosObject Decrypt(CosObject value, CosObjectId id) => Transform(value, id, encrypt: false);

    public override CosObject EncryptObject(CosObject value, CosObjectId id) => Transform(value, id, encrypt: true);

    private CosObject Transform(CosObject value, CosObjectId id, bool encrypt)
    {
        switch (value)
        {
            case CosString s:
                return new CosString(Apply(_strings, id, s.Bytes, encrypt), s.IsHex || encrypt);
            case CosStream stream:
                var type = (stream.Dictionary["Type"] as CosName)?.Value;
                var dictionary = (CosDictionary)Transform(stream.Dictionary, id, encrypt);
                if (type == "XRef" || (type == "Metadata" && !_encryptMetadata))
                {
                    return new CosStream(dictionary, stream.Data);
                }

                return new CosStream(dictionary, Apply(_streams, id, stream.Data.Span, encrypt));
            case CosArray array:
                return new CosArray(array.Items.Select(i => Transform(i, id, encrypt)));
            case CosDictionary d:
                // The /Contents of a signature is never encrypted (§7.6.2).
                var isSignature = (d["Type"] as CosName)?.Value is "Sig" or "DocTimeStamp" || d["ByteRange"] is not null;
                var copy = new CosDictionary();
                foreach (var (key, entry) in d.Entries)
                {
                    copy[key] = isSignature && key == "Contents" ? entry : Transform(entry, id, encrypt);
                }

                return copy;
            default:
                return value;
        }
    }

    private byte[] Apply(Method method, CosObjectId id, ReadOnlySpan<byte> data, bool encrypt)
    {
        switch (method)
        {
            case Method.None:
                return data.ToArray();
            case Method.Rc4:
                return Rc4(ObjectKey(id, aes: false), data);
            default:
                var key = method == Method.Aes256 ? _key : ObjectKey(id, aes: true);
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    if (encrypt)
                    {
                        var iv = RandomNumberGenerator.GetBytes(16);
                        return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
                    }

                    if (data.Length < 32 || data.Length % 16 != 0)
                    {
                        return data.Length < 16 ? [] : data.ToArray();
                    }

                    try
                    {
                        return aes.DecryptCbc(data[16..], data[..16], PaddingMode.PKCS7);
                    }
                    catch (CryptographicException)
                    {
                        return aes.DecryptCbc(data[16..], data[..16], PaddingMode.None);
                    }
                }
        }
    }

    /// <summary>Algorithm 1: the key for one object (RC4 and AES-128).</summary>
    private byte[] ObjectKey(CosObjectId id, bool aes)
    {
        byte[] input =
        [
            .. _key,
            (byte)id.Number, (byte)(id.Number >> 8), (byte)(id.Number >> 16),
            (byte)id.Generation, (byte)(id.Generation >> 8),
            .. aes ? "sAlT"u8.ToArray() : [],
        ];
        return MD5.HashData(input)[..Math.Min(_key.Length + 5, 16)];
    }

    private static (Method Strings, Method Streams) Methods(CosDictionary encrypt, int v)
    {
        if (v < 4)
        {
            return (Method.Rc4, Method.Rc4);
        }

        Method Named(string key)
        {
            var name = (encrypt[key] as CosName)?.Value ?? "Identity";
            if (name == "Identity")
            {
                return Method.None;
            }

            var filter = (encrypt["CF"] as CosDictionary)?[name] as CosDictionary;
            return (filter?["CFM"] as CosName)?.Value switch
            {
                "V2" => Method.Rc4,
                "AESV2" => Method.Aes128,
                "AESV3" => Method.Aes256,
                "None" => Method.None,
                _ => throw new PdfException("This PDF uses an encryption method that is not supported."),
            };
        }

        return (Named("StrF"), Named("StmF"));
    }

    /// <summary>Algorithm 2: the file key from a padded user password.</summary>
    private static byte[] FileKey(byte[] paddedUser, byte[] o, int p, byte[] id, int r, int length, bool encryptMetadata)
    {
        byte[] input =
        [
            .. paddedUser[..32],
            .. o.Length >= 32 ? o[..32] : o,
            (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24),
            .. id,
            .. r >= 4 && !encryptMetadata ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF } : [],
        ];
        var hash = MD5.HashData(input);
        if (r >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash.AsSpan(0, length));
            }
        }

        return hash[..length];
    }

    /// <summary>Algorithms 4 and 5: whether the file key reproduces /U.</summary>
    private static bool UserMatches(byte[] key, byte[] u, byte[] id, int r)
    {
        if (r == 2)
        {
            return u.Length >= 32 && Rc4(key, Padding).AsSpan().SequenceEqual(u.AsSpan(0, 32));
        }

        var value = Rc4(key, MD5.HashData([.. Padding, .. id]));
        for (var i = 1; i <= 19; i++)
        {
            value = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), value);
        }

        return u.Length >= 16 && value.AsSpan().SequenceEqual(u.AsSpan(0, 16));
    }

    private static byte[] Pad(byte[] password) => [.. password.Take(32), .. Padding.Take(32 - Math.Min(32, password.Length))];

    private static byte[] Hash(int r, byte[] password, byte[] salt, byte[] userKey) =>
        r == 5 ? SHA256.HashData([.. password, .. salt, .. userKey]) : PdfSecurity.Hash(password, salt, userKey);

    private static byte[] Unwrap(byte[] key, byte[] wrapped)
    {
        using var aes = Aes.Create();
        aes.Key = key[..32];
        return aes.DecryptCbc(wrapped, new byte[16], PaddingMode.None);
    }

    internal static byte[] Rc4(byte[] key, ReadOnlySpan<byte> data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }
}
