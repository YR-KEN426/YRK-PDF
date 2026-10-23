namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// Collects new and changed objects and appends them to the original bytes as an incremental
/// update (§7.5.6). The original bytes are never changed, which keeps existing signatures valid.
/// </summary>
internal sealed class CosIncrementalUpdate(CosDocument document)
{
    private readonly CosDocument _document = document;
    private readonly SortedDictionary<int, (CosObjectId Id, CosObject Value)> _objects = [];
    private int _nextNumber = document.Size;

    /// <summary>Adds a new indirect object and returns a reference to it.</summary>
    public CosReference Add(CosObject value)
    {
        var id = new CosObjectId(_nextNumber++, 0);
        _objects[id.Number] = (id, value);
        return new CosReference(id);
    }

    /// <summary>Replaces an existing object in the update (or writes one under a number the caller chose).</summary>
    public void Replace(CosObjectId id, CosObject value)
    {
        _objects[id.Number] = (id, value);
        _nextNumber = Math.Max(_nextNumber, id.Number + 1); // Keeps /Size above every number written.
    }

    /// <summary>Writes original bytes + update. Returns the output and where each placeholder landed.</summary>
    public (byte[] Bytes, Dictionary<CosPlaceholder, long> Placeholders) Build()
    {
        using var output = new MemoryStream(_document.Data.Length + 64 * 1024);
        output.Write(_document.Data);
        var writer = new CosWriter(output);
        if (_document.Data.Length > 0 && _document.Data[^1] is not (10 or 13))
        {
            writer.WriteRaw("\n");
        }

        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        foreach (var (number, (id, value)) in _objects)
        {
            offsets[number] = (writer.Position, id.Generation);
            writer.WriteIndirectObject(id, value);
        }

        var trailer = new CosDictionary();
        foreach (var key in new[] { "Root", "Info", "ID" })
        {
            if (_document.Trailer[key] is { } value)
            {
                trailer[key] = value;
            }
        }

        if (_document.StartXref > 0)
        {
            trailer["Prev"] = new CosInteger(_document.StartXref);
        }

        long xrefOffset;
        if (_document.UsesXrefStream)
        {
            // The xref stream is itself an object of the update and lists itself.
            var streamNumber = _nextNumber;
            xrefOffset = writer.Position;
            offsets[streamNumber] = (xrefOffset, 0);
            trailer["Type"] = new CosName("XRef");
            trailer["Size"] = new CosInteger(streamNumber + 1);
            trailer["W"] = new CosArray([new CosInteger(1), new CosInteger(4), new CosInteger(2)]);
            trailer["Index"] = new CosArray(Subsections(offsets).SelectMany(s => new CosObject[] { new CosInteger(s.First), new CosInteger(s.Count) }));
            var data = new List<byte>();
            foreach (var (_, (offset, generation)) in offsets)
            {
                data.Add(1);
                data.AddRange([(byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
                data.AddRange([(byte)(generation >> 8), (byte)generation]);
            }

            writer.WriteIndirectObject(new CosObjectId(streamNumber, 0), new CosStream(trailer, data.ToArray()));
        }
        else
        {
            xrefOffset = writer.Position;
            writer.WriteRaw("xref\n");
            foreach (var (first, count) in Subsections(offsets))
            {
                writer.WriteRaw($"{first} {count}\n");
                for (var number = first; number < first + count; number++)
                {
                    var (offset, generation) = offsets[number];
                    writer.WriteRaw($"{offset:D10} {generation:D5} n\r\n");
                }
            }

            trailer["Size"] = new CosInteger(_nextNumber);
            writer.WriteRaw("trailer\n");
            writer.Write(trailer);
            writer.WriteRaw("\n");
        }

        writer.WriteRaw($"startxref\n{xrefOffset}\n%%EOF\n");
        return (output.ToArray(), writer.PlaceholderOffsets);
    }

    private static IEnumerable<(int First, int Count)> Subsections(SortedDictionary<int, (long Offset, int Generation)> offsets)
    {
        int? first = null;
        var count = 0;
        foreach (var number in offsets.Keys)
        {
            if (first is not null && number == first + count)
            {
                count++;
                continue;
            }

            if (first is not null)
            {
                yield return (first.Value, count);
            }

            first = number;
            count = 1;
        }

        if (first is not null)
        {
            yield return (first.Value, count);
        }
    }
}
