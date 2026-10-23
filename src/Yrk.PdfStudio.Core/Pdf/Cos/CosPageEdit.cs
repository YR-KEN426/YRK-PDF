using System.Globalization;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>
/// Collects changes to one page (new resources, content added on top or a replacement content
/// stream) and writes them as one changed page object.
/// </summary>
internal sealed class CosPageEdit
{
    private readonly CosDocument _document;
    private readonly CosDictionary _resources;
    private readonly Dictionary<string, CosDictionary> _categories = new(StringComparer.Ordinal);
    private readonly List<byte[]> _appended = [];
    private readonly List<byte[]> _prepended = [];
    private readonly Dictionary<string, CosObject?> _entries = new(StringComparer.Ordinal);
    private byte[]? _replacement;

    public CosPageEdit(CosDocument document, CosPage page)
    {
        _document = document;
        Page = page;
        _resources = (document.Resolve(page.Resources) as CosDictionary)?.Clone() ?? new CosDictionary();
    }

    public CosPage Page { get; }

    /// <summary>The page's resources as they will be written (inherited ones included).</summary>
    public CosDictionary Resources => _resources;

    /// <summary>Adds a resource under a name not used yet, for example /Font /YrkF1. Returns the name.</summary>
    public string AddResource(string category, string prefix, CosObject value)
    {
        var dictionary = Category(category);
        for (var i = 1; ; i++)
        {
            var name = prefix + i.ToString(CultureInfo.InvariantCulture);
            if (dictionary[name] is null)
            {
                dictionary[name] = value;
                return name;
            }
        }
    }

    /// <summary>Sets a resource by name (replacing it), for example a redacted copy of a form XObject.</summary>
    public void SetResource(string category, string name, CosObject value) => Category(category)[name] = value;

    /// <summary>Replaces all resources (used when the page becomes a single image).</summary>
    public void ClearResources()
    {
        _resources.Entries.Clear();
        _categories.Clear();
    }

    /// <summary>Sets an entry of the page dictionary itself, for example /Annots; null removes it.</summary>
    public void SetEntry(string key, CosObject? value) => _entries[key] = value;

    /// <summary>Content drawn after (on top of) the existing content, in a clean graphics state.</summary>
    public void Append(string content) => _appended.Add(Encoding.Latin1.GetBytes(content));

    public void Append(byte[] content) => _appended.Add(content);

    /// <summary>Content drawn before (under) the existing content, in its own graphics state.</summary>
    public void Prepend(string content) => _prepended.Add(Encoding.Latin1.GetBytes(content));

    /// <summary>Replaces the page's whole content (appended content still goes on top).</summary>
    public void ReplaceContent(byte[] content) => _replacement = content;

    /// <summary>The page's decoded content streams, joined as one stream would be.</summary>
    public byte[] ReadContent()
    {
        var parts = _document.Resolve(Page.Dictionary["Contents"]) switch
        {
            CosStream stream => [stream],
            CosArray array => array.Items.Select(_document.Resolve).OfType<CosStream>().ToList(),
            _ => new List<CosStream>(),
        };

        using var output = new MemoryStream();
        foreach (var part in parts)
        {
            output.Write(CosFilters.Decode(part, _document.Resolve));
            output.WriteByte((byte)'\n'); // Streams are split at token boundaries; keep them apart.
        }

        return output.ToArray();
    }

    public void Commit(CosIncrementalUpdate update)
    {
        var page = Page.Dictionary.Clone();
        page["Resources"] = _resources;
        foreach (var (key, value) in _entries)
        {
            page[key] = value;
        }


        var contents = new CosArray();
        if (_replacement is not null)
        {
            contents.Items.Add(update.Add(Stream([.. "q\n"u8, .. _replacement, .. "\nQ\n"u8])));
        }
        else if (_appended.Count > 0 || _prepended.Count > 0)
        {
            // Wrap the old content in q/Q so its graphics state cannot leak into what we add; content
            // drawn underneath comes first, in a q/Q of its own.
            byte[] start = _prepended.Count == 0
                ? "q\n"u8.ToArray()
                : [.. "q\n"u8, .. _prepended.SelectMany(p => p.Append((byte)'\n')), .. "Q\nq\n"u8];
            contents.Items.Add(update.Add(Stream(start)));
            switch (page["Contents"])
            {
                case CosArray existing:
                    contents.Items.AddRange(existing.Items);
                    break;
                case CosReference reference when _document.Resolve(reference) is CosArray shared:
                    contents.Items.AddRange(shared.Items);
                    break;
                case { } single:
                    contents.Items.Add(single);
                    break;
            }

            _appended.Insert(0, "\nQ\n"u8.ToArray());
        }
        else if (page["Contents"] is { } unchanged)
        {
            contents = null;
            page["Contents"] = unchanged;
        }

        if (contents is not null)
        {
            if (_appended.Count > 0)
            {
                contents.Items.Add(update.Add(Stream(_appended.SelectMany(a => a.Append((byte)'\n')).ToArray())));
            }

            page["Contents"] = contents;
        }

        update.Replace(Page.Id, page);
    }

    public static CosStream Stream(byte[] data, CosDictionary? dictionary = null)
    {
        var d = dictionary ?? new CosDictionary();
        d["Filter"] = new CosName("FlateDecode");
        return new CosStream(d, CosFilters.Deflate(data));
    }

    private CosDictionary Category(string category)
    {
        if (!_categories.TryGetValue(category, out var dictionary))
        {
            dictionary = (_document.Resolve(_resources[category]) as CosDictionary)?.Clone() ?? new CosDictionary();
            _resources[category] = dictionary;
            _categories[category] = dictionary;
        }

        return dictionary;
    }
}
