using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

internal sealed partial class PdfiumDocument
{
    private readonly Guid _itemDocumentId = Guid.NewGuid();
    private sealed record ItemObjects(PdfPageItem Item, List<IntPtr> Objects, PdfRect UserBounds);

    public Task<IReadOnlyList<PdfPageItem>> GetPageItemsAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync<IReadOnlyList<PdfPageItem>>(() => ReadItems(pageIndex).Select(i => i.Item).ToArray(),
            PdfWorkPriority.Interactive, cancellationToken);
    }

    public Task MovePageItemAsync(PdfPageItemRef item, NormalizedPoint delta, CancellationToken cancellationToken = default, PdfItemEditOptions? options = null)
    {
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) throw new ArgumentOutOfRangeException(nameof(delta));
        return EditItem(item, selection =>
        {
            var page = GetPage(item.PageIndex);
            var (w, h) = GetDeviceSize(item.PageIndex);
            var origin = DeviceToPage(page, w, h, 0, 0);
            var end = DeviceToPage(page, w, h, delta.X, delta.Y);
            Transform(selection, new(1, 0, 0, 1, end.X - origin.X, end.Y - origin.Y));
        }, false, cancellationToken, options);
    }

    public Task RotatePageItemAsync(PdfPageItemRef item, double degrees, CancellationToken cancellationToken = default, PdfItemEditOptions? options = null)
    {
        if (!double.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        return EditItem(item, selection =>
        {
            var b = selection.UserBounds;
            var x = (b.X0 + b.X1) / 2;
            var y = (b.Y0 + b.Y1) / 2;
            var theta = degrees * Math.PI / 180;
            var c = Math.Cos(theta);
            var s = Math.Sin(theta);
            Transform(selection, new(c, -s, s, c, x - c * x - s * y, y + s * x - c * y));
        }, false, cancellationToken, options);
    }

    public Task SetPageItemOpacityAsync(PdfPageItemRef item, double opacity, CancellationToken cancellationToken = default, PdfItemEditOptions? options = null)
    {
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        return EditItem(item, selection =>
        {
            var obj = selection.Objects.Single();
            var alpha = PlacementAlpha(255, opacity);
            if (PdfiumNative.FPDFPageObj_SetFillColor(obj, 0, 0, 0, alpha) == 0
                || PdfiumNative.FPDFPageObj_SetStrokeColor(obj, 0, 0, 0, alpha) == 0)
                throw new PdfException("The group's opacity could not be changed.");
        }, false, cancellationToken, options);
    }

    public Task RemovePageItemAsync(PdfPageItemRef item, CancellationToken cancellationToken = default, PdfItemEditOptions? options = null) =>
        EditItem(item, selection =>
        {
            foreach (var obj in selection.Objects)
            {
                if (PdfiumNative.FPDFPage_RemoveObject(GetPage(item.PageIndex), obj) == 0)
                    throw new PdfException("The placed item could not be removed.");
                PdfiumNative.FPDFPageObj_Destroy(obj);
            }
        }, true, cancellationToken, options);

    private Task EditItem(PdfPageItemRef reference, Action<ItemObjects> edit, bool delete, CancellationToken cancellationToken, PdfItemEditOptions? options)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidatePageIndex(reference.PageIndex);
        return _engine.InvokeAsync(() =>
        {
            ThrowIfClosed();
            if (reference.DocumentId != _itemDocumentId || reference.Revision != _revision)
                throw new PdfException("This placed-item reference is stale or belongs to another document. Read the page's items again.");
            if (PdfiumNative.FPDF_GetSignatureCount(_handle) > 0 && options?.AcknowledgeDigitalSignatures != true)
                throw new PdfException("This PDF has digital signatures. The caller must warn the user and explicitly acknowledge signature changes before editing.");
            if (!Permissions.HasFlag(PdfPermissions.Modify)) throw new PdfRestrictedException();
            var selection = ReadItems(reference.PageIndex).SingleOrDefault(i => i.Item.Reference == reference)
                ?? throw new PdfException("The placed item was not found. Read the page's items again.");
            if (selection.Item.EditError is { } error) throw new PdfException(error);
            if (!delete && selection.Item.OpacityMode == PdfItemOpacityMode.DeleteOnly)
                throw new PdfException("An image without a YRK mark can only be deleted.");

            // All members become one form before moving, rotating or applying a multiplier.
            if (selection.Item.HasYrkMark && selection.Item.OpacityMode == PdfItemOpacityMode.FadeOnly
                && !IsGrouped(selection.Objects[0]))
            {
                MigrateLegacy(reference.PageIndex, selection);
                selection = ReadItems(reference.PageIndex).Single(i => i.Item.Group == selection.Item.Group);
            }
            else
            {
                NormalizeItemPage(reference.PageIndex);
                selection = ReadItems(reference.PageIndex).Single(i => selection.Item.HasYrkMark
                    ? i.Item.Group == selection.Item.Group : i.Item.Reference.Index == reference.Index);
            }
            edit(selection);
            if (PdfiumNative.FPDFPage_GenerateContent(GetPage(reference.PageIndex)) == 0)
                throw new PdfException("The edited page could not be generated.");
            if (!delete && selection.Item.Group is { } group) PrivatizeGroupState(reference.PageIndex, group);
            _revision++;
            return true;
        }, PdfWorkPriority.Interactive, cancellationToken);
    }

    private static void Transform(ItemObjects item, PdfMatrix m)
    {
        foreach (var obj in item.Objects) PdfiumNative.FPDFPageObj_Transform(obj, m.A, m.B, m.C, m.D, m.E, m.F);
    }

    private static IntPtr YrkMarkOf(IntPtr obj)
    {
        IntPtr result = IntPtr.Zero;
        for (uint i = 0; i < PdfiumNative.FPDFPageObj_CountMarks(obj); i++)
        {
            var mark = PdfiumNative.FPDFPageObj_GetMark(obj, i);
            if (PdfiumMarks.GetName(mark) == "YRK") result = mark;
        }
        return result;
    }

    private static bool IsGrouped(IntPtr obj) => PdfiumMarks.GetString(YrkMarkOf(obj), "Format") == "Group"
        && PdfiumNative.FPDFPageObj_GetType(obj) == PdfiumNative.FPDF_PAGEOBJ_FORM;

    private List<ItemObjects> ReadItems(int pageIndex)
    {
        var page = GetPage(pageIndex);
        var (dw, dh) = GetDeviceSize(pageIndex);
        var nested = new List<ItemObjects>();
        var groups = new List<(string? Group, YrkMarkKind? Kind, bool Grouped, bool Fade, List<IntPtr> Objects, int Index)>();
        for (var i = 0; i < PdfiumNative.FPDFPage_CountObjects(page); i++)
        {
            var obj = PdfiumNative.FPDFPage_GetObject(page, i);
            var mark = YrkMarkOf(obj);
            var group = mark == IntPtr.Zero ? null : PdfiumMarks.GetString(mark, "Group");
            YrkMarkKind? kind = Enum.TryParse<YrkMarkKind>(PdfiumMarks.GetString(mark, "Kind"), out var k) && Enum.IsDefined(k) ? k : null;
            if (mark != IntPtr.Zero)
            {
                var existing = groups.FindIndex(g => g.Group == group);
                if (existing >= 0)
                {
                    groups[existing].Objects.Add(obj);
                    if (groups[existing].Kind == YrkMarkKind.StampText && kind == YrkMarkKind.Stamp)
                    {
                        var value = groups[existing];
                        value.Kind = YrkMarkKind.Stamp;
                        groups[existing] = value;
                    }
                    continue;
                }
            }
            else if (!IsImageOccurrence(obj))
            {
                if (PdfiumNative.FPDFPageObj_GetType(obj) == PdfiumNative.FPDF_PAGEOBJ_FORM)
                    ReadNested(obj, PdfMatrix.Identity, 0);
                continue;
            }
            groups.Add((group, kind, IsGrouped(obj), PdfiumMarks.GetString(mark, "Fade") == "1", [obj], i));
        }
        var doc = CurrentCos();
        var pageEdit = new CosPageEdit(doc, doc.GetPages()[pageIndex]);
        var units = PlacedContent.Read(doc, pageEdit);
        // NormalizeItemPage merges the streams before GenerateContent. PDFium does not
        // regenerate BI/sh faithfully, even when they belong to a different item. Refuse
        // the entire page before migration or native mutation (including deletion).
        var pageError = ContentTokenizer.Parse(pageEdit.ReadContent()).Any(o => o.Operator is "BI" or "sh")
            ? "Inline images and shadings cannot be regenerated safely; placed-item editing is unavailable on this page."
            : null;
        var result = new List<ItemObjects>();
        foreach (var group in groups)
        {
            var marked = YrkMarkOf(group.Objects[0]) != IntPtr.Zero;
            var boxes = group.Objects.Select(obj =>
            {
                if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t) == 0)
                    throw new PdfException("The placed item's bounds could not be read.");
                return new PdfRect(l, b, r, t);
            }).ToArray();
            var box = new PdfRect(boxes.Min(b => b.X0), boxes.Min(b => b.Y0), boxes.Max(b => b.X1), boxes.Max(b => b.Y1));
            string? error = null;
            if (marked)
            {
                var selected = units.Where(u => u.Mark?.Group == group.Group).ToList();
                error = selected.FirstOrDefault(u => u.Error is not null)?.Error;
                if (selected.Count == 0 || group.Kind is null) error = "The item's YRK mark or content is unsupported.";
                else if (units.IndexOf(selected[^1]) - units.IndexOf(selected[0]) + 1 != selected.Count)
                    error = "Members of this group are separated by other painted content; editing would change drawing order.";
                else if (group.Grouped && (selected.Count != 1 || group.Objects.Count != 1))
                    error = "A grouped item has multiple occurrences; editing it is ambiguous.";
                else if (group.Grouped)
                {
                    var xobjects = doc.Resolve(doc.GetPages()[pageIndex].Resources) as CosDictionary;
                    var xdict = doc.Resolve(xobjects?["XObject"]) as CosDictionary;
                    var form = doc.Resolve(selected[0].XObject is { } name ? xdict?[name] : null) as CosStream;
                    var transparency = doc.Resolve(form?.Dictionary["Group"]) as CosDictionary;
                    if ((transparency?["S"] as CosName)?.Value != "Transparency"
                        || transparency?["I"] is not CosBoolean { Value: true }
                        || transparency?["K"] is not CosBoolean { Value: false }
                        || (transparency?["CS"] as CosName)?.Value != "DeviceRGB")
                        error = "The YRK form is not a supported isolated transparency group.";
                }
                var kinds = group.Objects.Select(o => PdfiumMarks.GetString(YrkMarkOf(o), "Kind")).Distinct().ToArray();
                if (kinds.Length > 1 && kinds.Any(k => k is not ("Stamp" or "StampText")))
                    error = "This YRK group contains conflicting item kinds.";
            }
            error = pageError ?? error;
            var mode = !marked ? PdfItemOpacityMode.DeleteOnly
                : group.Grouped && !group.Fade ? PdfItemOpacityMode.Group : PdfItemOpacityMode.FadeOnly;
            var opacity = group.Grouped && PdfiumNative.FPDFPageObj_GetFillColor(group.Objects[0], out _, out _, out _, out var alpha) != 0
                ? alpha / 255.0 : 1;
            var bounds = ToNormalized(page, dw, dh, box.X0, box.Y1, box.X1, box.Y0);
            result.Add(new(new(new(_itemDocumentId, pageIndex, _revision, group.Index), bounds, marked, group.Kind, group.Group, opacity, mode, error), group.Objects, box));
        }
        result.AddRange(nested);
        return result;

        void ReadNested(IntPtr form, PdfMatrix parent, int depth)
        {
            if (depth > MaxFormDepth) throw new PdfException("The page contains forms nested too deeply to list safely.");
            if (PdfiumNative.FPDFPageObj_GetMatrix(form, out var fm) == 0)
                throw new PdfException("A nested form's transformation could not be read.");
            var matrix = new PdfMatrix(fm.A, fm.B, fm.C, fm.D, fm.E, fm.F).Then(parent);
            for (uint childIndex = 0; childIndex < PdfiumNative.FPDFFormObj_CountObjects(form); childIndex++)
            {
                var child = PdfiumNative.FPDFFormObj_GetObject(form, childIndex);
                var mark = YrkMarkOf(child);
                if (mark == IntPtr.Zero && PdfiumNative.FPDFPageObj_GetType(child) != PdfiumNative.FPDF_PAGEOBJ_IMAGE)
                {
                    if (PdfiumNative.FPDFPageObj_GetType(child) == PdfiumNative.FPDF_PAGEOBJ_FORM) ReadNested(child, matrix, depth + 1);
                    continue;
                }
                if (PdfiumNative.FPDFPageObj_GetBounds(child, out var l, out var b, out var r, out var t) == 0)
                    throw new PdfException("A nested item's bounds could not be read.");
                var box = PdfRect.Of(matrix, new(l, b, r, t));
                var bounds = ToNormalized(page, dw, dh, box.X0, box.Y1, box.X1, box.Y0);
                var marked = mark != IntPtr.Zero;
                YrkMarkKind? kind = Enum.TryParse<YrkMarkKind>(PdfiumMarks.GetString(mark, "Kind"), out var value) && Enum.IsDefined(value) ? value : null;
                var group = PdfiumMarks.GetString(mark, "Group");
                var mode = !marked ? PdfItemOpacityMode.DeleteOnly : IsGrouped(child) && PdfiumMarks.GetString(mark, "Fade") != "1"
                    ? PdfItemOpacityMode.Group : PdfItemOpacityMode.FadeOnly;
                var opacity = IsGrouped(child) && PdfiumNative.FPDFPageObj_GetFillColor(child, out _, out _, out _, out var a) != 0 ? a / 255.0 : 1;
                var item = new PdfPageItem(new(_itemDocumentId, pageIndex, _revision, -nested.Count - 1), bounds,
                    marked, kind, group, opacity, mode, "This item is nested inside a foreign Form; independent editing is not supported.");
                nested.Add(new(item, [child], box));
            }
        }
    }

    private static bool IsImageOccurrence(IntPtr obj)
    {
        if (PdfiumNative.FPDFPageObj_GetType(obj) == PdfiumNative.FPDF_PAGEOBJ_IMAGE) return true;
        // Unmarked images placed by 1.2.1 have an unmarked group wrapper.
        return PdfiumNative.FPDFPageObj_GetType(obj) == PdfiumNative.FPDF_PAGEOBJ_FORM
            && PdfiumNative.FPDFFormObj_CountObjects(obj) == 1
            && PdfiumNative.FPDFPageObj_GetType(PdfiumNative.FPDFFormObj_GetObject(obj, 0)) == PdfiumNative.FPDF_PAGEOBJ_IMAGE;
    }

    private void MigrateLegacy(int pageIndex, ItemObjects item, bool legacy = true)
    {
        var doc = CurrentCos();
        var update = new CosIncrementalUpdate(doc);
        var page = new CosPageEdit(doc, doc.GetPages()[pageIndex]);
        var units = PlacedContent.Read(doc, page).Where(u => u.Mark?.Group == item.Item.Group).ToList();
        if (units.Count == 0 || units.Any(u => u.Error is not null)) throw new PdfException("The legacy group cannot be isolated safely.");
        var first = units[0];
        var m = first.Matrix;
        var inverse = PlacedGroupBuilder.Inverse(m);
        var mark = new YrkMark(item.Item.Kind!.Value, Guid.ParseExact(item.Item.Group!, "N"));
        var invocation = "q\n" + PlacedGroupBuilder.Matrix(inverse) + PlacedGroupBuilder.Create(update, page,
            units.SelectMany(u => u.Snapshot).ToArray(), page.Resources.Clone(), item.UserBounds, 1, mark, legacy) + "Q\n";
        page.ReplaceContent(PlacedContent.Replace(page.ReadContent(), units, invocation, item.Item.Group));
        page.Commit(update);
        ApplyCos(update.Build().Bytes);
    }
}
