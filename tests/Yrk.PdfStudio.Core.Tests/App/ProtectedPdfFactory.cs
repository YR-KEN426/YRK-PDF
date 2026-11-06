using Yrk.PdfStudio.Core.Pdf.Security;
using System.Security.Cryptography;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Tests;

// Same synthetic standard-handler fixture as ProtectedEditTests; never uses personal PDFs.
internal static class ProtectedPdfFactory
{
    private static readonly byte[] Padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    /// <summary>Encrypts with the standard handler, revision 3 or 4 (algorithms 2, 3 and 5), all permissions but copying.</summary>
    public static byte[] Legacy(byte[] pdf, int revision, string method, string user, string owner)
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
