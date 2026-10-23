using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// Writes a new file with only the objects reachable from the trailer, numbered from 1, in one revision.
/// Earlier revisions, replaced objects and unused objects are gone, so content removed by redaction
/// cannot be recovered from the file. Existing digital signatures do not survive this.
/// </summary>
internal static class CosCompactWriter
{
    /// <param name="encryption">Encrypts every string and stream, and adds the /Encrypt dictionary (null: none).</param>
    /// <param name="version">The header version to write ("1.7"); null keeps the file's own.</param>
    public static byte[] Write(CosDocument document, Security.CosEncryption? encryption = null, string? version = null)
    {
        if (document.IsEncrypted)
        {
            throw new PdfException("Password-protected PDFs cannot be rewritten yet.");
        }

        var numbers = new Dictionary<CosObjectId, int>();
        var order = new List<CosObjectId>();
        var pending = new Queue<CosObject>();
        foreach (var key in new[] { "Root", "Info" })
        {
            if (document.Trailer[key] is { } value)
            {
                pending.Enqueue(value);
            }
        }

        while (pending.Count > 0)
        {
            switch (pending.Dequeue())
            {
                case CosReference reference when !numbers.ContainsKey(reference.Id):
                    var target = document.GetObject(reference.Id);
                    if (target is null or CosNull)
                    {
                        break;
                    }

                    numbers[reference.Id] = order.Count + 1;
                    order.Add(reference.Id);
                    pending.Enqueue(target);
                    break;
                case CosArray array:
                    array.Items.ForEach(pending.Enqueue);
                    break;
                case CosStream stream:
                    foreach (var (key, value) in stream.Dictionary.Entries)
                    {
                        if (key != "Length")
                        {
                            pending.Enqueue(value);
                        }
                    }

                    break;
                case CosDictionary dictionary:
                    foreach (var value in dictionary.Entries.Values)
                    {
                        pending.Enqueue(value);
                    }

                    break;
            }
        }

        using var output = new MemoryStream(document.Data.Length);
        var writer = new CosWriter(output);
        writer.WriteRaw(version is not null ? $"%PDF-{version}\n" : encryption is null ? Header(document.Data) : "%PDF-1.7\n"); // AES-256 is Acrobat's extension level 8 of 1.7.
        writer.WriteRaw([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var rootId = (document.Trailer["Root"] as CosReference)?.Id;
        var offsets = new long[order.Count + (encryption is null ? 0 : 1)];
        for (var i = 0; i < order.Count; i++)
        {
            offsets[i] = writer.Position;
            var value = Remap(document.GetObject(order[i])!, numbers);
            if (encryption is not null)
            {
                if (order[i] == rootId && value is CosDictionary catalog)
                {
                    value = WithExtensionLevel(catalog);
                }

                value = Encrypt(value, encryption);
            }

            writer.WriteIndirectObject(new CosObjectId(i + 1, 0), value);
        }

        if (encryption is not null)
        {
            // The encryption dictionary itself is written in the clear, as the last object.
            offsets[^1] = writer.Position;
            writer.WriteIndirectObject(new CosObjectId(offsets.Length, 0), encryption.Dictionary);
        }

        var xref = writer.Position;
        writer.WriteRaw($"xref\n0 {offsets.Length + 1}\n0000000000 65535 f\r\n");
        foreach (var offset in offsets)
        {
            writer.WriteRaw($"{offset:D10} 00000 n\r\n");
        }

        var trailer = new CosDictionary().Set("Size", new CosInteger(offsets.Length + 1));
        if (encryption is not null)
        {
            trailer["Encrypt"] = new CosReference(offsets.Length, 0);
            if (document.Trailer["ID"] is not CosArray)
            {
                var fileId = new CosString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), isHex: true);
                trailer["ID"] = new CosArray([fileId, fileId]);
            }
        }

        foreach (var key in new[] { "Root", "Info" })
        {
            if (document.Trailer[key] is { } value && Remap(value, numbers) is CosReference remapped)
            {
                trailer[key] = remapped;
            }
        }

        if (document.Trailer["ID"] is CosArray id)
        {
            trailer["ID"] = id;
        }

        writer.WriteRaw("trailer\n");
        writer.Write(trailer);
        writer.WriteRaw($"\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static CosObject Remap(CosObject value, Dictionary<CosObjectId, int> numbers) => value switch
    {
        CosReference reference => numbers.TryGetValue(reference.Id, out var number) ? new CosReference(number, 0) : CosNull.Instance,
        CosArray array => new CosArray(array.Items.Select(i => Remap(i, numbers))),
        CosStream stream => new CosStream((CosDictionary)Remap(stream.Dictionary, numbers), stream.Data),
        CosDictionary dictionary => RemapDictionary(dictionary, numbers),
        _ => value,
    };

    /// <summary>Encrypts the strings and stream data inside one object (references and names stay as they are).</summary>
    private static CosObject Encrypt(CosObject value, Security.CosEncryption encryption) => value switch
    {
        CosString s => new CosString(encryption.Encrypt(s.Bytes), isHex: true),
        CosArray array => new CosArray(array.Items.Select(i => Encrypt(i, encryption))),
        CosStream stream => new CosStream((CosDictionary)Encrypt(stream.Dictionary, encryption), encryption.Encrypt(stream.Data.Span)),
        CosDictionary dictionary => EncryptDictionary(dictionary, encryption),
        _ => value,
    };

    private static CosDictionary EncryptDictionary(CosDictionary dictionary, Security.CosEncryption encryption)
    {
        var copy = new CosDictionary();
        foreach (var (key, value) in dictionary.Entries)
        {
            copy[key] = Encrypt(value, encryption);
        }

        return copy;
    }

    /// <summary>Declares Adobe extension level 8 (AES-256) in the catalog, as Acrobat does for 1.7 files.</summary>
    private static CosDictionary WithExtensionLevel(CosDictionary catalog)
    {
        var copy = catalog.Clone();
        copy["Extensions"] = new CosDictionary().Set("ADBE", new CosDictionary()
            .Set("BaseVersion", new CosName("1.7"))
            .Set("ExtensionLevel", new CosInteger(8)));
        return copy;
    }

    private static CosDictionary RemapDictionary(CosDictionary dictionary, Dictionary<CosObjectId, int> numbers)
    {
        var copy = new CosDictionary();
        foreach (var (key, value) in dictionary.Entries)
        {
            var remapped = Remap(value, numbers);
            if (remapped is not CosNull)
            {
                copy[key] = remapped;
            }
        }

        return copy;
    }

    private static string Header(byte[] data)
    {
        var start = data.AsSpan(0, Math.Min(1024, data.Length)).IndexOf("%PDF-"u8);
        if (start >= 0 && start + 8 <= data.Length)
        {
            var version = Encoding.ASCII.GetString(data, start + 5, 3);
            if (version.Length == 3 && char.IsDigit(version[0]) && version[1] == '.' && char.IsDigit(version[2]))
            {
                return $"%PDF-{version}\n";
            }
        }

        return "%PDF-1.7\n";
    }
}
