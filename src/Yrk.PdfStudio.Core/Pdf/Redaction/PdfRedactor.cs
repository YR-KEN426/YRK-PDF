using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Pdf.Redaction;

/// <summary>An area to redact, relative to the page as displayed (0..1, origin top-left).</summary>
public sealed record PdfRedactionArea(int PageIndex, NormalizedRect Bounds);

/// <param name="ImagePages">Pages that had to be turned into images (for example scans), 0-based.</param>
public sealed record PdfRedactionResult(byte[] Output, IReadOnlyList<int> ImagePages);

/// <summary>
/// True redaction: the content under each area is removed from the file, not just covered.
/// <list type="number">
/// <item>Characters, paths, annotations and form fields inside the areas are removed and black boxes drawn.</item>
/// <item>PDFium reopens the result and reads the areas; a page where any text is left, or where an image or
/// unmeasurable text lies under an area, is replaced by an image of itself with the areas blacked out.</item>
/// <item>The file is rewritten without earlier revisions or unused objects, and checked once more.</item>
/// </list>
/// </summary>
public static class PdfRedactor
{
    private const int ImageDpi = 200;
    private const int JpegQuality = 90;

    public static async Task<PdfRedactionResult> RedactAsync(
        byte[] pdf,
        IReadOnlyList<PdfRedactionArea> areas,
        IPdfEngine engine,
        IPdfImageCodec? codec = null,
        CancellationToken cancellationToken = default,
        string? password = null)
    {
        // A protected file is redacted without its encryption, then gets the same protection back.
        var original = pdf;
        var locked = Cos.CosDocument.Load(pdf).IsEncrypted;
        if (locked)
        {
            pdf = Security.PdfSecurity.Unlock(pdf, password, Security.PdfPermissions.Modify);
        }

        var byPage = areas
            .Where(a => a.Bounds.Width > 0 && a.Bounds.Height > 0)
            .GroupBy(a => a.PageIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NormalizedRect>)g.Select(a => a.Bounds).ToList());
        if (byPage.Count == 0)
        {
            throw new ArgumentException("There is nothing to redact.", nameof(areas));
        }

        var source = CosDocument.Load(pdf);
        MaterializePageAttributes(source);
        pdf = CosCompactWriter.Write(source);
        var (removed, imagePages) = RemoveContent(pdf, byPage);
        var folder = locked ? null : Directory.CreateDirectory(Path.Combine(PdfTemporaryFiles.Shared.GetFolder("redact"), Guid.NewGuid().ToString("N")));
        try
        {
            var firstPass = folder is null ? null : Path.Combine(folder.FullName, "1.pdf");
            if (firstPass is not null) await PdfTemporaryFiles.WriteDurableAsync(firstPass, removed, cancellationToken).ConfigureAwait(false);
            var withImages = removed;
            await using (var document = await (firstPass is null ? engine.OpenBytesAsync(removed, "redaction.pdf", cancellationToken: cancellationToken)
                : engine.OpenAsync(firstPass, cancellationToken: cancellationToken)).ConfigureAwait(false))
            {
                foreach (var (page, rects) in byPage)
                {
                    if (!imagePages.Contains(page) && await HasTextAsync(document, page, rects, cancellationToken).ConfigureAwait(false))
                    {
                        imagePages.Add(page);
                    }
                }

                if (imagePages.Count > 0)
                {
                    var images = new Dictionary<int, PdfBitmap>();
                    foreach (var page in imagePages)
                    {
                        images[page] = await RenderBlackedOutAsync(document, page, byPage[page], cancellationToken).ConfigureAwait(false);
                    }

                    withImages = ReplaceWithImages(removed, images, codec);
                }
            }

            var cleaned = CosDocument.Load(withImages);
            StripHiddenData(cleaned);
            PruneUnusedXObjects(cleaned, byPage.Keys.ToHashSet());
            // A fresh compact file, never an incremental save: detached objects and old revisions go too.
            var output = CosCompactWriter.Write(cleaned);
            VerifyImages(source, CosDocument.Load(output), imagePages, byPage.Keys.ToHashSet());

            // Never hand back a file in which a redacted area still has text.
            var finalPass = folder is null ? null : Path.Combine(folder.FullName, "2.pdf");
            if (finalPass is not null) await PdfTemporaryFiles.WriteDurableAsync(finalPass, output, cancellationToken).ConfigureAwait(false);
            await using (var check = await (finalPass is null ? engine.OpenBytesAsync(output, "redaction.pdf", cancellationToken: cancellationToken)
                : engine.OpenAsync(finalPass, cancellationToken: cancellationToken)).ConfigureAwait(false))
            {
                foreach (var (page, rects) in byPage)
                {
                    if (await HasTextAsync(check, page, rects, cancellationToken).ConfigureAwait(false))
                    {
                        throw new PdfException($"Text on page {page + 1} could not be removed, so nothing was saved.");
                    }

                    await VerifyRenderedAreasAsync(check, page, rects, cancellationToken).ConfigureAwait(false);
                }
            }

            return new PdfRedactionResult(locked ? Security.PdfSecurity.Relock(output, original, password) : output, imagePages.Order().ToList());
        }
        finally
        {
            if (folder is not null) PdfTemporaryFiles.Shared.DeleteOwnedDirectory(folder.FullName);
        }
    }

    private static async Task<bool> HasTextAsync(IPdfDocument document, int page, IReadOnlyList<NormalizedRect> rects, CancellationToken cancellationToken)
    {
        foreach (var rect in rects)
        {
            var text = await document.GetTextInAreaAsync(page, rect, cancellationToken).ConfigureAwait(false);
            if (text.Any(c => !char.IsWhiteSpace(c) && !char.IsControl(c)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Step 1: content, annotations and fields under the areas are removed; black boxes are drawn.</summary>
    internal static (byte[] Output, HashSet<int> ImagePages) RemoveContent(
        byte[] pdf, IReadOnlyDictionary<int, IReadOnlyList<NormalizedRect>> byPage, bool blackBoxes = true, bool removeAnnotations = true, string? password = null)
    {
        var document = CosDocument.Load(pdf, password);
        if (document.IsLocked)
        {
            throw new PdfException("Password-protected PDFs cannot be redacted yet. Save an unprotected copy first.");
        }

        var pages = document.GetPages();
        var update = new CosIncrementalUpdate(document);
        var imagePages = new HashSet<int>();
        var removedAnnotations = new HashSet<CosObjectId>();
        foreach (var (index, rects) in byPage)
        {
            if (index < 0 || index >= pages.Count)
            {
                continue;
            }

            var page = pages[index];
            var userRects = rects.Select(r => ToUserSpace(page, r)).ToList();
            var edit = new CosPageEdit(document, page);
            // Text editing calls this helper with both options off; keep its established behavior.
            var redactor = new ContentRedactor(document, update, userRects, securePaths: blackBoxes || removeAnnotations);
            var result = redactor.Redact(edit.ReadContent(), edit.Resources, PdfMatrix.Identity);
            if (result.NeedsRaster)
            {
                imagePages.Add(index);
            }

            if (result.Changed)
            {
                edit.ReplaceContent(result.Content);
                foreach (var (name, value) in result.XObjectReplacements)
                {
                    edit.SetResource("XObject", name, value);
                }
            }

            foreach (var (name, value) in result.PropertyReplacements)
                edit.SetResource("Properties", name, value);
            if (blackBoxes || removeAnnotations)
                edit.SetEntry("Resources", redactor.PruneNamedProperties(result.Content, edit.Resources));
            if (removeAnnotations)
            {
                RemoveAnnotations(document, page, edit, userRects, removedAnnotations);
            }

            if (blackBoxes)
            {
                var boxes = new StringBuilder("q 0 g\n");
                foreach (var r in userRects)
                {
                    boxes.Append(CultureInfo.InvariantCulture, $"{F(r.X0)} {F(r.Y0)} {F(r.Width)} {F(r.Height)} re f\n");
                }

                edit.Append(boxes.Append("Q\n").ToString());
            }

            edit.Commit(update);
        }

        if (removedAnnotations.Count > 0)
        {
            RemoveFields(document, update, removedAnnotations);
        }

        var output = update.Build().Bytes;
        if (removedAnnotations.Count > 0)
        {
            var cleaned = CosDocument.Load(output);
            DetachRemovedAnnotations(cleaned, removedAnnotations);
            output = CosCompactWriter.Write(cleaned);
        }
        return (output, imagePages);
    }

    /// <summary>Step 2: whole pages replaced by an image of themselves.</summary>
    internal static byte[] ReplaceWithImages(byte[] pdf, IReadOnlyDictionary<int, PdfBitmap> images, IPdfImageCodec? codec)
    {
        var document = CosDocument.Load(pdf);
        var pages = document.GetPages();
        var update = new CosIncrementalUpdate(document);
        foreach (var (index, bitmap) in images)
        {
            var page = pages[index];
            var edit = new CosPageEdit(document, page);
            edit.ClearResources();
            var name = edit.AddResource("XObject", "YrkPage", update.Add(ImageStream(bitmap, codec)));

            // The bitmap is the page as displayed, so map it through the page rotation.
            var bottomLeft = page.ToUserSpace(0, 1);
            var bottomRight = page.ToUserSpace(1, 1);
            var topLeft = page.ToUserSpace(0, 0);
            edit.ReplaceContent(Encoding.ASCII.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"q {F(bottomRight.X - bottomLeft.X)} {F(bottomRight.Y - bottomLeft.Y)} {F(topLeft.X - bottomLeft.X)} {F(topLeft.Y - bottomLeft.Y)} {F(bottomLeft.X)} {F(bottomLeft.Y)} cm /{name} Do Q")));
            edit.Commit(update);
        }

        return update.Build().Bytes;
    }

    private static async Task<PdfBitmap> RenderBlackedOutAsync(IPdfDocument document, int page, IReadOnlyList<NormalizedRect> rects, CancellationToken cancellationToken)
    {
        var size = document.PageSizes[page];
        var scale = ImageDpi / 72.0;
        var bitmap = await document.RenderPageAsync(
            page,
            Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale)),
            PdfWorkPriority.Background,
            cancellationToken).ConfigureAwait(false);

        foreach (var r in rects)
        {
            var x0 = Math.Clamp((int)Math.Floor(r.X * bitmap.Width), 0, bitmap.Width);
            var x1 = Math.Clamp((int)Math.Ceiling((r.X + r.Width) * bitmap.Width), 0, bitmap.Width);
            var y0 = Math.Clamp((int)Math.Floor(r.Y * bitmap.Height), 0, bitmap.Height);
            var y1 = Math.Clamp((int)Math.Ceiling((r.Y + r.Height) * bitmap.Height), 0, bitmap.Height);
            for (var y = y0; y < y1; y++)
            {
                bitmap.Pixels.AsSpan((y * bitmap.Stride) + (x0 * 4), (x1 - x0) * 4).Clear();
            }
        }

        return bitmap;
    }

    private static CosStream ImageStream(PdfBitmap bitmap, IPdfImageCodec? codec)
    {
        var dictionary = new CosDictionary()
            .Set("Type", new CosName("XObject"))
            .Set("Subtype", new CosName("Image"))
            .Set("Width", new CosInteger(bitmap.Width))
            .Set("Height", new CosInteger(bitmap.Height))
            .Set("ColorSpace", new CosName("DeviceRGB"))
            .Set("BitsPerComponent", new CosInteger(8));
        if (codec is not null)
        {
            var jpeg = codec.EncodeJpeg(new PdfRawPixels(bitmap.Width, bitmap.Height, bitmap.Stride, PdfPixelFormat.Bgrx32, bitmap.Pixels), bitmap.Width, bitmap.Height, JpegQuality);
            return new CosStream(dictionary.Set("Filter", new CosName("DCTDecode")), jpeg);
        }

        var rgb = new byte[bitmap.Width * bitmap.Height * 3];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var s = (y * bitmap.Stride) + (x * 4);
                var t = ((y * bitmap.Width) + x) * 3;
                rgb[t] = bitmap.Pixels[s + 2];
                rgb[t + 1] = bitmap.Pixels[s + 1];
                rgb[t + 2] = bitmap.Pixels[s];
            }
        }

        return CosPageEdit.Stream(rgb, dictionary);
    }

    private static PdfRect ToUserSpace(CosPage page, NormalizedRect r) => PdfRect.Bounds(
    [
        page.ToUserSpace(r.X, r.Y), page.ToUserSpace(r.X + r.Width, r.Y),
        page.ToUserSpace(r.X, r.Y + r.Height), page.ToUserSpace(r.X + r.Width, r.Y + r.Height),
    ]);

    private static void DetachRemovedAnnotations(CosDocument document, HashSet<CosObjectId> removed)
    {
        var removedNumbers = removed.Select(id => id.Number).ToHashSet();
        bool IsRemoved(CosObject? value) => value is CosReference r && removedNumbers.Contains(r.Id.Number);
        bool IsRemovedAnnotation(CosObject? value) => IsRemoved(value)
            || document.Resolve(value) is CosDictionary d && (document.Resolve(d["Subtype"]) as CosName)?.Value == "Popup" && IsRemoved(d["Parent"]);
        // Include popups not listed in /Annots, and propagate through popup-parent chains.
        var annotations = document.ObjectIds.Select(id => (Id: id, Value: document.GetObject(id)))
            .Where(p => p.Value is CosDictionary).Select(p => (p.Id, Dictionary: (CosDictionary)p.Value!)).ToList();
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, annotation) in annotations)
                if ((document.Resolve(annotation["Subtype"]) as CosName)?.Value == "Popup" && IsRemoved(annotation["Parent"]))
                {
                    changed |= removed.Add(id);
                    removedNumbers.Add(id.Number);
                }
        }
        while (changed);

        var seen = new HashSet<CosObject>(ReferenceEqualityComparer.Instance);
        void Clean(CosObject? value)
        {
            value = document.Resolve(value);
            if (value is null || !seen.Add(value)) return;
            if (value is CosArray array)
            {
                array.Items.RemoveAll(IsRemovedAnnotation);
                foreach (var child in array.Items) Clean(child);
            }
            var dictionary = value is CosStream stream ? stream.Dictionary : value as CosDictionary;
            if (dictionary is null) return;
            if ((document.Resolve(dictionary["Subtype"]) as CosName)?.Value == "Popup" && IsRemoved(dictionary["Parent"]))
            {
                dictionary.Entries.Clear();
                return;
            }
            foreach (var key in new[] { "IRT", "Parent", "Popup" })
                if (IsRemovedAnnotation(dictionary[key])) dictionary[key] = null;
            foreach (var child in dictionary.Entries.Values.ToList()) Clean(child);
        }

        Clean(document.Catalog);
        // Other links may still reach the removed objects. Erase their payload and appearance.
        foreach (var id in removed)
            if (document.GetObject(id) is CosDictionary dictionary) dictionary.Entries.Clear();
    }

    private static void RemoveAnnotations(CosDocument document, CosPage page, CosPageEdit edit, List<PdfRect> areas, HashSet<CosObjectId> removed)
    {
        if (document.Resolve(page.Dictionary["Annots"]) is not CosArray annotations)
        {
            return;
        }

        var kept = new CosArray();
        foreach (var item in annotations.Items)
        {
            if (document.Resolve(item) is CosDictionary annotation
                && document.ToRect(annotation["Rect"]) is { } r
                && areas.Any(a => a.Intersects(new PdfRect(r.X0, r.Y0, Math.Max(r.X1, r.X0 + 0.1), Math.Max(r.Y1, r.Y0 + 0.1)))))
            {
                if (item is CosReference reference)
                {
                    removed.Add(reference.Id);
                }
                else annotation.Entries.Clear();

                continue;
            }

            kept.Items.Add(item);
        }

        if (kept.Count != annotations.Count)
        {
            edit.SetEntry("Annots", kept);
        }
    }

    /// <summary>Removes deleted widgets from the form, so their values are not kept in the field tree.</summary>
    private static void RemoveFields(CosDocument document, CosIncrementalUpdate update, HashSet<CosObjectId> removed)
    {
        if (document.Catalog["AcroForm"] is not { } acroFormValue || document.Resolve(acroFormValue) is not CosDictionary acroForm)
        {
            return;
        }

        // Clear the actual Parent chains as well as the field-tree traversal below. A shared
        // parent may survive, and a deeply nested field must not retain its inherited value.
        var cleared = new HashSet<CosObjectId>();
        foreach (var id in removed)
        {
            var widget = document.GetObject(id) as CosDictionary;
            var parent = widget?["Parent"] as CosReference;
            while (parent is not null && cleared.Add(parent.Id) && document.Resolve(parent) is CosDictionary field)
            {
                var copy = field.Clone();
                copy["V"] = null;
                copy["DV"] = null;
                update.Replace(parent.Id, copy);
                parent = field["Parent"] as CosReference;
            }
        }

        CosArray? Filter(CosArray array, int depth)
        {
            var changed = false;
            var result = new CosArray();
            foreach (var item in array.Items)
            {
                if (item is CosReference reference && removed.Contains(reference.Id))
                {
                    changed = true;
                    continue;
                }

                if (depth < 32 && item is CosReference parentRef && document.Resolve(parentRef) is CosDictionary parent
                    && document.Resolve(parent["Kids"]) is CosArray kids && Filter(kids, depth + 1) is { } newKids)
                {
                    if (newKids.Count == 0)
                    {
                        changed = true; // A field whose widgets are all gone goes too (its value with it).
                        continue;
                    }

                    var cleaned = parent.Clone().Set("Kids", newKids);
                    cleaned["V"] = null;
                    cleaned["DV"] = null;
                    update.Replace(parentRef.Id, cleaned);
                    changed = true; // Propagate through every ancestor, including shared field parents.
                }

                result.Items.Add(item);
            }

            return changed ? result : null;
        }

        if (document.Resolve(acroForm["Fields"]) is CosArray fields && Filter(fields, 0) is { } newFields)
        {
            var newForm = acroForm.Clone().Set("Fields", newFields);
            if (acroFormValue is CosReference formRef)
            {
                update.Replace(formRef.Id, newForm);
            }
            else if (document.Trailer["Root"] is CosReference root)
            {
                update.Replace(root.Id, document.Catalog.Clone().Set("AcroForm", newForm));
            }
        }
    }

    private static string F(double value) => CosWriter.FormatReal(value);

    private static void MaterializePageAttributes(CosDocument document)
    {
        var nodes = new HashSet<CosDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.GetPages())
        {
            var chain = new List<CosDictionary> { page.Dictionary };
            var seen = new HashSet<CosDictionary>(ReferenceEqualityComparer.Instance) { page.Dictionary };
            var parent = document.Resolve(page.Dictionary["Parent"]) as CosDictionary;
            while (parent is not null && seen.Add(parent))
            {
                chain.Add(parent);
                nodes.Add(parent);
                parent = document.Resolve(parent["Parent"]) as CosDictionary;
            }

            foreach (var key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
            {
                var inherited = chain.Select(d => d[key]).FirstOrDefault(v => key switch
                {
                    "MediaBox" or "CropBox" => document.Resolve(v) is CosArray,
                    "Rotate" => document.Resolve(v) is CosInteger,
                    _ => v is not null,
                });
                if (inherited is not null)
                {
                    page.Dictionary[key] = inherited;
                }
            }

            page.Dictionary["Resources"] ??= new CosDictionary();
            page.Dictionary["MediaBox"] ??= CosArray.FromNumbers(0, 0, 612, 792);
            page.Dictionary["CropBox"] ??= page.Dictionary["MediaBox"];
            page.Dictionary["Rotate"] = new CosInteger(page.Rotate);
        }

        // All leaves must be materialized before changing any ancestor.
        foreach (var id in document.ObjectIds)
        {
            if (document.GetObject(id) is CosDictionary dictionary && (dictionary["Type"] as CosName)?.Value == "Pages")
            {
                nodes.Add(dictionary);
            }
        }

        foreach (var node in nodes)
        {
            foreach (var key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
            {
                node[key] = null;
            }
        }
    }

    private static void StripHiddenData(CosDocument document)
    {
        document.Trailer["Info"] = null;
        foreach (var key in new[] { "Metadata", "StructTreeRoot", "MarkInfo", "Outlines" })
        {
            document.Catalog[key] = null;
        }

        if (document.Resolve(document.Catalog["Names"]) is CosDictionary names)
        {
            names["EmbeddedFiles"] = null;
            names["JavaScript"] = null;
        }

        if (document.Resolve(document.Catalog["AcroForm"]) is CosDictionary form)
        {
            form["XFA"] = null;
        }

        bool RemoveAction(CosObject? value)
        {
            if (document.Resolve(value) is not CosDictionary action
                || (document.Resolve(action["S"]) as CosName)?.Value is not
                    ("JavaScript" or "Launch" or "ImportData" or "SubmitForm" or "GoToE" or "GoToR"
                    or "Rendition" or "Sound" or "Movie" or "RichMedia"))
            {
                return false;
            }

            action["JS"] = null;
            return true;
        }

        var visited = new HashSet<CosObject>(ReferenceEqualityComparer.Instance);
        void Clean(CosObject? value)
        {
            value = document.Resolve(value);
            if (value is null || !visited.Add(value))
            {
                return;
            }

            if (value is CosArray array)
            {
                array.Items.RemoveAll(RemoveAction);
                foreach (var item in array.Items)
                {
                    Clean(item);
                }
            }

            var dictionary = value is CosStream stream ? stream.Dictionary : value as CosDictionary;
            if (dictionary is null)
            {
                return;
            }

            var type = (document.Resolve(dictionary["Type"]) as CosName)?.Value;
            var subtype = (document.Resolve(dictionary["Subtype"]) as CosName)?.Value;
            if (type == "Filespec" || subtype == "FileAttachment")
            {
                dictionary.Entries.Clear();
                return;
            }

            foreach (var key in new[] { "AF", "Metadata", "PieceInfo", "Thumb", "AA" })
            {
                dictionary[key] = null;
            }

            if (document.Resolve(dictionary["Annots"]) is CosArray annotations)
            {
                var kept = new CosArray();
                foreach (var item in annotations.Items)
                {
                    if (document.Resolve(item) is not CosDictionary annotation)
                    {
                        continue;
                    }

                    if ((document.Resolve(annotation["Subtype"]) as CosName)?.Value == "FileAttachment")
                    {
                        continue;
                    }

                    kept.Items.Add(item);
                }

                dictionary["Annots"] = kept;
            }

            // Remove every reference to a forbidden action, including aliases and /Next chains.
            // Keep allowed action dictionaries and their other entries intact.
            foreach (var (key, child) in dictionary.Entries.ToList())
            {
                if (RemoveAction(child))
                {
                    dictionary[key] = null;
                }
                else
                {
                    Clean(child);
                }
            }
        }

        Clean(document.Catalog);
    }

    internal static CosDictionary PruneResources(CosDocument document, byte[] content, CosDictionary resources)
    {
        var copy = resources.Clone();
        if (document.Resolve(resources["XObject"]) is not CosDictionary xobjects) return copy;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<CosStream>(ReferenceEqualityComparer.Instance);
        var uncertain = false;
        var budget = 100000;
        // These consumers can use the enclosing scope. If their use cannot be proven,
        // retaining resources is safer than changing the rendered result.
        foreach (var category in new[] { "Pattern", "Shading", "ExtGState" })
            if (document.Resolve(resources[category]) is CosDictionary { Entries.Count: > 0 }) return copy;
        if (document.Resolve(resources["Font"]) is CosDictionary fonts)
        {
            foreach (var fontValue in fonts.Entries.Values)
            {
                if (document.Resolve(fontValue) is not CosDictionary font) return copy;
                if ((document.Resolve(font["Subtype"]) as CosName)?.Value != "Type3") continue;
                if (font["Resources"] is not null) continue;
                if (document.Resolve(font["CharProcs"]) is not CosDictionary procs) return copy;
                foreach (var proc in procs.Entries.Values)
                {
                    if (document.Resolve(proc) is not CosStream glyph) return copy;
                    Read(CosFilters.Decode(glyph, document.Resolve));
                }
            }
        }
        void Read(byte[] bytes, int depth = 0)
        {
            if (depth > 32) { uncertain = true; return; }
            foreach (var op in ContentTokenizer.Parse(bytes))
            {
                if (--budget < 0) { uncertain = true; return; }
                if (op.Operator != "Do") continue;
                if (op.Operands.FirstOrDefault() is not CosName name) { uncertain = true; continue; }
                used.Add(name.Value);
                var target = document.Resolve(xobjects[name.Value]) as CosStream;
                if (target is null || (document.Resolve(target.Dictionary["Subtype"]) as CosName)?.Value is not ("Image" or "Form"))
                    uncertain = true;
                // A form without its own resources uses this same resource scope.
                if (target is CosStream form
                    && (form.Dictionary["Subtype"] as CosName)?.Value == "Form"
                    && form.Dictionary["Resources"] is null && seen.Add(form))
                {
                    Read(CosFilters.Decode(form, document.Resolve), depth + 1);
                }
            }
        }

        Read(content);
        if (uncertain) return copy;
        var kept = new CosDictionary();
        foreach (var (key, value) in xobjects.Entries)
        {
            if (used.Contains(key)) kept[key] = value;
        }

        copy["XObject"] = kept;
        return copy;
    }

    private static void PruneUnusedXObjects(CosDocument document, HashSet<int> markedPages)
    {
        var pages = document.GetPages();
        foreach (var index in markedPages.Where(i => i >= 0 && i < pages.Count))
        {
            var page = pages[index];
            var edit = new CosPageEdit(document, page);
            try
            {
                page.Dictionary["Resources"] = PruneResources(document, edit.ReadContent(), edit.Resources);
            }
            catch (PdfException)
            {
                // A consumer that cannot be decoded does not prove a resource is unused.
            }
        }
    }

    private static string ImageHash(CosDocument document, CosStream stream)
    {
        var filters = document.Resolve(stream.Dictionary["Filter"]) switch
        {
            CosName name => new[] { name.Value },
            CosArray array => array.Items.Select(document.Resolve).OfType<CosName>().Select(n => n.Value).ToArray(),
            _ => [],
        };
        // JPEG/JPX/JBIG2/CCITT stay in their image encoding. Structural filters are decoded,
        // so changing compression alone cannot hide an original image from the final check.
        var structural = filters.All(f => f is "FlateDecode" or "Fl" or "LZWDecode" or "LZW"
            or "ASCIIHexDecode" or "AHx" or "ASCII85Decode" or "A85");
        return Convert.ToHexString(SHA256.HashData(structural ? CosFilters.Decode(stream, document.Resolve) : stream.Data.Span));
    }

    internal static void VerifyImages(CosDocument source, CosDocument output, HashSet<int> rasterPages, HashSet<int> markedPages)
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        void Images(CosDocument doc, CosObject? value, HashSet<string> hashes, HashSet<CosObject> seen)
        {
            value = doc.Resolve(value);
            if (value is null || !seen.Add(value)) return;
            if (value is CosArray array)
            {
                foreach (var item in array.Items) Images(doc, item, hashes, seen);
            }

            var dictionary = value is CosStream stream ? stream.Dictionary : value as CosDictionary;
            if (dictionary is null) return;
            if (value is CosStream image && (doc.Resolve(dictionary["Subtype"]) as CosName)?.Value == "Image")
            {
                hashes.Add(ImageHash(doc, image));
            }

            foreach (var (key, child) in dictionary.Entries)
            {
                if (key != "Parent") Images(doc, child, hashes, seen);
            }
        }

        var pages = source.GetPages();
        foreach (var index in rasterPages)
        {
            Images(source, pages[index].Resources, forbidden, new(ReferenceEqualityComparer.Instance));
        }

        var resultPages = output.GetPages();
        for (var i = 0; i < resultPages.Count; i++)
        {
            if (!markedPages.Contains(i))
            {
                // Include Type 3 glyphs, patterns, shading and unknown resource consumers.
                // Untouched pages keep their complete resources, including shared images.
                Images(output, resultPages[i].Resources, allowed, new(ReferenceEqualityComparer.Instance));
            }
        }

        forbidden.ExceptWith(allowed);
        foreach (var id in output.ObjectIds)
        {
            if (output.GetObject(id) is CosStream stream
                && (output.Resolve(stream.Dictionary["Subtype"]) as CosName)?.Value == "Image"
                && forbidden.Contains(ImageHash(output, stream)))
            {
                throw new PdfException("An original image survived redaction, so nothing was saved.");
            }
        }
    }

    internal static async Task VerifyRenderedAreasAsync(IPdfDocument document, int page, IReadOnlyList<NormalizedRect> rects, CancellationToken cancellationToken)
    {
        var size = document.PageSizes[page];
        var bitmap = await document.RenderPageAsync(page, Math.Max(1, (int)Math.Round(size.Width * ImageDpi / 72)),
            Math.Max(1, (int)Math.Round(size.Height * ImageDpi / 72)), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
        foreach (var r in rects)
        {
            // Skip two edge pixels where PDF antialiasing or JPEG ringing can mix with adjacent content.
            var x0 = Math.Clamp((int)Math.Ceiling(r.X * bitmap.Width) + 2, 0, bitmap.Width);
            var x1 = Math.Clamp((int)Math.Floor((r.X + r.Width) * bitmap.Width) - 2, 0, bitmap.Width);
            var y0 = Math.Clamp((int)Math.Ceiling(r.Y * bitmap.Height) + 2, 0, bitmap.Height);
            var y1 = Math.Clamp((int)Math.Floor((r.Y + r.Height) * bitmap.Height) - 2, 0, bitmap.Height);
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var p = y * bitmap.Stride + x * 4;
                if (bitmap.Pixels[p] > 50 || bitmap.Pixels[p + 1] > 50 || bitmap.Pixels[p + 2] > 50)
                {
                    throw new PdfException($"The redacted area on page {page + 1} could not be verified, so nothing was saved.");
                }
            }
        }
    }
}
