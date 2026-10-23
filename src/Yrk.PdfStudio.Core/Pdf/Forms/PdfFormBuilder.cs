using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Forms;

public enum PdfNewFieldKind
{
    Text,
    MultilineText,
    CheckBox,
    Dropdown,
    Signature,
}

/// <summary>A form field to add: where it goes (relative to the page as displayed), what it is and its name.</summary>
/// <param name="Options">The choices of a dropdown.</param>
/// <param name="Required">The field must be filled in before the form is submitted (a flag viewers show).</param>
public sealed record PdfNewField(
    int PageIndex,
    NormalizedRect Bounds,
    PdfNewFieldKind Kind,
    string Name,
    IReadOnlyList<string>? Options = null,
    bool Required = false);

/// <summary>A change to a field already in the PDF, found by its full name ("parent.child").</summary>
/// <param name="Delete">Take the field and all its widgets out.</param>
/// <param name="NewName">Its new (last) name part; null keeps it.</param>
/// <param name="Options">New dropdown or list choices; null keeps them.</param>
/// <param name="Required">Set or clear "must be filled in"; null keeps it.</param>
public sealed record PdfFieldChange(string FullName, bool Delete = false, string? NewName = null, IReadOnlyList<string>? Options = null, bool? Required = null);

/// <summary>
/// Adds interactive form fields (AcroForm, ISO 32000 §12.7) to a PDF: text boxes, check boxes, dropdowns and
/// signature fields, each a field merged with its widget and given an appearance, so every viewer shows and
/// fills them. Written as an incremental update: existing digital signatures stay valid.
/// </summary>
public static class PdfFormBuilder
{
    private const string BorderColor = "0.55 0.6 0.66"; // Light grey-blue frame, as most form tools draw.

    /// <exception cref="PdfException">The PDF is encrypted or cannot be read.</exception>
    public static byte[] AddFields(byte[] pdf, IReadOnlyList<PdfNewField> fields)
    {
        if (fields.Count == 0)
        {
            return pdf;
        }

        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
        {
            throw new PdfException("Remove the password protection first, then add form fields.");
        }

        var update = new CosIncrementalUpdate(document);
        var pages = document.GetPages();
        var catalog = document.Catalog;
        var formValue = catalog["AcroForm"];
        var form = (document.Resolve(formValue) as CosDictionary)?.Clone() ?? new CosDictionary();
        var fieldList = (document.Resolve(form["Fields"]) as CosArray)?.Clone() ?? new CosArray();
        var names = ExistingNames(document, fieldList);

        // Default resources: Helvetica for typing, ZapfDingbats for check marks.
        var resources = (document.Resolve(form["DR"]) as CosDictionary)?.Clone() ?? new CosDictionary();
        var fonts = (document.Resolve(resources["Font"]) as CosDictionary)?.Clone() ?? new CosDictionary();
        var helv = fonts["Helv"] ?? update.Add(new CosDictionary()
            .Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type1"))
            .Set("BaseFont", new CosName("Helvetica")).Set("Encoding", new CosName("WinAnsiEncoding")));
        var zadb = fonts["ZaDb"] ?? update.Add(new CosDictionary()
            .Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("ZapfDingbats")));
        fonts["Helv"] = helv;
        fonts["ZaDb"] = zadb;
        resources["Font"] = fonts;
        form["DR"] = resources;
        form["DA"] ??= CosString.FromText("/Helv 0 Tf 0 g");
        form["NeedAppearances"] = new CosBoolean(false); // Every field below has its appearance.

        // Page dictionaries (or their /Annots arrays) are replaced once with all their new widgets.
        var newAnnots = new Dictionary<int, List<CosObject>>();
        foreach (var field in fields)
        {
            if (field.PageIndex < 0 || field.PageIndex >= pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(fields), "A field is on a page that does not exist.");
            }

            var page = pages[field.PageIndex];
            var (ax, ay) = page.ToUserSpace(field.Bounds.X, field.Bounds.Y);
            var (bx, by) = page.ToUserSpace(field.Bounds.X + field.Bounds.Width, field.Bounds.Y + field.Bounds.Height);
            double x0 = Math.Min(ax, bx), y0 = Math.Min(ay, by), x1 = Math.Max(ax, bx), y1 = Math.Max(ay, by);
            var name = Unique(field.Name, names);
            var widget = Widget(update, field, name, page, x0, y0, x1, y1, zadb);
            var reference = update.Add(widget);
            fieldList.Items.Add(reference);
            if (!newAnnots.TryGetValue(field.PageIndex, out var list))
            {
                newAnnots[field.PageIndex] = list = [];
            }

            list.Add(reference);
        }

        foreach (var (pageIndex, added) in newAnnots)
        {
            var page = pages[pageIndex];
            var annotsValue = page.Dictionary["Annots"];
            var annots = (document.Resolve(annotsValue) as CosArray)?.Clone() ?? new CosArray();
            annots.Items.AddRange(added);
            if (annotsValue is CosReference annotsRef)
            {
                update.Replace(annotsRef.Id, annots);
            }
            else
            {
                update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", annots));
            }
        }

        form["Fields"] = fieldList;
        if (formValue is CosReference formRef)
        {
            update.Replace(formRef.Id, form);
        }
        else if (document.Trailer["Root"] is CosReference root)
        {
            update.Replace(root.Id, catalog.Clone().Set("AcroForm", update.Add(form)));
        }

        return update.Build().Bytes;
    }

    /// <summary>
    /// Renames, re-options, flags or deletes fields already in the PDF (an incremental update). Deleting
    /// takes the field out of the form and its widgets off their pages.
    /// </summary>
    /// <exception cref="PdfException">The PDF is encrypted, has no form, or a field is not found.</exception>
    public static byte[] ChangeFields(byte[] pdf, IReadOnlyList<PdfFieldChange> changes)
    {
        if (changes.Count == 0)
        {
            return pdf;
        }

        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
        {
            throw new PdfException("Remove the password protection first, then change form fields.");
        }

        var catalog = document.Catalog;
        if (document.Resolve(catalog["AcroForm"]) is not CosDictionary form || document.Resolve(form["Fields"]) is not CosArray topFields)
        {
            throw new PdfException("This document has no form fields to change.");
        }

        // Every field by full name, with where it hangs (the /Fields array, or its parent's /Kids).
        var found = new Dictionary<string, (CosReference Ref, CosDictionary Field, CosObject? Parent)>(StringComparer.Ordinal);
        void Walk(CosArray array, CosObject? parent, string prefix, int depth)
        {
            foreach (var item in array.Items)
            {
                if (item is not CosReference reference || document.Resolve(item) is not CosDictionary field)
                {
                    continue;
                }

                var own = field["T"] is CosString t ? t.ToText() : null;
                if (own is null)
                {
                    continue; // A widget of its parent field.
                }

                var full = prefix.Length == 0 ? own : prefix + "." + own;
                found[full] = (reference, field, parent);
                if (depth < 32 && document.Resolve(field["Kids"]) is CosArray kids)
                {
                    Walk(kids, reference, full, depth + 1);
                }
            }
        }

        Walk(topFields, null, string.Empty, 0);
        var update = new CosIncrementalUpdate(document);
        var names = new HashSet<string>(found.Keys, StringComparer.Ordinal);
        var replaced = new Dictionary<CosObjectId, CosObject>();
        CosObject Current(CosReference reference) => replaced.TryGetValue(reference.Id, out var value) ? value : document.Resolve(reference)!;
        void Put(CosReference reference, CosObject value)
        {
            replaced[reference.Id] = value;
        }

        var fieldsArray = topFields.Clone();
        var removedWidgets = new HashSet<CosObjectId>();
        foreach (var change in changes)
        {
            if (!found.TryGetValue(change.FullName, out var entry))
            {
                throw new PdfException($"The field \"{change.FullName}\" was not found.");
            }

            if (change.Delete)
            {
                // Its widgets: the field itself when merged, else its widget kids.
                removedWidgets.Add(entry.Ref.Id);
                if (document.Resolve(entry.Field["Kids"]) is CosArray kids)
                {
                    foreach (var kid in kids.Items.OfType<CosReference>())
                    {
                        removedWidgets.Add(kid.Id);
                    }
                }

                if (entry.Parent is CosReference parentRef && Current(parentRef) is CosDictionary parent
                    && document.Resolve(parent["Kids"]) is CosArray siblings)
                {
                    var kept = new CosArray(siblings.Items.Where(k => k is not CosReference r || r.Id != entry.Ref.Id));
                    Put(parentRef, parent.Clone().Set("Kids", kept));
                }
                else
                {
                    fieldsArray = new CosArray(fieldsArray.Items.Where(k => k is not CosReference r || r.Id != entry.Ref.Id));
                }

                continue;
            }

            var changed = ((CosDictionary)Current(entry.Ref)).Clone();
            if (change.NewName is { } newName && newName.Trim().Length > 0)
            {
                var prefix = change.FullName.Contains('.') ? change.FullName[..change.FullName.LastIndexOf('.')] + "." : string.Empty;
                names.Remove(change.FullName);
                var siblings = names
                    .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && !n[prefix.Length..].Contains('.'))
                    .Select(n => n[prefix.Length..])
                    .ToHashSet(StringComparer.Ordinal);
                var unique = Unique(newName, siblings);
                names.Add(prefix + unique);
                changed["T"] = CosString.FromText(unique);
            }

            if (change.Options is { } options)
            {
                changed["Opt"] = new CosArray(options.Where(o => o.Length > 0).Select(o => (CosObject)CosString.FromText(o)));
                if (changed["V"] is CosString value && !options.Contains(value.ToText()))
                {
                    changed.Entries.Remove("V");
                    changed.Entries.Remove("I");
                }
            }

            if (change.Required is { } required)
            {
                var flags = (changed["Ff"] as CosInteger)?.Value ?? 0;
                changed["Ff"] = new CosInteger(required ? flags | 2 : flags & ~2L);
            }

            Put(entry.Ref, changed);
        }

        // Take deleted widgets off every page.
        if (removedWidgets.Count > 0)
        {
            foreach (var page in document.GetPages())
            {
                var annotsValue = page.Dictionary["Annots"];
                if (document.Resolve(annotsValue) is not CosArray annots
                    || !annots.Items.Any(a => a is CosReference r && removedWidgets.Contains(r.Id)))
                {
                    continue;
                }

                var kept = new CosArray(annots.Items.Where(a => a is not CosReference r || !removedWidgets.Contains(r.Id)));
                if (annotsValue is CosReference annotsRef)
                {
                    update.Replace(annotsRef.Id, kept);
                }
                else
                {
                    update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", kept));
                }
            }
        }

        foreach (var (id, value) in replaced)
        {
            if (!removedWidgets.Contains(id))
            {
                update.Replace(id, value);
            }
        }

        if (fieldsArray.Count != topFields.Count)
        {
            var newForm = form.Clone().Set("Fields", fieldsArray);
            if (catalog["AcroForm"] is CosReference formRef)
            {
                update.Replace(formRef.Id, newForm);
            }
            else if (document.Trailer["Root"] is CosReference root)
            {
                update.Replace(root.Id, catalog.Clone().Set("AcroForm", newForm));
            }
        }

        return update.Build().Bytes;
    }

    private static CosDictionary Widget(
        CosIncrementalUpdate update, PdfNewField field, string name, CosPage page, double x0, double y0, double x1, double y1, CosObject zadb)
    {
        double w = x1 - x0, h = y1 - y0;
        var widget = new CosDictionary()
            .Set("Type", new CosName("Annot"))
            .Set("Subtype", new CosName("Widget"))
            .Set("T", CosString.FromText(name))
            .Set("Rect", CosArray.FromNumbers(x0, y0, x1, y1))
            .Set("F", new CosInteger(4)) // Printed.
            .Set("P", new CosReference(page.Id))
            .Set("MK", new CosDictionary().Set("BC", RealArray(0.55, 0.6, 0.66)));
        if (page.Rotate != 0)
        {
            ((CosDictionary)widget["MK"]!)["R"] = new CosInteger(page.Rotate);
        }

        var flags = field.Required ? 2 : 0;
        var frame = Frame(w, h, dashed: field.Kind == PdfNewFieldKind.Signature);
        switch (field.Kind)
        {
            case PdfNewFieldKind.Text or PdfNewFieldKind.MultilineText:
                flags |= field.Kind == PdfNewFieldKind.MultilineText ? 1 << 12 : 0;
                widget.Set("FT", new CosName("Tx"))
                    .Set("DA", CosString.FromText(field.Kind == PdfNewFieldKind.MultilineText ? "/Helv 10 Tf 0 g" : "/Helv 0 Tf 0 g"))
                    .Set("AP", new CosDictionary().Set("N", update.Add(Appearance(w, h, frame + "/Tx BMC\nEMC\n", null))));
                break;

            case PdfNewFieldKind.CheckBox:
                var mark = Math.Min(w, h) * 0.8;
                var on = frame + FormattableString.Invariant(
                    $"q BT 0 g /ZaDb {Num(mark)} Tf {Num((w - (mark * 0.78)) / 2)} {Num((h - (mark * 0.7)) / 2)} Td (4) Tj ET Q\n");
                var zadbResources = new CosDictionary().Set("Font", new CosDictionary().Set("ZaDb", zadb));
                widget.Set("FT", new CosName("Btn"))
                    .Set("V", new CosName("Off"))
                    .Set("AS", new CosName("Off"))
                    .Set("DA", CosString.FromText("/ZaDb 0 Tf 0 g"))
                    .Set("AP", new CosDictionary().Set("N", new CosDictionary()
                        .Set("Yes", update.Add(Appearance(w, h, on, zadbResources)))
                        .Set("Off", update.Add(Appearance(w, h, frame, null)))));
                ((CosDictionary)widget["MK"]!)["CA"] = CosString.FromText("4");
                break;

            case PdfNewFieldKind.Dropdown:
                flags |= 1 << 17; // Combo box.
                var options = (field.Options ?? []).Where(o => o.Length > 0).Select(o => (CosObject)CosString.FromText(o));
                widget.Set("FT", new CosName("Ch"))
                    .Set("Opt", new CosArray(options))
                    .Set("DA", CosString.FromText("/Helv 0 Tf 0 g"))
                    .Set("AP", new CosDictionary().Set("N", update.Add(Appearance(w, h, frame + Arrow(w, h) + "/Tx BMC\nEMC\n", null))));
                break;

            case PdfNewFieldKind.Signature:
                widget.Set("FT", new CosName("Sig"))
                    .Set("AP", new CosDictionary().Set("N", update.Add(Appearance(w, h, frame, null))));
                break;
        }

        if (flags != 0)
        {
            widget["Ff"] = new CosInteger(flags);
        }

        return widget;
    }

    private static CosStream Appearance(double w, double h, string content, CosDictionary? resources)
    {
        var dictionary = new CosDictionary()
            .Set("Type", new CosName("XObject"))
            .Set("Subtype", new CosName("Form"))
            .Set("BBox", CosArray.FromNumbers(0, 0, w, h));
        if (resources is not null)
        {
            dictionary["Resources"] = resources;
        }

        return new CosStream(dictionary, Encoding.ASCII.GetBytes(content));
    }

    /// <summary>A thin frame just inside the box (dashed for signature fields).</summary>
    private static string Frame(double w, double h, bool dashed) =>
        FormattableString.Invariant(
            $"q {BorderColor} RG 0.75 w {(dashed ? "[3 2] 0 d " : string.Empty)}0.375 0.375 {Num(w - 0.75)} {Num(h - 0.75)} re S Q\n");

    /// <summary>A small down-arrow at the right of a dropdown.</summary>
    private static string Arrow(double w, double h)
    {
        var size = Math.Min(h * 0.35, 6);
        var cx = w - (h / 2);
        var cy = h / 2;
        return FormattableString.Invariant(
            $"q {BorderColor} rg {Num(cx - size)} {Num(cy + (size / 2))} m {Num(cx + size)} {Num(cy + (size / 2))} l {Num(cx)} {Num(cy - (size / 2))} l f Q\n");
    }

    private static HashSet<string> ExistingNames(CosDocument document, CosArray fields)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(CosArray array, string prefix, int depth)
        {
            foreach (var field in array.Items.Select(document.Resolve).OfType<CosDictionary>())
            {
                var own = field["T"] is CosString t ? t.ToText() : null;
                var full = own is null ? prefix : prefix.Length == 0 ? own : prefix + "." + own;
                if (full.Length > 0)
                {
                    names.Add(full);
                }

                if (depth < 32 && document.Resolve(field["Kids"]) is CosArray kids)
                {
                    Walk(kids, full, depth + 1);
                }
            }
        }

        Walk(fields, string.Empty, 0);
        return names;
    }

    /// <summary>The name, or "name 2", "name 3"… when taken. Dots would make it a child of another field.</summary>
    private static string Unique(string name, HashSet<string> taken)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Field" : name.Trim().Replace('.', '_');
        var candidate = clean;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = $"{clean} {n.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    private static CosArray RealArray(params double[] values) => CosArray.FromNumbers(values);

    private static string Num(double value) => CosWriter.FormatReal(value);
}
