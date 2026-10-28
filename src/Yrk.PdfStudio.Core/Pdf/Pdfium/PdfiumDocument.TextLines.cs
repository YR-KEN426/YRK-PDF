using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Finding a line of existing text and its formatting, for text editing.</summary>
internal sealed partial class PdfiumDocument
{
    private const int ItalicFlag = 1 << 6;
    private const int ForceBoldFlag = 1 << 18;

    public Task<PdfTextLine?> GetTextLineAtAsync(int pageIndex, NormalizedPoint point, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadTextLine(pageIndex, point), PdfWorkPriority.Interactive, cancellationToken);
    }

    private PdfTextLine? ReadTextLine(int pageIndex, NormalizedPoint point)
    {
        var page = GetPage(pageIndex);
        var text = GetTextPage(pageIndex, page);
        if (text == IntPtr.Zero)
        {
            return null;
        }

        var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
        var hit = CharIndexAt(text, page, deviceWidth, deviceHeight, point);
        if (hit < 0)
        {
            // A click in a table row's padding, a little above or below the letters, still means that line.
            var (x, y) = DeviceToPage(page, deviceWidth, deviceHeight, point.X, point.Y);
            hit = PdfiumNative.FPDFText_GetCharIndexAtPos(text, x, y, 40, 16);
        }

        var count = PdfiumNative.FPDFText_CountChars(text);
        if (hit < 0 || count <= 0)
        {
            return null;
        }

        var buffer = new ushort[count + 1];
        PdfiumNative.FPDFText_GetText(text, 0, count, buffer);
        static bool IsBreak(ushort c) => c is '\r' or '\n';
        while (hit > 0 && IsBreak(buffer[hit]))
        {
            hit--; // A click just below a line can find the line break PDFium adds after it: the line is the one it ends.
        }

        if (IsBreak(buffer[hit]))
        {
            return null;
        }

        // PDFium separates lines with generated CR LF; the line is the run between them.
        var first = hit;
        while (first > 0 && !IsBreak(buffer[first - 1]))
        {
            first--;
        }

        var last = hit;
        while (last + 1 < count && !IsBreak(buffer[last + 1]))
        {
            last++;
        }

        (first, last) = CellAround(text, first, last, hit, EffectiveFontSize(text, hit));
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        for (var i = first; i <= last; i++)
        {
            if (PdfiumNative.FPDFText_GetCharBox(text, i, out var l, out var r, out var b, out var t) != 0 && r > l && t > b)
            {
                left = Math.Min(left, l);
                right = Math.Max(right, r);
                bottom = Math.Min(bottom, b);
                top = Math.Max(top, t);
            }
        }

        if (left > right)
        {
            return null;
        }

        // The same Thai repairs as copying (sara am circle, sara aa, mark order), so the editor gets the words as they read.
        var repaired = RepairedText(text, buffer, count);
        var lineText = (repaired is null
            ? new string(buffer.AsSpan(first, last - first + 1).ToArray().Select(u => (char)u).ToArray())
            : new string(repaired.Where(e => e.Source >= first && e.Source <= last).Select(e => e.C).ToArray())).Trim();
        var fontSize = EffectiveFontSize(text, hit);
        PdfiumNative.FPDFText_GetFillColor(text, hit, out var red, out var green, out var blue, out _);
        var (fontName, flags) = FontInfo(text, hit);
        var weight = PdfiumNative.FPDFText_GetFontWeight(text, hit);

        PdfiumNative.FPDFText_GetCharOrigin(text, first, out var originX, out var originY);
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, originX, originY, out var deviceX, out var deviceY);

        return new PdfTextLine(
            pageIndex,
            lineText,
            ToNormalized(page, deviceWidth, deviceHeight, left, top, right, bottom),
            new NormalizedPoint((double)deviceX / deviceWidth, (double)deviceY / deviceHeight),
            fontSize > 0 ? fontSize : 12,
            new PdfColor((byte)red, (byte)green, (byte)blue),
            fontName,
            weight >= 600 || (flags & ForceBoldFlag) != 0 || fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase),
            (flags & ItalicFlag) != 0 || fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The part of the line around <paramref name="hit"/> that belongs together. PDFium reads a table row as one
    /// line, so a gap wider than about one and a half letters (between columns) ends the part: editing a cell
    /// must not move the other cells of its row.
    /// </summary>
    private static (int First, int Last) CellAround(IntPtr text, int first, int last, int hit, double fontSize)
    {
        var gap = Math.Max(fontSize, 1) * 1.5;
        bool Box(int i, out double l, out double r)
        {
            l = r = 0;
            return PdfiumNative.FPDFText_IsGenerated(text, i) != 1
                && PdfiumNative.FPDFText_GetCharBox(text, i, out l, out r, out _, out _) != 0 && r > l;
        }

        // Start from a character that has a box (the click can land on a generated space).
        var start = hit;
        while (start <= last && !Box(start, out _, out _))
        {
            start++;
        }

        if (start > last)
        {
            return (first, last);
        }

        Box(start, out var edgeLeft, out var edgeRight);
        var cellFirst = start;
        for (var i = start - 1; i >= first; i--)
        {
            if (Box(i, out var l, out var r))
            {
                if (edgeLeft - r > gap)
                {
                    break;
                }

                cellFirst = i;
                edgeLeft = Math.Min(edgeLeft, l);
            }
        }

        var cellLast = start;
        for (var i = start + 1; i <= last; i++)
        {
            if (Box(i, out var l, out var r))
            {
                if (l - edgeRight > gap)
                {
                    break;
                }

                cellLast = i;
                edgeRight = Math.Max(edgeRight, r);
            }
        }

        return (cellFirst, cellLast);
    }

    /// <summary>
    /// The font size as it appears on the page: the size set in the content times the text matrix and page
    /// scaling. Many PDFs (browsers, printers, converters) set size 1 and scale with the matrix, or draw at
    /// 0.75 pixels per point, so the content's own size alone can be far too small or too large.
    /// </summary>
    internal static double EffectiveFontSize(IntPtr text, int index)
    {
        var size = PdfiumNative.FPDFText_GetFontSize(text, index);
        if (PdfiumNative.FPDFText_GetMatrix(text, index, out var m) != 0)
        {
            var scale = Math.Sqrt((m.C * (double)m.C) + (m.D * (double)m.D)); // Length of the matrix's vertical axis.
            if (scale > 1e-6 && double.IsFinite(scale))
            {
                size *= scale;
            }
        }

        return size;
    }

    private static (string Name, int Flags) FontInfo(IntPtr text, int index)
    {
        var length = PdfiumNative.FPDFText_GetFontInfo(text, index, null, 0, out var flags);
        if (length == 0)
        {
            return (string.Empty, flags);
        }

        var bytes = new byte[length];
        PdfiumNative.FPDFText_GetFontInfo(text, index, bytes, length, out flags);
        var name = Encoding.UTF8.GetString(bytes).TrimEnd('\0');

        // Subset fonts are named like "ABCDEF+Sarabun-Bold".
        var plus = name.IndexOf('+');
        if (plus == 6 && name[..6].All(char.IsAsciiLetterUpper))
        {
            name = name[(plus + 1)..];
        }

        return (name, flags);
    }
}
