using System.Runtime.InteropServices;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>
/// P/Invoke declarations for the subset of the PDFium C API we use (public/fpdfview.h, fpdf_text.h, fpdf_doc.h).
/// Note: C <c>unsigned long</c> is 32-bit on Windows, hence <see cref="uint"/>.
/// PDFium is not thread-safe: every call must happen on <see cref="PdfiumEngine"/>'s worker thread.
/// </summary>
internal static class PdfiumNative
{
    private const string Library = "pdfium";

    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_UNKNOWN = 1;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_GRAYSCALE = 0x08;
    public const int FPDF_PRINTING = 0x800;

    public const uint FPDF_MATCHCASE = 0x01;
    public const uint FPDF_MATCHWHOLEWORD = 0x02;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetBlockCallback(IntPtr param, uint position, IntPtr buffer, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public uint m_FileLen;
        public IntPtr m_GetBlock;
        public IntPtr m_Param;
    }

    // --- Library / document ---------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_DestroyLibrary();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadCustomDocument(
        IntPtr fileAccess,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetLastError();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageSizeByIndex(IntPtr document, int pageIndex, out double width, out double height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetMetaText(
        IntPtr document,
        [MarshalAs(UnmanagedType.LPStr)] string tag,
        byte[]? buffer,
        uint bufferLength);

    // --- Pages / rendering ----------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // --- Page objects (fpdf_edit.h), read for snapping and for exporting --------------------------

    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_SHADING = 4;
    public const int FPDF_PAGEOBJ_FORM = 5;
    public const int FPDF_SEGMENT_LINETO = 0;
    public const int FPDF_SEGMENT_BEZIERTO = 1;
    public const int FPDF_SEGMENT_MOVETO = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FsMatrix
    {
        public float A;
        public float B;
        public float C;
        public float D;
        public float E;
        public float F;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_CountObjects(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPageObj_GetType(IntPtr pageObject);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPageObj_GetMatrix(IntPtr pageObject, out FsMatrix matrix);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFFormObj_CountObjects(IntPtr formObject);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFFormObj_GetObject(IntPtr formObject, uint index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPath_CountSegments(IntPtr path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFPath_GetPathSegment(IntPtr path, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPathSegment_GetPoint(IntPtr segment, out float x, out float y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPathSegment_GetType(IntPtr segment);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPathSegment_GetClose(IntPtr segment);

    /// <summary><paramref name="fillMode"/>: 0 none, 1 even-odd, 2 winding; <paramref name="stroke"/>: nonzero when stroked.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPath_GetDrawMode(IntPtr path, out int fillMode, out int stroke);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPageObj_GetStrokeColor(IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPageObj_GetFillColor(IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_PageToDevice(
        IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, out int deviceX, out int deviceY);

    // --- Text / search --------------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFText_LoadPage(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFText_ClosePage(IntPtr textPage);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_CountChars(IntPtr textPage);

    /// <summary>Writes <paramref name="count"/> UTF-16 code units plus a terminator; returns units written including it.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, [Out] ushort[] result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);

    /// <summary>1 for a space or line break PDFium added (not in the PDF's text), 0 otherwise, -1 on error.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_IsGenerated(IntPtr textPage, int index);

    /// <summary>The font size set in the content (Tf), before the text matrix and page scaling.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern double FPDFText_GetFontSize(IntPtr textPage, int index);

    /// <summary>A character's text matrix combined with the page's transformation (no font size).</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetMatrix(IntPtr textPage, int index, out FsMatrix matrix);

    /// <summary>Font name (UTF-8, terminated); returns the bytes needed. <paramref name="flags"/> are the font descriptor flags.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFText_GetFontInfo(IntPtr textPage, int index, [Out] byte[]? buffer, uint length, out int flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetFontWeight(IntPtr textPage, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetFillColor(IntPtr textPage, int index, out uint r, out uint g, out uint b, out uint a);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharOrigin(IntPtr textPage, int index, out double x, out double y);

    /// <summary>Text inside a rectangle in page coordinates; returns UTF-16 units written (no terminator) or needed.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetBoundedText(IntPtr textPage, double left, double top, double right, double bottom, [Out] ushort[]? buffer, int bufferLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFText_FindStart(
        IntPtr textPage,
        [MarshalAs(UnmanagedType.LPWStr)] string findWhat,
        uint flags,
        int startIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_FindNext(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetSchResultIndex(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetSchCount(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFText_FindClose(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetRect(
        IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom);

    // --- Forms (fpdf_formfill.h) -----------------------------------------------------------

    public const int FORMTYPE_NONE = 0;

    /// <summary>
    /// Bytes reserved for FPDF_FORMFILLINFO. Version 1 only needs ~17 pointers; the rest stays zeroed
    /// so every optional callback PDFium checks for is null (which also keeps form JavaScript disabled).
    /// </summary>
    public const int FormFillInfoSize = 1024;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetFormType(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_FFLDraw(
        IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FORM_SetFocusedAnnot(IntPtr form, IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FORM_ForceToKillFocus(IntPtr form);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FORM_SelectAllText(IntPtr form, IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_ReplaceSelection(IntPtr form, IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FORM_SetIndexSelected(IntPtr form, IntPtr page, int index, int selected);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FORM_OnMouseMove(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FORM_OnLButtonDown(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FORM_OnLButtonUp(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);

    // --- Annotations (fpdf_annot.h) --------------------------------------------------------

    public const int FPDF_ANNOT_WIDGET = 20;

    public const int FPDF_FORMFLAG_READONLY = 1 << 0;
    public const int FPDF_FORMFLAG_TEXT_MULTILINE = 1 << 12;
    public const int FPDF_FORMFLAG_TEXT_PASSWORD = 1 << 13;
    public const int FPDF_FORMFLAG_CHOICE_EDIT = 1 << 18;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_GetAnnotCount(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_CloseAnnot(IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetSubtype(IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetRect(IntPtr annot, out FS_RECTF rect);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetFormFieldType(IntPtr form, IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetFormFieldFlags(IntPtr form, IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAnnot_GetFormFieldName(IntPtr form, IntPtr annot, byte[]? buffer, uint bufferLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAnnot_GetFormFieldValue(IntPtr form, IntPtr annot, byte[]? buffer, uint bufferLength);

    /// <summary>The content of the annotation's appearance stream (<paramref name="mode"/> 0: normal), as UTF-16.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAnnot_GetAP(IntPtr annot, int mode, byte[]? buffer, uint bufferLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetOptionCount(IntPtr form, IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAnnot_GetOptionLabel(IntPtr form, IntPtr annot, int index, byte[]? buffer, uint bufferLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_IsOptionSelected(IntPtr form, IntPtr annot, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_IsChecked(IntPtr form, IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetFontSize(IntPtr form, IntPtr annot, out float size);

    // --- Annotation editing (fpdf_annot.h) --------------------------------------------------

    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_LINK = 2;
    public const int FPDF_ANNOT_SQUARE = 5;
    public const int FPDF_ANNOT_CIRCLE = 6;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_UNDERLINE = 10;
    public const int FPDF_ANNOT_SQUIGGLY = 11;
    public const int FPDF_ANNOT_STRIKEOUT = 12;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_POPUP = 16;

    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const int FPDFANNOT_COLORTYPE_InteriorColor = 1;

    public const int FPDF_ANNOT_FLAG_HIDDEN = 1 << 1;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_POINTF
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF
    {
        public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, out uint r, out uint g, out uint b, out uint a);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref FS_QUADPOINTSF quadPoints);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF[] points, nuint pointCount);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetFlags(IntPtr annot);

    // "unsigned long" is 32 bits on Windows.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAnnot_GetInkListCount(IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAnnot_GetInkListPath(IntPtr annot, uint pathIndex, [Out] FS_POINTF[]? buffer, uint length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_RemoveInkList(IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetStringValue(
        IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key,
        [MarshalAs(UnmanagedType.LPWStr)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAnnot_GetStringValue(
        IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key,
        byte[]? buffer,
        uint bufferLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);

    // --- Page editing (fpdf_edit.h) --------------------------------------------------------

    public const int FPDFBitmap_BGRA = 4;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPageObj_Destroy(IntPtr pageObject);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFImageObj_SetBitmap(IntPtr pages, int count, IntPtr imageObject, IntPtr bitmap);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFImageObj_SetMatrix(IntPtr imageObject, double a, double b, double c, double d, double e, double f);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

    public const int FPDF_FILLMODE_ALTERNATE = 1;
    public const int FPDF_FILLMODE_WINDING = 2;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPath_MoveTo(IntPtr path, float x, float y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPath_Close(IntPtr path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPStr)] string? password);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_NewXObjectFromPage(IntPtr destination, IntPtr source, int sourcePageIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseXObject(IntPtr xobject);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_NewFormObjectFromXObject(IntPtr xobject);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_SetMediaBox(IntPtr page, float left, float bottom, float right, float top);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_GenerateContent(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_DeviceToPage(
        IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, out double pageX, out double pageY);

    // --- Page structure (fpdf_edit.h, fpdf_ppo.h) ---------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_CreateNewDocument();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFPage_New(IntPtr document, int pageIndex, double width, double height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_Delete(IntPtr document, int pageIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_GetRotation(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_MovePages(IntPtr document, int[] pageIndices, uint count, int destinationPageIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_ImportPagesByIndex(IntPtr destination, IntPtr source, int[]? pageIndices, uint count, int index);

    // --- Image objects (fpdf_edit.h) -----------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFImageObj_LoadJpegFileInline(IntPtr pages, int count, IntPtr imageObject, IntPtr fileAccess);

    // --- Saving (fpdf_save.h) ---------------------------------------------------------------

    public const uint FPDF_INCREMENTAL = 1;
    public const uint FPDF_NO_INCREMENTAL = 2;
    public const uint FPDF_REMOVE_SECURITY = 3;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetSecurityHandlerRevision(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetDocPermissions(IntPtr document);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteBlockCallback(IntPtr fileWrite, IntPtr data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int version;
        public IntPtr WriteBlock;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_SaveAsCopy(IntPtr document, IntPtr fileWrite, uint flags);

    // --- Outline (bookmarks) --------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint bufferLength);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr destination);
}
