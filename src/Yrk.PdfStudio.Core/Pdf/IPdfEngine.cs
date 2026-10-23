namespace Yrk.PdfStudio.Core.Pdf;

/// <summary>
/// Entry point to a PDF engine. The UI only talks to these interfaces, so the engine behind them
/// (PDFium today) can be replaced or moved into a separate process without touching the UI.
/// </summary>
public interface IPdfEngine : IDisposable
{
    /// <exception cref="PdfPasswordRequiredException">The document is encrypted and the password is missing or wrong.</exception>
    /// <exception cref="PdfException">The file is not a readable PDF.</exception>
    Task<IPdfDocument> OpenAsync(string filePath, string? password = null, CancellationToken cancellationToken = default);

    /// <summary>Writes all pages of <paramref name="sources"/>, in order, into a new PDF.</summary>
    Task CombineAsync(IReadOnlyList<IPdfDocument> sources, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>Creates a PDF with one page per image.</summary>
    Task CreateFromImagesAsync(IReadOnlyList<PdfImageInput> images, ImagesToPdfOptions options, string outputPath, CancellationToken cancellationToken = default);
}

public interface IPdfDocument : IAsyncDisposable
{
    string FilePath { get; }

    /// <summary>The /Title from the document information dictionary, if any.</summary>
    string? Title { get; }

    /// <summary>The file is encrypted (with an open password, restrictions, or both).</summary>
    bool IsEncrypted { get; }

    /// <summary>
    /// What is allowed with the password the file was opened with: everything for unencrypted files and
    /// when it was opened with the owner password.
    /// </summary>
    Security.PdfPermissions Permissions { get; }

    /// <summary>The document as it is now (including changes made in memory), without encryption.</summary>
    Task<byte[]> SaveDecryptedAsync(CancellationToken cancellationToken = default);

    int PageCount { get; }

    IReadOnlyList<PdfPageSize> PageSizes { get; }

    Task<PdfBitmap> RenderPageAsync(
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        PdfWorkPriority priority = PdfWorkPriority.Interactive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders only <paramref name="region"/> of the page as it would appear at
    /// <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> (for sharp, very high zoom).
    /// </summary>
    Task<PdfBitmap> RenderPageRegionAsync(
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        PdfPixelRegion region,
        PdfWorkPriority priority = PdfWorkPriority.Interactive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders the page for a printer: only annotations marked printable (and none without
    /// <paramref name="comments"/>), no form highlighting, optionally in shades of grey. Hidden layers stay hidden.
    /// </summary>
    Task<PdfBitmap> RenderPageForPrintAsync(
        int pageIndex,
        int pixelWidth,
        int pixelHeight,
        bool grayscale,
        bool comments,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken cancellationToken = default);

    /// <summary>The page's text in content order, as PDFium extracts it (used for copy and search diagnostics).</summary>
    Task<string> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default);

    /// <summary>The page's text with positions and styles, for export (see <see cref="Export.TextLayout"/>).</summary>
    Task<Export.PdfPageText> GetPageTextSpansAsync(int pageIndex, CancellationToken cancellationToken = default);

    /// <summary>The page's lines, filled boxes, pictures and drawings, for export (see <see cref="Export.PageLayout"/>).</summary>
    Task<Export.PdfPageGraphics> GetPageGraphicsAsync(int pageIndex, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PdfTextMatch>> FindTextAsync(
        int pageIndex,
        string query,
        PdfSearchOptions options,
        PdfWorkPriority priority = PdfWorkPriority.Background,
        CancellationToken cancellationToken = default);

    /// <summary>True when the document has an interactive (AcroForm) form.</summary>
    bool HasForm { get; }

    Task<IReadOnlyList<PdfFormField>> GetFormFieldsAsync(int pageIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets field values in memory through PDFium's form engine, which also regenerates each
    /// field's appearance. Read-only fields are skipped. Call <see cref="SaveAsync"/> to persist.
    /// </summary>
    Task ApplyFormEditsAsync(IReadOnlyList<PdfFormFieldEdit> edits, CancellationToken cancellationToken = default);

    /// <summary>Draws an image into the page content (flattened) at a page-relative position.</summary>
    Task AddImageAsync(int pageIndex, PdfImage image, NormalizedRect bounds, CancellationToken cancellationToken = default);

    /// <summary>Draws filled vector shapes into the page content, scaled to a page-relative rectangle (stays upright on rotated pages).</summary>
    Task AddVectorAsync(int pageIndex, PdfVectorArt art, NormalizedRect bounds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places the first page of another PDF into this page as vector content (a form XObject), scaled to a
    /// page-relative rectangle. Only <paramref name="sourceCrop"/> of that page (relative, top-left origin)
    /// is shown. Used for signatures supplied as PDF.
    /// </summary>
    Task AddPdfPageAsync(int pageIndex, byte[] pdf, NormalizedRect sourceCrop, NormalizedRect bounds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the document with all in-memory changes to <paramref name="path"/> as an incremental
    /// update (original bytes followed by the changes), which keeps existing digital signatures valid.
    /// The path must be a new file, not the document's own file.
    /// </summary>
    Task SaveAsync(string path, CancellationToken cancellationToken = default);

    // Page tools. They change the document in memory; PageCount and PageSizes reflect the result.

    /// <summary>Rotates pages clockwise by <paramref name="quarterTurns"/> × 90° (negative = counter-clockwise).</summary>
    Task RotatePagesAsync(IReadOnlyList<int> pageIndices, int quarterTurns, CancellationToken cancellationToken = default);

    /// <exception cref="PdfException">The deletion would leave the document without pages.</exception>
    Task DeletePagesAsync(IReadOnlyList<int> pageIndices, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves pages (kept in ascending order) so the first of them ends up at <paramref name="destinationIndex"/>,
    /// counted in the document after the pages are taken out.
    /// </summary>
    Task MovePagesAsync(IReadOnlyList<int> pageIndices, int destinationIndex, CancellationToken cancellationToken = default);

    /// <summary>Copies pages of another open document into this one before <paramref name="insertIndex"/>.</summary>
    Task InsertPagesAsync(IPdfDocument source, int insertIndex, IReadOnlyList<int>? sourcePageIndices = null, CancellationToken cancellationToken = default);

    /// <summary>Writes the given pages (in the given order) to a new PDF.</summary>
    Task ExtractPagesAsync(IReadOnlyList<int> pageIndices, string outputPath, CancellationToken cancellationToken = default);

    // Text selection and annotations.

    /// <summary>The characters between two points (in reading order), or null when there is no text there.</summary>
    Task<PdfTextSelection?> GetTextSelectionAsync(int pageIndex, NormalizedPoint start, NormalizedPoint end, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, CancellationToken cancellationToken = default);

    /// <summary>Creates an annotation with a generated appearance. Returns its id (/NM).</summary>
    Task<string> AddAnnotationAsync(int pageIndex, PdfAnnotationSpec spec, CancellationToken cancellationToken = default);

    Task SetAnnotationContentsAsync(PdfAnnotationRef annotation, string contents, CancellationToken cancellationToken = default);

    Task RemoveAnnotationAsync(PdfAnnotationRef annotation, CancellationToken cancellationToken = default);

    /// <summary>The line of text at a point (for editing), or null when there is no text there.</summary>
    Task<PdfTextLine?> GetTextLineAtAsync(int pageIndex, NormalizedPoint point, CancellationToken cancellationToken = default);

    /// <summary>The text whose characters lie inside an area (used to verify redaction).</summary>
    Task<string> GetTextInAreaAsync(int pageIndex, NormalizedRect area, CancellationToken cancellationToken = default);

    // Layers (optional content).

    /// <summary>The document's layers, as listed in the file (empty when it has none).</summary>
    Task<IReadOnlyList<Layers.PdfLayer>> GetLayersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders with exactly these layers hidden (by <see cref="Layers.PdfLayer.Id"/>) and all others shown;
    /// null goes back to the document's own default. The document itself is not changed.
    /// </summary>
    Task SetHiddenLayersAsync(IReadOnlyCollection<string>? layerIds, CancellationToken cancellationToken = default);

    /// <summary>Drawing scales stored on a page (for measuring); empty when the page has none.</summary>
    Task<IReadOnlyList<Measure.PdfPageScale>> GetPageScalesAsync(int pageIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Points worth snapping to when measuring: the ends of lines and curves and the middles of straight
    /// lines in the page's drawing (including forms), relative to the page as displayed.
    /// </summary>
    Task<IReadOnlyList<Measure.PdfSnapPoint>> GetSnapPointsAsync(int pageIndex, CancellationToken cancellationToken = default);
}
