using System.Security.Cryptography.X509Certificates;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.Services;

public enum SaveChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <param name="LockWithDigitalId">After placing it, also sign the document with a digital ID.</param>
public sealed record SignatureDialogResult(SignatureArt Art, bool Remember, bool LockWithDigitalId);

/// <summary>Where to save the parts and which pages (0-based) go into each file.</summary>
public sealed record SplitPlan(string Folder, IReadOnlyList<IReadOnlyList<int>> Parts);

public sealed record ImagesToPdfRequest(IReadOnlyList<string> Files, ImagesToPdfOptions Options);

/// <summary>What the user chose in the digital signature dialog. The caller owns (and disposes) the certificate.</summary>
public sealed record DigitalSignOptions(
    X509Certificate2 Certificate,
    string? Reason,
    string? Location,
    bool Visible,
    BitmapSource? Handwritten,
    Uri? TimestampUrl);

/// <summary>Keeps view models free of window and dialog code.</summary>
public interface IDialogService
{
    string? PickPdfFile(string title = "Open");

    /// <returns>A picture file (PNG, JPEG, BMP, GIF, TIFF), or null if the user cancelled.</returns>
    string? PickImageFile(string title);

    /// <returns>The chosen path, or null if the user cancelled.</returns>
    string? PickSavePdfFile(string currentPath);

    /// <returns>The password, or null if the user cancelled.</returns>
    string? PromptPassword(string fileName, bool previousAttemptFailed);

    /// <summary>Asks for the owner password to lift a document's restrictions; null when cancelled.</summary>
    string? AskOwnerPassword(string fileName, string reason, bool previousAttemptFailed);

    /// <summary>The Protect with password dialog; null when cancelled.</summary>
    Yrk.PdfStudio.Core.Pdf.Security.PdfProtection? AskProtection();

    /// <summary>The watermark, header and footer dialog; null when cancelled.</summary>
    Imaging.PageMarkSettings? AskPageMarks(ViewModels.PageMarksViewModel model);

    SaveChangesChoice AskSaveChanges(string fileName);

    /// <summary>Lets the user draw, type or import a signature.</summary>
    /// <param name="preparePdf">Renders and crops a PDF chosen as signature (null when it has no page).</param>
    SignatureDialogResult? CreateSignature(Func<byte[], Task<SignatureArt?>> preparePdf);

    CertifiedCopyOptions? AskCertifiedCopy(IReadOnlyList<SavedSignature> signatures);

    /// <summary>Name, dropdown choices and "required" for a new form field; null when cancelled.</summary>
    FormFieldAnswer? AskFormField(Yrk.PdfStudio.Core.Pdf.Forms.PdfNewFieldKind kind, string suggestedName, FormFieldAnswer? existing = null);

    /// <summary>Save dialog for a new PDF; asks before overwriting.</summary>
    string? PickSavePdfFile(string suggestedFileName, string? initialFolder, string title);

    /// <summary>The ordered list of PDFs to combine, or null if cancelled.</summary>
    IReadOnlyList<string>? AskCombine(IReadOnlyList<string> initialFiles);

    ImagesToPdfRequest? AskImagesToPdf();

    SplitPlan? AskSplit(int pageCount, string defaultFolder);

    PdfCompressionOptions? AskCompression();

    /// <param name="lockOnly">A handwritten signature was just placed: offer an invisible signature first.</param>
    DigitalSignOptions? AskDigitalSign(IReadOnlyList<SavedSignature> signatures, bool lockOnly = false);

    OcrOptions? AskOcr();

    /// <summary>A real-world length for calibrating a scale, or null if cancelled.</summary>
    (double Value, string Unit)? AskLength(string defaultUnit);

    void ShowAbout(ViewModels.MainViewModel.AboutModel model);

    void ShowPrivacy(ViewModels.MainViewModel.AboutModel model);

    /// <summary>Ways to send suggestions and problem reports (the app sends nothing itself).</summary>
    void ShowFeedback(string version);

    /// <summary>Shows the comparison in its own window (not modal); the window disposes the model when closed.</summary>
    void ShowCompare(ViewModels.CompareViewModel model);

    /// <summary>The print dialog; null when cancelled.</summary>
    PrintSettings? AskPrint(ViewModels.PrintViewModel model);

    void ShowInfo(string message);

    /// <summary>Shows the Windows certificate details window.</summary>
    void ShowCertificate(X509Certificate2 certificate);

    /// <summary>OK / Cancel question; true for OK.</summary>
    bool Confirm(string message);

    /// <summary>The Export dialog, with <paramref name="format"/> chosen when given; null when cancelled.</summary>
    ExportSettings? AskExport(int pageCount, int currentPage, bool batch = false, ExportFormat? format = null);

    /// <summary>Tools ▸ Process many files: the files, the job and the output folder; null when cancelled.</summary>
    BatchRequest? AskBatch(IEnumerable<string> initialFiles);

    /// <summary>Save dialog for any file type; asks before overwriting.</summary>
    string? PickSaveFile(string suggestedFileName, string? initialFolder, string title, string filter, string extension);

    /// <summary>Yes / No question; true for Yes.</summary>
    bool AskYesNo(string message);

    void ShowError(string message);
}
