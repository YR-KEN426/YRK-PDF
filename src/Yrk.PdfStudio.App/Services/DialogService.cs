using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Ocr;

namespace Yrk.PdfStudio.App.Services;

public sealed class DialogService : IDialogService
{
    public const string Caption = "YRK PDF";

    private const string PdfFilter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*";

    private const string OpenFilter = "All supported files (*.pdf;*.ai;*.dwg;*.dxf)|*.pdf;*.ai;*.dwg;*.dxf|PDF documents (*.pdf)|*.pdf|" +
        "AutoCAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|Illustrator files (*.ai)|*.ai|All files (*.*)|*.*";

    private static Window? Owner => Application.Current?.MainWindow;

    public string? PickPdfFile(string title = "Open")
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T(title),
            Filter = Loc.T(OpenFilter),
            CheckFileExists = true,
        };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    public string? PickImageFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T(title),
            Filter = Loc.T("Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff"),
            CheckFileExists = true,
        };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    public string? PickSavePdfFile(string currentPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Save a copy"),
            Filter = Loc.T(PdfFilter),
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(currentPath),
            FileName = Path.GetFileNameWithoutExtension(currentPath) + " (signed).pdf",
        };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    public string? PromptPassword(string fileName, bool previousAttemptFailed)
    {
        var dialog = new PasswordDialog(fileName, previousAttemptFailed) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public string? AskOwnerPassword(string fileName, string reason, bool previousAttemptFailed)
    {
        var dialog = new PasswordDialog(fileName, previousAttemptFailed, reason, Loc.T("Unlock")) { Owner = Owner, Title = Loc.T("Owner password") };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public Imaging.PageMarkSettings? AskPageMarks(ViewModels.PageMarksViewModel model)
    {
        var dialog = new PageMarksDialog(model) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    public Yrk.PdfStudio.Core.Pdf.Security.PdfProtection? AskProtection()
    {
        var dialog = new ProtectDialog { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Protection : null;
    }

    public SaveChangesChoice AskSaveChanges(string fileName)
    {
        var result = Owner is { } owner
            ? MessageBox.Show(owner, Loc.F("Save changes to \"{0}\"?", fileName), Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show(Loc.F("Save changes to \"{0}\"?", fileName), Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => SaveChangesChoice.Save,
            MessageBoxResult.No => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel,
        };
    }

    public SignatureDialogResult? CreateSignature(Func<byte[], Task<SignatureArt?>> preparePdf)
    {
        var dialog = new SignatureDialog(preparePdf) { Owner = Owner };
        return dialog.ShowDialog() == true && dialog.Signature is { } art
            ? new SignatureDialogResult(art, dialog.Remember, dialog.LockWithDigitalId)
            : null;
    }

    public CertifiedCopyOptions? AskCertifiedCopy(IReadOnlyList<SavedSignature> signatures)
    {
        var dialog = new CertifiedCopyDialog(signatures) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Options : null;
    }

    public FormFieldAnswer? AskFormField(Yrk.PdfStudio.Core.Pdf.Forms.PdfNewFieldKind kind, string suggestedName, FormFieldAnswer? existing = null)
    {
        var dialog = new FormFieldDialog(kind, suggestedName, existing) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Answer : null;
    }

    public string? PickSavePdfFile(string suggestedFileName, string? initialFolder, string title)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T(title),
            Filter = Loc.T(PdfFilter),
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = initialFolder,
            FileName = suggestedFileName,
        };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    public IReadOnlyList<string>? AskCombine(IReadOnlyList<string> initialFiles)
    {
        var dialog = new FileListDialog(FileListMode.CombinePdfs, initialFiles) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Files : null;
    }

    public ImagesToPdfRequest? AskImagesToPdf()
    {
        var dialog = new FileListDialog(FileListMode.ImagesToPdf, []) { Owner = Owner };
        return dialog.ShowDialog() == true ? new ImagesToPdfRequest(dialog.Files, dialog.ImageOptionsResult) : null;
    }

    public SplitPlan? AskSplit(int pageCount, string defaultFolder)
    {
        var dialog = new SplitDialog(pageCount, defaultFolder) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Plan : null;
    }

    public PdfCompressionOptions? AskCompression()
    {
        var dialog = new CompressDialog { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Options : null;
    }

    public DigitalSignOptions? AskDigitalSign(IReadOnlyList<SavedSignature> signatures, bool lockOnly = false)
    {
        var dialog = new DigitalSignDialog(new DigitalIdStore(), signatures, lockOnly) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Options : null;
    }

    public OcrOptions? AskOcr()
    {
        var dialog = new OcrDialog { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Options : null;
    }

    public void ShowInfo(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information);

    public (double Value, string Unit)? AskLength(string defaultUnit)
    {
        var dialog = new LengthDialog(defaultUnit) { Owner = Owner };
        return dialog.ShowDialog() == true ? (dialog.Value, dialog.Unit) : null;
    }

    public void ShowAbout(ViewModels.MainViewModel.AboutModel model) => new AboutDialog(model) { Owner = Owner }.ShowDialog();

    public void ShowPrivacy(ViewModels.MainViewModel.AboutModel model) => new PrivacyDialog(model) { Owner = Owner }.ShowDialog();

    public void ShowFeedback(string version) => new FeedbackDialog(version) { Owner = Owner }.ShowDialog();

    public void ShowCompare(ViewModels.CompareViewModel model) => new CompareWindow(model) { Owner = Owner }.Show();

    public PrintSettings? AskPrint(ViewModels.PrintViewModel model)
    {
        var dialog = new PrintDialog(model) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    public BatchRequest? AskBatch(IEnumerable<string> initialFiles)
    {
        var dialog = new BatchDialog(initialFiles) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    public ExportSettings? AskExport(int pageCount, int currentPage, bool batch = false, ExportFormat? format = null)
    {
        var dialog = new ExportDialog(pageCount, currentPage, batch, format) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    public string? PickSaveFile(string suggestedFileName, string? initialFolder, string title, string filter, string extension)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T(title),
            Filter = Loc.T(filter),
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = initialFolder,
            FileName = suggestedFileName,
        };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    public void ShowCertificate(X509Certificate2 certificate)
    {
        var owner = Owner is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        CryptUIDlgViewContext(CertStoreCertificateContext, certificate.Handle, owner, "Certificate", 0, IntPtr.Zero);
    }

    private const uint CertStoreCertificateContext = 1;

    [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUIDlgViewContext(uint contextType, IntPtr context, IntPtr owner, string title, uint flags, IntPtr reserved);

    public bool Confirm(string message) =>
        Show(message, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public bool AskYesNo(string message) =>
        Show(message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage icon) =>
        Owner is { } owner
            ? MessageBox.Show(owner, Loc.T(message), Caption, buttons, icon)
            : MessageBox.Show(Loc.T(message), Caption, buttons, icon);

    public void ShowError(string message)
    {
        if (Owner is { } owner)
        {
            MessageBox.Show(owner, Loc.T(message), Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(Loc.T(message), Caption, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool ShowDialog(CommonDialog dialog) =>
        (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
}
