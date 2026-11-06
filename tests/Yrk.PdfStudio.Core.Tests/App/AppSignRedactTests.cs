using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class AppSignRedactTests
{
    private const string Secret = "REDACTION-PRIVATE-73921";
    private const string PublicText = "Public text outside the marked area";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Signing_saves_a_signed_copy_with_an_intact_signature_and_preserves_source()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        var target = app.PathFor("source (signed).pdf");
        Assert.True(await app.OpenAsync(source));
        using var certificate = NewSigner();
        app.Dialogs.Answer("AskDigitalSign", new DigitalSignOptions(certificate, null, null,
            Visible: false, Handwritten: null, TimestampUrl: null));
        app.Dialogs.Answer("PickSavePdfFile", target);

        await StartSigningAsync(app);
        await WaitForSignaturesAsync(app);

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal("source (signed).pdf", Assert.Single(app.Dialogs.Named("PickSavePdfFile")).Arguments[0]);
        Assert.Single(app.Dialogs.Named("AskDigitalSign"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        var signature = Assert.Single(PdfSignatureVerifier.Verify(File.ReadAllBytes(target), new(CheckRevocation: false)));
        // A self-signed test ID need not be trusted; its digest must still verify.
        Assert.True(signature.IsIntact);
        Assert.False(signature.ModifiedAfterSigning);
        Assert.True(signature.CoversWholeDocument);
        Assert.Single(app.Model.Signatures);
        AssertFiles(app, source, target);
    }

    [Fact]
    public async Task Saving_version_before_signing_restores_exact_bytes_and_strips_signed_suffix()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var before = File.ReadAllBytes(source);
        var signedPath = await CreateSignedCopyAsync(app, source);
        var signedBytes = File.ReadAllBytes(signedPath);
        Assert.True(await app.OpenAsync(signedPath));
        await WaitForSignaturesAsync(app);
        var signature = Assert.Single(app.Model.Signatures);
        Assert.True(signature.CanSaveVersionBeforeSigning);
        var target = app.PathFor("source (before signing).pdf");
        app.Dialogs.Answer("Confirm", true);
        app.Dialogs.Answer("PickSavePdfFile", target);
        app.Dialogs.Answer("AskYesNo", false); // Keep the signed document open.

        await app.Model.SaveVersionBeforeSigningCommand.ExecuteAsync(signature);

        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(signedBytes, File.ReadAllBytes(signedPath));
        Assert.Equal("source (before signing).pdf", Assert.Single(app.Dialogs.Named("PickSavePdfFile")).Arguments[0]);
        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        AssertFiles(app, source, signedPath, target);
    }

    [Fact]
    public async Task Cancelling_sign_again_writes_nothing_and_does_not_ask_for_a_digital_id()
    {
        await using var app = new AppTestHarness();
        var source = app.CreatePdf();
        var signedPath = await CreateSignedCopyAsync(app, source);
        var snapshot = Directory.GetFiles(app.Folder).ToDictionary(path => path, File.ReadAllBytes);
        Assert.True(await app.OpenAsync(signedPath));
        // StartDigitalSignAsync must itself wait for the offline signature pass.
        app.Dialogs.Answer("ConfirmSignAgain", false);

        await StartSigningAsync(app);

        var warning = Assert.Single(app.Dialogs.Named("ConfirmSignAgain"));
        Assert.Equal(1, warning.Arguments[0]);
        Assert.Equal(false, warning.Arguments[1]);
        Assert.Empty(app.Dialogs.Named("AskDigitalSign"));
        Assert.Empty(app.Dialogs.Named("PickSavePdfFile"));
        Assert.Empty(app.Dialogs.Named("ShowError"));
        AssertFiles(app, snapshot.Keys.ToArray());
        foreach (var (path, bytes) in snapshot) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Applying_redaction_removes_secret_keeps_public_text_and_preserves_source()
    {
        await using var app = new AppTestHarness();
        var source = app.PathFor("secret.pdf");
        var before = CreateSecretPdf();
        File.WriteAllBytes(source, before);
        // Prove that both extraction paths can see the secret before redaction.
        await using (var original = await app.Engine.OpenAsync(source))
        {
            Assert.Contains(Secret, await original.GetPageTextAsync(0));
            Assert.Contains(PublicText, await original.GetPageTextAsync(0));
            Assert.NotEmpty(await original.FindTextAsync(0, Secret, PdfSearchOptions.None));
        }
        Assert.Contains(Secret, DecodedObjects(before));
        Assert.True(await app.OpenAsync(source));
        var mark = new RedactionMarkViewModel(Assert.Single(app.Model.Pages), new NormalizedRect(.08, .3, .75, .16));
        typeof(MainViewModel).GetMethod("AttachMark", PrivateInstance, [typeof(RedactionMarkViewModel)])!
            .Invoke(app.Model, [mark]);
        Assert.True(app.Model.HasRedactionMarks);
        var target = app.PathFor("secret (redacted).pdf");
        app.Dialogs.Answer("Confirm", true);
        app.Dialogs.Answer("PickSavePdfFile", target);

        await app.Model.ApplyRedactionsCommand.ExecuteAsync(null);

        Assert.Empty(app.Dialogs.Named("ShowError"));
        Assert.Single(app.Dialogs.Named("Confirm"));
        Assert.Equal("secret (redacted).pdf", Assert.Single(app.Dialogs.Named("PickSavePdfFile")).Arguments[0]);
        Assert.Equal(before, File.ReadAllBytes(source));
        await using (var output = await app.Engine.OpenAsync(target))
        {
            var text = await output.GetPageTextAsync(0);
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.Empty(await output.FindTextAsync(0, Secret, PdfSearchOptions.None));
            Assert.Contains(PublicText, text);
            Assert.NotEmpty(await output.FindTextAsync(0, PublicText, PdfSearchOptions.None));
        }
        var bytes = File.ReadAllBytes(target);
        Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, DecodedObjects(bytes), StringComparison.Ordinal);
        AssertFiles(app, source, target);
    }

    private static X509Certificate2 NewSigner()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=App signing test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static async Task<string> CreateSignedCopyAsync(AppTestHarness app, string source)
    {
        using var certificate = NewSigner();
        var target = app.PathFor("source (signed).pdf");
        File.WriteAllBytes(target, await PdfSigner.SignAsync(File.ReadAllBytes(source), new PdfSignRequest(certificate)));
        return target;
    }

    private static Task StartSigningAsync(AppTestHarness app) =>
        (Task)typeof(MainViewModel).GetMethod("StartDigitalSignAsync", PrivateInstance)!.Invoke(app.Model, [false])!;

    private static async Task WaitForSignaturesAsync(AppTestHarness app)
    {
        await ((Task)typeof(MainViewModel).GetField("_signatureFirstPassTask", PrivateInstance)!.GetValue(app.Model)!)
            .WaitAsync(TimeSpan.FromSeconds(30));
        await ((Task)typeof(MainViewModel).GetField("_signatureLoadTask", PrivateInstance)!.GetValue(app.Model)!)
            .WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void AssertFiles(AppTestHarness app, params string[] expected) =>
        Assert.Equal(expected.Order(), Directory.GetFiles(app.Folder, "*", SearchOption.AllDirectories).Order());

    private static byte[] CreateSecretPdf()
    {
        var document = new CosNewDocument();
        var pages = document.Reserve();
        var font = document.Add(new CosDictionary().Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Helvetica"))
            .Set("FirstChar", new CosInteger(32)).Set("LastChar", new CosInteger(126))
            .Set("Widths", new CosArray(Enumerable.Range(32, 95).Select(_ => new CosInteger(600)))));
        var content = $"BT /F1 10 Tf 40 120 Td ({Secret}) Tj ET BT /F1 10 Tf 40 30 Td ({PublicText}) Tj ET";
        var page = document.Add(new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", pages)
            .Set("MediaBox", CosArray.FromNumbers(0, 0, 400, 200))
            .Set("Resources", new CosDictionary().Set("Font", new CosDictionary().Set("F1", font)))
            .Set("Contents", document.Add(CosPageEdit.Stream(Encoding.ASCII.GetBytes(content)))));
        document.Set(pages, new CosDictionary().Set("Type", new CosName("Pages"))
            .Set("Count", new CosInteger(1)).Set("Kids", new CosArray([page])));
        return document.Build(document.Add(new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", pages)));
    }

    // Same object/decoded-stream inspection as RedactionSecurityTests, including detached objects.
    private static string DecodedObjects(byte[] bytes)
    {
        var document = CosDocument.Load(bytes);
        var text = new StringBuilder();
        var seen = new HashSet<CosObject>(ReferenceEqualityComparer.Instance);
        void Read(CosObject? value)
        {
            value = document.Resolve(value);
            if (value is null || !seen.Add(value)) return;
            switch (value)
            {
                case CosString s: text.AppendLine(s.ToText()); break;
                case CosArray a: foreach (var item in a.Items) Read(item); break;
                case CosStream s:
                    text.AppendLine(Encoding.Latin1.GetString(CosFilters.Decode(s, document.Resolve)));
                    Read(s.Dictionary);
                    break;
                case CosDictionary d: foreach (var item in d.Entries.Values) Read(item); break;
            }
        }
        foreach (var id in document.ObjectIds) Read(document.GetObject(id));
        return text.ToString();
    }
}
