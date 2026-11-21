using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Forms;

namespace Yrk.PdfStudio.Testing;

internal sealed record WindowCase(Type Type, string Fixture, Func<WindowFixture, Window>? Create, string? Unavailable = null)
{
    public string Name => Type.Name;
}

/// <summary>Explicit, reviewed construction table. Tests assert it covers every concrete app Window.</summary>
internal static class WindowCatalog
{
    public static IReadOnlyList<string> Themes { get; } = ["light", "dark"];

    public static string PalettePath(string theme) => theme switch
    {
        "light" => "Themes/Colors.Light.xaml",
        "dark" => "Themes/Colors.Dark.xaml",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Expected light or dark.")
    };

    public static IReadOnlyList<WindowCase> Cases { get; } =
    [
        new(typeof(MainWindow), "Empty MainViewModel, no personal signatures or recent files", f => new MainWindow(f.Model, () => f.Model)),
        new(typeof(AboutDialog), "AboutModel from fixture MainViewModel", f => new AboutDialog(new(f.Model))),
        new(typeof(AmountWordsDialog), "Placement enabled", _ => new AmountWordsDialog(true)),
        new(typeof(BackupDialog), "UI only; no backup action", _ => new BackupDialog()),
        new(typeof(BatchDialog), "Empty file list", _ => new BatchDialog([])),
        new(typeof(CertifiedCopyDialog), "Empty signature list", _ => new CertifiedCopyDialog([])),
        new(typeof(CompareWindow), "Two synthetic one-page documents with example names", f => new CompareWindow(new(f.Document, "example-before.pdf", f.Document, "example-after.pdf"))),
        new(typeof(CompressDialog), "Default options", _ => new CompressDialog()),
        new(typeof(ConvertScanDialog), "Empty input list", _ => new ConvertScanDialog([])),
        new(typeof(ConvertScanResultDialog), "Example output paths and text", _ => new ConvertScanResultDialog(new("Example result", [@"C:\Example\output.pdf"], "Example text / ข้อความตัวอย่าง"))),
        new(typeof(CreateDigitalIdDialog), "UI only; no certificate created", _ => new CreateDigitalIdDialog()),
        new(typeof(DigitalSignDialog), "Two synthetic IDs: fictional .pfx file and expired Windows certificate; no real folder/certificate store read",
            _ => new DigitalSignDialog(new DigitalIdStore([
                new DigitalId("Example file ID", null, null, null, @"C:\Example\synthetic.pfx"),
                new DigitalId("Example expired Windows ID", "Example issuer", new DateTime(2020, 1, 1), "SYNTHETIC-EXPIRED-ID", null)
            ]), [])),
        new(typeof(ErrorDialog), "Catalogue error with no real exception/path", _ => new ErrorDialog(ErrorCatalog.PrintList)),
        new(typeof(ETaxInvoiceDialog), "Fresh in-memory settings", _ => new ETaxInvoiceDialog(new())),
        new(typeof(ExportDialog), "Three pages, current page zero", _ => new ExportDialog(3, 0)),
        new(typeof(FeedbackDialog), "Example version", _ => new FeedbackDialog("1.2.4-example")),
        new(typeof(FileListDialog), "Combine PDFs, empty list", _ => new FileListDialog(FileListMode.CombinePdfs, [])),
        new(typeof(FormFieldDialog), "Example text field", _ => new FormFieldDialog(PdfNewFieldKind.Text, "Example field")),
        new(typeof(ImportDialog), "UI only; no import selected", _ => new ImportDialog()),
        new(typeof(LengthDialog), "Millimetres", _ => new LengthDialog("mm")),
        new(typeof(MessageDialog), "Save changes with explicit safe dismissal", _ => new MessageDialog(
            Loc.F("Save changes to \"{0}\"?", "example.pdf"), DialogService.Caption,
            [new("Save", MessageBoxResult.Yes, 'S', true), new("Don't save", MessageBoxResult.No, 'D'), new("Cancel", MessageBoxResult.Cancel, 'C')],
            MessageBoxResult.Yes, MessageBoxResult.Cancel, MessageBoxImage.Question, null)),
        new(typeof(OcrDialog), "Default language choices; no OCR/download", _ => new OcrDialog()),
        new(typeof(OcrReviewDialog), "Synthetic document and one uncertain example word", f => new OcrReviewDialog(new(
            [new(0, [new("Example / ตัวอย่าง", new(.1, .1, .3, .04), 30, false)])], f.Document, false))),
        new(typeof(PageMarksDialog), "Synthetic document and example filename", f => new PageMarksDialog(new(f.Document, "example.pdf", 0))),
        new(typeof(PasswordDialog), "Example filename", _ => new PasswordDialog("example.pdf", false)),
        new(typeof(PrintDialog), "Synthetic one-page document and example printer name; no print submitted", f => CreatePrint(f)),
        new(typeof(PrivacyDialog), "UI only; no clear action", _ => new PrivacyDialog()),
        new(typeof(PrivacyPolicyDialog), "UI only", _ => new PrivacyPolicyDialog()),
        new(typeof(ProtectDialog), "UI only", _ => new ProtectDialog()),
        new(typeof(ReaderBookmarkDialog), "Example bookmark", _ => new ReaderBookmarkDialog("Example / ตัวอย่าง", "#FFFF00", false)),
        new(typeof(ScanCleanupDialog), "Example page classifications", _ => new ScanCleanupDialog([0], [1], [2])),
        new(typeof(ScanDialog), "Default constructor uses FakeScannerService, no scan requested", _ => new ScanDialog()),
        new(typeof(ShortcutsDialog), "UI only", _ => new ShortcutsDialog()),
        new(typeof(SignAgainDialog), "One signature, no edits", _ => new SignAgainDialog(1, false)),
        new(typeof(SignatureDialog), "Empty signature canvas, no PDF import", _ => new SignatureDialog(_ => Task.FromResult<SignatureArt?>(null))),
        new(typeof(SplitDialog), "Three pages, example output folder and bookmark", _ => new SplitDialog(3, @"C:\Example", [new("Example", 0, [])])),
        new(typeof(StampDialog), "Fresh in-memory stamp settings", _ => new StampDialog(new())),
        new(typeof(SupportDialog), "UI only", _ => new SupportDialog()),
        new(typeof(TrustCertificateDialog), "Ephemeral self-signed example certificate", f => new TrustCertificateDialog(f.Certificate)),
        new(typeof(TrustedCertificatesDialog), "Empty isolated certificate store", f => new TrustedCertificatesDialog(new(f.PathFor("trusted"), File.Delete)))
    ];

    private static Window CreatePrint(WindowFixture fixture)
    {
        var model = new PrintViewModel(fixture.Document, 0);
        // PrintViewModel currently discovers printers itself. Keep any machine/server names out of the image;
        // set fixture state before binding, without calling Printer's setter (which queries the named printer).
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PrintViewModel).GetField("<Printers>k__BackingField", fields)!.SetValue(model, new[] { "Example printer" });
        typeof(PrintViewModel).GetField("_printer", fields)!.SetValue(model, "Example printer");
        typeof(PrintViewModel).GetField("_printerError", fields)!.SetValue(model, string.Empty);
        return new PrintDialog(model);
    }
}

internal sealed class WindowFixture : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("yrk-window-fixture-").FullName;
    private readonly IDisposable _recent;
    public IPdfDocument Document { get; }
    public MainViewModel Model { get; }
    public X509Certificate2 Certificate { get; }

    public WindowFixture()
    {
        // Internal seam via reflection lets the standalone renderer share this fixture without opening the app API.
        _recent = (IDisposable)typeof(RecentFiles).GetMethod("UseStorage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [PathFor("recent.json"), null])!;
        Document = FixtureProxy.For<IPdfDocument>((method, args) => method.Name switch
        {
            "get_FilePath" => @"C:\Example\example.pdf",
            "get_PageCount" => 1,
            "get_PageSizes" => new PdfPageSize[] { new(595, 842) },
            "RenderPageAsync" or "RenderPageForPrintAsync" => Task.FromResult(BlankBitmap((int)args![1]!, (int)args[2]!)),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new NotSupportedException("Preview document: " + method.Name)
        });
        var engine = FixtureProxy.For<IPdfEngine>((method, _) => throw new NotSupportedException("Preview engine: " + method.Name));
        var dialogs = FixtureProxy.For<IDialogService>((method, _) => throw new NotSupportedException("Preview dialog service: " + method.Name));
        var signatures = FixtureProxy.For<ISignatureStore>((method, _) => method.Name == "LoadAll"
            ? Array.Empty<SavedSignature>() : throw new NotSupportedException(method.Name));
        Model = (MainViewModel)Activator.CreateInstance(typeof(MainViewModel), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [engine, dialogs, signatures, new TrustedCertificateStore(PathFor("trusted"), File.Delete)], null)!;
        using var rsa = RSA.Create(2048);
        Certificate = new CertificateRequest("CN=Example Certificate, O=Example", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    public string PathFor(string name) => Path.Combine(_folder, name);
    private static PdfBitmap BlankBitmap(int width, int height) => new(width, height, width * 4,
        Enumerable.Repeat((byte)255, checked(width * height * 4)).ToArray());

    public void Dispose()
    {
        WpfTestApp.Await(Model.CloseTabAsync());
        Certificate.Dispose();
        _recent.Dispose();
        Directory.Delete(_folder, true);
    }
}

public class FixtureProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> _invoke = null!;
    internal static T For<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var result = Create<T, FixtureProxy>();
        ((FixtureProxy)(object)result)._invoke = invoke;
        return result;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
}
