using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Media;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureCoSigningTests
{
    private static byte[] Source => SignatureFormFillTests.BlankDocument();

    private static X509Certificate2 NewSigner()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Co-signing test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static PdfVerifyOptions Trust(params X509Certificate2[] certificates) =>
        new(CheckRevocation: false) { CustomTrustRoots = new X509Certificate2Collection(certificates) };

    private static MainViewModel Banner(params PdfSignatureInfo[] infos)
    {
        // Never run the app constructor or access user settings/IDs or the Windows trust store.
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel).GetField("_signatures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, infos.Select(i => new SignatureInfoViewModel(i)).ToArray());
        return model;
    }

    [Theory]
    [InlineData(PdfCertification.None, false)]
    [InlineData(PdfCertification.None, true)]
    [InlineData(PdfCertification.FormFilling, true)]
    [InlineData(PdfCertification.FormFillingAndComments, true)]
    public async Task Two_app_signatures_are_valid_and_green(PdfCertification permission, bool visible)
    {
        using var a = NewSigner();
        using var b = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(a, Certification: permission));
        var appearance = visible ? new PdfSignatureAppearance(0, new NormalizedRect(.1, .1, .3, .1),
            new PdfImage(1, 1, [20, 40, 80, 128])) : null;
        var second = await PdfSigner.SignAsync(first, new PdfSignRequest(b, appearance));

        Assert.False(CosDocument.Load(first).WasReconstructed, "first reconstructed");
        Assert.False(CosDocument.Load(second).WasReconstructed, "second reconstructed");
        Assert.Equal(CosDocument.Load(first).StartXref, ((CosInteger)CosDocument.Load(second).Trailer["Prev"]!).Value);

        var results = PdfSignatureVerifier.Verify(second, Trust(a, b));
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(PdfSignatureStatus.Valid, r.Status);
            Assert.True(r.IsIntact);
            Assert.False(r.ModifiedAfterSigning);
            Assert.Equal(Color.FromRgb(0x10, 0x7C, 0x10), ((SolidColorBrush)new SignatureInfoViewModel(r).StatusBrush).Color);
        });
        Assert.False(results[0].CoversWholeDocument);
        var firstView = new SignatureInfoViewModel(results[0]);
        Assert.True(firstView.CanViewSignedVersion);
        Assert.Contains(Loc.T("Only signatures or validation data were added after this signature."), firstView.Details);
        Assert.True(results[1].CoversWholeDocument);
        var banner = Banner(results.ToArray());
        Assert.Equal(Loc.T("Signed and all signatures are valid."), banner.SignatureBannerText);
        Assert.Equal(results[0], banner.BannerSample!.Info);

        // Without explicit test trust, cryptographic validity must not confer trust on our IDs.
        Assert.All(PdfSignatureVerifier.Verify(second, new(CheckRevocation: false)),
            r => Assert.Equal(PdfSignatureStatus.IdentityUnknown, r.Status));
    }

    [Fact]
    public async Task Content_between_app_signatures_modifies_A_but_B_is_valid()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var doc = CosDocument.Load(first);
        var update = new CosIncrementalUpdate(doc);
        ChangeContent(doc, update);
        var second = await PdfSigner.SignAsync(update.Build().Bytes, new PdfSignRequest(signer));
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(2, results.Count);
        Assert.True(results[0].ModifiedAfterSigning);
        Assert.Equal(PdfSignatureStatus.Modified, results[0].Status);
        Assert.False(results[1].ModifiedAfterSigning);
        Assert.Equal(PdfSignatureStatus.Valid, results[1].Status);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("page-resource")]
    [InlineData("annotation")]
    [InlineData("field-value")]
    [InlineData("page-rotate")]
    [InlineData("malformed-annots")]
    [InlineData("shared-appearance")]
    [InlineData("aliased-appearance")]
    public async Task Signing_revision_cannot_hide_document_edits(string change)
    {
        using var signer = NewSigner();
        var source = change == "field-value" ? PrepareField(Source, signature: false, split: false) : Source;
        var first = await PdfSigner.SignAsync(source, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, (doc, update, widget) =>
        {
            var page = doc.GetPages()[0];
            switch (change)
            {
                case "content":
                    ChangeContent(doc, update);
                    break;
                case "page-resource":
                    update.Replace(page.Id, page.Dictionary.Clone().Set("Resources", new CosDictionary()));
                    break;
                case "annotation":
                    var annots = ((CosArray)doc.Resolve(page.Dictionary["Annots"])!).Clone();
                    annots.Items.Add(update.Add(new CosDictionary().Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Text"))));
                    update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", annots));
                    break;
                case "field-value":
                    var form = (CosDictionary)doc.Resolve(doc.Catalog["AcroForm"])!;
                    var fields = (CosArray)doc.Resolve(form["Fields"])!;
                    var text = (CosReference)fields.Items.Single(v =>
                        (doc.Resolve(v) as CosDictionary)?["FT"] is CosName { Value: "Tx" });
                    update.Replace(text.Id, ((CosDictionary)doc.Resolve(text)!).Clone().Set("V", CosString.FromText("changed")));
                    break;
                case "page-rotate":
                    update.Replace(page.Id, page.Dictionary.Clone().Set("Rotate", new CosInteger(90)));
                    break;
                case "malformed-annots":
                    update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", update.Add(new CosDictionary())));
                    break;
                default:
                    var content = ChangeContent(doc, update);
                    var alias = change == "aliased-appearance" ? new CosReference(content.Id.Number, 99) : content;
                    widget["AP"] = new CosDictionary().Set("N", update.Add(new CosStream(new CosDictionary()
                        .Set("Subtype", new CosName("Form")).Set("Resources", new CosDictionary()
                            .Set("XObject", new CosDictionary().Set("Hidden", alias))), "q Q"u8.ToArray())));
                    break;
            }
        });
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsIntact);
        Assert.Equal(change != "field-value", results[0].ModifiedAfterSigning);
        Assert.Equal(change == "field-value", results[0].FormFilledAfterSigning);
        Assert.Equal(change == "field-value" ? PdfSignatureStatus.Valid : PdfSignatureStatus.Modified, results[0].Status);
        Assert.Equal(PdfSignatureStatus.Valid, results[1].Status);
    }

    [Fact]
    public async Task P1_followed_by_another_signature_is_invalid()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer, Certification: PdfCertification.NoChanges));
        // The app correctly refuses this operation; construct the prohibited revision as another writer could.
        var second = await AppendSignature(first, signer);
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(PdfSignatureStatus.Invalid, results[0].Status);
        Assert.False(results[0].ModifiedAfterSigning);
        Assert.Contains("allows no changes", results[0].Problem);
        Assert.Equal(PdfSignatureStatus.Valid, results[1].Status);
        var banner = Banner(results.ToArray());
        Assert.Equal(Loc.T("At least one signature is invalid."), banner.SignatureBannerText);
        Assert.Equal(results[0], banner.BannerSample!.Info);
    }

    [Fact]
    public async Task Invalid_banner_and_sample_take_priority_over_a_different_modified_signature()
    {
        using var signer = NewSigner();
        var bytes = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var info = Assert.Single(PdfSignatureVerifier.Verify(bytes, Trust(signer)));
        var modified = info with { Status = PdfSignatureStatus.Modified, ModifiedAfterSigning = true };
        var invalid = info with { Status = PdfSignatureStatus.Invalid, ModifiedAfterSigning = false };
        var banner = Banner(modified, invalid);
        Assert.Equal(Loc.T("At least one signature is invalid."), banner.SignatureBannerText);
        Assert.Same(invalid, banner.BannerSample!.Info);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Setting_V_on_an_existing_signature_field_is_allowed(bool split)
    {
        using var signer = NewSigner();
        var source = PrepareField(Source, signature: true, split);
        var first = await PdfSigner.SignAsync(source, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, (_, _, widget) =>
        {
            widget["MK"] = new CosDictionary().Set("BC", CosArray.FromNumbers(0));
            widget["BS"] = new CosDictionary().Set("W", new CosInteger(1));
            widget["Lock"] = new CosDictionary().Set("Type", new CosName("SigFieldLock")).Set("Action", new CosName("All"));
        });
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(PdfSignatureStatus.Valid, r.Status));
        Assert.All(results, r => Assert.False(r.ModifiedAfterSigning));
    }

    [Fact]
    public async Task Three_app_signatures_followed_by_DSS_are_allowed()
    {
        using var signer = NewSigner();
        var bytes = Source;
        for (var i = 0; i < 3; i++)
        {
            bytes = await PdfSigner.SignAsync(bytes, new PdfSignRequest(signer));
        }
        var doc = CosDocument.Load(bytes);
        var update = new CosIncrementalUpdate(doc);
        var cert = update.Add(new CosStream(new CosDictionary(), signer.RawData));
        update.Replace(((CosReference)doc.Trailer["Root"]!).Id, doc.Catalog.Clone().Set("DSS",
            update.Add(new CosDictionary().Set("Certs", new CosArray([cert])))));
        var results = PdfSignatureVerifier.Verify(update.Build().Bytes, Trust(signer));
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(PdfSignatureStatus.Valid, r.Status));
        Assert.All(results, r => Assert.False(r.ModifiedAfterSigning));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Appearance_traversal_has_depth_and_object_budgets(bool wide)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, (_, update, widget) =>
        {
            CosObject resource = new CosDictionary();
            if (wide)
            {
                var dictionary = new CosDictionary();
                for (var i = 0; i < 11000; i++)
                {
                    dictionary[i.ToString(CultureInfo.InvariantCulture)] = new CosName("DeviceRGB");
                }
                resource = dictionary;
            }
            else
            {
                for (var i = 0; i < 40; i++)
                {
                    resource = update.Add(new CosDictionary().Set("Next", resource));
                }
            }
            widget["AP"] = new CosDictionary().Set("N", update.Add(new CosStream(new CosDictionary()
                .Set("Subtype", new CosName("Form")).Set("Resources", new CosDictionary().Set("ColorSpace", resource)), "q Q"u8.ToArray())));
        });
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(PdfSignatureStatus.Modified, results[0].Status);
        Assert.True(results[0].ModifiedAfterSigning);
        Assert.Equal(PdfSignatureStatus.Valid, results[1].Status);
    }

    [Fact]
    public async Task Restoring_content_in_a_later_revision_does_not_erase_a_modification()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var original = CosDocument.Load(first);
        var change = new CosIncrementalUpdate(original);
        var reference = ChangeContent(original, change);
        var changed = CosDocument.Load(change.Build().Bytes);
        var restore = new CosIncrementalUpdate(changed);
        restore.Replace(reference.Id, original.GetObject(reference.Id)!);
        var second = await PdfSigner.SignAsync(restore.Build().Bytes, new PdfSignRequest(signer));
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(PdfSignatureStatus.Modified, results[0].Status);
        Assert.True(results[0].ModifiedAfterSigning);
        Assert.Equal(PdfSignatureStatus.Valid, results[1].Status);
    }

    [Fact]
    public async Task A_following_document_timestamp_is_signing_data()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, signatureType: "DocTimeStamp");
        var result = PdfSignatureVerifier.Verify(second, Trust(signer))[0];
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
        Assert.False(result.ModifiedAfterSigning);
    }

    [Fact]
    public async Task A_cyclic_appearance_resource_is_bounded()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, (_, update, widget) =>
        {
            var cycle = update.Add(new CosDictionary());
            update.Replace(cycle.Id, new CosDictionary().Set("Loop", cycle));
            widget["AP"] = new CosDictionary().Set("N", update.Add(new CosStream(new CosDictionary()
                .Set("Subtype", new CosName("Form")).Set("Resources", new CosDictionary().Set("Font", cycle)), "q Q"u8.ToArray())));
        });
        Assert.True(PdfSignatureVerifier.Verify(second, Trust(signer))[0].ModifiedAfterSigning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Appearance_can_share_existing_page_resources_but_cannot_change_them(bool change)
    {
        using var signer = NewSigner();
        var source = CosDocument.Load(Source);
        var prepare = new CosIncrementalUpdate(source);
        var font = prepare.Add(new CosDictionary().Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Helvetica")));
        var page = source.GetPages()[0];
        prepare.Replace(page.Id, page.Dictionary.Clone().Set("Resources", new CosDictionary()
            .Set("Font", new CosDictionary().Set("FTest", font))));
        var first = await PdfSigner.SignAsync(prepare.Build().Bytes, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, (_, update, widget) =>
        {
            if (change)
            {
                update.Replace(font.Id, new CosDictionary().Set("Type", new CosName("Font"))
                    .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Courier")));
            }
            widget["AP"] = new CosDictionary().Set("N", update.Add(new CosStream(new CosDictionary()
                .Set("Subtype", new CosName("Form")).Set("Resources", new CosDictionary()
                    .Set("Font", new CosDictionary().Set("FTest", font))), "q Q"u8.ToArray())));
        });
        var result = PdfSignatureVerifier.Verify(second, Trust(signer))[0];
        Assert.Equal(change, result.ModifiedAfterSigning);
        Assert.Equal(change ? PdfSignatureStatus.Modified : PdfSignatureStatus.Valid, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DSS_in_the_signing_revision_still_requires_bounded_validation_data(bool hiddenContent)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, (doc, update, _) =>
        {
            var data = hiddenContent ? ChangeContent(doc, update)
                : update.Add(new CosStream(new CosDictionary(), signer.RawData));
            update.Replace(((CosReference)doc.Trailer["Root"]!).Id, doc.Catalog.Clone().Set("DSS",
                update.Add(new CosDictionary().Set("Certs", new CosArray([data])))));
        });
        var result = PdfSignatureVerifier.Verify(second, Trust(signer))[0];
        Assert.Equal(hiddenContent, result.ModifiedAfterSigning);
        Assert.Equal(hiddenContent ? PdfSignatureStatus.Modified : PdfSignatureStatus.Valid, result.Status);
    }

    [Fact]
    public async Task Signature_reference_to_the_catalog_does_not_whitelist_document_objects()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, catalogReference: true);
        var third = await PdfSigner.SignAsync(second, new PdfSignRequest(signer));
        var results = PdfSignatureVerifier.Verify(third, Trust(signer));
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(PdfSignatureStatus.Valid, r.Status));
        Assert.All(results, r => Assert.False(r.ModifiedAfterSigning));
        var doc = CosDocument.Load(third);
        var change = new CosIncrementalUpdate(doc);
        ChangeContent(doc, change);
        Assert.All(PdfSignatureVerifier.Verify(change.Build().Bytes, Trust(signer)),
            r => Assert.True(r.ModifiedAfterSigning));
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("da-add")]
    [InlineData("da-change")]
    [InlineData("content")]
    [InlineData("font-change")]
    [InlineData("font-remove")]
    [InlineData("first-id")]
    [InlineData("shared-icon")]
    [InlineData("bad-MK")]
    public async Task External_cosigner_widget_fonts_and_metadata_are_bounded(string change)
    {
        using var signer = NewSigner();
        var source = CosDocument.Load(Source);
        var prepare = new CosIncrementalUpdate(source);
        var originalFont = prepare.Add(new CosDictionary().Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Helvetica")));
        var originalResources = new CosDictionary().Set("Font", new CosDictionary().Set("Original", originalFont));
        var initialForm = new CosDictionary().Set("Fields", new CosArray()).Set("DR", prepare.Add(originalResources));
        if (change != "da-add") initialForm["DA"] = CosString.FromText("/Original 10 Tf 0 g");
        prepare.Replace(((CosReference)source.Trailer["Root"]!).Id, source.Catalog.Clone()
            .Set("AcroForm", prepare.Add(initialForm))
            .Set("Metadata", prepare.Add(new CosStream(new CosDictionary().Set("Type", new CosName("Metadata"))
                .Set("Subtype", new CosName("XML")), "<x:xmpmeta>original</x:xmpmeta>"u8.ToArray()))));
        source.Trailer["Info"] = prepare.Add(new CosDictionary().Set("Title", CosString.FromText("preserved title"))
            .Set("ModDate", CosString.FromText("D:20260101000000Z")).Set("Producer", CosString.FromText("original producer")));
        source.Trailer["ID"] = new CosArray([CosString.FromText("first-id"), CosString.FromText("old-second-id")]);
        var first = await PdfSigner.SignAsync(prepare.Build().Bytes, new PdfSignRequest(signer));
        var second = await AppendSignature(first, signer, (doc, update, widget) =>
        {
            var icon = update.Add(new CosStream(new CosDictionary().Set("Type", new CosName("XObject"))
                .Set("Subtype", new CosName("Form")).Set("BBox", CosArray.FromNumbers(0, 0, 10, 10)), "q Q"u8.ToArray()));
            widget["MK"] = new CosDictionary().Set("R", new CosInteger(0)).Set("BC", CosArray.FromNumbers(0, 0, 0))
                .Set("BG", CosArray.FromNumbers(1)).Set("CA", CosString.FromText("Sign"))
                .Set("I", icon).Set("IF", new CosDictionary().Set("SW", new CosName("A")).Set("S", new CosName("P"))
                    .Set("A", CosArray.FromNumbers(.5, .5)).Set("FB", new CosBoolean(false)));
            widget["BS"] = new CosDictionary().Set("Type", new CosName("Border")).Set("W", new CosInteger(1))
                .Set("S", new CosName("D")).Set("D", CosArray.FromNumbers(3));
            widget["Border"] = new CosArray([new CosInteger(0), new CosInteger(0), new CosInteger(1), CosArray.FromNumbers(3)]);
            widget["Lock"] = new CosDictionary().Set("Type", new CosName("SigFieldLock")).Set("Action", new CosName("Include"))
                .Set("Fields", new CosArray([CosString.FromText("Second")]));
            widget["SV"] = new CosDictionary().Set("Type", new CosName("SV")).Set("Ff", new CosInteger(0))
                .Set("Filter", new CosName("Adobe.PPKLite")).Set("SubFilter", new CosArray([new CosName("ETSI.CAdES.detached")]))
                .Set("MDP", new CosDictionary().Set("P", new CosInteger(2)))
                .Set("Cert", new CosDictionary().Set("Type", new CosName("SVCert")).Set("Subject", new CosArray([new CosString(signer.RawData)])))
                .Set("TimeStamp", new CosDictionary().Set("URL", CosString.FromText("https://timestamp.example.invalid")).Set("Ff", new CosInteger(0)));
            widget["F"] = new CosInteger(4);
            widget["Q"] = new CosInteger(1);
            widget["DA"] = CosString.FromText("/External 10 Tf 0 g");
            widget["DR"] = new CosDictionary().Set("Font", new CosDictionary().Set("Original", originalFont));
            widget["TU"] = CosString.FromText("Signature tooltip");
            widget["TM"] = CosString.FromText("mapping");
            widget["Ff"] = new CosInteger(0);
            widget["StructParent"] = new CosInteger(1);
            widget["NM"] = CosString.FromText("external signature widget");
            widget["M"] = CosString.FromText("D:20261105000000Z");
            var infoRef = (CosReference)doc.Trailer["Info"]!;
            update.Replace(infoRef.Id, ((CosDictionary)doc.Resolve(infoRef)!).Clone()
                .Set("ModDate", CosString.FromText("D:20261105000000Z")).Set("Producer", CosString.FromText("External co-signer")));
            var metadata = (CosReference)doc.Catalog["Metadata"]!;
            update.Replace(metadata.Id, new CosStream(new CosDictionary().Set("Type", new CosName("Metadata"))
                .Set("Subtype", new CosName("XML")), "<x:xmpmeta>co-signed</x:xmpmeta>"u8.ToArray()));
            doc.Trailer["ID"] = new CosArray([CosString.FromText(change == "first-id" ? "changed" : "first-id"), CosString.FromText("new-second-id")]);
            if (change == "content") ChangeContent(doc, update);
            if (change == "shared-icon")
                ((CosDictionary)widget["MK"]!)["I"] = update.Add(new CosStream(new CosDictionary()
                    .Set("Subtype", new CosName("Form")).Set("BBox", CosArray.FromNumbers(0, 0, 10, 10))
                    .Set("Resources", new CosDictionary().Set("XObject", new CosDictionary().Set("Hidden", ChangeContent(doc, update)))), "q Q"u8.ToArray()));
            if (change == "bad-MK") ((CosDictionary)widget["MK"]!)["CA"] = new CosDictionary();
            if (change == "font-change") update.Replace(originalFont.Id, new CosDictionary().Set("Type", new CosName("Font"))
                .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Courier")));
        }, editForm: (doc, update, form) =>
        {
            var dr = ((CosDictionary)doc.Resolve(form["DR"])!).Clone();
            var fonts = ((CosDictionary)doc.Resolve(dr["Font"])!).Clone();
            fonts["External"] = update.Add(new CosDictionary().Set("Type", new CosName("Font"))
                .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Courier")));
            if (change == "font-remove") fonts["Original"] = null;
            dr["Font"] = fonts;
            form["DR"] = update.Add(dr);
            if (change is "da-add" or "da-change") form["DA"] = CosString.FromText("/External 10 Tf 0 g");
        });
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsIntact);
        var permitted = change is "allowed" or "da-add";
        Assert.Equal(!permitted, results[0].ModifiedAfterSigning);
        Assert.Equal(permitted ? PdfSignatureStatus.Valid : PdfSignatureStatus.Modified, results[0].Status);
        Assert.Equal(PdfSignatureStatus.Valid, results[1].Status);
    }

    private static byte[] PrepareField(byte[] bytes, bool signature, bool split)
    {
        var doc = CosDocument.Load(bytes);
        var update = new CosIncrementalUpdate(doc);
        var page = doc.GetPages()[0];
        var field = new CosDictionary().Set("FT", new CosName(signature ? "Sig" : "Tx"))
            .Set("T", CosString.FromText(signature ? "Pending" : "Text"));
        if (!signature)
        {
            field["V"] = CosString.FromText("original");
        }
        var fieldRef = update.Add(field);
        CosReference widgetRef;
        if (split)
        {
            var widget = new CosDictionary().Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Widget"))
                .Set("Parent", fieldRef).Set("P", new CosReference(page.Id)).Set("Rect", CosArray.FromNumbers(0, 0, 0, 0));
            widgetRef = update.Add(widget);
            field["Kids"] = new CosArray([widgetRef]);
        }
        else
        {
            field.Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Widget"))
                .Set("P", new CosReference(page.Id)).Set("Rect", CosArray.FromNumbers(0, 0, 0, 0));
            widgetRef = fieldRef;
        }
        var annots = (doc.Resolve(page.Dictionary["Annots"]) as CosArray)?.Clone() ?? new CosArray();
        annots.Items.Add(widgetRef);
        update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", annots));
        update.Replace(((CosReference)doc.Trailer["Root"]!).Id, doc.Catalog.Clone().Set("AcroForm",
            update.Add(new CosDictionary().Set("Fields", new CosArray([fieldRef])))));
        return update.Build().Bytes;
    }

    private static CosReference ChangeContent(CosDocument doc, CosIncrementalUpdate update)
    {
        var contents = doc.GetPages()[0].Dictionary["Contents"];
        if (doc.Resolve(contents) is CosArray a)
        {
            contents = a[0];
        }
        var reference = Assert.IsType<CosReference>(contents);
        update.Replace(reference.Id, new CosStream(new CosDictionary(), "0 0 100 100 re f"u8.ToArray()));
        return reference;
    }

    // Test writer for malformed/prohibited signing updates. CMS is still generated by the app.
    private static async Task<byte[]> AppendSignature(byte[] input, X509Certificate2 signer,
        Action<CosDocument, CosIncrementalUpdate, CosDictionary>? edit = null, string signatureType = "Sig", bool catalogReference = false,
        Action<CosDocument, CosIncrementalUpdate, CosDictionary>? editForm = null)
    {
        var doc = CosDocument.Load(input);
        var update = new CosIncrementalUpdate(doc);
        const string rangeText = "[0 0000000000 0000000000 0000000000]";
        const int reserve = 16384;
        var range = new CosPlaceholder(rangeText);
        var contents = new CosPlaceholder("<" + new string('0', reserve * 2) + ">");
        var signatureDictionary = new CosDictionary().Set("Type", new CosName(signatureType))
            .Set("SubFilter", new CosName(signatureType == "DocTimeStamp" ? "ETSI.RFC3161" : "ETSI.CAdES.detached"))
            .Set("ByteRange", range).Set("Contents", contents);
        if (catalogReference)
        {
            signatureDictionary["Reference"] = new CosArray([new CosDictionary().Set("Type", new CosName("SigRef"))
                .Set("TransformMethod", new CosName("FieldMDP")).Set("Data", doc.Trailer["Root"]!)]);
        }
        var signature = update.Add(signatureDictionary);
        var page = doc.GetPages()[0];
        var formRef = (CosReference)doc.Catalog["AcroForm"]!;
        var form = ((CosDictionary)doc.Resolve(formRef)!).Clone();
        var fields = ((CosArray)doc.Resolve(form["Fields"])!).Clone();
        var pending = fields.Items.OfType<CosReference>().FirstOrDefault(r =>
            doc.Resolve(r) is CosDictionary d && (d["T"] as CosString)?.ToText() == "Pending");
        CosDictionary widget;
        if (pending is not null)
        {
            var pendingField = ((CosDictionary)doc.Resolve(pending)!).Clone().Set("V", signature);
            update.Replace(pending.Id, pendingField);
            if (doc.Resolve(pendingField["Kids"]) is CosArray kids)
            {
                var widgetRef = (CosReference)kids[0];
                widget = ((CosDictionary)doc.Resolve(widgetRef)!).Clone();
                update.Replace(widgetRef.Id, widget);
            }
            else
            {
                widget = pendingField;
            }
        }
        else
        {
            widget = new CosDictionary().Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Widget"))
                .Set("FT", new CosName("Sig")).Set("T", CosString.FromText("Second"))
                .Set("V", signature).Set("Rect", CosArray.FromNumbers(0, 0, 0, 0)).Set("P", new CosReference(page.Id));
            var field = update.Add(widget);
            var annots = ((CosArray)doc.Resolve(page.Dictionary["Annots"])!).Clone();
            annots.Items.Add(field);
            update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", annots));
            fields.Items.Add(field);
            update.Replace(formRef.Id, form.Set("Fields", fields).Set("SigFlags", new CosInteger(3)));
        }
        edit?.Invoke(doc, update, widget);
        editForm?.Invoke(doc, update, form);
        var (bytes, offsets) = update.Build();
        var begin = (int)offsets[contents];
        var end = begin + reserve * 2 + 2;
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"[0 {begin} {end} {bytes.Length - end}")
            .PadRight(rangeText.Length - 1) + "]").CopyTo(bytes, (int)offsets[range]);
        var signedData = bytes[..begin].Concat(bytes[end..]).ToArray();
        byte[] cms;
        if (signatureType == "DocTimeStamp")
        {
            var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4"), new Oid(CadesSignature.Sha256),
                SHA256.HashData(signedData), new byte[] { 1 }, DateTimeOffset.UtcNow);
            var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
            token.ComputeSignature(new CmsSigner(signer));
            cms = token.Encode();
        }
        else
        {
            cms = await CadesSignature.CreateAsync(signedData, signer, null, default);
        }
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(bytes, begin + 1);
        return bytes;
    }
}
