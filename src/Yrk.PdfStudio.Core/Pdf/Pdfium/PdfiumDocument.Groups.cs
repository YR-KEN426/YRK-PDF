using System.Runtime.InteropServices;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

internal sealed partial class PdfiumDocument
{
    private CosDocument CurrentCos()
    {
        ThrowIfClosed();
        if (_formHandle != IntPtr.Zero) PdfiumNative.FORM_ForceToKillFocus(_formHandle);
        var document = CosDocument.Load(PdfiumIo.WriteToBytes(_handle, PdfiumNative.FPDF_INCREMENTAL), OpenPassword);
        if (document.IsLocked) throw new PdfException("The document must be unlocked before editing placed items.");
        if (document.WasReconstructed) throw new PdfException("Placed-item editing requires a valid PDF cross-reference table.");
        return document;
    }

    private void ApplyCos(byte[] bytes)
    {
        // Validate with the actual reader before releasing the working document.
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var probe = PdfiumNative.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)bytes.Length, OpenPassword);
            if (probe == IntPtr.Zero) throw new PdfException("The edited PDF could not be reopened; no changes were applied.");
            PdfiumNative.FPDF_CloseDocument(probe);
        }
        finally { pin.Free(); }

        foreach (var (_, page) in _pageCache) ClosePage(page);
        _pageCache.Clear();
        CloseLayerView();
        ExitForms();
        PdfiumNative.FPDF_CloseDocument(_handle);
        _handle = IntPtr.Zero;
        _stream.Dispose();
        _stream = new MemoryStream(bytes, writable: false);
        Marshal.StructureToPtr(new PdfiumNative.FPDF_FILEACCESS
        {
            m_FileLen = checked((uint)bytes.Length),
            m_GetBlock = Marshal.GetFunctionPointerForDelegate(_getBlock),
        }, _fileAccess, false);
        Load(OpenPassword);
        _revision++;
        _layers = null;
        _pageScales = null;
    }

    private PdfMatrix GroupPlacement(int pageIndex, NormalizedRect bounds, PlacementStyle? style)
    {
        var (a, b, c, d, e, f) = PlacementMatrix(pageIndex, GetPage(pageIndex), bounds, style);
        return new(a, b, c, d, e, f);
    }

    private void AddGroupedImage(int pageIndex, PdfImage image, NormalizedRect bounds, PlacementStyle? style)
    {
        var matrix = GroupPlacement(pageIndex, bounds, style);
        var doc = CurrentCos();
        var update = new CosIncrementalUpdate(doc);
        var edit = new CosPageEdit(doc, doc.GetPages()[pageIndex]);
        var rgb = new byte[checked(image.Width * image.Height * 3)];
        var mask = new byte[checked(image.Width * image.Height)];
        for (int s = 0, p = 0; p < mask.Length; s += 4, p++)
        {
            rgb[p * 3] = image.Pixels[s + 2];
            rgb[p * 3 + 1] = image.Pixels[s + 1];
            rgb[p * 3 + 2] = image.Pixels[s];
            mask[p] = image.Pixels[s + 3]; // Intrinsic alpha only; placement opacity belongs to the group.
        }
        CosDictionary ImageDictionary(string space) => new CosDictionary()
            .Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Image"))
            .Set("Width", new CosInteger(image.Width)).Set("Height", new CosInteger(image.Height))
            .Set("ColorSpace", new CosName(space)).Set("BitsPerComponent", new CosInteger(8));
        var dictionary = ImageDictionary("DeviceRGB");
        if (mask.Any(a => a != 255)) dictionary["SMask"] = update.Add(CosPageEdit.Stream(mask, ImageDictionary("DeviceGray")));
        var imageRef = update.Add(CosPageEdit.Stream(rgb, dictionary));
        var resources = new CosDictionary().Set("XObject", new CosDictionary().Set("Image", imageRef));
        var content = PlacedGroupBuilder.Matrix(matrix) + "/Image Do\n";
        AppendGroup(edit, PlacedGroupBuilder.Create(update, edit, PlacedGroupBuilder.Bytes(content), resources,
            PdfRect.Of(matrix, new(0, 0, 1, 1)), style?.Opacity ?? 1, style?.Mark));
        edit.Commit(update);
        ApplyCos(update.Build().Bytes);
    }

    private void AddGroupedVector(int pageIndex, PdfVectorArt art, NormalizedRect bounds, PlacementStyle? style)
    {
        var unit = GroupPlacement(pageIndex, bounds, style);
        var matrix = new PdfMatrix(1 / art.Width, 0, 0, -1 / art.Height, 0, 1).Then(unit);
        var doc = CurrentCos();
        var update = new CosIncrementalUpdate(doc);
        var edit = new CosPageEdit(doc, doc.GetPages()[pageIndex]);
        var content = new StringBuilder();
        var resources = new CosDictionary();
        var states = new CosDictionary();
        resources["ExtGState"] = states;
        content.Append(PlacedGroupBuilder.Matrix(matrix));
        var i = 0;
        foreach (var path in art.Paths)
        {
            var color = path.Color;
            // Preserve intrinsic artwork alpha; never multiply it by placement opacity.
            var gs = "Color" + ++i;
            states[gs] = update.Add(PlacedGroupBuilder.Alpha(color.A / 255.0));
            content.Append($"q /{gs} gs {F(color.R / 255.0)} {F(color.G / 255.0)} {F(color.B / 255.0)} rg\n");
            foreach (var c in path.Commands)
            {
                content.Append(c.Op switch
                {
                    PdfPathOp.MoveTo => $"{F(c.X1)} {F(c.Y1)} m\n",
                    PdfPathOp.LineTo => $"{F(c.X1)} {F(c.Y1)} l\n",
                    PdfPathOp.CubicTo => $"{F(c.X1)} {F(c.Y1)} {F(c.X2)} {F(c.Y2)} {F(c.X3)} {F(c.Y3)} c\n",
                    PdfPathOp.Close => "h\n",
                    _ => throw new PdfException("Unsupported vector command."),
                });
            }
            content.Append(path.EvenOdd ? "f* Q\n" : "f Q\n");
        }
        AppendGroup(edit, PlacedGroupBuilder.Create(update, edit, PlacedGroupBuilder.Bytes(content.ToString()), resources,
            PdfRect.Of(unit, new(0, 0, 1, 1)), style?.Opacity ?? 1, style?.Mark));
        edit.Commit(update);
        ApplyCos(update.Build().Bytes);
    }

    private static string F(double value) => CosWriter.FormatReal(value);

    private static void AppendGroup(CosPageEdit edit, string invocation) =>
        edit.ReplaceContent([.. "q\n"u8, .. edit.ReadContent(), .. "\nQ\n"u8, .. PlacedGroupBuilder.Bytes(invocation)]);

    private void NormalizeItemPage(int pageIndex)
    {
        var doc = CurrentCos();
        var edit = new CosPageEdit(doc, doc.GetPages()[pageIndex]);
        var update = new CosIncrementalUpdate(doc);
        // PDFium regenerates individual streams. Never let it regenerate one half of a q/Q
        // that crosses stream boundaries: first put the decoded operations in one private stream.
        edit.ReplaceContent(edit.ReadContent());
        edit.Commit(update);
        ApplyCos(update.Build().Bytes);
    }

    private void PrivatizeGroupState(int pageIndex, string group)
    {
        var doc = CurrentCos();
        var edit = new CosPageEdit(doc, doc.GetPages()[pageIndex]);
        var unit = PlacedContent.Read(doc, edit).Single(u => u.Grouped && u.Mark?.Group == group);
        var states = doc.Resolve(edit.Resources["ExtGState"]) as CosDictionary;
        var effective = new CosDictionary();
        // GenerateContent may intern equal native graphics states. Clone the effective state and
        // rebind just this occurrence so even equal-opacity groups keep independent resources.
        foreach (var op in ContentTokenizer.Parse(unit.Snapshot).Where(o => o.Operator == "gs"))
            if (op.Operands.FirstOrDefault() is CosName name && doc.Resolve(states?[name.Value]) is CosDictionary gs)
                foreach (var pair in gs.Entries) effective[pair.Key] = pair.Value;
        var update = new CosIncrementalUpdate(doc);
        var resource = edit.AddResource("ExtGState", "YrkPrivateAlpha", update.Add(effective));
        var replacement = unit with { Replacement = $"q /{resource} gs /{unit.XObject} Do Q\n" };
        edit.ReplaceContent(PlacedContent.Replace(edit.ReadContent(), [replacement], ""));
        edit.Commit(update);
        ApplyCos(update.Build().Bytes);
    }
}
