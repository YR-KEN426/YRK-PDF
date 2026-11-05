using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Media;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureFormFillTests
{
    // Entirely synthetic, in memory. The first signature always uses the application's signer.
    private static CosReference Ref(int n) => new(n, 0);

    private static X509Certificate2 NewSigner()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Synthetic form fill", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static PdfVerifyOptions Trust(X509Certificate2 signer) => new(CheckRevocation: false)
    { CustomTrustRoots = new X509Certificate2Collection(signer) };

    internal static byte[] BlankDocument()
    {
        var pdf = new CosNewDocument();
        var root = pdf.Reserve();
        var pages = pdf.Reserve();
        var page = pdf.Reserve();
        var content = pdf.Add(new CosStream(new CosDictionary(), "q Q"u8.ToArray()));
        pdf.Set(root, new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", pages));
        pdf.Set(pages, new CosDictionary().Set("Type", new CosName("Pages")).Set("Count", new CosInteger(1)).Set("Kids", new CosArray([page])));
        pdf.Set(page, new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", pages)
            .Set("MediaBox", CosArray.FromNumbers(0, 0, 612, 792)).Set("Resources", new CosDictionary()
                .Set("Font", new CosDictionary().Set("F1", new CosDictionary().Set("Type", new CosName("Font"))
                    .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Helvetica")))))
            .Set("Contents", content));
        return pdf.Build(root);
    }

    private static byte[] Source(string type = "Tx", bool split = false, string? share = null, bool indirectValue = false)
    {
        var doc = CosDocument.Load(BlankDocument());
        var update = new CosIncrementalUpdate(doc);
        var page = doc.GetPages()[0];
        var field = new CosDictionary().Set("FT", new CosName(type)).Set("T", CosString.FromText("Customer"))
            .Set("Ff", new CosInteger(0)).Set("V", type == "Btn" ? new CosName("Off") : CosString.FromText("original"));
        var fieldRef = update.Add(field); // 5
        if (indirectValue) field["V"] = update.Add(field["V"]!);
        var widget = split ? new CosDictionary().Set("Parent", fieldRef) : field;
        widget.Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Widget"))
            .Set("Rect", CosArray.FromNumbers(10, 10, 210, 40)).Set("P", new CosReference(page.Id));
        var widgetRef = split ? update.Add(widget) : fieldRef;
        if (split) field["Kids"] = new CosArray([widgetRef]);
        var originalFont = update.Add(new CosDictionary().Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Helvetica")));
        var appearance = update.Add(Appearance(originalFont, "original"));
        widget["AP"] = new CosDictionary().Set("N", appearance);
        var annots = new CosArray([widgetRef]);
        CosReference? otherField = null;
        var resources = new CosDictionary();
        if (share is "page-font" or "page-ap")
            resources[share == "page-font" ? "Font" : "XObject"] = new CosDictionary().Set("Shared", share == "page-font" ? originalFont : appearance);
        if (share is "annotation-font" or "annotation-ap" or "widget-font" or "widget-ap")
        {
            var other = new CosDictionary().Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Text"))
                .Set("Rect", CosArray.FromNumbers(20, 50, 60, 70)).Set("AP", new CosDictionary()
                    .Set("N", share.EndsWith("-ap", StringComparison.Ordinal) ? appearance : update.Add(Appearance(originalFont, "other"))));
            var otherRef = update.Add(other);
            annots.Items.Add(otherRef);
            if (share.StartsWith("widget", StringComparison.Ordinal))
            {
                other.Set("Subtype", new CosName("Widget")).Set("FT", new CosName("Tx"))
                    .Set("T", CosString.FromText("Other")).Set("V", CosString.FromText("original"));
                otherField = otherRef;
            }
        }
        var fields = new CosArray([fieldRef]);
        if (otherField is not null) fields.Items.Add(otherField);
        var fonts = update.Add(new CosDictionary().Set("Original", originalFont));
        var form = update.Add(new CosDictionary().Set("Fields", fields).Set("DR", new CosDictionary().Set("Font", fonts))
            .Set("NeedAppearances", new CosBoolean(false)));
        update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", annots).Set("Resources", resources));
        update.Replace(((CosReference)doc.Trailer["Root"]!).Id, doc.Catalog.Clone().Set("AcroForm", form)
            .Set("Metadata", update.Add(Metadata("original"))));
        doc.Trailer["Info"] = update.Add(new CosDictionary().Set("Title", CosString.FromText("Synthetic"))
            .Set("ModDate", CosString.FromText("D:20260101000000Z")));
        return update.Build().Bytes;
    }

    private static CosStream Metadata(string text) => new(new CosDictionary().Set("Type", new CosName("Metadata"))
        .Set("Subtype", new CosName("XML")), Encoding.UTF8.GetBytes($"<x:xmpmeta>{text}</x:xmpmeta>"));

    private static CosStream Appearance(CosReference font, string text) => new(new CosDictionary()
        .Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Form"))
        .Set("BBox", CosArray.FromNumbers(0, 0, 200, 30)).Set("Resources", new CosDictionary()
            .Set("Font", new CosDictionary().Set("AdobeThai", font))), Encoding.ASCII.GetBytes($"BT /AdobeThai 12 Tf ({text}) Tj ET"));

    private static byte[] Fill(byte[] bytes, string? change = null, bool split = false, string type = "Tx")
    {
        var doc = CosDocument.Load(bytes);
        var update = new CosIncrementalUpdate(doc);
        var fieldRef = Ref(5);
        var field = ((CosDictionary)doc.Resolve(fieldRef)!).Clone();
        var widgetRef = split ? (CosReference)((CosArray)doc.Resolve(field["Kids"])!)[0] : fieldRef;
        var widget = split ? ((CosDictionary)doc.Resolve(widgetRef)!).Clone() : field;
        var oldAp = (CosReference)((CosDictionary)doc.Resolve(widget["AP"])!)["N"]!;
        var oldFont = (CosReference)((CosDictionary)((CosDictionary)((CosStream)doc.Resolve(oldAp)!).Dictionary["Resources"]!)["Font"]!)["AdobeThai"]!;

        // Acrobat-like Type0 -> CIDFont -> descriptor -> CIDSet/font program graph,
        // with indirect ToUnicode, CIDSystemInfo, widths, and descendant array.
        var program = update.Add(new CosStream(new CosDictionary().Set("Subtype", new CosName("CIDFontType0C")), new byte[] { 1, 2, 3 }));
        var descriptor = update.Add(new CosDictionary().Set("Type", new CosName("FontDescriptor"))
            .Set("FontName", new CosName("AdobeThai")).Set("Flags", new CosInteger(4))
            .Set("FontBBox", CosArray.FromNumbers(0, -200, 1000, 800)).Set("FontFile3", program)
            .Set("CIDSet", update.Add(new CosStream(new CosDictionary(), new byte[] { 0xFF }))));
        var cid = update.Add(new CosDictionary().Set("Type", new CosName("Font")).Set("Subtype", new CosName("CIDFontType0"))
            .Set("BaseFont", new CosName("AdobeThai")).Set("FontDescriptor", descriptor)
            .Set("W", update.Add(new CosArray([new CosInteger(0), CosArray.FromNumbers(500, 600)])))
            .Set("CIDSystemInfo", update.Add(new CosDictionary().Set("Registry", CosString.FromText("Adobe"))
                .Set("Ordering", CosString.FromText("Identity")).Set("Supplement", new CosInteger(0)))));
        var font = update.Add(new CosDictionary().Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type0"))
            .Set("BaseFont", new CosName("AdobeThai")).Set("Encoding", new CosName("Identity-H"))
            .Set("DescendantFonts", update.Add(new CosArray([cid])))
            .Set("ToUnicode", update.Add(new CosStream(new CosDictionary(), "begincmap endcmap"u8.ToArray()))));
        if (change == "font-javascript") update.Replace(descriptor.Id, new CosDictionary().Set("Type", new CosName("FontDescriptor"))
            .Set("AA", new CosDictionary().Set("K", new CosDictionary().Set("S", new CosName("JavaScript"))
                .Set("JS", CosString.FromText("app.alert('test')")))));
        CosObject value = type == "Btn" ? new CosName("Yes") : type == "Ch"
            ? new CosArray([CosString.FromText("filled")]) : CosString.FromText("filled");
        if (change == "indirect-value") update.Replace(((CosReference)field["V"]!).Id, value);
        else field["V"] = value;
        widget["AP"] = new CosDictionary().Set("N", update.Add(Appearance(font, "filled")));
        widget["AS"] = new CosName("Yes");
        if (change == "rect") widget["Rect"] = CosArray.FromNumbers(0, 0, 500, 500);
        if (change == "flags") field["Ff"] = new CosInteger(1);
        if (change == "action") field["AA"] = new CosDictionary().Set("K", new CosDictionary().Set("S", new CosName("JavaScript")).Set("JS", CosString.FromText("app.alert('test')")));
        if (change == "signature-value")
        {
            var sig = doc.ObjectIds.First(id => doc.GetObject(id) is CosDictionary d && d["FT"] is CosName { Value: "Sig" } && d["V"] is CosReference);
            var signatureField = ((CosDictionary)doc.GetObject(sig)!).Clone();
            var signature = ((CosDictionary)doc.Resolve(signatureField["V"])!).Clone();
            update.Replace(sig, signatureField.Set("V", update.Add(signature)));
        }
        update.Replace(fieldRef.Id, field);
        if (split) update.Replace(widgetRef.Id, widget);
        var formRef = (CosReference)doc.Catalog["AcroForm"]!;
        var form = ((CosDictionary)doc.Resolve(formRef)!).Clone();
        var dr = ((CosDictionary)doc.Resolve(form["DR"])!).Clone();
        var fontCategory = (CosReference)dr["Font"]!;
        var fonts = ((CosDictionary)doc.Resolve(fontCategory)!).Clone().Set("AdobeThai", font);
        if (change == "remove-font") fonts["Original"] = null;
        if (change == "replace-font") fonts["Original"] = font;
        update.Replace(fontCategory.Id, fonts); // Same category reference: still enforce add-only.
        form["NeedAppearances"] = new CosBoolean(true);
        update.Replace(formRef.Id, form);
        var infoRef = (CosReference)doc.Trailer["Info"]!;
        var info = ((CosDictionary)doc.Resolve(infoRef)!).Clone().Set("ModDate", CosString.FromText("D:20261105000000Z"))
            .Set("Producer", CosString.FromText("Synthetic Acrobat"));
        if (change == "title") info["Title"] = CosString.FromText("changed");
        update.Replace(infoRef.Id, info);
        update.Replace(((CosReference)doc.Catalog["Metadata"]!).Id, Metadata("filled"));
        var page = doc.GetPages()[0];
        if (change == "content") update.Replace(((CosReference)page.Dictionary["Contents"]!).Id, new CosStream(new CosDictionary(), "0 0 100 100 re f"u8.ToArray()));
        if (change is "annotation" or "remove-annotation")
        {
            var annots = ((CosArray)doc.Resolve(page.Dictionary["Annots"])!).Clone();
            if (change == "annotation") annots.Items.Add(update.Add(new CosDictionary().Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Text"))));
            else annots.Items.RemoveAt(0);
            update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", annots));
        }
        if (change == "page-resources") update.Replace(page.Id, page.Dictionary.Clone().Set("Resources", new CosDictionary().Set("Font", new CosDictionary().Set("New", font))));
        if (change == "shared-font") update.Replace(oldFont.Id, new CosDictionary().Set("Type", new CosName("Font"))
            .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName("Courier")));
        if (change == "shared-ap") update.Replace(oldAp.Id, Appearance(font, "changed shared appearance"));
        if (change == "cycle") update.Replace(program.Id, new CosStream(new CosDictionary().Set("Resources", new CosDictionary().Set("Font", new CosDictionary().Set("Loop", font))), new byte[] { 1 }));
        return update.Build().Bytes;
    }

    [Theory]
    [InlineData("Tx", false)]
    [InlineData("Tx", true)]
    [InlineData("Ch", false)]
    [InlineData("Ch", true)]
    [InlineData("Btn", false)]
    [InlineData("Btn", true)]
    public async Task Form_fill_with_Acrobat_font_graph_and_later_DSS_is_allowed(string type, bool split)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(type, split), new PdfSignRequest(signer));
        var filled = Fill(first, split: split, type: type);
        var doc = CosDocument.Load(filled);
        var update = new CosIncrementalUpdate(doc);
        update.Replace(((CosReference)doc.Trailer["Root"]!).Id, doc.Catalog.Clone().Set("DSS", update.Add(new CosDictionary()
            .Set("Certs", new CosArray([update.Add(new CosStream(new CosDictionary(), signer.RawData))])))));
        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Trust(signer)));
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
        Assert.True(result.IsIntact);
        Assert.True(result.FormFilledAfterSigning);
        Assert.False(result.ModifiedAfterSigning);
        Assert.Equal(["Customer"], result.FilledFieldNames);
        Assert.False(result.CoversWholeDocument);
        var view = new SignatureInfoViewModel(result);
        Assert.Equal(Loc.F("Signed and unchanged, but form fields were filled in after signing: {0}", "Customer"), view.StatusText);
        Assert.Equal(Color.FromRgb(0xC1, 0x7C, 0x00), ((SolidColorBrush)view.StatusBrush).Color);
        Assert.NotEqual("", view.Glyph);
        Assert.Contains(Loc.F("Form fields filled in after signing: {0}", "Customer"), view.Details);
        var unknown = Assert.Single(PdfSignatureVerifier.Verify(filled, new(CheckRevocation: false) { CustomTrustRoots = [] }));
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, unknown.Status);
        Assert.Equal(view.StatusText, new SignatureInfoViewModel(unknown).StatusText);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("annotation")]
    [InlineData("remove-annotation")]
    [InlineData("signature-value")]
    [InlineData("rect")]
    [InlineData("flags")]
    [InlineData("action")]
    [InlineData("font-javascript")]
    [InlineData("title")]
    [InlineData("remove-font")]
    [InlineData("replace-font")]
    [InlineData("page-resources")]
    [InlineData("cycle")]
    public async Task Form_fill_cannot_hide_other_changes(string change)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(), new PdfSignRequest(signer));
        var result = Assert.Single(PdfSignatureVerifier.Verify(Fill(first, change), Trust(signer)));
        Assert.True(result.ModifiedAfterSigning);
        Assert.False(result.FormFilledAfterSigning);
        Assert.Empty(result.FilledFieldNames);
        Assert.Equal(PdfSignatureStatus.Modified, result.Status);
    }

    [Theory]
    [InlineData("page-font")]
    [InlineData("page-ap")]
    [InlineData("annotation-font")]
    [InlineData("annotation-ap")]
    [InlineData("widget-font")]
    [InlineData("widget-ap")]
    public async Task Existing_shared_appearance_objects_cannot_change(string share)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(share: share), new PdfSignRequest(signer));
        var change = share.EndsWith("-font", StringComparison.Ordinal) ? "shared-font" : "shared-ap";
        var result = Assert.Single(PdfSignatureVerifier.Verify(Fill(first, change), Trust(signer)));
        Assert.True(result.ModifiedAfterSigning);
        Assert.Equal(PdfSignatureStatus.Modified, result.Status);
    }

    [Theory]
    [InlineData(PdfCertification.NoChanges)]
    [InlineData(PdfCertification.FormFilling)]
    [InlineData(PdfCertification.FormFillingAndComments)]
    public async Task Certification_permissions_apply_to_form_fill(PdfCertification permission)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(), new PdfSignRequest(signer, Certification: permission));
        var result = Assert.Single(PdfSignatureVerifier.Verify(Fill(first), Trust(signer)));
        Assert.Equal(permission == PdfCertification.NoChanges ? PdfSignatureStatus.Invalid : PdfSignatureStatus.Valid, result.Status);
        Assert.False(result.ModifiedAfterSigning);
        Assert.True(result.FormFilledAfterSigning);
        Assert.Equal(["Customer"], result.FilledFieldNames);
    }

    [Fact]
    public async Task Indirect_value_changes_are_reported()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(indirectValue: true), new PdfSignRequest(signer));
        var result = Assert.Single(PdfSignatureVerifier.Verify(Fill(first, "indirect-value"), Trust(signer)));
        Assert.True(result.FormFilledAfterSigning);
        Assert.False(result.ModifiedAfterSigning);
        Assert.Equal(["Customer"], result.FilledFieldNames);
    }

    [Fact]
    public async Task Inherited_field_type_and_qualified_names_are_reported()
    {
        using var signer = NewSigner();
        var doc = CosDocument.Load(Source(split: true));
        var prepare = new CosIncrementalUpdate(doc);
        var field = ((CosDictionary)doc.Resolve(Ref(5))!).Clone();
        var parent = prepare.Add(new CosDictionary().Set("FT", new CosName("Tx")).Set("T", CosString.FromText("Section"))
            .Set("Kids", new CosArray([Ref(5)])));
        field["FT"] = null;
        prepare.Replace(Ref(5).Id, field.Set("Parent", parent));
        var formRef = (CosReference)doc.Catalog["AcroForm"]!;
        prepare.Replace(formRef.Id, ((CosDictionary)doc.Resolve(formRef)!).Clone().Set("Fields", new CosArray([parent])));
        var first = await PdfSigner.SignAsync(prepare.Build().Bytes, new PdfSignRequest(signer));
        var result = Assert.Single(PdfSignatureVerifier.Verify(Fill(first, split: true), Trust(signer)));
        Assert.True(result.FormFilledAfterSigning);
        Assert.False(result.ModifiedAfterSigning);
        Assert.Equal(["Section.Customer"], result.FilledFieldNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_signature_reports_only_its_later_revisions(bool restoreContent)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(), new PdfSignRequest(signer));
        var filled = Fill(first, restoreContent ? "content" : null);
        if (restoreContent)
        {
            var doc = CosDocument.Load(filled);
            var restore = new CosIncrementalUpdate(doc);
            var content = (CosReference)doc.GetPages()[0].Dictionary["Contents"]!;
            restore.Replace(content.Id, CosDocument.Load(first).GetObject(content.Id)!);
            filled = restore.Build().Bytes;
        }
        var second = await PdfSigner.SignAsync(filled, new PdfSignRequest(signer));
        var results = PdfSignatureVerifier.Verify(second, Trust(signer));
        Assert.Equal(2, results.Count);
        Assert.Equal(restoreContent, results[0].ModifiedAfterSigning);
        Assert.Equal(!restoreContent, results[0].FormFilledAfterSigning);
        Assert.False(results[1].FormFilledAfterSigning);
        Assert.False(results[1].ModifiedAfterSigning);
        Assert.Empty(results[1].FilledFieldNames);
        Assert.Equal(PdfSignatureStatus.Valid, results[1].Status);
    }

    [Fact]
    public async Task Form_fill_cannot_add_new_non_signature_fields()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(), new PdfSignRequest(signer));
        var doc = CosDocument.Load(Fill(first));
        var update = new CosIncrementalUpdate(doc);
        var formRef = (CosReference)doc.Catalog["AcroForm"]!;
        var form = ((CosDictionary)doc.Resolve(formRef)!).Clone();
        var fields = ((CosArray)doc.Resolve(form["Fields"])!).Clone();
        fields.Items.Add(update.Add(new CosDictionary().Set("FT", new CosName("Tx")).Set("T", CosString.FromText("NewField"))
            .Set("V", CosString.FromText("new"))));
        update.Replace(formRef.Id, form.Set("Fields", fields));
        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Trust(signer)));
        Assert.True(result.ModifiedAfterSigning);
        Assert.False(result.FormFilledAfterSigning);
    }

    [Fact]
    public async Task NeedAppearances_is_a_form_update_even_without_a_field_value_change()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(), new PdfSignRequest(signer));
        var doc = CosDocument.Load(first);
        var update = new CosIncrementalUpdate(doc);
        var formRef = (CosReference)doc.Catalog["AcroForm"]!;
        update.Replace(formRef.Id, ((CosDictionary)doc.Resolve(formRef)!).Clone().Set("NeedAppearances", new CosBoolean(true)));
        var result = Assert.Single(PdfSignatureVerifier.Verify(update.Build().Bytes, Trust(signer)));
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
        Assert.False(result.ModifiedAfterSigning);
        Assert.True(result.FormFilledAfterSigning);
        Assert.Empty(result.FilledFieldNames);
    }

    private static MainViewModel Banner(params PdfSignatureInfo[] infos)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel).GetField("_signatures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, infos.Select(i => new SignatureInfoViewModel(i)).ToArray());
        return model;
    }

    [Fact]
    public async Task Banner_precedence_and_sample_match_the_warning()
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(), new PdfSignRequest(signer));
        var fill = Assert.Single(PdfSignatureVerifier.Verify(Fill(first), Trust(signer)));
        var valid = Assert.Single(PdfSignatureVerifier.Verify(first, Trust(signer)));
        var unknown = valid with { Status = PdfSignatureStatus.IdentityUnknown };
        var modified = valid with { Status = PdfSignatureStatus.Modified, ModifiedAfterSigning = true };
        var invalid = valid with { Status = PdfSignatureStatus.Invalid };
        var banner = Banner(valid, unknown, fill);
        Assert.Equal(Loc.T("Signed, but form fields were filled in after signing"), banner.SignatureBannerText);
        Assert.Same(fill, banner.BannerSample!.Info);
        banner = Banner(fill, modified, unknown);
        Assert.Equal(Loc.T("Signed, but the document was changed after signing"), banner.SignatureBannerText);
        Assert.Same(modified, banner.BannerSample!.Info);
        banner = Banner(fill, modified, invalid);
        Assert.Equal(Loc.T("At least one signature is invalid."), banner.SignatureBannerText);
        Assert.Same(invalid, banner.BannerSample!.Info);
    }
}
