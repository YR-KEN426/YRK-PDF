namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>Page tools: rotate, delete, move, insert and extract.</summary>
internal sealed partial class PdfiumDocument
{
    /// <summary>The native document handle; only valid on the worker thread while the document is open.</summary>
    internal IntPtr Handle
    {
        get
        {
            ThrowIfClosed();
            return _handle;
        }
    }

    public Task RotatePagesAsync(IReadOnlyList<int> pageIndices, int quarterTurns, CancellationToken cancellationToken = default)
    {
        var pages = ValidatePageList(pageIndices);
        return RunStructural(() =>
        {
            foreach (var index in pages)
            {
                var page = GetPage(index);
                var rotation = PdfiumNative.FPDFPage_GetRotation(page);
                PdfiumNative.FPDFPage_SetRotation(page, (((rotation + quarterTurns) % 4) + 4) % 4);
            }
        }, cancellationToken);
    }

    public Task StraightenPageAsync(int pageIndex, double degrees, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return RunStructural(() =>
        {
            // About the centre of the page as displayed; a quarter-turn /Rotate does not change the direction.
            var page = GetPage(pageIndex);
            var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
            var (cx, cy) = DeviceToPage(page, deviceWidth, deviceHeight, 0.5, 0.5);
            var radians = degrees * Math.PI / 180;
            var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
            var matrix = new PdfiumNative.FsMatrix
            {
                A = (float)cos,
                B = (float)sin,
                C = (float)-sin,
                D = (float)cos,
                E = (float)(cx - (cx * cos) + (cy * sin)),
                F = (float)(cy - (cx * sin) - (cy * cos)),
            };
            if (PdfiumNative.FPDFPage_TransFormWithClip(page, ref matrix, IntPtr.Zero) == 0)
            {
                throw new PdfException($"Page {pageIndex + 1} could not be straightened.");
            }
        }, cancellationToken);
    }

    public Task CropPageAsync(int pageIndex, NormalizedRect keep, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return RunStructural(() =>
        {
            var page = GetPage(pageIndex);
            var (deviceWidth, deviceHeight) = GetDeviceSize(pageIndex);
            var a = DeviceToPage(page, deviceWidth, deviceHeight, keep.X, keep.Y);
            var b = DeviceToPage(page, deviceWidth, deviceHeight, keep.X + keep.Width, keep.Y + keep.Height);
            PdfiumNative.FPDFPage_SetCropBox(page, (float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y));
        }, cancellationToken);
    }

    public Task DeletePagesAsync(IReadOnlyList<int> pageIndices, CancellationToken cancellationToken = default)
    {
        var pages = ValidatePageList(pageIndices);
        return RunStructural(() =>
        {
            if (pages.Length >= PageCount)
            {
                throw new PdfException("A PDF must keep at least one page.");
            }

            ClearPageCache();
            foreach (var index in pages.Reverse())
            {
                PdfiumNative.FPDFPage_Delete(_handle, index);
            }
        }, cancellationToken);
    }

    public Task MovePagesAsync(IReadOnlyList<int> pageIndices, int destinationIndex, CancellationToken cancellationToken = default)
    {
        var pages = ValidatePageList(pageIndices);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(destinationIndex, PageCount - pages.Length);
        return RunStructural(() =>
        {
            ClearPageCache();
            if (PdfiumNative.FPDF_MovePages(_handle, pages, (uint)pages.Length, destinationIndex) == 0)
            {
                throw new PdfException("The pages could not be moved.");
            }
        }, cancellationToken);
    }

    public Task InsertPagesAsync(IPdfDocument source, int insertIndex, IReadOnlyList<int>? sourcePageIndices = null, CancellationToken cancellationToken = default)
    {
        if (source is not PdfiumDocument pdfiumSource)
        {
            throw new ArgumentException("The source must be opened by the PDFium engine.", nameof(source));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(insertIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(insertIndex, PageCount);
        var sourcePages = sourcePageIndices?.ToArray();
        return RunStructural(() =>
        {
            ClearPageCache();
            if (PdfiumNative.FPDF_ImportPagesByIndex(_handle, pdfiumSource.Handle, sourcePages, (uint)(sourcePages?.Length ?? 0), insertIndex) == 0)
            {
                throw new PdfException("The pages could not be inserted.");
            }
        }, cancellationToken);
    }

    public Task ExtractPagesAsync(IReadOnlyList<int> pageIndices, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var pages = pageIndices.ToArray(); // Order is kept: callers may reorder while extracting.
        if (pages.Length == 0)
        {
            throw new ArgumentException("Choose at least one page.", nameof(pageIndices));
        }

        foreach (var index in pages)
        {
            ValidatePageIndex(index);
        }

        return _engine.InvokeAsync(
            () =>
            {
                ThrowIfClosed();
                if (_formHandle != IntPtr.Zero)
                {
                    PdfiumNative.FORM_ForceToKillFocus(_formHandle);
                }

                WriteNewDocument(outputPath, target =>
                    PdfiumNative.FPDF_ImportPagesByIndex(target, _handle, pages, (uint)pages.Length, 0) != 0);
                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);
    }

    /// <summary>Creates a new document, lets <paramref name="fill"/> add pages, and saves it to <paramref name="outputPath"/>.</summary>
    internal static void WriteNewDocument(string outputPath, Func<IntPtr, bool> fill)
    {
        var document = PdfiumNative.FPDF_CreateNewDocument();
        if (document == IntPtr.Zero)
        {
            throw new PdfException("A new PDF could not be created.");
        }

        try
        {
            if (!fill(document))
            {
                throw new PdfException("The pages could not be copied.");
            }

            PdfiumIo.WriteReplacing(document, outputPath, PdfiumNative.FPDF_NO_INCREMENTAL);
        }
        finally
        {
            PdfiumNative.FPDF_CloseDocument(document);
        }
    }

    /// <summary>Runs a page-structure change, then refreshes the page count and sizes.</summary>
    private Task RunStructural(Action change, CancellationToken cancellationToken) =>
        _engine.InvokeAsync(
            () =>
            {
                ThrowIfClosed();
                _revision++;
                if (_formHandle != IntPtr.Zero)
                {
                    PdfiumNative.FORM_ForceToKillFocus(_formHandle);
                }

                try
                {
                    change();
                }
                finally
                {
                    // Loaded pages describe the old structure (indices, rotation, size).
                    ClearPageCache();
                    RefreshPageInfo();
                }

                return true;
            },
            PdfWorkPriority.Interactive,
            cancellationToken);

    /// <summary>Distinct, ascending, validated page indices.</summary>
    private int[] ValidatePageList(IReadOnlyList<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(pageIndices);
        var pages = pageIndices.Distinct().Order().ToArray();
        if (pages.Length == 0)
        {
            throw new ArgumentException("Choose at least one page.", nameof(pageIndices));
        }

        foreach (var index in pages)
        {
            ValidatePageIndex(index);
        }

        return pages;
    }

    private void ClearPageCache()
    {
        foreach (var (_, page) in _pageCache)
        {
            ClosePage(page);
        }

        _pageCache.Clear();
    }

    private void RefreshPageInfo()
    {
        var count = Math.Max(0, PdfiumNative.FPDF_GetPageCount(_handle));
        var sizes = new PdfPageSize[count];
        for (var i = 0; i < sizes.Length; i++)
        {
            sizes[i] = PdfiumNative.FPDF_GetPageSizeByIndex(_handle, i, out var width, out var height) != 0 && width > 0 && height > 0
                ? new PdfPageSize(width, height)
                : FallbackPageSize;
        }

        PageSizes = sizes; // One snapshot: PageCount is derived from it.

    }
}
