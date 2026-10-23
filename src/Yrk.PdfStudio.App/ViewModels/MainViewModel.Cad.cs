using System.IO;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cad;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>
/// AutoCAD drawings (.dwg, .dxf): converted to a PDF with layers when opened, then viewed like any PDF.
/// "Save" asks where to keep the PDF; the drawing itself is never changed.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly string[] OpenableExtensions = [".pdf", ".ai", ".dwg", ".dxf"];

    /// <summary>The drawing the open document was converted from (null for ordinary PDFs).</summary>
    private string? _convertedFrom;

    public static bool CanOpen(string path) => OpenableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsDrawing(string path) => Path.GetExtension(path) is var e
        && (e.Equals(".dwg", StringComparison.OrdinalIgnoreCase) || e.Equals(".dxf", StringComparison.OrdinalIgnoreCase));

    /// <summary>A converted drawing can always be saved (as a PDF); other documents when changed.</summary>
    public bool CanSave => _document is not null && (IsDirty || _convertedFrom is not null);

    /// <summary>Converts a drawing to a PDF in a temporary folder (the text is shaped here, in the app).</summary>
    private static async Task<(string PdfPath, CadPdfResult Result)> ConvertDrawingAsync(string path)
    {
        var result = await Task.Run(() => CadToPdf.Convert(path));
        var blocks = CadText.Shape(result.Texts, result.Pages);
        var bytes = await Task.Run(() => PdfFiles.Compact(blocks.Count > 0 ? PdfTextWriter.AddText(result.Pdf, blocks) : result.Pdf));

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "YRK PDF", "drawings", Guid.NewGuid().ToString("N")));
        var pdfPath = Path.Combine(folder.FullName, Path.GetFileNameWithoutExtension(path) + ".pdf");
        await File.WriteAllBytesAsync(pdfPath, bytes);
        return (pdfPath, result);
    }

    private async Task<bool> OpenDrawingAsync(string path)
    {
        IsBusy = true;
        StatusMessage = "Converting the drawing…";
        try
        {
            var (pdfPath, result) = await ConvertDrawingAsync(path);
            if (!await OpenDocumentAsync(pdfPath, null, null, keepEdits: false))
            {
                return false;
            }

            _convertedFrom = path;
            RaiseDocumentPropertiesChanged();
            var layouts = result.Pages.Count(p => p.IsLayout);
            StatusMessage = (result.Pages[0].IsLayout ? Loc.T("Drawing shown with its layouts") : Loc.F("Drawing shown at {0}", result.Scale))
                + (layouts > 0 ? Loc.F(" · {0} layout pages (see Bookmarks)", layouts) : string.Empty)
                + (result.Warnings.Count > 0 ? Loc.F(" · not drawn: {0}", string.Join(", ", result.Warnings.Take(3))) : string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = string.Empty;
            _dialogs.ShowError(Loc.F("Couldn't open \"{0}\".\n\n{1}", Path.GetFileName(path), ex.Message));
            return false;
        }
        finally
        {
            IsBusy = false;
            if (StatusMessage == Loc.T("Converting the drawing…"))
            {
                StatusMessage = string.Empty;
            }
        }
    }

    /// <summary>Saves a converted drawing as a PDF next to the drawing (by default).</summary>
    private async Task SaveDrawingAsPdfAsync()
    {
        if (_convertedFrom is not { } source || _document is null)
        {
            return;
        }

        var target = _dialogs.PickSavePdfFile(Path.GetFileNameWithoutExtension(source) + ".pdf", Path.GetDirectoryName(source), "Save the drawing as PDF");
        if (target is null)
        {
            return;
        }

        var restoreSource = _convertedFrom;
        _convertedFrom = null; // From here on it is an ordinary PDF.
        if (!await SaveToAsync(target))
        {
            _convertedFrom = restoreSource;
            RaiseDocumentPropertiesChanged();
        }
    }
}
