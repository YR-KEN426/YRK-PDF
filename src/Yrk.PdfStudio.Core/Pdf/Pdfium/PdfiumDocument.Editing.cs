using System.Runtime.InteropServices;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Forms, page editing and saving.</summary>
internal sealed partial class PdfiumDocument
{
    public bool HasForm => _formHandle != IntPtr.Zero;

    public Task<IReadOnlyList<PdfFormField>> GetFormFieldsAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadFormFields(pageIndex), PdfWorkPriority.Background, cancellationToken);
    }

    public Task ApplyFormEditsAsync(IReadOnlyList<PdfFormFieldEdit> edits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var edit in edits)
        {
            ValidatePageIndex(edit.PageIndex);
        }

        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                ApplyFormEdits(edits);
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task AddImageAsync(int pageIndex, PdfImage image, NormalizedRect bounds, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < image.Width * image.Height * 4)
        {
            throw new ArgumentException("The image size does not match its pixel buffer.", nameof(image));
        }

        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                AddImage(pageIndex, image, bounds);
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task AddVectorAsync(int pageIndex, PdfVectorArt art, NormalizedRect bounds, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentNullException.ThrowIfNull(art);
        if (art.Width <= 0 || art.Height <= 0)
        {
            throw new ArgumentException("The artwork has no size.", nameof(art));
        }

        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                AddVector(pageIndex, art, bounds);
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task AddPdfPageAsync(int pageIndex, byte[] pdf, NormalizedRect sourceCrop, NormalizedRect bounds, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentNullException.ThrowIfNull(pdf);
        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                AddPdfPage(pageIndex, pdf, sourceCrop, bounds);
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Save to a new file first; the open document is still being read.", nameof(path));
        }

        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                Save(path);
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    // --- Form environment ------------------------------------------------------------------

    private unsafe void InitForms()
    {
        if (PdfiumNative.FPDF_GetFormType(_handle) == PdfiumNative.FORMTYPE_NONE)
        {
            return;
        }

        _formInfo = (IntPtr)NativeMemory.AllocZeroed(PdfiumNative.FormFillInfoSize);
        Marshal.WriteInt32(_formInfo, 1); // FPDF_FORMFILLINFO.version
        _formHandle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(_handle, _formInfo);
        if (_formHandle == IntPtr.Zero)
        {
            NativeMemory.Free((void*)_formInfo);
            _formInfo = IntPtr.Zero;
        }
    }

    private unsafe void ExitForms()
    {
        if (_formHandle != IntPtr.Zero)
        {
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(_formHandle);
            _formHandle = IntPtr.Zero;
        }

        if (_formInfo != IntPtr.Zero)
        {
            NativeMemory.Free((void*)_formInfo);
            _formInfo = IntPtr.Zero;
        }
    }

    // --- Reading fields --------------------------------------------------------------------

    private IReadOnlyList<PdfFormField> ReadFormFields(int pageIndex)
    {
        if (_formHandle == IntPtr.Zero)
        {
            return [];
        }

        var page = GetPage(pageIndex);
        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        var fields = new List<PdfFormField>();
        var count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        for (var i = 0; i < count; i++)
        {
            var annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                if (PdfiumNative.FPDFAnnot_GetSubtype(annot) != PdfiumNative.FPDF_ANNOT_WIDGET)
                {
                    continue;
                }

                var type = PdfiumNative.FPDFAnnot_GetFormFieldType(_formHandle, annot);
                if (type is < (int)PdfFormFieldType.PushButton or > (int)PdfFormFieldType.Signature
                    || PdfiumNative.FPDFAnnot_GetRect(annot, out var rect) == 0)
                {
                    continue;
                }

                fields.Add(ReadFormField(page, annot, pageIndex, i, (PdfFormFieldType)type, rect, deviceWidth, deviceHeight));
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }

        return fields;
    }

    private PdfFormField ReadFormField(
        IntPtr page, IntPtr annot, int pageIndex, int annotIndex, PdfFormFieldType type,
        PdfiumNative.FS_RECTF rect, int deviceWidth, int deviceHeight)
    {
        var form = _formHandle;
        var flags = PdfiumNative.FPDFAnnot_GetFormFieldFlags(form, annot);
        var options = new List<string>();
        var selectedIndex = -1;
        if (type is PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox)
        {
            var optionCount = PdfiumNative.FPDFAnnot_GetOptionCount(form, annot);
            for (var i = 0; i < optionCount; i++)
            {
                options.Add(ReadUtf16((buffer, length) => PdfiumNative.FPDFAnnot_GetOptionLabel(form, annot, i, buffer, length)));
                if (selectedIndex < 0 && PdfiumNative.FPDFAnnot_IsOptionSelected(form, annot, i) != 0)
                {
                    selectedIndex = i;
                }
            }
        }

        return new PdfFormField(
            pageIndex,
            annotIndex,
            ReadUtf16((buffer, length) => PdfiumNative.FPDFAnnot_GetFormFieldName(form, annot, buffer, length)),
            type,
            ToNormalized(page, deviceWidth, deviceHeight, rect.Left, rect.Top, rect.Right, rect.Bottom),
            ReadUtf16((buffer, length) => PdfiumNative.FPDFAnnot_GetFormFieldValue(form, annot, buffer, length)),
            IsChecked: type is PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton && PdfiumNative.FPDFAnnot_IsChecked(form, annot) != 0,
            IsReadOnly: (flags & PdfiumNative.FPDF_FORMFLAG_READONLY) != 0,
            IsMultiline: type == PdfFormFieldType.Text && (flags & PdfiumNative.FPDF_FORMFLAG_TEXT_MULTILINE) != 0,
            IsPassword: type == PdfFormFieldType.Text && (flags & PdfiumNative.FPDF_FORMFLAG_TEXT_PASSWORD) != 0,
            IsEditableChoice: type == PdfFormFieldType.ComboBox && (flags & PdfiumNative.FPDF_FORMFLAG_CHOICE_EDIT) != 0,
            options,
            selectedIndex,
            PdfiumNative.FPDFAnnot_GetFontSize(form, annot, out var fontSize) != 0 ? fontSize : 0,
            IsRequired: (flags & 2) != 0, // Bit 2 of /Ff: the field must be filled in.
            IsComb: type == PdfFormFieldType.Text && (flags & (1 << 24)) != 0, // Bit 25: one character per box.
            // An image field that already shows a picture draws it with an XObject (Do) in its appearance.
            HasPicture: type == PdfFormFieldType.PushButton
                && ReadUtf16((buffer, length) => PdfiumNative.FPDFAnnot_GetAP(annot, 0, buffer, length)).Contains(" Do", StringComparison.Ordinal));
    }

    // --- Writing fields --------------------------------------------------------------------

    private void ApplyFormEdits(IReadOnlyList<PdfFormFieldEdit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        if (_formHandle == IntPtr.Zero)
        {
            throw new PdfException("This document has no form fields.");
        }

        foreach (var edit in edits)
        {
            var page = GetPage(edit.PageIndex);
            var annot = PdfiumNative.FPDFPage_GetAnnot(page, edit.AnnotIndex);
            if (annot == IntPtr.Zero)
            {
                throw new PdfException($"Form field {edit.AnnotIndex} on page {edit.PageIndex + 1} was not found.");
            }

            try
            {
                if ((PdfiumNative.FPDFAnnot_GetFormFieldFlags(_formHandle, annot) & PdfiumNative.FPDF_FORMFLAG_READONLY) != 0)
                {
                    continue;
                }

                switch ((PdfFormFieldType)PdfiumNative.FPDFAnnot_GetFormFieldType(_formHandle, annot))
                {
                    case PdfFormFieldType.Text:
                        if (edit.Text is not null)
                        {
                            ReplaceText(page, annot, edit.Text);
                        }

                        break;

                    case PdfFormFieldType.CheckBox:
                    case PdfFormFieldType.RadioButton:
                        // Clicking toggles; for radio buttons the form engine clears the rest of the group.
                        if (edit.IsChecked is bool wanted && (PdfiumNative.FPDFAnnot_IsChecked(_formHandle, annot) != 0) != wanted)
                        {
                            Click(page, annot);
                        }

                        break;

                    case PdfFormFieldType.ComboBox:
                    case PdfFormFieldType.ListBox:
                        if (edit.SelectedIndex is int index && index >= 0)
                        {
                            if (PdfiumNative.FORM_SetFocusedAnnot(_formHandle, annot) != 0)
                            {
                                PdfiumNative.FORM_SetIndexSelected(_formHandle, page, index, 1);
                            }
                        }
                        else if (edit.Text is not null)
                        {
                            ReplaceText(page, annot, edit.Text); // Editable combo box with a custom value.
                        }

                        break;
                }
            }
            finally
            {
                // Losing focus commits the value and regenerates the widget appearance.
                PdfiumNative.FORM_ForceToKillFocus(_formHandle);
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }
    }

    private void ReplaceText(IntPtr page, IntPtr annot, string text)
    {
        if (PdfiumNative.FORM_SetFocusedAnnot(_formHandle, annot) == 0)
        {
            return;
        }

        PdfiumNative.FORM_SelectAllText(_formHandle, page);
        PdfiumNative.FORM_ReplaceSelection(_formHandle, page, text);
    }

    private void Click(IntPtr page, IntPtr annot)
    {
        if (PdfiumNative.FPDFAnnot_GetRect(annot, out var rect) == 0)
        {
            return;
        }

        double x = (rect.Left + rect.Right) / 2;
        double y = (rect.Top + rect.Bottom) / 2;
        PdfiumNative.FORM_OnMouseMove(_formHandle, page, 0, x, y);
        PdfiumNative.FORM_OnLButtonDown(_formHandle, page, 0, x, y);
        PdfiumNative.FORM_OnLButtonUp(_formHandle, page, 0, x, y);
    }

    // --- Images ----------------------------------------------------------------------------

    private unsafe void AddImage(int pageIndex, PdfImage image, NormalizedRect bounds)
    {
        var page = GetPage(pageIndex);
        var imageObject = PdfiumNative.FPDFPageObj_NewImageObj(_handle);
        if (imageObject == IntPtr.Zero)
        {
            throw new PdfException("The image could not be created.");
        }

        var inserted = false;
        try
        {
            fixed (byte* pixels = image.Pixels)
            {
                var bitmap = PdfiumNative.FPDFBitmap_CreateEx(image.Width, image.Height, PdfiumNative.FPDFBitmap_BGRA, (IntPtr)pixels, image.Width * 4);
                if (bitmap == IntPtr.Zero)
                {
                    throw new PdfException("Not enough memory to add the image.");
                }

                try
                {
                    if (PdfiumNative.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, imageObject, bitmap) == 0)
                    {
                        throw new PdfException("The image could not be added.");
                    }
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            }

            var (a, b, c, d, e, f) = UnitSquareTo(pageIndex, page, bounds);
            PdfiumNative.FPDFImageObj_SetMatrix(imageObject, a, b, c, d, e, f);

            PdfiumNative.FPDFPage_InsertObject(page, imageObject);
            inserted = true; // The page owns the object from here on.
            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                throw new PdfException("The page content could not be updated.");
            }
        }
        finally
        {
            if (!inserted)
            {
                PdfiumNative.FPDFPageObj_Destroy(imageObject);
            }
        }
    }

    /// <summary>
    /// The matrix that maps the unit square (y up) onto the target rectangle as displayed, so content stays
    /// upright on rotated pages: origin = bottom-left, x axis = bottom edge, y axis = left edge.
    /// </summary>
    private (double A, double B, double C, double D, double E, double F) UnitSquareTo(int pageIndex, IntPtr page, NormalizedRect bounds)
    {
        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        var bottomLeft = DeviceToPage(page, deviceWidth, deviceHeight, bounds.X, bounds.Y + bounds.Height);
        var bottomRight = DeviceToPage(page, deviceWidth, deviceHeight, bounds.X + bounds.Width, bounds.Y + bounds.Height);
        var topLeft = DeviceToPage(page, deviceWidth, deviceHeight, bounds.X, bounds.Y);
        return (bottomRight.X - bottomLeft.X, bottomRight.Y - bottomLeft.Y, topLeft.X - bottomLeft.X, topLeft.Y - bottomLeft.Y, bottomLeft.X, bottomLeft.Y);
    }

    // --- Vector artwork --------------------------------------------------------------------

    private void AddVector(int pageIndex, PdfVectorArt art, NormalizedRect bounds)
    {
        var page = GetPage(pageIndex);
        var (a, b, c, d, e, f) = UnitSquareTo(pageIndex, page, bounds);
        foreach (var shape in art.Paths.Where(p => p.Commands.Count > 0))
        {
            // Built in the unit square (y up), then placed with the same matrix as an image.
            var path = PdfiumNative.FPDFPageObj_CreateNewPath(0, 0);
            if (path == IntPtr.Zero)
            {
                throw new PdfException("The drawing could not be created.");
            }

            var inserted = false;
            try
            {
                float X(double x) => (float)(x / art.Width);
                float Y(double y) => (float)(1 - (y / art.Height));
                foreach (var command in shape.Commands)
                {
                    switch (command.Op)
                    {
                        case PdfPathOp.MoveTo:
                            PdfiumNative.FPDFPath_MoveTo(path, X(command.X1), Y(command.Y1));
                            break;
                        case PdfPathOp.LineTo:
                            PdfiumNative.FPDFPath_LineTo(path, X(command.X1), Y(command.Y1));
                            break;
                        case PdfPathOp.CubicTo:
                            PdfiumNative.FPDFPath_BezierTo(path, X(command.X1), Y(command.Y1), X(command.X2), Y(command.Y2), X(command.X3), Y(command.Y3));
                            break;
                        case PdfPathOp.Close:
                            PdfiumNative.FPDFPath_Close(path);
                            break;
                    }
                }

                var color = shape.Color;
                PdfiumNative.FPDFPageObj_SetFillColor(path, color.R, color.G, color.B, color.A);
                PdfiumNative.FPDFPath_SetDrawMode(path, shape.EvenOdd ? PdfiumNative.FPDF_FILLMODE_ALTERNATE : PdfiumNative.FPDF_FILLMODE_WINDING, 0);
                PdfiumNative.FPDFPageObj_Transform(path, a, b, c, d, e, f);
                PdfiumNative.FPDFPage_InsertObject(page, path);
                inserted = true;
            }
            finally
            {
                if (!inserted)
                {
                    PdfiumNative.FPDFPageObj_Destroy(path);
                }
            }
        }

        if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
        {
            throw new PdfException("The page content could not be updated.");
        }
    }

    private unsafe void AddPdfPage(int pageIndex, byte[] pdf, NormalizedRect sourceCrop, NormalizedRect bounds)
    {
        var page = GetPage(pageIndex);
        fixed (byte* data = pdf)
        {
            // PDFium reads the bytes in place, so they stay pinned until the source is closed.
            var source = PdfiumNative.FPDF_LoadMemDocument64((IntPtr)data, (nuint)pdf.Length, null);
            if (source == IntPtr.Zero)
            {
                throw new PdfException("The signature PDF could not be read.");
            }

            try
            {
                if (PdfiumNative.FPDF_GetPageSizeByIndex(source, 0, out var width, out var height) == 0 || width <= 0 || height <= 0)
                {
                    throw new PdfException("The signature PDF has no page.");
                }

                // Shrink the source page's boxes to the crop: the copied form uses them as its clip (BBox).
                var left = sourceCrop.X * width;
                var right = (sourceCrop.X + sourceCrop.Width) * width;
                var top = height - (sourceCrop.Y * height);
                var bottom = height - ((sourceCrop.Y + sourceCrop.Height) * height);
                var sourcePage = PdfiumNative.FPDF_LoadPage(source, 0);
                if (sourcePage == IntPtr.Zero)
                {
                    throw new PdfException("The signature PDF has no page.");
                }

                PdfiumNative.FPDFPage_SetMediaBox(sourcePage, (float)left, (float)bottom, (float)right, (float)top);
                PdfiumNative.FPDFPage_SetCropBox(sourcePage, (float)left, (float)bottom, (float)right, (float)top);
                PdfiumNative.FPDF_ClosePage(sourcePage);

                var xobject = PdfiumNative.FPDF_NewXObjectFromPage(_handle, source, 0);
                if (xobject == IntPtr.Zero)
                {
                    throw new PdfException("The signature PDF could not be copied.");
                }

                IntPtr form;
                try
                {
                    form = PdfiumNative.FPDF_NewFormObjectFromXObject(xobject);
                }
                finally
                {
                    PdfiumNative.FPDF_CloseXObject(xobject);
                }

                if (form == IntPtr.Zero)
                {
                    throw new PdfException("The signature PDF could not be placed.");
                }

                // PDFium's form starts at the crop box's lower-left corner (0, 0): scale it to the unit square,
                // then map the unit square onto the target.
                double cropWidth = right - left, cropHeight = top - bottom;
                var (a, b, c, d, e, f) = UnitSquareTo(pageIndex, page, bounds);
                PdfiumNative.FPDFPageObj_Transform(form, a / cropWidth, b / cropWidth, c / cropHeight, d / cropHeight, e, f);
                PdfiumNative.FPDFPage_InsertObject(page, form);
                if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
                {
                    throw new PdfException("The page content could not be updated.");
                }
            }
            finally
            {
                PdfiumNative.FPDF_CloseDocument(source);
            }
        }
    }

    private static (double X, double Y) DeviceToPage(IntPtr page, int deviceWidth, int deviceHeight, double x, double y)
    {
        PdfiumNative.FPDF_DeviceToPage(
            page, 0, 0, deviceWidth, deviceHeight, 0,
            (int)Math.Round(x * deviceWidth), (int)Math.Round(y * deviceHeight),
            out var pageX, out var pageY);
        return (pageX, pageY);
    }

    private (int Width, int Height) GetDeviceSize(int pageIndex)
    {
        var size = PageSizes[pageIndex];
        return (Math.Max(1, (int)Math.Round(size.Width * DeviceUnitsPerPoint)), Math.Max(1, (int)Math.Round(size.Height * DeviceUnitsPerPoint)));
    }

    // --- Saving ----------------------------------------------------------------------------

    private void Save(string path)
    {
        ThrowIfClosed();
        if (_formHandle != IntPtr.Zero)
        {
            PdfiumNative.FORM_ForceToKillFocus(_formHandle);
        }

        PdfiumIo.Write(_handle, path, PdfiumNative.FPDF_INCREMENTAL);
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>Reads a PDFium UTF-16LE string through the usual "call once for the size" pattern.</summary>
    private static string ReadUtf16(Func<byte[]?, uint, uint> read)
    {
        var length = read(null, 0);
        if (length <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        read(buffer, length);
        return Encoding.Unicode.GetString(buffer, 0, (int)length - 2);
    }
}
