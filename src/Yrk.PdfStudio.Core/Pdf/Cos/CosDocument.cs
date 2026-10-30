using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// Read access to the objects of a PDF file held in memory: cross-reference tables and streams
/// (with /Prev chains and hybrid files), object streams, and the page tree.
/// </summary>
internal sealed partial class CosDocument
{
    private const int MaxXrefSections = 1000;
    private const int MaxPageTreeDepth = 64;

    private readonly Dictionary<int, XrefEntry> _xref = [];
    private readonly Dictionary<CosObjectId, CosObject> _cache = [];
    private readonly Dictionary<int, IReadOnlyList<(int Number, CosObject Value)>> _objectStreams = [];
    private Dictionary<int, int>? _scannedOffsets;

    private CosDocument(byte[] data) => Data = data;

    public byte[] Data { get; }

    /// <summary>The newest trailer dictionary (from a classic trailer or a cross-reference stream).</summary>
    public CosDictionary Trailer { get; private set; } = new();

    /// <summary>Offset of the newest cross-reference section, which an update's /Prev must point to.</summary>
    public long StartXref { get; private set; }

    /// <summary>True when the newest section is a cross-reference stream (an update must then add one too).</summary>
    public bool UsesXrefStream { get; private set; }

    /// <summary>True when the cross-reference data was unusable and objects were found by scanning the file.</summary>
    public bool WasReconstructed { get; private set; }

    public int Size => (int)Math.Max((Trailer["Size"] as CosInteger)?.Value ?? 0, _xref.Count == 0 ? 0 : _xref.Keys.Max() + 1);

    /// <summary>True when every object in use has generation 0 (as almost every file has).</summary>
    public bool AllGenerationsZero => _xref.Values.All(e => e.Kind == XrefKind.Free || e.Generation == 0);

    public bool IsEncrypted => Trailer["Encrypt"] is not null;

    /// <summary>Encrypted and not unlocked: objects are read as stored and cannot be changed.</summary>
    public bool IsLocked => IsEncrypted && Crypt is null;

    /// <summary>The unlocked security handler: objects are decrypted when read and encrypted again when written.</summary>
    public Security.CosCrypt? Crypt { get; private set; }

    private CosObjectId? _encryptId;

    public CosDictionary Catalog => Resolve(Trailer["Root"]) as CosDictionary ?? throw new PdfException("The document has no catalog.");

    /// <param name="password">Unlocks an encrypted file (an empty password is tried when null).</param>
    /// <exception cref="PdfPasswordRequiredException">A password was given and it fits neither password of the file.</exception>
    public static CosDocument Load(byte[] data, string? password = null)
    {
        var document = new CosDocument(data);
        try
        {
            document.ReadXref();
            _ = document.Catalog;
        }
        catch (PdfException)
        {
            document.Reconstruct();
        }

        if (document.IsEncrypted)
        {
            document.Unlock(password);
        }

        return document;
    }

    private void Unlock(string? password)
    {
        _encryptId = (Trailer["Encrypt"] as CosReference)?.Id;
        if (Resolve(Trailer["Encrypt"]) is not CosDictionary encrypt)
        {
            return;
        }

        Crypt = Security.CosCrypt.Open(encrypt, Trailer, password);
        if (Crypt is null)
        {
            if (password is not null)
            {
                throw new PdfPasswordRequiredException(passwordWasWrong: true);
            }

            return;
        }

        // Everything read so far is still encrypted: read it again.
        _cache.Clear();
        _objectStreams.Clear();
        if (_encryptId is { } id)
        {
            _cache[id] = encrypt;
        }
    }

    private int _nextNumber = -1;

    /// <summary>
    /// Replaces an object in memory (for a rewrite with <see cref="CosCompactWriter"/>; the file data is untouched).
    /// </summary>
    public void Replace(CosObjectId id, CosObject value) => _cache[id] = value;

    /// <summary>Adds a new object in memory and returns a reference to it (for a rewrite).</summary>
    public CosReference AddObject(CosObject value)
    {
        if (_nextNumber < 0)
        {
            _nextNumber = Math.Max(Size, _cache.Count == 0 ? 1 : _cache.Keys.Max(k => k.Number) + 1);
        }

        var id = new CosObjectId(_nextNumber++, 0);
        _cache[id] = value;
        return new CosReference(id);
    }

    public CosObject? Resolve(CosObject? value)
    {
        for (var hops = 0; value is CosReference reference && hops < 32; hops++)
        {
            value = GetObject(reference.Id);
        }

        return value is CosReference ? null : value;
    }

    public CosObject? GetObject(CosObjectId id)
    {
        if (_cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        CosObject? value = null;
        if (_xref.TryGetValue(id.Number, out var entry))
        {
            value = entry.Kind switch
            {
                XrefKind.InFile => Decrypt(ReadAt(entry.Offset, id.Number), id),
                XrefKind.Compressed => ReadFromObjectStream((int)entry.Offset, id.Number), // Decrypted with its stream.
                _ => null,
            };
        }
        else if (WasReconstructed && _scannedOffsets!.TryGetValue(id.Number, out var offset))
        {
            value = Decrypt(ReadAt(offset, id.Number), id);
        }

        value ??= CosNull.Instance;
        _cache[id] = value;
        return value;
    }

    /// <summary>The page dictionaries in order, with inherited attributes resolved.</summary>
    public IReadOnlyList<CosPage> GetPages()
    {
        var pages = new List<CosPage>();
        var visited = new HashSet<CosObjectId>();
        if (Catalog["Pages"] is CosReference root)
        {
            Walk(root, null, null, 0, null, 0);
        }

        return pages;

        void Walk(CosReference node, CosArray? mediaBox, CosArray? cropBox, int rotate, CosObject? resources, int depth)
        {
            if (depth > MaxPageTreeDepth || !visited.Add(node.Id) || Resolve(node) is not CosDictionary dictionary)
            {
                return;
            }

            mediaBox = Resolve(dictionary["MediaBox"]) as CosArray ?? mediaBox;
            cropBox = Resolve(dictionary["CropBox"]) as CosArray ?? cropBox;
            rotate = Resolve(dictionary["Rotate"]) is CosInteger r ? (int)r.Value : rotate;
            resources = dictionary["Resources"] ?? resources;

            var type = (dictionary["Type"] as CosName)?.Value;
            if (Resolve(dictionary["Kids"]) is CosArray kids && type != "Page")
            {
                foreach (var kid in kids.Items.OfType<CosReference>())
                {
                    Walk(kid, mediaBox, cropBox, rotate, resources, depth + 1);
                }

                return;
            }

            var media = ToRect(mediaBox) ?? (0, 0, 612, 792);
            var crop = ToRect(cropBox) is { } c ? Intersect(c, media) : media;
            pages.Add(new CosPage(node.Id, dictionary, crop, ((rotate % 360) + 360) % 360, resources));
        }
    }

    public (double X0, double Y0, double X1, double Y1)? ToRect(CosObject? value)
    {
        if (Resolve(value) is not CosArray { Count: 4 } array)
        {
            return null;
        }

        var n = array.Items.Select(i => Resolve(i) switch
        {
            CosInteger x => (double)x.Value,
            CosReal x => x.Value,
            _ => 0,
        }).ToArray();
        return (Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
    }

    private static (double X0, double Y0, double X1, double Y1) Intersect((double X0, double Y0, double X1, double Y1) a, (double X0, double Y0, double X1, double Y1) b)
    {
        var r = (Math.Max(a.X0, b.X0), Math.Max(a.Y0, b.Y0), Math.Min(a.X1, b.X1), Math.Min(a.Y1, b.Y1));
        return r.Item3 > r.Item1 && r.Item4 > r.Item2 ? r : b;
    }

    // --- Cross-reference data ---------------------------------------------------------------

    private void ReadXref()
    {
        StartXref = FindStartXref();
        var offset = StartXref;
        var seen = new HashSet<long>();
        var first = true;
        while (offset > 0 && seen.Add(offset) && seen.Count <= MaxXrefSections)
        {
            var (trailer, isStream) = ReadXrefSection(offset);
            if (first)
            {
                Trailer = trailer;
                UsesXrefStream = isStream;
                first = false;
            }

            offset = (trailer["Prev"] as CosInteger)?.Value ?? 0;
        }

        if (first)
        {
            throw new PdfException("No cross-reference data.");
        }
    }

    private long FindStartXref()
    {
        var tail = Data.AsSpan(Math.Max(0, Data.Length - 2048));
        var index = tail.LastIndexOf("startxref"u8);
        if (index < 0)
        {
            throw new PdfException("'startxref' not found.");
        }

        var parser = new CosParser(Data) { Position = Data.Length - tail.Length + index + "startxref".Length };
        parser.SkipWhitespace();
        var offset = parser.ReadInteger();
        return offset > 0 && offset < Data.Length ? offset : throw new PdfException("Invalid 'startxref' offset.");
    }

    private (CosDictionary Trailer, bool IsStream) ReadXrefSection(long offset)
    {
        var parser = new CosParser(Data) { Position = (int)offset };
        parser.SkipWhitespace();
        if (!parser.PeekKeyword("xref"))
        {
            var (_, value) = parser.ParseIndirectObject();
            if (value is not CosStream stream || (stream.Dictionary["Type"] as CosName)?.Value != "XRef")
            {
                throw new PdfException("Invalid cross-reference section.");
            }

            AddXrefStreamEntries(stream, overrideFree: false);
            return (stream.Dictionary, true);
        }

        parser.Position += 4;
        var section = new Dictionary<int, XrefEntry>();
        while (true)
        {
            parser.SkipWhitespace();
            if (parser.PeekKeyword("trailer"))
            {
                parser.Position += "trailer".Length;
                break;
            }

            var start = parser.ReadInteger();
            parser.SkipWhitespace();
            var count = parser.ReadInteger();
            for (var i = 0; i < count; i++)
            {
                parser.SkipWhitespace();
                var entryOffset = parser.ReadInteger();
                parser.SkipWhitespace();
                var generation = parser.ReadInteger();
                parser.SkipWhitespace();
                var inUse = parser.PeekKeyword("n");
                parser.Position++;
                section[(int)(start + i)] = new XrefEntry(inUse ? XrefKind.InFile : XrefKind.Free, entryOffset, (int)generation);
            }
        }

        if (parser.ParseObject() is not CosDictionary trailer)
        {
            throw new PdfException("Invalid trailer.");
        }

        foreach (var (number, entry) in section)
        {
            _xref.TryAdd(number, entry); // Newer sections are read first and win.
        }

        // Hybrid file: objects hidden from old readers are listed in an extra stream (§7.5.8.4).
        if (trailer["XRefStm"] is CosInteger hidden && hidden.Value > 0 && hidden.Value < Data.Length)
        {
            var streamParser = new CosParser(Data) { Position = (int)hidden.Value };
            if (streamParser.ParseIndirectObject().Value is CosStream xrefStream)
            {
                AddXrefStreamEntries(xrefStream, overrideFree: true);
            }
        }

        return (trailer, false);
    }

    private void AddXrefStreamEntries(CosStream stream, bool overrideFree)
    {
        var dictionary = stream.Dictionary;
        if (dictionary["W"] is not CosArray { Count: 3 } widthArray)
        {
            throw new PdfException("Cross-reference stream without /W.");
        }

        var widths = widthArray.Items.Select(i => (int)((i as CosInteger)?.Value ?? 0)).ToArray();
        if (widths.Any(w => w is < 0 or > 8))
        {
            throw new PdfException("Invalid cross-reference stream widths.");
        }

        var size = (int)((dictionary["Size"] as CosInteger)?.Value ?? 0);
        var index = dictionary["Index"] is CosArray indexArray
            ? indexArray.Items.Select(i => (i as CosInteger)?.Value ?? 0).ToArray()
            : [0, size];

        var data = CosFilters.Decode(stream, Resolve);
        var entryLength = widths.Sum();
        var position = 0;
        for (var pair = 0; pair + 1 < index.Length; pair += 2)
        {
            for (var i = 0; i < index[pair + 1] && position + entryLength <= data.Length; i++)
            {
                var type = widths[0] == 0 ? 1 : Field(widths[0]);
                var field2 = Field(widths[1]);
                var field3 = Field(widths[2]);
                var number = (int)(index[pair] + i);
                var entry = type switch
                {
                    0 => new XrefEntry(XrefKind.Free, 0, (int)field3),
                    1 => new XrefEntry(XrefKind.InFile, field2, (int)field3),
                    2 => new XrefEntry(XrefKind.Compressed, field2, 0),
                    _ => (XrefEntry?)null,
                };

                if (entry is null)
                {
                    continue;
                }

                if (overrideFree && _xref.TryGetValue(number, out var existing) && existing.Kind == XrefKind.Free)
                {
                    _xref[number] = entry.Value;
                }
                else
                {
                    _xref.TryAdd(number, entry.Value);
                }
            }
        }

        long Field(int width)
        {
            long value = 0;
            for (var b = 0; b < width; b++)
            {
                value = (value << 8) | data[position++];
            }

            return value;
        }
    }

    private CosObject? Decrypt(CosObject? value, CosObjectId id) =>
        value is not null && Crypt is { } crypt && id != _encryptId ? crypt.Decrypt(value, id) : value;

    private CosObject? ReadAt(long offset, int expectedNumber)
    {
        if (offset > 0 && offset < Data.Length)
        {
            try
            {
                var (id, value) = new CosParser(Data, GetObject) { Position = (int)offset }.ParseIndirectObject();
                if (id.Number == expectedNumber)
                {
                    return value;
                }
            }
            catch (PdfException)
            {
                // Fall through to scanning: the offset is often off by a few bytes in damaged files.
            }
        }

        _scannedOffsets ??= ScanObjects();
        if (_scannedOffsets.TryGetValue(expectedNumber, out var scanned) && scanned != offset)
        {
            try
            {
                return new CosParser(Data, GetObject) { Position = scanned }.ParseIndirectObject().Value;
            }
            catch (PdfException)
            {
            }
        }

        return null;
    }

    private CosObject? ReadFromObjectStream(int streamNumber, int objectNumber)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out var objects))
        {
            objects = [];
            _objectStreams[streamNumber] = objects; // Set first, so a stream that refers to itself cannot loop.
            if (GetObject(new CosObjectId(streamNumber, 0)) is CosStream stream)
            {
                objects = ParseObjectStream(stream);
                _objectStreams[streamNumber] = objects;
            }
        }

        return objects.FirstOrDefault(o => o.Number == objectNumber).Value;
    }

    private List<(int Number, CosObject Value)> ParseObjectStream(CosStream stream)
    {
        var count = (int)((Resolve(stream.Dictionary["N"]) as CosInteger)?.Value ?? 0);
        var first = (int)((Resolve(stream.Dictionary["First"]) as CosInteger)?.Value ?? 0);
        var data = CosFilters.Decode(stream, Resolve);
        var parser = new CosParser(data, GetObject);
        var header = new List<(int Number, int Offset)>();
        for (var i = 0; i < count; i++)
        {
            parser.SkipWhitespace();
            var number = parser.ReadInteger();
            parser.SkipWhitespace();
            header.Add(((int)number, (int)parser.ReadInteger()));
        }

        var objects = new List<(int Number, CosObject Value)>();
        foreach (var (number, offset) in header)
        {
            parser.Position = first + offset;
            try
            {
                objects.Add((number, parser.ParseObject()));
            }
            catch (PdfException)
            {
            }
        }

        return objects;
    }

    // --- Recovery -------------------------------------------------------------------------

    [GeneratedRegex(@"(?<![0-9])([0-9]{1,10})[ \r\n\t\f\0]+([0-9]{1,5})[ \r\n\t\f\0]+obj\b", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectHeader();

    private Dictionary<int, int> ScanObjects()
    {
        var offsets = new Dictionary<int, int>();
        var text = Encoding.Latin1.GetString(Data);
        foreach (Match match in ObjectHeader().Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                offsets[number] = match.Index; // Later definitions replace earlier ones, like updates do.
            }
        }

        return offsets;
    }

    private void Reconstruct()
    {
        WasReconstructed = true;
        _xref.Clear();
        _cache.Clear();
        _scannedOffsets = ScanObjects();
        foreach (var (number, offset) in _scannedOffsets)
        {
            _xref[number] = new XrefEntry(XrefKind.InFile, offset, 0);
        }

        // Objects inside object streams, and the catalog from the newest trailer or xref stream.
        CosDictionary? trailer = null;
        foreach (var number in _scannedOffsets.Keys.Order().ToList())
        {
            if (GetObject(new CosObjectId(number, 0)) is CosStream { Dictionary: var dictionary } stream)
            {
                var type = (dictionary["Type"] as CosName)?.Value;
                if (type == "ObjStm")
                {
                    foreach (var (inner, _) in ParseObjectStream(stream))
                    {
                        _xref.TryAdd(inner, new XrefEntry(XrefKind.Compressed, number, 0));
                    }
                }
                else if (type == "XRef" && dictionary["Root"] is not null)
                {
                    trailer = dictionary;
                }
            }
        }

        var text = Encoding.Latin1.GetString(Data);
        var trailerIndex = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (trailerIndex >= 0)
        {
            try
            {
                if (new CosParser(Data) { Position = trailerIndex + 7 }.ParseObject() is CosDictionary classic && classic["Root"] is not null)
                {
                    trailer = classic;
                }
            }
            catch (PdfException)
            {
            }
        }

        if (trailer is null)
        {
            var catalog = _scannedOffsets.Keys.FirstOrDefault(n => GetObject(new CosObjectId(n, 0)) is CosDictionary { } d && (d["Type"] as CosName)?.Value == "Catalog");
            if (catalog == 0)
            {
                throw new PdfException("The file is damaged and its structure could not be recovered.");
            }

            trailer = new CosDictionary().Set("Root", new CosReference(catalog, 0));
        }

        Trailer = trailer;
        StartXref = 0;
        UsesXrefStream = false;
        _cache.Clear();
    }

    private enum XrefKind
    {
        Free,
        InFile,
        Compressed,
    }

    /// <summary>For <see cref="XrefKind.Compressed"/>, <see cref="Offset"/> is the object stream's number.</summary>
    private readonly record struct XrefEntry(XrefKind Kind, long Offset, int Generation);
}

/// <summary>A page with its inherited, effective crop box (in default user space) and rotation (0, 90, 180 or 270). <see cref="Resources"/> is the page's own or inherited /Resources entry (a dictionary or a reference).</summary>
internal sealed record CosPage(CosObjectId Id, CosDictionary Dictionary, (double X0, double Y0, double X1, double Y1) CropBox, int Rotate, CosObject? Resources = null)
{
    /// <summary>
    /// Converts a point given relative to the page as displayed (0..1, origin top-left, rotation
    /// applied) to default user space.
    /// </summary>
    public (double X, double Y) ToUserSpace(double nx, double ny)
    {
        var (x0, y0, x1, y1) = CropBox;
        double w = x1 - x0, h = y1 - y0;
        return Rotate switch
        {
            90 => (x0 + (ny * w), y0 + (nx * h)),
            180 => (x1 - (nx * w), y0 + (ny * h)),
            270 => (x1 - (ny * w), y1 - (nx * h)),
            _ => (x0 + (nx * w), y1 - (ny * h)),
        };
    }
}
