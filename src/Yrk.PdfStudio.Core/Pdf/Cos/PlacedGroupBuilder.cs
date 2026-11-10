using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Cos;

/// <summary>The single representation used by images, ink, text and legacy migration.</summary>
internal static class PlacedGroupBuilder
{
    internal static string Mark(YrkMark mark, bool legacy = false) =>
        $"/YRK <</Kind({mark.Kind})/Group({mark.Group})/V(1)/Format(Group)/Fade({(legacy ? 1 : 0)})>> BDC\n";

    internal static CosDictionary Alpha(double opacity) => new CosDictionary()
        .Set("Type", new CosName("ExtGState"))
        .Set("ca", new CosReal(opacity)).Set("CA", new CosReal(opacity));

    internal static CosDictionary FormDictionary(CosDictionary resources, PdfRect bounds) => new CosDictionary()
        .Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Form"))
        .Set("FormType", new CosInteger(1))
        .Set("BBox", CosArray.FromNumbers(bounds.X0, bounds.Y0, bounds.X1, bounds.Y1))
        .Set("Resources", resources)
        .Set("Group", new CosDictionary().Set("S", new CosName("Transparency"))
            .Set("I", new CosBoolean(true)).Set("K", new CosBoolean(false)).Set("CS", new CosName("DeviceRGB")));

    internal static string Create(CosIncrementalUpdate update, CosPageEdit edit, byte[] children,
        CosDictionary resources, PdfRect bounds, double opacity, YrkMark? mark, bool legacy = false)
    {
        var form = edit.AddResource("XObject", "YrkGroup", update.Add(CosPageEdit.Stream(children, FormDictionary(resources, bounds))));
        // Always allocate a private GS. No resource, including an inherited dictionary, is mutated.
        var alpha = edit.AddResource("ExtGState", "YrkAlpha", update.Add(Alpha(opacity)));
        return $"q /{alpha} gs\n{(mark is null ? "" : Mark(mark, legacy))}/{form} Do\n{(mark is null ? "" : "EMC\n")}Q\n";
    }

    internal static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
    internal static string Matrix(PdfMatrix m) => string.Join(" ", new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(CosWriter.FormatReal)) + " cm\n";

    internal static PdfMatrix Inverse(PdfMatrix m)
    {
        var det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-12) throw new PdfException("The item has a singular transformation.");
        return new(m.D / det, -m.B / det, -m.C / det, m.A / det,
            (m.C * m.F - m.D * m.E) / det, (m.B * m.E - m.A * m.F) / det);
    }

    internal static byte[] GroupText(byte[] bytes, IReadOnlyList<Text.PdfTextBlock> blocks, string? password)
    {
        foreach (var batch in blocks.Where(b => b.Mark is not null).GroupBy(b => (b.PageIndex, b.Mark!.Group)))
        {
            if (batch.Select(b => b.Opacity).Distinct().Count() != 1 || batch.Select(b => b.Mark!.Kind).Distinct().Count() != 1)
                throw new PdfException("Members of a new text group must have the same kind and opacity.");
            var document = CosDocument.Load(bytes, password);
            if (batch.Key.PageIndex < 0 || batch.Key.PageIndex >= document.GetPages().Count) continue;
            var update = new CosIncrementalUpdate(document);
            var edit = new CosPageEdit(document, document.GetPages()[batch.Key.PageIndex]);
            var units = PlacedContent.Read(document, edit);
            var selected = units.Where(u => u.Mark?.Group == batch.Key.Group).ToList();
            if (selected.Count == 0 || selected.Any(u => u.Error is not null))
                throw new PdfException("The marked text cannot be grouped safely.");
            if (units.IndexOf(selected[^1]) - units.IndexOf(selected[0]) + 1 != selected.Count)
                throw new PdfException("The stamp and its text are separated by other painted content.");
            var wrappers = selected.Where(u => u.Grouped).ToList();
            if (wrappers.Count > 0)
            {
                if (wrappers.Count != 1 || selected.Any(u => !u.Grouped && u.Mark?.Kind != YrkMarkKind.StampText))
                    throw new PdfException("Text can only be attached to one existing stamp group.");
                var wrapper = wrappers[0];
                if (wrapper.Mark?.Kind != YrkMarkKind.Stamp || wrapper.XObject is null)
                    throw new PdfException("StampText requires a stamp frame in the same group.");
                var xobjects = (CosDictionary)document.Resolve(edit.Resources["XObject"])!;
                var form = document.Resolve(xobjects[wrapper.XObject]) as CosStream
                    ?? throw new PdfException("The stamp group is not a readable form.");
                var resources = (document.Resolve(form.Dictionary["Resources"]) as CosDictionary)?.Clone() ?? new();
                // Add page resources under collision-free names allocated by PdfTextWriter.
                foreach (var category in new[] { "Font", "Properties", "ExtGState" })
                {
                    var merged = (document.Resolve(resources[category]) as CosDictionary)?.Clone() ?? new();
                    if (document.Resolve(edit.Resources[category]) is CosDictionary pageResources)
                        foreach (var pair in pageResources.Entries)
                        {
                            if (merged[pair.Key] is { } existing)
                            {
                                if (ReferenceEquals(existing, pair.Value) || existing is CosReference a && pair.Value is CosReference b && a.Id == b.Id) continue;
                                throw new PdfException("Stamp text resources conflict with existing group resources.");
                            }
                            merged[pair.Key] = pair.Value;
                        }
                    resources[category] = merged;
                }
                byte[] children = [.. "q\n"u8, .. CosFilters.Decode(form, document.Resolve), .. "\nQ\n"u8,
                    .. selected.Where(u => !u.Grouped).SelectMany(u => u.Snapshot)];
                var dictionary = form.Dictionary.Clone();
                dictionary["Resources"] = resources;
                // Bounds expand to the page so newly attached words are never clipped by the old frame.
                var box = edit.Page.CropBox;
                dictionary["BBox"] = CosArray.FromNumbers(box.X0, box.Y0, box.X1, box.Y1);
                var name = edit.AddResource("XObject", "YrkStamp", update.Add(CosPageEdit.Stream(children, dictionary)));
                var replacements = selected.Select(u => u == wrapper ? u with { Replacement = $"/{name} Do\n" } : u).ToList();
                edit.ReplaceContent(PlacedContent.Replace(edit.ReadContent(), replacements, ""));
            }
            else
            {
                if (selected.Any(u => u.Mark?.Kind != batch.First().Mark!.Kind))
                    throw new PdfException("New StampText cannot be attached to a legacy frame. Migrate the stamp through the placed-item API first.");
                var first = selected[0];
                var box = edit.Page.CropBox;
                var invocation = "q\n" + Matrix(Inverse(first.Matrix)) + Create(update, edit,
                    selected.SelectMany(u => u.Snapshot).ToArray(), edit.Resources.Clone(), new(box.X0, box.Y0, box.X1, box.Y1),
                    Math.Clamp(batch.First().Opacity, 0, 1), batch.First().Mark) + "Q\n";
                edit.ReplaceContent(PlacedContent.Replace(edit.ReadContent(), selected, invocation, batch.Key.Group));
            }
            edit.Commit(update);
            bytes = update.Build().Bytes;
        }
        return bytes;
    }
}
