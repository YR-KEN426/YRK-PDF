namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// Shrinks an incremental save to the objects that really changed. PDFium's incremental save writes every
/// object it has loaded, changed or not: after filling in a form that is every field and the fonts used to
/// show them, about a megabyte more on each save. The update is rebuilt here from the objects whose content
/// differs from the original, still appended to the original bytes, so existing signatures stay valid.
/// </summary>
internal static class CosUpdateTrimmer
{
    public static byte[] Trim(byte[] original, byte[] saved)
    {
        if (saved.Length <= original.Length || !saved.AsSpan(0, original.Length).SequenceEqual(original))
        {
            return saved; // Not an update of these bytes (a rewritten file): nothing to trim.
        }

        CosDocument before, after;
        try
        {
            before = CosDocument.Load(original);
            after = CosDocument.Load(saved);
        }
        catch (PdfException)
        {
            return saved;
        }

        if (before.IsEncrypted || after.IsEncrypted || before.WasReconstructed || after.WasReconstructed || !after.AllGenerationsZero)
        {
            // Encrypted objects cannot be compared as written, and objects are rewritten here as generation 0:
            // leave such files as PDFium made them.
            return saved;
        }

        var update = new CosIncrementalUpdate(before);
        var changed = 0;
        for (var number = 1; number < after.Size; number++)
        {
            var id = new CosObjectId(number, 0);
            var value = after.GetObject(id);
            if (value is null or CosNull || IsFileStructure(value))
            {
                continue;
            }

            if (!Same(value, before.GetObject(id), before, after))
            {
                update.Replace(id, value);
                changed++;
            }
        }

        var trimmed = changed == 0 ? original : update.Build().Bytes;
        return trimmed.Length < saved.Length ? trimmed : saved;
    }

    /// <summary>Cross-reference and object streams belong to how a revision is stored, not to its content.</summary>
    private static bool IsFileStructure(CosObject value) =>
        value is CosStream { Dictionary: var d } && d["Type"] is CosName { Value: "XRef" or "ObjStm" };

    private static bool Same(CosObject? a, CosObject? b, CosDocument before, CosDocument after)
    {
        switch (a, b)
        {
            case (null or CosNull, null or CosNull):
                return true;
            case (CosBoolean x, CosBoolean y):
                return x.Value == y.Value;
            case (CosInteger or CosReal, CosInteger or CosReal):
                return Math.Abs(Number(a) - Number(b)) < 1e-9;
            case (CosName x, CosName y):
                return x.Value == y.Value;
            case (CosString x, CosString y):
                return x.Bytes.AsSpan().SequenceEqual(y.Bytes);
            case (CosReference x, CosReference y):
                return x.Id == y.Id;
            case (CosArray x, CosArray y):
                if (x.Count != y.Count)
                {
                    return false;
                }

                for (var i = 0; i < x.Count; i++)
                {
                    if (!Same(x[i], y[i], before, after))
                    {
                        return false;
                    }
                }

                return true;
            case (CosStream x, CosStream y):
                if (!SameEntries(x.Dictionary, y.Dictionary, before, after, ignoreEncoding: true))
                {
                    return false;
                }

                if (SameEntries(x.Dictionary, y.Dictionary, before, after, ignoreEncoding: false)
                    && x.Data.Span.SequenceEqual(y.Data.Span))
                {
                    return true;
                }

                // Same content stored with other compression is still the same content.
                try
                {
                    return CosFilters.Decode(x, after.Resolve).AsSpan().SequenceEqual(CosFilters.Decode(y, before.Resolve));
                }
                catch (Exception ex) when (ex is PdfException or InvalidDataException or NotSupportedException or IndexOutOfRangeException)
                {
                    return false;
                }

            case (CosDictionary x, CosDictionary y):
                return SameEntries(x, y, before, after, ignoreEncoding: false);
            default:
                return false;
        }
    }

    private static readonly HashSet<string> EncodingKeys = new(StringComparer.Ordinal) { "Length", "Filter", "DecodeParms", "DL" };

    private static bool SameEntries(CosDictionary x, CosDictionary y, CosDocument before, CosDocument after, bool ignoreEncoding)
    {
        var keys = x.Entries.Keys.Concat(y.Entries.Keys).Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (ignoreEncoding && EncodingKeys.Contains(key))
            {
                continue;
            }

            var a = x[key];
            var b = y[key];
            if (key == "Length" && !ignoreEncoding)
            {
                // A stream length may be an indirect number; compare the numbers.
                a = after.Resolve(a);
                b = before.Resolve(b);
            }

            if (!Same(a, b, before, after))
            {
                return false;
            }
        }

        return true;
    }

    private static double Number(CosObject? value) => value switch
    {
        CosInteger i => i.Value,
        CosReal r => r.Value,
        _ => double.NaN,
    };
}
