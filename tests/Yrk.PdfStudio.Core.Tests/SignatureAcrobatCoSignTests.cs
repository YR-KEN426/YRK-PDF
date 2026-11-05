using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureAcrobatCoSignTests
{
    // All PDF bytes and IDs are synthetic. No files, user settings, PFX imports or trust-store writes.
    private static readonly PdfVerifyOptions Offline = new(CheckRevocation: false)
    { CustomTrustRoots = new X509Certificate2Collection() };

    private static X509Certificate2 NewSigner()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Synthetic Acrobat co-signer", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acrobat_incremental_cosign_preserves_the_first_signature(bool indirectInfo)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(indirectInfo), new PdfSignRequest(signer));
        var original = Assert.Single(PdfSignatureVerifier.Verify(first, Offline));
        Assert.Equal(first.Length, original.SignedLength);
        var second = await CoSign(first, signer);
        Assert.True(second.AsSpan(0, first.Length).SequenceEqual(first));

        var before = CosDocument.Load(first);
        var after = CosDocument.Load(second);
        Assert.IsType<CosArray>(before.GetPages()[0].Dictionary["Annots"]);
        Assert.Equal(115, Assert.IsType<CosReference>(after.GetPages()[0].Dictionary["Annots"]).Id.Number);
        Assert.Equal(3, Assert.IsType<CosArray>(after.Resolve(Ref(115))).Count);
        Assert.Null(Assert.IsType<CosDictionary>(after.Resolve(Ref(95)))["V"]);
        var oldInfo = Assert.IsType<CosDictionary>(before.Resolve(Ref(21)));
        var newInfo = Assert.IsType<CosDictionary>(after.Resolve(Ref(21)));
        foreach (var key in new[] { "Creator", "Producer", "Author", "CreationDate" })
        {
            var oldText = Assert.IsType<CosString>(before.Resolve(oldInfo[key]));
            var newText = Assert.IsType<CosString>(after.Resolve(newInfo[key]));
            Assert.NotEqual(oldText.Bytes, newText.Bytes);
            Assert.Equal(oldText.ToText(), newText.ToText());
        }

        var results = PdfSignatureVerifier.Verify(second, Offline);
        Assert.Equal(2, results.Count);
        Assert.Equal(first.Length, results[0].SignedLength);
        Assert.All(results, r => Assert.True(r.IsIntact));
        Assert.All(results, r => Assert.False(r.ModifiedAfterSigning));
        Assert.All(results, r => Assert.Equal(PdfSignatureStatus.IdentityUnknown, r.Status));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("title")]
    [InlineData("author")]
    [InlineData("creator")]
    [InlineData("creation-date")]
    [InlineData("reorder-annots")]
    [InlineData("remove-annot")]
    [InlineData("change-annot")]
    [InlineData("page-resources")]
    [InlineData("page-key")]
    [InlineData("field-value")]
    [InlineData("shared-font")]
    [InlineData("shared-info")]
    [InlineData("remove-title")]
    [InlineData("add-info")]
    public async Task Acrobat_cosign_does_not_hide_document_changes(string change)
    {
        using var signer = NewSigner();
        var first = await PdfSigner.SignAsync(Source(indirectInfo: change == "shared-info",
            includeTextField: change == "field-value", shareInfoWithPage: change == "shared-info"), new PdfSignRequest(signer));
        var second = await CoSign(first, signer, change);
        var results = PdfSignatureVerifier.Verify(second, Offline);
        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsIntact);
        Assert.Equal(change != "field-value", results[0].ModifiedAfterSigning);
        Assert.Equal(change == "field-value", results[0].FormFilledAfterSigning);
        Assert.Equal(change == "field-value" ? PdfSignatureStatus.IdentityUnknown : PdfSignatureStatus.Modified, results[0].Status);
        Assert.False(results[1].ModifiedAfterSigning);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, results[1].Status);
    }

    private static CosReference Ref(int number) => new(number, 0);
    private static CosString Utf16(string text) => new([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)], isHex: true);
    private static CosString PdfDoc(string text) => new(Encoding.Latin1.GetBytes(text));

    private static byte[] Source(bool indirectInfo = false, bool includeTextField = false, bool shareInfoWithPage = false)
    {
        var pdf = new CosNewDocument();
        for (var i = 0; i < 102; i++) pdf.Reserve();
        pdf.Set(Ref(1), new CosDictionary().Set("Type", new CosName("Catalog")).Set("Pages", Ref(2))
            .Set("AcroForm", Ref(96)).Set("Metadata", Ref(89)));
        pdf.Set(Ref(2), new CosDictionary().Set("Type", new CosName("Pages"))
            .Set("Kids", new CosArray([Ref(3)])).Set("Count", new CosInteger(1)));
        pdf.Set(Ref(3), new CosDictionary().Set("Type", new CosName("Page")).Set("Parent", Ref(2))
            .Set("MediaBox", CosArray.FromNumbers(0, 0, 612, 792)).Set("Contents", Ref(4))
            .Set("Resources", new CosDictionary().Set("Font", new CosDictionary().Set("PageFont", Ref(5))))
            .Set("Group", new CosDictionary().Set("S", new CosName("Transparency")))
            .Set("StructParents", new CosInteger(0)).Set("Tabs", new CosName("S"))
            .Set("Annots", new CosArray([Ref(95)])));
        pdf.Set(Ref(4), new CosStream(new CosDictionary(), "BT /PageFont 12 Tf (Synthetic) Tj ET"u8.ToArray()));
        pdf.Set(Ref(5), SimpleFont("Helvetica"));
        pdf.Set(Ref(21), new CosDictionary().Set("Title", Utf16("Synthetic title"))
            .Set("Author", Utf16("Synthetic author")).Set("CreationDate", Utf16("D:20260101000000Z"))
            .Set("Creator", Utf16("Synthetic Creator®")).Set("Producer", Utf16("Synthetic Producer®"))
            .Set("ModDate", Utf16("D:20260101000000Z")));
        pdf.Set(Ref(89), Metadata("original"));
        pdf.Set(Ref(95), new CosDictionary().Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Widget"))
            .Set("FT", new CosName("Sig")).Set("T", CosString.FromText("Empty"))
            .Set("P", Ref(3)).Set("Rect", CosArray.FromNumbers(0, 0, 0, 0)));
        // A form made by Acrobat also has /Encoding in its default resources.
        pdf.Set(Ref(90), new CosDictionary().Set("Type", new CosName("Encoding"))
            .Set("Differences", new CosArray([new CosInteger(24), new CosName("breve")])));
        pdf.Set(Ref(96), new CosDictionary().Set("Fields", new CosArray([Ref(95)]))
            .Set("DA", CosString.FromText("/Helv 0 Tf 0 g")).Set("NeedAppearances", new CosBoolean(false))
            .Set("SigFlags", new CosInteger(3)).Set("DR", new CosDictionary().Set("Font", new CosDictionary())
                .Set("Encoding", new CosDictionary().Set("PDFDocEncoding", Ref(90)))));
        if (includeTextField)
        {
            pdf.Set(Ref(97), new CosDictionary().Set("FT", new CosName("Tx")).Set("T", PdfDoc("ExistingText"))
                .Set("V", PdfDoc("Original value")));
            // This negative variant adds a text field; the positive fixture retains exactly 95,104.
            pdf.Set(Ref(96), new CosDictionary().Set("Fields", new CosArray([Ref(95), Ref(97)]))
                .Set("DA", PdfDoc("/Helv 0 Tf 0 g")).Set("NeedAppearances", new CosBoolean(false))
                .Set("SigFlags", new CosInteger(3)).Set("DR", new CosDictionary().Set("Font", new CosDictionary())));
        }
        if (indirectInfo)
        {
            var info = new CosDictionary().Set("Title", Utf16("Synthetic title"))
                .Set("ModDate", Utf16("D:20260101000000Z"));
            var keys = new[] { "Creator", "Producer", "Author", "CreationDate" };
            var texts = new[] { "Synthetic Creator®", "Synthetic Producer®", "Synthetic author", "D:20260101000000Z" };
            for (var i = 0; i < keys.Length; i++)
            {
                pdf.Set(Ref(6 + i), Utf16(texts[i]));
                info[keys[i]] = Ref(6 + i);
            }
            pdf.Set(Ref(21), info);
        }
        var doc = CosDocument.Load(pdf.Build(Ref(1), Ref(21)));
        // Add /ID in the trailer without allocating objects, so the app's widget is exactly 104.
        doc.Trailer["ID"] = new CosArray([PdfDoc("stable-id"), PdfDoc("first-revision-id")]);
        var prepare = new CosIncrementalUpdate(doc);
        if (shareInfoWithPage)
        {
            var page = doc.GetPages()[0];
            var resources = Assert.IsType<CosDictionary>(page.Dictionary["Resources"]).Clone()
                .Set("Properties", new CosDictionary().Set("SharedText", Ref(6)));
            prepare.Replace(page.Id, page.Dictionary.Clone().Set("Resources", resources));
        }
        return prepare.Build().Bytes;
    }

    private static CosDictionary SimpleFont(string name) => new CosDictionary().Set("Type", new CosName("Font"))
        .Set("Subtype", new CosName("Type1")).Set("BaseFont", new CosName(name));

    private static CosStream Metadata(string value) => new(new CosDictionary().Set("Type", new CosName("Metadata"))
        .Set("Subtype", new CosName("XML")), Encoding.UTF8.GetBytes($"<x:xmpmeta>{value}</x:xmpmeta>"));

    private static async Task<byte[]> CoSign(byte[] first, X509Certificate2 signer, string? change = null)
    {
        var doc = CosDocument.Load(first);
        var update = new CosIncrementalUpdate(doc);
        void Put(int number, CosObject value) => update.Replace(Ref(number).Id, value);
        const string rangeText = "[0 0000000000 0000000000 0000000000]";
        const int reserve = 16384;
        var range = new CosPlaceholder(rangeText);
        var contents = new CosPlaceholder("<" + new string('0', reserve * 2) + ">");
        Put(117, new CosDictionary().Set("Type", new CosName("Sig")).Set("Filter", new CosName("Adobe.PPKLite"))
            .Set("SubFilter", new CosName("ETSI.CAdES.detached")).Set("ByteRange", range).Set("Contents", contents));

        Put(114, SimpleFont("MyriadPro-Regular").Set("Encoding", new CosName("WinAnsiEncoding"))
            .Set("FirstChar", new CosInteger(32)).Set("LastChar", new CosInteger(33))
            .Set("Widths", Ref(130)).Set("FontDescriptor", Ref(124)));
        Put(124, Descriptor("MyriadPro-Regular").Set("FontFile3", Ref(125)));
        Put(125, new CosStream(new CosDictionary().Set("Subtype", new CosName("Type1C")), new byte[] { 1, 2, 3 }));
        Put(130, CosArray.FromNumbers(250, 300));
        for (var i = 0; i < 2; i++)
        {
            var start = 131 + i * 8;
            Put(122 + i, new CosDictionary().Set("Type", new CosName("Font")).Set("Subtype", new CosName("Type0"))
                .Set("BaseFont", new CosName($"DummyCID{i}")).Set("Encoding", new CosName("Identity-H"))
                .Set("ToUnicode", Ref(start)).Set("DescendantFonts", Ref(start + 1)));
            Put(start, new CosStream(new CosDictionary(), "begincmap endcmap"u8.ToArray()));
            Put(start + 1, new CosArray([Ref(start + 2)]));
            Put(start + 2, new CosDictionary().Set("Type", new CosName("Font")).Set("Subtype", new CosName("CIDFontType0"))
                .Set("BaseFont", new CosName($"DummyCID{i}")).Set("CIDSystemInfo", Ref(start + 3))
                .Set("DW", new CosInteger(1000)).Set("W", Ref(start + 4)).Set("FontDescriptor", Ref(start + 5)));
            Put(start + 3, new CosDictionary().Set("Registry", PdfDoc("Adobe")).Set("Ordering", PdfDoc("Identity"))
                .Set("Supplement", new CosInteger(0)));
            Put(start + 4, new CosArray([new CosInteger(0), CosArray.FromNumbers(500, 600)]));
            Put(start + 5, Descriptor($"DummyCID{i}").Set("CIDSet", Ref(start + 6)).Set("FontFile3", Ref(start + 7)));
            Put(start + 6, new CosStream(new CosDictionary(), new byte[] { 0xFF }));
            Put(start + 7, new CosStream(new CosDictionary().Set("Subtype", new CosName("CIDFontType0C")), new byte[] { 4, 5, 6 }));
        }

        CosStream Form(CosDictionary resources, string operators) => new(new CosDictionary()
            .Set("Type", new CosName("XObject")).Set("Subtype", new CosName("Form"))
            .Set("BBox", CosArray.FromNumbers(0, 0, 150, 40)).Set("Resources", resources), Encoding.ASCII.GetBytes(operators));
        Put(118, Form(new CosDictionary().Set("XObject", new CosDictionary().Set("FRM", Ref(119))), "/FRM Do"));
        Put(119, Form(new CosDictionary().Set("XObject", new CosDictionary().Set("n0", Ref(120)).Set("n2", Ref(121))), "/n0 Do /n2 Do"));
        Put(120, Form(new CosDictionary(), ""));
        Put(121, Form(new CosDictionary().Set("Font", new CosDictionary().Set("C0_0", Ref(122))
                .Set("C0_1", Ref(123)).Set("T1_0", Ref(114)))
            .Set("ProcSet", new CosArray([new CosName("PDF"), new CosName("Text")])), "BT /T1_0 10 Tf (Signed) Tj ET"));
        Put(116, new CosDictionary().Set("AP", new CosDictionary().Set("N", Ref(118)))
            .Set("DA", CosString.FromText("/MyriadPro-Regular 0 Tf 0 Tz 0 g")).Set("F", new CosInteger(132))
            .Set("FT", new CosName("Sig")).Set("MK", new CosDictionary()).Set("P", Ref(3))
            .Set("Rect", CosArray.FromNumbers(20, 20, 170, 60)).Set("Subtype", new CosName("Widget"))
            .Set("T", CosString.FromText("Signature4")).Set("Type", new CosName("Annot")).Set("V", Ref(117)));

        var annots = new CosArray([Ref(95), Ref(104), Ref(116)]);
        if (change == "reorder-annots") annots = new CosArray([Ref(104), Ref(95), Ref(116)]);
        if (change == "remove-annot") annots = new CosArray([Ref(104), Ref(116)]);
        Put(115, annots);
        // A placeholder deliberately varies whitespace and key order without changing page values.
        var page = new CosDictionary();
        foreach (var (key, value) in doc.GetPages()[0].Dictionary.Entries.Reverse()) page[key] = value;
        page["Annots"] = Ref(115);
        if (change == "page-resources") page["Resources"] = new CosDictionary();
        if (change == "page-key") page["Tabs"] = new CosName("R");
        using (var buffer = new MemoryStream())
        {
            new CosWriter(buffer).Write(page);
            Put(3, new CosPlaceholder(Encoding.Latin1.GetString(buffer.ToArray()).Replace("/", "\n /", StringComparison.Ordinal)));
        }
        var form = Assert.IsType<CosDictionary>(doc.Resolve(Ref(96))).Clone();
        var fields = Assert.IsType<CosArray>(doc.Resolve(form["Fields"])).Clone();
        fields.Items.Add(Ref(116));
        form["Fields"] = fields;
        // Acrobat keeps the form's /Encoding resource and adds its font.
        form["DR"] = Assert.IsType<CosDictionary>(doc.Resolve(form["DR"])).Clone()
            .Set("Font", new CosDictionary().Set("MyriadPro-Regular", Ref(114)));
        Put(96, form);
        var info = Assert.IsType<CosDictionary>(doc.Resolve(Ref(21))).Clone();
        foreach (var key in new[] { "Creator", "Producer", "Author", "CreationDate" })
        {
            var text = PdfDoc(Assert.IsType<CosString>(doc.Resolve(info[key])).ToText());
            if (info[key] is CosReference reference) Put(reference.Id.Number, text);
            else info[key] = text;
        }
        info["ModDate"] = PdfDoc("D:20261105000000Z");
        if (change == "title") info["Title"] = PdfDoc("Changed title");
        if (change == "remove-title") info["Title"] = null;
        if (change == "add-info") info["Added"] = new CosInteger(1);
        if (change == "author") info["Author"] = PdfDoc("Changed author");
        if (change == "creator") info["Creator"] = PdfDoc("Changed creator");
        if (change == "creation-date") info["CreationDate"] = PdfDoc("D:20261105000000Z");
        Put(21, info);
        Put(89, Metadata("co-signed"));
        doc.Trailer["ID"] = new CosArray([PdfDoc("stable-id"), PdfDoc("second-revision-id")]);
        if (change == "content") Put(4, new CosStream(new CosDictionary(), "0 0 100 100 re f"u8.ToArray()));
        if (change == "change-annot")
        {
            Put(95, Assert.IsType<CosDictionary>(doc.Resolve(Ref(95))).Clone().Set("Rect", CosArray.FromNumbers(1, 1, 2, 2)));
        }
        if (change == "field-value")
            Put(97, Assert.IsType<CosDictionary>(doc.Resolve(Ref(97))).Clone().Set("V", PdfDoc("Changed value")));
        if (change == "shared-font")
        {
            // A newly embedded font must not whitelist a changed font used by page content.
            Put(5, SimpleFont("Courier").Set("FontDescriptor", Ref(124)));
        }

        var (bytes, offsets) = update.Build();
        var begin = (int)offsets[contents];
        var end = begin + reserve * 2 + 2;
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"[0 {begin} {end} {bytes.Length - end}")
            .PadRight(rangeText.Length - 1) + "]").CopyTo(bytes, (int)offsets[range]);
        var cms = await CadesSignature.CreateAsync(bytes[..begin].Concat(bytes[end..]).ToArray(), signer, null, default);
        Assert.True(cms.Length <= reserve);
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(bytes, begin + 1);
        return bytes;
    }

    private static CosDictionary Descriptor(string name) => new CosDictionary().Set("Type", new CosName("FontDescriptor"))
        .Set("FontName", new CosName(name)).Set("Flags", new CosInteger(4)).Set("FontBBox", CosArray.FromNumbers(0, -200, 1000, 800))
        .Set("ItalicAngle", new CosInteger(0)).Set("Ascent", new CosInteger(800)).Set("Descent", new CosInteger(-200))
        .Set("CapHeight", new CosInteger(700)).Set("StemV", new CosInteger(80));
}
