using System.Globalization;
using System.Runtime.InteropServices;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Text selection and annotations (highlight, notes, ink, shapes).</summary>
internal sealed partial class PdfiumDocument
{
    private const double NoteSize = 20;      // points
    private const double ArrowHeadAngle = 25; // degrees

    // The text page of the page being selected on, kept while the user drags.
    private (int Index, IntPtr Page, IntPtr Text)? _textPageCache;

    public Task<PdfTextSelection?> GetTextSelectionAsync(int pageIndex, NormalizedPoint start, NormalizedPoint end, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => SelectText(pageIndex, start, end), PdfWorkPriority.Interactive, cancellationToken);
    }

    public Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadAnnotations(pageIndex), PdfWorkPriority.Background, cancellationToken);
    }

    public Task<string> AddAnnotationAsync(int pageIndex, PdfAnnotationSpec spec, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentNullException.ThrowIfNull(spec);
        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                return AddAnnotation(pageIndex, spec);
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task SetAnnotationContentsAsync(PdfAnnotationRef annotation, string contents, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(annotation.PageIndex);
        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                WithAnnotation(annotation, (_, annot, _) =>
                {
                    PdfiumNative.FPDFAnnot_SetStringValue(annot, "Contents", contents ?? string.Empty);
                    PdfiumNative.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTimeOffset.Now));
                });
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task RemoveAnnotationAsync(PdfAnnotationRef annotation, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(annotation.PageIndex);
        return _engine.InvokeAsync(
            () =>
            {
                _revision++;
                var page = GetPage(annotation.PageIndex);
                var index = FindAnnotationIndex(page, annotation);
                if (index < 0 || PdfiumNative.FPDFPage_RemoveAnnot(page, index) == 0)
                {
                    throw new PdfException("The annotation could not be removed.");
                }
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    public Task<string> GetTextInAreaAsync(int pageIndex, NormalizedRect area, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadTextInArea(pageIndex, area), PdfWorkPriority.Interactive, cancellationToken);
    }

    // --- Text selection --------------------------------------------------------------------

    private PdfTextSelection? SelectText(int pageIndex, NormalizedPoint start, NormalizedPoint end)
    {
        var page = GetPage(pageIndex);
        var text = GetTextPage(pageIndex, page);
        if (text == IntPtr.Zero)
        {
            return null;
        }

        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        var first = CharIndexAt(text, page, deviceWidth, deviceHeight, start);
        var last = CharIndexAt(text, page, deviceWidth, deviceHeight, end);
        if (first < 0 && last < 0)
        {
            return null;
        }

        if (first < 0)
        {
            first = last;
        }
        else if (last < 0)
        {
            last = first;
        }

        if (first > last)
        {
            (first, last) = (last, first);
        }

        var count = last - first + 1;
        var rects = new List<NormalizedRect>();
        var rectCount = PdfiumNative.FPDFText_CountRects(text, first, count);
        for (var i = 0; i < rectCount; i++)
        {
            if (PdfiumNative.FPDFText_GetRect(text, i, out var left, out var top, out var right, out var bottom) != 0)
            {
                rects.Add(ToNormalized(page, deviceWidth, deviceHeight, left, top, right, bottom));
            }
        }

        var buffer = new ushort[count + 1];
        var written = PdfiumNative.FPDFText_GetText(text, first, count, buffer);
        var selected = new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, Math.Max(0, written - 1))));
        return new PdfTextSelection(pageIndex, first, count, rects, selected);
    }

    /// <summary>The character at a point; a wider search catches clicks in the margin next to a line.</summary>
    private static int CharIndexAt(IntPtr text, IntPtr page, int deviceWidth, int deviceHeight, NormalizedPoint point)
    {
        var (x, y) = DeviceToPage(page, deviceWidth, deviceHeight, point.X, point.Y);
        var index = PdfiumNative.FPDFText_GetCharIndexAtPos(text, x, y, 4, 4);
        if (index < 0)
        {
            index = PdfiumNative.FPDFText_GetCharIndexAtPos(text, x, y, 40, 8);
        }

        return index >= 0 ? index : -1;
    }

    private string ReadTextInArea(int pageIndex, NormalizedRect area)
    {
        var page = GetPage(pageIndex);
        var text = GetTextPage(pageIndex, page);
        if (text == IntPtr.Zero)
        {
            return string.Empty;
        }

        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        var a = DeviceToPage(page, deviceWidth, deviceHeight, area.X, area.Y);
        var b = DeviceToPage(page, deviceWidth, deviceHeight, area.X + area.Width, area.Y + area.Height);
        double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), bottom = Math.Min(a.Y, b.Y), top = Math.Max(a.Y, b.Y);
        var length = PdfiumNative.FPDFText_GetBoundedText(text, left, top, right, bottom, null, 0);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new ushort[length + 1];
        var written = PdfiumNative.FPDFText_GetBoundedText(text, left, top, right, bottom, buffer, buffer.Length);
        return new string(buffer.Take(Math.Max(0, written)).Select(u => (char)u).ToArray()).TrimEnd('\0');
    }

    private IntPtr GetTextPage(int pageIndex, IntPtr page)
    {
        if (_textPageCache is { } cached && cached.Index == pageIndex && cached.Page == page)
        {
            return cached.Text;
        }

        CloseTextPageCache();
        var text = PdfiumNative.FPDFText_LoadPage(page);
        if (text != IntPtr.Zero)
        {
            _textPageCache = (pageIndex, page, text);
        }

        return text;
    }

    /// <summary>Closes the cached text page; must run before its page is closed.</summary>
    private void CloseTextPageCache()
    {
        if (_textPageCache is { } cached)
        {
            PdfiumNative.FPDFText_ClosePage(cached.Text);
            _textPageCache = null;
        }
    }

    // --- Reading ---------------------------------------------------------------------------

    private IReadOnlyList<PdfAnnotation> ReadAnnotations(int pageIndex)
    {
        var page = GetPage(pageIndex);
        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        var result = new List<PdfAnnotation>();
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
                if (PdfiumNative.FPDFAnnot_GetRect(annot, out var rect) == 0)
                {
                    continue;
                }

                var kind = PdfiumNative.FPDFAnnot_GetSubtype(annot);
                PdfColor? color = PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumNative.FPDFANNOT_COLORTYPE_Color, out var r, out var g, out var b, out var a) != 0
                    ? new PdfColor((byte)r, (byte)g, (byte)b, (byte)a)
                    : null;
                result.Add(new PdfAnnotation(
                    pageIndex,
                    i,
                    ReadString(annot, "NM"),
                    Enum.IsDefined(typeof(PdfAnnotationKind), kind) ? (PdfAnnotationKind)kind : PdfAnnotationKind.Unknown,
                    ToNormalized(page, deviceWidth, deviceHeight, rect.Left, rect.Top, rect.Right, rect.Bottom),
                    color,
                    ReadString(annot, "Contents"),
                    ReadString(annot, "T"),
                    ReadString(annot, "M")));
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }

        return result;
    }

    private static string ReadString(IntPtr annot, string key) =>
        ReadUtf16((buffer, length) => PdfiumNative.FPDFAnnot_GetStringValue(annot, key, buffer, length)).Trim('\0');

    /// <summary>Runs <paramref name="action"/> with the referenced annotation open (index -1 when it is gone).</summary>
    private void WithAnnotation(PdfAnnotationRef reference, Action<IntPtr, IntPtr, int> action)
    {
        var page = GetPage(reference.PageIndex);
        var index = FindAnnotationIndex(page, reference);
        if (index < 0)
        {
            throw new PdfException("The annotation no longer exists.");
        }

        var annot = PdfiumNative.FPDFPage_GetAnnot(page, index);
        try
        {
            action(page, annot, index);
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }
    }

    private static int FindAnnotationIndex(IntPtr page, PdfAnnotationRef reference)
    {
        var count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        if (string.IsNullOrEmpty(reference.Id))
        {
            return reference.Index >= 0 && reference.Index < count ? reference.Index : -1;
        }

        for (var i = 0; i < count; i++)
        {
            var annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            try
            {
                if (ReadString(annot, "NM") == reference.Id)
                {
                    return i;
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }

        return -1;
    }

    // --- Creating --------------------------------------------------------------------------

    private string AddAnnotation(int pageIndex, PdfAnnotationSpec spec)
    {
        var page = GetPage(pageIndex);
        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        (double X, double Y) ToPage(NormalizedPoint p) => DeviceToPage(page, deviceWidth, deviceHeight, p.X, p.Y);

        var subtype = spec switch
        {
            PdfTextMarkupSpec { Kind: PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly or PdfAnnotationKind.StrikeOut } m => (int)m.Kind,
            PdfNoteSpec => PdfiumNative.FPDF_ANNOT_TEXT,
            PdfInkSpec or PdfArrowSpec => PdfiumNative.FPDF_ANNOT_INK,
            PdfShapeSpec { Kind: PdfAnnotationKind.Square or PdfAnnotationKind.Circle } s => (int)s.Kind,
            _ => throw new ArgumentException("Unsupported annotation.", nameof(spec)),
        };

        var annot = PdfiumNative.FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero)
        {
            throw new PdfException("The annotation could not be created.");
        }

        var id = Guid.NewGuid().ToString("N");
        try
        {
            var bounds = new Bounds();
            var padding = 0.0;
            switch (spec)
            {
                case PdfTextMarkupSpec markup:
                    foreach (var r in markup.Rects)
                    {
                        // Acrobat's order: upper-left, upper-right, lower-left, lower-right (as displayed).
                        var ul = ToPage(new NormalizedPoint(r.X, r.Y));
                        var ur = ToPage(new NormalizedPoint(r.X + r.Width, r.Y));
                        var ll = ToPage(new NormalizedPoint(r.X, r.Y + r.Height));
                        var lr = ToPage(new NormalizedPoint(r.X + r.Width, r.Y + r.Height));
                        var quad = new PdfiumNative.FS_QUADPOINTSF
                        {
                            X1 = (float)ul.X, Y1 = (float)ul.Y, X2 = (float)ur.X, Y2 = (float)ur.Y,
                            X3 = (float)ll.X, Y3 = (float)ll.Y, X4 = (float)lr.X, Y4 = (float)lr.Y,
                        };
                        PdfiumNative.FPDFAnnot_AppendAttachmentPoints(annot, ref quad);
                        bounds.Add(ul, ur, ll, lr);
                    }

                    break;

                case PdfNoteSpec note:
                    var corner = ToPage(note.Position);
                    bounds.Add(corner, (corner.X + NoteSize, corner.Y - NoteSize));
                    break;

                case PdfInkSpec ink:
                    foreach (var stroke in ink.Strokes.Where(s => s.Count > 0))
                    {
                        AddStroke(annot, stroke.Select(ToPage).ToList(), bounds);
                    }

                    padding = ink.Width + 1;
                    PdfiumNative.FPDFAnnot_SetBorder(annot, 0, 0, (float)ink.Width);
                    break;

                case PdfArrowSpec arrow:
                    var start = ToPage(arrow.Start);
                    var end = ToPage(arrow.End);
                    AddStroke(annot, [start, end], bounds);
                    AddStroke(annot, ArrowHead(start, end, arrow.Width), bounds);
                    padding = arrow.Width + 1;
                    PdfiumNative.FPDFAnnot_SetBorder(annot, 0, 0, (float)arrow.Width);
                    break;

                case PdfShapeSpec shape:
                    var b = shape.Bounds;
                    bounds.Add(ToPage(new NormalizedPoint(b.X, b.Y)), ToPage(new NormalizedPoint(b.X + b.Width, b.Y + b.Height)));
                    PdfiumNative.FPDFAnnot_SetBorder(annot, 0, 0, (float)shape.Width);
                    break;
            }

            if (bounds.IsEmpty)
            {
                throw new ArgumentException("The annotation has no area.", nameof(spec));
            }

            var rect = bounds.ToRect(padding);
            PdfiumNative.FPDFAnnot_SetRect(annot, ref rect);
            PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumNative.FPDFANNOT_COLORTYPE_Color, spec.Color.R, spec.Color.G, spec.Color.B, spec.Color.A);
            PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumNative.FPDF_ANNOT_FLAG_PRINT);
            PdfiumNative.FPDFAnnot_SetStringValue(annot, "NM", id);
            PdfiumNative.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTimeOffset.Now));
            if (!string.IsNullOrEmpty(spec.Author))
            {
                PdfiumNative.FPDFAnnot_SetStringValue(annot, "T", spec.Author);
            }

            if (!string.IsNullOrEmpty(spec.Contents))
            {
                PdfiumNative.FPDFAnnot_SetStringValue(annot, "Contents", spec.Contents);
            }
        }
        catch
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
            var index = FindAnnotationIndex(page, new PdfAnnotationRef(pageIndex, -1, id));
            if (index >= 0)
            {
                PdfiumNative.FPDFPage_RemoveAnnot(page, index);
            }

            throw;
        }

        PdfiumNative.FPDFPage_CloseAnnot(annot);
        GenerateAppearances(page);
        return id;
    }

    private static void AddStroke(IntPtr annot, IReadOnlyList<(double X, double Y)> points, Bounds bounds)
    {
        var native = points.Select(p => new PdfiumNative.FS_POINTF { X = (float)p.X, Y = (float)p.Y }).ToArray();
        PdfiumNative.FPDFAnnot_AddInkStroke(annot, native, (nuint)native.Length);
        bounds.Add([.. points]);
    }

    /// <summary>A "V" at the end of the arrow, opening back towards the start.</summary>
    private static List<(double X, double Y)> ArrowHead((double X, double Y) start, (double X, double Y) end, double width)
    {
        var dx = start.X - end.X;
        var dy = start.Y - end.Y;
        var length = Math.Max(Math.Sqrt((dx * dx) + (dy * dy)), 0.001);
        var headLength = Math.Max(8, width * 4);
        (dx, dy) = (dx / length * headLength, dy / length * headLength);
        var angle = ArrowHeadAngle * Math.PI / 180;
        (double X, double Y) Rotate(double a) => (end.X + (dx * Math.Cos(a)) - (dy * Math.Sin(a)), end.Y + (dx * Math.Sin(a)) + (dy * Math.Cos(a)));
        return [Rotate(angle), end, Rotate(-angle)];
    }

    /// <summary>
    /// PDFium writes an appearance stream for annotations that lack one when it lists a page's
    /// annotations for drawing. Drawing a 1×1 bitmap does that now, so the annotation looks right in
    /// every viewer even if the file is saved before the page is shown again.
    /// </summary>
    private static void GenerateAppearances(IntPtr page)
    {
        var bitmap = PdfiumNative.FPDFBitmap_Create(1, 1, 0);
        if (bitmap == IntPtr.Zero)
        {
            return;
        }

        PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, 1, 1, 0, PdfiumNative.FPDF_ANNOT);
        PdfiumNative.FPDFBitmap_Destroy(bitmap);
    }

    /// <summary>PDF date string, e.g. D:20261017213000+07'00'.</summary>
    private static string PdfDate(DateTimeOffset time)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture, $"D:{time:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }

    /// <summary>Bounding box in PDF page coordinates (y up).</summary>
    private sealed class Bounds
    {
        private double _left = double.MaxValue, _bottom = double.MaxValue, _right = double.MinValue, _top = double.MinValue;

        public bool IsEmpty => _right < _left || _top < _bottom;

        public void Add(params (double X, double Y)[] points)
        {
            foreach (var (x, y) in points)
            {
                _left = Math.Min(_left, x);
                _right = Math.Max(_right, x);
                _bottom = Math.Min(_bottom, y);
                _top = Math.Max(_top, y);
            }
        }

        public PdfiumNative.FS_RECTF ToRect(double padding) => new()
        {
            Left = (float)(_left - padding),
            Bottom = (float)(_bottom - padding),
            Right = (float)(_right + padding),
            Top = (float)(_top + padding),
        };
    }
}
