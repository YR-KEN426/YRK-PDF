using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Media;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureSecurityTests
{
    private static readonly PdfVerifyOptions Offline = new(CheckRevocation: false);
    private static byte[] Source => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"));

    // Generated in memory for each test. No user IDs, settings or certificate-store changes.
    private static X509Certificate2 NewSigner()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Signature security test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    }

    [Theory]
    [InlineData(PdfCertification.None, PdfSignatureStatus.Modified)]
    [InlineData(PdfCertification.NoChanges, PdfSignatureStatus.Invalid)]
    [InlineData(PdfCertification.FormFilling, PdfSignatureStatus.Modified)]
    [InlineData(PdfCertification.FormFillingAndComments, PdfSignatureStatus.Modified)]
    public async Task Appended_page_change_is_never_valid(PdfCertification permission, PdfSignatureStatus expected)
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer, Certification: permission));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var page = document.GetPages()[0];
        update.Replace(page.Id, page.Dictionary.Clone().Set("Rotate", new CosInteger(90)));

        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Offline));
        Assert.True(result.IsIntact);
        Assert.False(result.CoversWholeDocument);
        Assert.Equal(expected, result.Status);
        Assert.Equal(permission, result.Certification);
        if (permission == PdfCertification.NoChanges)
        {
            Assert.Contains("allows no changes", result.Problem);
        }
        if (permission is PdfCertification.FormFilling or PdfCertification.FormFillingAndComments)
        {
            Assert.Contains(new SignatureInfoViewModel(result).Details, line => line == Loc.T(
                "The document was changed after certification. Whether these changes are permitted has not been checked."));
        }
    }

    [Theory]
    [InlineData(PdfCertification.NoChanges, 2, PdfSignatureStatus.Invalid)]
    [InlineData(PdfCertification.NoChanges, 0, PdfSignatureStatus.Invalid)]
    [InlineData(PdfCertification.FormFilling, 1, PdfSignatureStatus.Modified)]
    [InlineData(PdfCertification.FormFillingAndComments, 1, PdfSignatureStatus.Modified)]
    public async Task DocMDP_uses_signed_revision_even_if_latest_signature_permissions_are_replaced(
        PdfCertification signedPermission, int latestPermission, PdfSignatureStatus expected)
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer, Certification: signedPermission));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var perms = (CosDictionary)document.Resolve(document.Catalog["Perms"])!;
        var signatureRef = (CosReference)perms["DocMDP"]!;
        var signature = ((CosDictionary)document.Resolve(signatureRef)!).Clone();
        signature["Reference"] = latestPermission == 0 ? null : new CosArray([new CosDictionary()
            .Set("TransformMethod", new CosName("DocMDP"))
            .Set("TransformParams", new CosDictionary().Set("P", new CosInteger(latestPermission)))]);
        update.Replace(signatureRef.Id, signature);

        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Offline));
        Assert.True(result.IsIntact);
        Assert.True(result.IsCertification);
        Assert.Equal(signedPermission, result.Certification);
        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DSS_streams_arrays_and_VRI_are_validation_only(bool indirectArrays)
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer, Certification: PdfCertification.NoChanges));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var cert = update.Add(new CosStream(new CosDictionary(), signer.RawData));
        var crl = update.Add(new CosStream(new CosDictionary().Set("Filter", new CosName("FlateDecode")), CosFilters.Deflate([1, 2, 3])));
        var ocsp = update.Add(new CosStream(new CosDictionary(), new byte[] { 4, 5, 6 }));
        CosObject Array(CosObject value) => indirectArrays ? update.Add(new CosArray([value])) : new CosArray([value]);
        var entry = update.Add(new CosDictionary().Set("Type", new CosName("VRI"))
            .Set("Cert", Array(cert)).Set("CRL", Array(crl)).Set("OCSP", Array(ocsp)));
        var dss = new CosDictionary().Set("Type", new CosName("DSS"))
            .Set("Certs", Array(cert)).Set("CRLs", Array(crl)).Set("OCSPs", Array(ocsp))
            .Set("VRI", update.Add(new CosDictionary().Set(new string('A', 40), entry)));
        SetDss(document, update, dss);

        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Offline));
        Assert.True(result.IsIntact);
        Assert.True(result.CoversWholeDocument);
        Assert.False(result.ModifiedAfterSigning);
        Assert.NotEqual(PdfSignatureStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData("Certs")]
    [InlineData("CRLs")]
    [InlineData("OCSPs")]
    [InlineData("VRI")]
    [InlineData("Unknown")]
    [InlineData("StreamDictionary")]
    public async Task Page_changes_cannot_hide_behind_DSS_references(string route)
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var page = document.GetPages()[0];
        update.Replace(page.Id, page.Dictionary.Clone().Set("Rotate", new CosInteger(90)));
        var pageRef = new CosReference(page.Id);
        var dss = route switch
        {
            "VRI" => new CosDictionary().Set("VRI", new CosDictionary().Set(new string('B', 40),
                new CosDictionary().Set("Cert", new CosArray([pageRef])))),
            "StreamDictionary" => new CosDictionary().Set("Certs", new CosArray([
                update.Add(new CosStream(new CosDictionary().Set("Hidden", pageRef), signer.RawData))])),
            _ => new CosDictionary().Set(route, new CosArray([pageRef])),
        };
        SetDss(document, update, dss);

        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Offline));
        Assert.True(result.IsIntact);
        Assert.False(result.CoversWholeDocument);
        Assert.Equal(PdfSignatureStatus.Modified, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_page_content_stream_is_not_validation_data_even_with_a_valid_DSS_shape(bool wrongGeneration)
    {
        using var signer = NewSigner();
        var source = Source;
        if (wrongGeneration)
        {
            var original = CosDocument.Load(source);
            var page = original.GetPages()[0];
            var oldContents = original.Resolve(page.Dictionary["Contents"]) is CosArray streams ? streams[0] : page.Dictionary["Contents"];
            var oldRef = Assert.IsType<CosReference>(oldContents);
            var prepare = new CosIncrementalUpdate(original);
            prepare.Replace(page.Id, page.Dictionary.Clone().Set("Contents", new CosReference(oldRef.Id.Number, 99)));
            source = prepare.Build().Bytes;
        }
        var signed = await PdfSigner.SignAsync(source, new PdfSignRequest(signer));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var contents = document.GetPages()[0].Dictionary["Contents"]!;
        if (document.Resolve(contents) is CosArray array)
        {
            contents = array[0];
        }
        var reference = Assert.IsType<CosReference>(contents);
        var actualId = document.ObjectIds.Single(id => id.Number == reference.Id.Number);
        update.Replace(actualId, new CosStream(new CosDictionary(), "0 0 100 100 re f"u8.ToArray()));
        SetDss(document, update, new CosDictionary().Set("Certs", new CosArray([new CosReference(actualId)])));

        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Offline));
        Assert.True(result.IsIntact);
        Assert.False(result.CoversWholeDocument);
        Assert.Equal(PdfSignatureStatus.Modified, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cyclic_or_overdeep_DSS_references_are_bounded(bool cycle)
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var stream = update.Add(new CosStream(new CosDictionary(), signer.RawData));
        var first = update.Add(new CosArray([stream]));
        CosObject value = first;
        for (var i = 0; i < 40; i++)
        {
            value = update.Add(value);
        }
        if (cycle)
        {
            update.Replace(first.Id, first);
        }
        SetDss(document, update, new CosDictionary().Set("Certs", value));

        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Offline));
        Assert.False(result.CoversWholeDocument);
        Assert.Equal(PdfSignatureStatus.Modified, result.Status);
    }

    [Theory]
    [InlineData("Sig")]
    [InlineData("DocTimeStamp")]
    [InlineData("")]
    public void Encryption_leaves_signature_contents_in_the_clear_but_encrypts_other_strings(string type)
    {
        var contents = new CosString([1, 2, 3, 4], isHex: true);
        var signature = new CosDictionary().Set("Contents", contents).Set("Reason", CosString.FromText("test"));
        if (type.Length > 0)
        {
            signature["Type"] = new CosName(type);
        }
        else
        {
            signature["ByteRange"] = new CosArray([new CosInteger(0)]);
        }
        var encryption = new CosEncryption(RandomNumberGenerator.GetBytes(32), new CosDictionary());
        var encrypted = Assert.IsType<CosDictionary>(encryption.EncryptObject(signature, new CosObjectId(1, 0)));
        Assert.Same(contents, encrypted["Contents"]);
        Assert.NotEqual(((CosString)signature["Reason"]!).Bytes, ((CosString)encrypted["Reason"]!).Bytes);
    }

    [Theory]
    [InlineData(PdfSignatureStatus.Valid)]
    [InlineData(PdfSignatureStatus.Modified)]
    [InlineData(PdfSignatureStatus.Invalid)]
    public async Task Modified_signatures_have_a_warning_banner_and_never_a_green_status(PdfSignatureStatus status)
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var info = Assert.Single(PdfSignatureVerifier.Verify(signed, Offline)) with { Status = status, CoversWholeDocument = false };
        var signature = new SignatureInfoViewModel(info);
        // Skip constructors so tests cannot read real app settings or Digital IDs.
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel).GetField("_signatures", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, new[] { signature });
        Assert.Equal(Loc.T("Signed, but the document was changed after signing"), model.SignatureBannerText);
        Assert.Equal(signature, model.BannerSample);
        Assert.NotEqual(Color.FromRgb(0x10, 0x7C, 0x10), ((SolidColorBrush)signature.StatusBrush).Color);
        if (status != PdfSignatureStatus.Invalid)
        {
            Assert.Equal(Loc.T("Signed, but the document was changed after signing"), signature.StatusText);
        }
    }

    [Fact]
    public async Task Declining_signature_loss_stops_protection_before_the_password_dialog()
    {
        var document = DispatchProxy.Create<IPdfDocument, TestProxy>();
        ((TestProxy)(object)document).Handle = (method, _) => method.Name switch
        {
            "get_Permissions" => PdfPermissions.All,
            _ => throw new InvalidOperationException(method.Name),
        };
        string? warning = null;
        var dialogs = DispatchProxy.Create<IDialogService, TestProxy>();
        ((TestProxy)(object)dialogs).Handle = (method, args) =>
        {
            // AskProtection or any save operation after a refusal is a failure.
            Assert.Equal("Confirm", method.Name);
            warning = Assert.IsType<string>(args![0]);
            return false;
        };
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, value);
        Set("_document", document);
        Set("_dialogs", dialogs);
        Set("_signatures", new SignatureInfoViewModel[] { new(null!) }); // Only HasSignatures is read.

        await model.ProtectCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("Adding password protection rewrites the document and removes its digital signatures. The result is saved as a new file. Continue?"), warning);
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handle { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handle(targetMethod!, args);
    }

    private static void SetDss(CosDocument document, CosIncrementalUpdate update, CosDictionary dss) =>
        update.Replace(((CosReference)document.Trailer["Root"]!).Id, document.Catalog.Clone().Set("DSS", update.Add(dss)));
}
