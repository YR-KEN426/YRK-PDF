using System.Runtime.InteropServices;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>File input and output helpers for PDFium. Everything here runs on the worker thread.</summary>
internal static class PdfiumIo
{
    /// <summary>Writes a document into memory (for a rendering copy, for example).</summary>
    public static byte[] WriteToBytes(IntPtr document, uint flags)
    {
        using var output = new MemoryStream();
        WriteTo(document, output, flags);
        return output.ToArray();
    }

    /// <summary>Writes a document to a new file (fails if it exists). A partial file is deleted on failure.</summary>
    public static void Write(IntPtr document, string path, uint flags)
    {
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var saved = false;
        try
        {
            WriteTo(document, output, flags);
            output.Flush(flushToDisk: true);
            saved = true;
        }
        finally
        {
            output.Dispose();
            if (!saved)
            {
                File.Delete(path);
            }
        }
    }

    private static unsafe void WriteTo(IntPtr document, Stream output, uint flags)
    {
        Exception? writeError = null;
        PdfiumNative.WriteBlockCallback write = (_, data, size) =>
        {
            try
            {
                output.Write(new ReadOnlySpan<byte>((void*)data, checked((int)size)));
                return 1;
            }
            catch (Exception ex)
            {
                writeError = ex;
                return 0; // Never let exceptions cross into native code.
            }
        };

        var fileWrite = Marshal.AllocHGlobal(Marshal.SizeOf<PdfiumNative.FPDF_FILEWRITE>());
        try
        {
            Marshal.StructureToPtr(
                new PdfiumNative.FPDF_FILEWRITE { version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(write) },
                fileWrite,
                fDeleteOld: false);

            if (PdfiumNative.FPDF_SaveAsCopy(document, fileWrite, flags) == 0 || writeError is not null)
            {
                throw writeError is IOException io
                    ? (Exception)io
                    : new PdfException("The document could not be saved.", writeError ?? new IOException("PDFium reported a save failure."));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fileWrite);
            GC.KeepAlive(write);
        }
    }

    /// <summary>Writes to a temporary file next to <paramref name="path"/>, then replaces it in one step.</summary>
    public static void WriteReplacing(IntPtr document, string path, uint flags)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(fullPath)!, $"~{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.yrk-saving");
        Write(document, temp, flags);
        try
        {
            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// A FPDF_FILEACCESS over an in-memory buffer (used to hand JPEG data to PDFium).
    /// Keep the instance alive for as long as PDFium may read from it.
    /// </summary>
    public sealed class MemoryFileAccess : IDisposable
    {
        private readonly byte[] _data;
        private readonly PdfiumNative.GetBlockCallback _getBlock;

        public MemoryFileAccess(byte[] data)
        {
            _data = data;
            _getBlock = ReadBlock;
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PdfiumNative.FPDF_FILEACCESS>());
            Marshal.StructureToPtr(
                new PdfiumNative.FPDF_FILEACCESS
                {
                    m_FileLen = (uint)data.Length,
                    m_GetBlock = Marshal.GetFunctionPointerForDelegate(_getBlock),
                    m_Param = IntPtr.Zero,
                },
                Pointer,
                fDeleteOld: false);
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            GC.KeepAlive(_getBlock);
        }

        private int ReadBlock(IntPtr param, uint position, IntPtr buffer, uint size)
        {
            if ((ulong)position + size > (ulong)_data.Length)
            {
                return 0;
            }

            Marshal.Copy(_data, (int)position, buffer, (int)size);
            return 1;
        }
    }
}
