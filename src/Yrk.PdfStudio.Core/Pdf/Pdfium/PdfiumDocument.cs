using System.Runtime.InteropServices;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Pdfium;

/// <summary>
/// A document opened by PDFium. Public methods may be called from any thread; the private
/// methods they queue run on the engine thread and are the only code that touches native handles.
/// </summary>
internal sealed partial class PdfiumDocument : IPdfDocument
{
    private const int PageCacheSize = 8;
    private const int MaxBookmarkDepth = 32;
    private const int MaxBookmarks = 10_000;

    // Device size used to convert page coordinates to page-relative fractions (hundredths of a point).
    private const double DeviceUnitsPerPoint = 100;

    private static readonly PdfPageSize FallbackPageSize = new(612, 792);

    private readonly PdfiumEngine _engine;
    private readonly FileStream _stream;
    private readonly PdfiumNative.GetBlockCallback _getBlock; // Must stay alive while PDFium holds the pointer.
    private readonly IntPtr _fileAccess;
    private readonly LinkedList<(int Index, IntPtr Page)> _pageCache = new();
    private IntPtr _handle;
    private IntPtr _formHandle;
    private IntPtr _formInfo;
    private bool _closed;

    private PdfiumDocument(PdfiumEngine engine, string filePath, FileStream stream)
    {
        _engine = engine;
        _stream = stream;
        FilePath = filePath;
        _getBlock = ReadBlock;
        _fileAccess = Marshal.AllocHGlobal(Marshal.SizeOf<PdfiumNative.FPDF_FILEACCESS>());
        Marshal.StructureToPtr(
            new PdfiumNative.FPDF_FILEACCESS
            {
                m_FileLen = (uint)stream.Length,
                m_GetBlock = Marshal.GetFunctionPointerForDelegate(_getBlock),
                m_Param = IntPtr.Zero,
            },
            _fileAccess,
            fDeleteOld: false);
    }

    public string FilePath { get; }

    public string? Title { get; private set; }

    /// <summary>Derived from <see cref="PageSizes"/>, which is replaced as one snapshot after page changes.</summary>
    public int PageCount => PageSizes.Count;

    public IReadOnlyList<PdfPageSize> PageSizes { get; private set; } = [];

    /// <summary>Runs on the engine thread.</summary>
    internal static PdfiumDocument Open(PdfiumEngine engine, string filePath, string? password)
    {
        // Streaming through a callback (instead of FPDF_LoadDocument) keeps memory low and avoids
        // PDFium's narrow-string file paths, which break on Thai and other non-ASCII folder names.
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        if (stream.Length > uint.MaxValue)
        {
            stream.Dispose();
            throw new PdfException("Files larger than 4 GB are not supported yet.");
        }

        var document = new PdfiumDocument(engine, filePath, stream);
        try
        {
            document.Load(password);
            return document;
        }
        catch
        {
            document.ReleaseNative();
            throw;
        }
    }

    public Task<PdfBitmap> RenderPageAsync(
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        PdfWorkPriority priority = PdfWorkPriority.Interactive,
        CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        return _engine.InvokeAsync(() => Render(pageIndex, pixelWidth, pixelHeight, null), priority, cancellationToken);
    }

    public Task<PdfBitmap> RenderPageRegionAsync(
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        PdfPixelRegion region,
        PdfWorkPriority priority = PdfWorkPriority.Interactive,
        CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.Height);
        return _engine.InvokeAsync(() => Render(pageIndex, pixelWidth, pixelHeight, region), priority, cancellationToken);
    }

    public Task<PdfBitmap> RenderPageForPrintAsync(
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        bool grayscale,
        bool comments,
        CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        var flags = PdfiumNative.FPDF_PRINTING
            | (comments ? PdfiumNative.FPDF_ANNOT : 0)
            | (grayscale ? PdfiumNative.FPDF_GRAYSCALE : 0);
        return _engine.InvokeAsync(() => Render(pageIndex, pixelWidth, pixelHeight, null, flags), PdfWorkPriority.Interactive, cancellationToken);
    }

    public Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken cancellationToken = default) =>
        _engine.InvokeAsync(ReadOutline, PdfWorkPriority.Background, cancellationToken);

    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        return _engine.InvokeAsync(() => ReadPageText(pageIndex), PdfWorkPriority.Background, cancellationToken);
    }

    public Task<IReadOnlyList<PdfTextMatch>> FindTextAsync(
        int pageIndex,
        string query,
        PdfSearchOptions options,
        PdfWorkPriority priority = PdfWorkPriority.Background,
        CancellationToken cancellationToken = default)
    {
        ValidatePageIndex(pageIndex);
        ArgumentException.ThrowIfNullOrEmpty(query);
        return _engine.InvokeAsync(() => Find(pageIndex, query, options), priority, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _engine.InvokeAsync(
                () =>
                {
                    ReleaseNative();
                    return true;
                },
                PdfWorkPriority.Interactive,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The engine already stopped and released every open document.
        }
    }

    /// <summary>Runs on the engine thread. Safe to call more than once.</summary>
    internal void ReleaseNative()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        foreach (var (_, page) in _pageCache)
        {
            ClosePage(page);
        }

        _pageCache.Clear();
        CloseLayerView();
        ExitForms();
        if (_handle != IntPtr.Zero)
        {
            PdfiumNative.FPDF_CloseDocument(_handle);
            _handle = IntPtr.Zero;
        }

        Marshal.FreeHGlobal(_fileAccess);
        _stream.Dispose();
        _engine.OnDocumentClosed(this);
    }

    private void Load(string? password)
    {
        _password = password;
        _handle = PdfiumNative.FPDF_LoadCustomDocument(_fileAccess, password);
        if (_handle == IntPtr.Zero)
        {
            var error = PdfiumNative.FPDF_GetLastError();
            throw error switch
            {
                PdfiumNative.FPDF_ERR_PASSWORD => new PdfPasswordRequiredException(passwordWasWrong: password is not null),
                PdfiumNative.FPDF_ERR_FILE => new PdfException("The file could not be read."),
                PdfiumNative.FPDF_ERR_FORMAT => new PdfException("The file is not a PDF or is damaged."),
                PdfiumNative.FPDF_ERR_SECURITY => new PdfException("The document uses an unsupported security handler."),
                _ => new PdfException($"The document could not be opened (PDFium error {error})."),
            };
        }

        RefreshPageInfo();
        Title = ReadMetaText("Title");
        IsEncrypted = PdfiumNative.FPDF_GetSecurityHandlerRevision(_handle) >= 0;
        Permissions = IsEncrypted
            ? (Security.PdfPermissions)PdfiumNative.FPDF_GetDocPermissions(_handle) & Security.PdfPermissions.All
            : Security.PdfPermissions.All;
        InitForms();
    }

    public bool IsEncrypted { get; private set; }

    public Security.PdfPermissions Permissions { get; private set; }

    public Task<byte[]> SaveDecryptedAsync(CancellationToken cancellationToken = default) =>
        _engine.InvokeAsync(
            () =>
            {
                ThrowIfClosed();
                return PdfiumIo.WriteToBytes(_handle, PdfiumNative.FPDF_REMOVE_SECURITY);
            },
            PdfWorkPriority.Interactive,
            cancellationToken);

    /// <summary>
    /// Renders the page at <paramref name="width"/> × <paramref name="height"/> pixels, or only
    /// <paramref name="region"/> of it: the page is drawn at full size, offset so the region lands in a
    /// small bitmap. This is how very high zoom stays sharp without huge bitmaps.
    /// </summary>
    private PdfBitmap Render(int pageIndex, int width, int height, PdfPixelRegion? region, int flags = PdfiumNative.FPDF_ANNOT)
    {
        if (_hiddenLayers is not null && RenderWithLayers(pageIndex, width, height, region, flags) is { } withLayers)
        {
            return withLayers;
        }

        var page = GetPage(pageIndex);
        var (x, y, bitmapWidth, bitmapHeight) = region is { } r ? (r.X, r.Y, r.Width, r.Height) : (0, 0, width, height);
        var bitmap = PdfiumNative.FPDFBitmap_Create(bitmapWidth, bitmapHeight, alpha: 0);
        if (bitmap == IntPtr.Zero)
        {
            throw new PdfException("Not enough memory to render the page.");
        }

        try
        {
            PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, bitmapWidth, bitmapHeight, 0xFFFFFFFF);
            PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, -x, -y, width, height, rotate: 0, flags);
            if (_formHandle != IntPtr.Zero)
            {
                // Form widgets are drawn by the form engine, which also reflects values set in memory.
                PdfiumNative.FPDF_FFLDraw(_formHandle, bitmap, page, -x, -y, width, height, rotate: 0, flags | PdfiumNative.FPDF_ANNOT);
            }

            var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            var pixels = new byte[stride * bitmapHeight];
            Marshal.Copy(PdfiumNative.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
            return new PdfBitmap(bitmapWidth, bitmapHeight, stride, pixels);
        }
        finally
        {
            PdfiumNative.FPDFBitmap_Destroy(bitmap);
        }
    }

    private string ReadPageText(int pageIndex)
    {
        var textPage = PdfiumNative.FPDFText_LoadPage(GetPage(pageIndex));
        if (textPage == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var count = PdfiumNative.FPDFText_CountChars(textPage);
            if (count <= 0)
            {
                return string.Empty;
            }

            var buffer = new ushort[count + 1];
            var written = PdfiumNative.FPDFText_GetText(textPage, 0, count, buffer);
            if (RepeatedText(textPage, buffer, count) is { } repeated)
            {
                return string.Concat(Enumerable.Range(0, count).Where(i => !repeated[i]).Select(i => (char)buffer[i]));
            }

            return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, Math.Max(0, written - 1))));
        }
        finally
        {
            PdfiumNative.FPDFText_ClosePage(textPage);
        }
    }

    private IReadOnlyList<PdfTextMatch> Find(int pageIndex, string query, PdfSearchOptions options)
    {
        var page = GetPage(pageIndex);
        var textPage = PdfiumNative.FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            // PDFium's own search reads the text as stored: pages with repeated /ActualText or Thai stored out of
            // order are searched here instead, in the repaired text.
            var charCount = PdfiumNative.FPDFText_CountChars(textPage);
            if (charCount > 0)
            {
                var chars = new ushort[charCount + 1];
                PdfiumNative.FPDFText_GetText(textPage, 0, charCount, chars);
                if (RepairedText(textPage, chars, charCount) is { } repaired)
                {
                    return FindIn(page, pageIndex, textPage, repaired, query, options);
                }
            }

            uint flags = 0;
            if (options.HasFlag(PdfSearchOptions.MatchCase))
            {
                flags |= PdfiumNative.FPDF_MATCHCASE;
            }

            if (options.HasFlag(PdfSearchOptions.WholeWord))
            {
                flags |= PdfiumNative.FPDF_MATCHWHOLEWORD;
            }

            var search = PdfiumNative.FPDFText_FindStart(textPage, query, flags, 0);
            if (search == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                var size = PageSizes[pageIndex];
                var deviceWidth = Math.Max(1, (int)Math.Round(size.Width * DeviceUnitsPerPoint));
                var deviceHeight = Math.Max(1, (int)Math.Round(size.Height * DeviceUnitsPerPoint));
                var matches = new List<PdfTextMatch>();
                while (PdfiumNative.FPDFText_FindNext(search) != 0)
                {
                    var start = PdfiumNative.FPDFText_GetSchResultIndex(search);
                    var count = PdfiumNative.FPDFText_GetSchCount(search);
                    var rectCount = PdfiumNative.FPDFText_CountRects(textPage, start, count);
                    var rects = new List<NormalizedRect>(Math.Max(0, rectCount));
                    for (var i = 0; i < rectCount; i++)
                    {
                        if (PdfiumNative.FPDFText_GetRect(textPage, i, out var left, out var top, out var right, out var bottom) != 0)
                        {
                            rects.Add(ToNormalized(page, deviceWidth, deviceHeight, left, top, right, bottom));
                        }
                    }

                    matches.Add(new PdfTextMatch(pageIndex, start, count, rects));
                }

                return matches;
            }
            finally
            {
                PdfiumNative.FPDFText_FindClose(search);
            }
        }
        finally
        {
            PdfiumNative.FPDFText_ClosePage(textPage);
        }
    }

    /// <summary>Finds <paramref name="query"/> in the repaired page text (see <see cref="RepairedText"/>).</summary>
    private IReadOnlyList<PdfTextMatch> FindIn(
        IntPtr page, int pageIndex, IntPtr textPage, List<ReadChar> repaired, string query, PdfSearchOptions options)
    {
        var text = string.Concat(repaired.Select(e => e.C));
        var comparison = options.HasFlag(PdfSearchOptions.MatchCase) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var size = PageSizes[pageIndex];
        var deviceWidth = Math.Max(1, (int)Math.Round(size.Width * DeviceUnitsPerPoint));
        var deviceHeight = Math.Max(1, (int)Math.Round(size.Height * DeviceUnitsPerPoint));
        var matches = new List<PdfTextMatch>();
        for (var at = query.Length == 0 ? -1 : text.IndexOf(query, comparison); at >= 0; at = text.IndexOf(query, at + query.Length, comparison))
        {
            var end = at + query.Length;
            if (options.HasFlag(PdfSearchOptions.WholeWord)
                && ((at > 0 && char.IsLetterOrDigit(text[at - 1])) || (end < text.Length && char.IsLetterOrDigit(text[end]))))
            {
                continue;
            }

            // The match on the page: from its first to its last character (left-out ones in between lie inside;
            // reordered marks may come from either end).
            var sources = repaired.GetRange(at, end - at).Select(e => e.Source).ToList();
            var start = sources.Min();
            var length = sources.Max() - start + 1;
            var rectCount = PdfiumNative.FPDFText_CountRects(textPage, start, length);
            var rects = new List<NormalizedRect>(Math.Max(0, rectCount));
            for (var i = 0; i < rectCount; i++)
            {
                if (PdfiumNative.FPDFText_GetRect(textPage, i, out var left, out var top, out var right, out var bottom) != 0)
                {
                    rects.Add(ToNormalized(page, deviceWidth, deviceHeight, left, top, right, bottom));
                }
            }

            matches.Add(new PdfTextMatch(pageIndex, start, length, rects));
        }

        return matches;
    }

    /// <summary>Converts a rectangle in PDF user space to fractions of the displayed (rotated) page.</summary>
    private static NormalizedRect ToNormalized(IntPtr page, int deviceWidth, int deviceHeight, double left, double top, double right, double bottom)
    {
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, left, top, out var x1, out var y1);
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, right, bottom, out var x2, out var y2);
        return new NormalizedRect(
            (double)Math.Min(x1, x2) / deviceWidth,
            (double)Math.Min(y1, y2) / deviceHeight,
            (double)Math.Abs(x2 - x1) / deviceWidth,
            (double)Math.Abs(y2 - y1) / deviceHeight);
    }

    private IReadOnlyList<PdfBookmark> ReadOutline()
    {
        ThrowIfClosed();
        var visited = new HashSet<IntPtr>();
        return ReadBookmarks(IntPtr.Zero, visited, depth: 0);
    }

    private List<PdfBookmark> ReadBookmarks(IntPtr parent, HashSet<IntPtr> visited, int depth)
    {
        var result = new List<PdfBookmark>();
        if (depth > MaxBookmarkDepth)
        {
            return result;
        }

        for (var bookmark = PdfiumNative.FPDFBookmark_GetFirstChild(_handle, parent);
             bookmark != IntPtr.Zero;
             bookmark = PdfiumNative.FPDFBookmark_GetNextSibling(_handle, bookmark))
        {
            // Malformed outlines can contain cycles; stop at the first repeated entry.
            if (visited.Count >= MaxBookmarks || !visited.Add(bookmark))
            {
                break;
            }

            result.Add(new PdfBookmark(
                ReadBookmarkTitle(bookmark),
                ResolveBookmarkPage(bookmark),
                ReadBookmarks(bookmark, visited, depth + 1)));
        }

        return result;
    }

    private static string ReadBookmarkTitle(IntPtr bookmark)
    {
        var length = PdfiumNative.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (length <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        PdfiumNative.FPDFBookmark_GetTitle(bookmark, buffer, length);
        return CleanText(Encoding.Unicode.GetString(buffer, 0, (int)length - 2));
    }

    private int? ResolveBookmarkPage(IntPtr bookmark)
    {
        var destination = PdfiumNative.FPDFBookmark_GetDest(_handle, bookmark);
        if (destination == IntPtr.Zero)
        {
            var action = PdfiumNative.FPDFBookmark_GetAction(bookmark);
            if (action != IntPtr.Zero)
            {
                destination = PdfiumNative.FPDFAction_GetDest(_handle, action);
            }
        }

        if (destination == IntPtr.Zero)
        {
            return null;
        }

        var pageIndex = PdfiumNative.FPDFDest_GetDestPageIndex(_handle, destination);
        return pageIndex >= 0 && pageIndex < PageCount ? pageIndex : null;
    }

    private string? ReadMetaText(string tag)
    {
        var length = PdfiumNative.FPDF_GetMetaText(_handle, tag, null, 0);
        if (length <= 2)
        {
            return null;
        }

        var buffer = new byte[length];
        PdfiumNative.FPDF_GetMetaText(_handle, tag, buffer, length);
        var text = CleanText(Encoding.Unicode.GetString(buffer, 0, (int)length - 2));
        return text.Length == 0 ? null : text;
    }

    private static string CleanText(string text) =>
        new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();

    private IntPtr GetPage(int pageIndex)
    {
        ThrowIfClosed();
        for (var node = _pageCache.First; node is not null; node = node.Next)
        {
            if (node.Value.Index == pageIndex)
            {
                _pageCache.Remove(node);
                _pageCache.AddFirst(node);
                return node.Value.Page;
            }
        }

        var page = PdfiumNative.FPDF_LoadPage(_handle, pageIndex);
        if (page == IntPtr.Zero)
        {
            throw new PdfException($"Page {pageIndex + 1} could not be loaded.");
        }

        if (_formHandle != IntPtr.Zero)
        {
            PdfiumNative.FORM_OnAfterLoadPage(page, _formHandle);
        }

        _pageCache.AddFirst((pageIndex, page));
        if (_pageCache.Count > PageCacheSize)
        {
            ClosePage(_pageCache.Last!.Value.Page);
            _pageCache.RemoveLast();
        }

        return page;
    }

    private void ClosePage(IntPtr page)
    {
        if (_textPageCache is { } cached && cached.Page == page)
        {
            CloseTextPageCache(); // A text page must be closed before its page.
        }

        if (_formHandle != IntPtr.Zero)
        {
            PdfiumNative.FORM_OnBeforeClosePage(page, _formHandle);
        }

        PdfiumNative.FPDF_ClosePage(page);
    }

    private unsafe int ReadBlock(IntPtr param, uint position, IntPtr buffer, uint size)
    {
        try
        {
            _stream.Position = position;
            _stream.ReadExactly(new Span<byte>((void*)buffer, checked((int)size)));
            return 1;
        }
        catch (Exception)
        {
            return 0; // PDFium treats 0 as a read failure; never let exceptions cross into native code.
        }
    }

    private void ValidatePageIndex(int pageIndex) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)pageIndex, (uint)PageCount, nameof(pageIndex));

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);
}
