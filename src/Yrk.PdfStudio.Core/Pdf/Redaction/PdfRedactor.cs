using System.Globalization;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

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
        CancellationToken cancellationToken = default)
    {
        var byPage = areas
            .Where(a => a.Bounds.Width > 0 && a.Bounds.Height > 0)
            .GroupBy(a => a.PageIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NormalizedRect>)g.Select(a => a.Bounds).ToList());
        if (byPage.Count == 0)
        {
            throw new ArgumentException("There is nothing to redact.", nameof(areas));
        }

        var (removed, imagePages) = RemoveContent(pdf, byPage);
        var folder = Directory.CreateTempSubdirectory("yrk-redact-");
        try
        {
            var firstPass = Path.Combine(folder.FullName, "1.pdf");
            await File.WriteAllBytesAsync(firstPass, removed, cancellationToken).ConfigureAwait(false);
            var withImages = removed;
            await using (var document = await engine.OpenAsync(firstPass, cancellationToken: cancellationToken).ConfigureAwait(false))
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

            var output = CosCompactWriter.Write(CosDocument.Load(withImages));

            // Never hand back a file in which a redacted area still has text.
            var finalPass = Path.Combine(folder.FullName, "2.pdf");
            await File.WriteAllBytesAsync(finalPass, output, cancellationToken).ConfigureAwait(false);
            await using (var check = await engine.OpenAsync(finalPass, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                foreach (var (page, rects) in byPage)
                {
                    if (await HasTextAsync(check, page, rects, cancellationToken).ConfigureAwait(false))
                    {
                        throw new PdfException($"Text on page {page + 1} could not be removed, so nothing was saved.");
                    }
                }
            }

            return new PdfRedactionResult(output, imagePages.Order().ToList());
        }
        finally
        {
            folder.Delete(recursive: true);
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
        byte[] pdf, IReadOnlyDictionary<int, IReadOnlyList<NormalizedRect>> byPage, bool blackBoxes = true, bool removeAnnotations = true)
    {
        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
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
            var result = new ContentRedactor(document, update, userRects).Redact(edit.ReadContent(), edit.Resources, PdfMatrix.Identity);
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

            StripReplacementText(document, edit);
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

        return (update.Build().Bytes, imagePages);
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

    /// <summary>Named marked-content properties can carry /ActualText or /Alt copies of the text.</summary>
    private static void StripReplacementText(CosDocument document, CosPageEdit edit)
    {
        if (document.Resolve(edit.Resources["Properties"]) is not CosDictionary properties)
        {
            return;
        }

        foreach (var (name, value) in properties.Entries.ToList())
        {
            if (document.Resolve(value) is CosDictionary dictionary
                && (dictionary["ActualText"] is not null || dictionary["Alt"] is not null || dictionary["E"] is not null))
            {
                var cleaned = dictionary.Clone();
                cleaned["ActualText"] = null;
                cleaned["Alt"] = null;
                cleaned["E"] = null;
                edit.SetResource("Properties", name, cleaned);
            }
        }
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

                    update.Replace(parentRef.Id, parent.Clone().Set("Kids", newKids));
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
}
