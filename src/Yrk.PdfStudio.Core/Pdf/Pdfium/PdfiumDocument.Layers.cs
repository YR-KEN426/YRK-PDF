using System.Runtime.InteropServices;
using Yrk.PdfStudio.Core.Pdf.Layers;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>
/// Layers (optional content). PDFium has no API to switch layers, so hidden layers are shown by rendering
/// from a copy of the document whose default layer states are changed. The copy is made in memory from
/// the current state (including unsaved edits) and rebuilt after every change to the document.
/// </summary>
internal sealed partial class PdfiumDocument
{
    private int _revision;
    private string? _password;
    private HashSet<string>? _hiddenLayers; // null: the document's own default layer states
    private IReadOnlyList<PdfLayer>? _layers;
    private LayerView? _layerView;

    public Task<IReadOnlyList<PdfLayer>> GetLayersAsync(CancellationToken cancellationToken = default) =>
        _engine.InvokeAsync(() => _layers ??= PdfLayerReader.Read(ReadFileBytes()), PdfWorkPriority.Background, cancellationToken);

    private IReadOnlyList<IReadOnlyList<Measure.PdfPageScale>>? _pageScales;

    public Task<IReadOnlyList<Measure.PdfPageScale>> GetPageScalesAsync(int pageIndex, CancellationToken cancellationToken = default) =>
        _engine.InvokeAsync(
            () =>
            {
                _pageScales ??= Measure.PdfScaleReader.Read(ReadFileBytes());
                return pageIndex >= 0 && pageIndex < _pageScales.Count ? _pageScales[pageIndex] : (IReadOnlyList<Measure.PdfPageScale>)[];
            },
            PdfWorkPriority.Background,
            cancellationToken);

    public Task SetHiddenLayersAsync(IReadOnlyCollection<string>? layerIds, CancellationToken cancellationToken = default)
    {

        var hidden = layerIds?.ToHashSet(StringComparer.Ordinal);
        return _engine.InvokeAsync(
            () =>
            {
                _hiddenLayers = hidden;
                CloseLayerView();
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    /// <summary>Renders from the layer copy; null when it cannot be made (then all layers are shown).</summary>
    private PdfBitmap? RenderWithLayers(int pageIndex, int width, int height, PdfPixelRegion? region, int flags)
    {
        var view = LayerViewHandle();
        if (view == IntPtr.Zero)
        {
            return null;
        }

        var page = PdfiumNative.FPDF_LoadPage(view, pageIndex);
        if (page == IntPtr.Zero)
        {
            return null;
        }

        var (x, y, bitmapWidth, bitmapHeight) = region is { } r ? (r.X, r.Y, r.Width, r.Height) : (0, 0, width, height);
        var bitmap = PdfiumNative.FPDFBitmap_Create(bitmapWidth, bitmapHeight, alpha: 0);
        try
        {
            if (bitmap == IntPtr.Zero)
            {
                throw new PdfException("Not enough memory to render the page.");
            }

            PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, bitmapWidth, bitmapHeight, 0xFFFFFFFF);
            PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, -x, -y, width, height, rotate: 0, flags);
            var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            var pixels = new byte[stride * bitmapHeight];
            Marshal.Copy(PdfiumNative.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
            return new PdfBitmap(bitmapWidth, bitmapHeight, stride, pixels);
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }

            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private IntPtr LayerViewHandle()
    {
        if (_layerView is { } current && current.Revision == _revision)
        {
            return current.Handle;
        }

        CloseLayerView();
        try
        {
            if (_formHandle != IntPtr.Zero)
            {
                PdfiumNative.FORM_ForceToKillFocus(_formHandle);
            }

            var bytes = PdfLayerReader.WithVisibility(PdfiumIo.WriteToBytes(_handle, PdfiumNative.FPDF_INCREMENTAL), _hiddenLayers!);
            var access = new PdfiumIo.MemoryFileAccess(bytes);
            var handle = PdfiumNative.FPDF_LoadCustomDocument(access.Pointer, _password);
            if (handle == IntPtr.Zero)
            {
                access.Dispose();
                return IntPtr.Zero;
            }

            _layerView = new LayerView(handle, access, _revision);
            return handle;
        }
        catch (PdfException)
        {
            return IntPtr.Zero; // For example an encrypted document: show all layers rather than fail.
        }
    }

    private void CloseLayerView()
    {
        if (_layerView is { } view)
        {
            PdfiumNative.FPDF_CloseDocument(view.Handle);
            view.Access.Dispose();
            _layerView = null;
        }
    }

    private byte[] ReadFileBytes()
    {
        var bytes = new byte[_stream.Length];
        _stream.Position = 0;
        _stream.ReadExactly(bytes);
        return bytes;
    }

    private sealed record LayerView(IntPtr Handle, PdfiumIo.MemoryFileAccess Access, int Revision);
}
