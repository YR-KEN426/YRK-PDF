using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Layers;

/// <summary>
/// A layer (optional content group, ISO 32000-1 §8.11) or a heading that groups layers in the viewer's
/// list. AutoCAD, Illustrator, InDesign and Visio write their layers this way.
/// </summary>
/// <param name="Id">The group's object reference ("12 0"); empty for a heading.</param>
/// <param name="Depth">Nesting level in the document's layer list (/Order).</param>
public sealed record PdfLayer(string Id, string Name, bool IsVisibleByDefault, bool IsLocked, int Depth)
{
    public bool IsHeading => Id.Length == 0;
}

public static class PdfLayerReader
{
    private const int MaxDepth = 32;

    /// <summary>The layers in the order the document lists them (unlisted ones at the end).</summary>
    public static IReadOnlyList<PdfLayer> Read(byte[] pdf)
    {
        CosDocument document;
        try
        {
            document = CosDocument.Load(pdf);
        }
        catch (PdfException)
        {
            return [];
        }

        if (document.IsEncrypted || document.Resolve(document.Catalog["OCProperties"]) is not CosDictionary properties
            || document.Resolve(properties["OCGs"]) is not CosArray groups)
        {
            return [];
        }

        var config = document.Resolve(properties["D"]) as CosDictionary;
        var baseOff = (document.Resolve(config?["BaseState"]) as CosName)?.Value == "OFF";
        var on = IdSet(document, config?["ON"]);
        var off = IdSet(document, config?["OFF"]);
        var locked = IdSet(document, config?["Locked"]);
        var all = groups.Items.OfType<CosReference>().Select(r => r.Id).Distinct().ToList();

        bool Visible(CosObjectId id) => baseOff ? on.Contains(id) : !off.Contains(id);

        string Name(CosObjectId id) =>
            (document.Resolve(document.GetObject(id) is CosDictionary group ? group["Name"] : null) as CosString)?.ToText() is { Length: > 0 } name
                ? name
                : $"Layer {id.Number}";

        var result = new List<PdfLayer>();
        var listed = new HashSet<CosObjectId>();

        void Walk(CosArray order, int depth)
        {
            if (depth > MaxDepth)
            {
                return;
            }

            var items = order.Items;
            var start = 0;

            // An array that starts with a string is a heading with the layers below it.
            if (items.Count > 0 && document.Resolve(items[0]) is CosString label)
            {
                result.Add(new PdfLayer(string.Empty, label.ToText(), true, false, depth));
                depth++;
                start = 1;
            }

            for (var i = start; i < items.Count; i++)
            {
                switch (items[i])
                {
                    case CosReference reference when document.Resolve(reference) is CosDictionary && all.Contains(reference.Id):
                        if (listed.Add(reference.Id))
                        {
                            result.Add(new PdfLayer(Key(reference.Id), Name(reference.Id), Visible(reference.Id), locked.Contains(reference.Id), depth));
                        }

                        break;
                    case var nested when document.Resolve(nested) is CosArray children:
                        // An array right after a layer holds that layer's sub-layers.
                        Walk(children, depth + 1);
                        break;
                }
            }
        }

        if (document.Resolve(config?["Order"]) is CosArray orderArray)
        {
            Walk(orderArray, 0);
        }

        foreach (var id in all.Where(listed.Add))
        {
            result.Add(new PdfLayer(Key(id), Name(id), Visible(id), locked.Contains(id), 0));
        }

        return result;
    }

    /// <summary>
    /// A copy whose default view shows every layer except <paramref name="hiddenIds"/> (an incremental
    /// update of /OCProperties /D). Used to render a chosen set of layers, and to save it as the default.
    /// </summary>
    public static byte[] WithVisibility(byte[] pdf, IReadOnlySet<string> hiddenIds)
    {
        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
        {
            throw new PdfException("Layers of password-protected PDFs cannot be changed yet.");
        }

        var catalog = document.Catalog;
        var propertiesValue = catalog["OCProperties"];
        if (document.Resolve(propertiesValue) is not CosDictionary properties || document.Resolve(properties["OCGs"]) is not CosArray groups)
        {
            return pdf;
        }

        var update = new CosIncrementalUpdate(document);
        var config = (document.Resolve(properties["D"]) as CosDictionary)?.Clone() ?? new CosDictionary();

        // Auto states (/AS) would switch layers by "usage" and override the ON/OFF lists.
        config["AS"] = null;
        config["BaseState"] = new CosName("ON");
        var refs = groups.Items.OfType<CosReference>().DistinctBy(r => r.Id).ToList();
        config["ON"] = new CosArray(refs.Where(r => !hiddenIds.Contains(Key(r.Id))));
        config["OFF"] = new CosArray(refs.Where(r => hiddenIds.Contains(Key(r.Id))));

        var newProperties = properties.Clone().Set("D", config);
        if (propertiesValue is CosReference reference)
        {
            update.Replace(reference.Id, newProperties);
        }
        else if (document.Trailer["Root"] is CosReference root)
        {
            update.Replace(root.Id, catalog.Clone().Set("OCProperties", newProperties));
        }

        return update.Build().Bytes;
    }

    private static string Key(CosObjectId id) => $"{id.Number} {id.Generation}";

    private static HashSet<CosObjectId> IdSet(CosDocument document, CosObject? value) =>
        (document.Resolve(value) as CosArray)?.Items.OfType<CosReference>().Select(r => r.Id).ToHashSet() ?? [];
}
