using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Mark helpers. Like all native calls, use only on the PDFium worker thread.</summary>
internal static class PdfiumMarks
{
    internal static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value + '\0');

    internal static void Add(IntPtr document, IntPtr pageObject, YrkMark? value)
    {
        if (value is null) return;
        var mark = PdfiumNative.FPDFPageObj_AddMark(pageObject, Ascii("YRK"));
        if (mark == IntPtr.Zero) throw new PdfException("The placed item's mark could not be created.");
        Set("Kind", value.Kind.ToString());
        Set("Group", value.Group);
        Set("V", "1");

        void Set(string key, string text)
        {
            if (PdfiumNative.FPDFPageObjMark_SetStringParam(document, pageObject, mark, Ascii(key), Ascii(text)) == 0)
                throw new PdfException("The placed item's mark could not be written.");
        }
    }

    internal static string? GetName(IntPtr mark) => Read((byte[]? buffer, uint size, out uint written) =>
        PdfiumNative.FPDFPageObjMark_GetName(mark, buffer, size, out written));

    internal static string? GetString(IntPtr mark, string key) => Read((byte[]? buffer, uint size, out uint written) =>
        PdfiumNative.FPDFPageObjMark_GetParamStringValue(mark, Ascii(key), buffer, size, out written));

    private delegate int ReadString(byte[]? buffer, uint size, out uint written);

    private static string? Read(ReadString read)
    {
        if (read(null, 0, out var size) == 0 || size < 2 || size % 2 != 0) return null;
        var buffer = new byte[checked((int)size)];
        if (read(buffer, size, out var written) == 0 || written < 2 || written > size || written % 2 != 0) return null;
        return Encoding.Unicode.GetString(buffer, 0, checked((int)written) - 2);
    }
}
