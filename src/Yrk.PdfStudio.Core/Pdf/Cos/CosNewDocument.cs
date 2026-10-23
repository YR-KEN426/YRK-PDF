namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>Builds a new PDF file from objects (used for documents made from scratch, such as drawings).</summary>
internal sealed class CosNewDocument
{
    private readonly List<CosObject?> _objects = [];

    /// <summary>A number for an object that is set later (for objects that refer to each other).</summary>
    public CosReference Reserve()
    {
        _objects.Add(null);
        return new CosReference(_objects.Count, 0);
    }

    public CosReference Add(CosObject value)
    {
        _objects.Add(value);
        return new CosReference(_objects.Count, 0);
    }

    public void Set(CosReference reference, CosObject value) => _objects[reference.Id.Number - 1] = value;

    public byte[] Build(CosReference root, CosReference? info = null)
    {
        using var output = new MemoryStream();
        var writer = new CosWriter(output);
        writer.WriteRaw("%PDF-1.7\n");
        writer.WriteRaw([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var offsets = new long[_objects.Count];
        for (var i = 0; i < _objects.Count; i++)
        {
            offsets[i] = writer.Position;
            writer.WriteIndirectObject(new CosObjectId(i + 1, 0), _objects[i] ?? CosNull.Instance);
        }

        var xref = writer.Position;
        writer.WriteRaw($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f\r\n");
        foreach (var offset in offsets)
        {
            writer.WriteRaw($"{offset:D10} 00000 n\r\n");
        }

        var trailer = new CosDictionary().Set("Size", new CosInteger(_objects.Count + 1)).Set("Root", root);
        if (info is not null)
        {
            trailer["Info"] = info;
        }

        writer.WriteRaw("trailer\n");
        writer.Write(trailer);
        writer.WriteRaw($"\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
