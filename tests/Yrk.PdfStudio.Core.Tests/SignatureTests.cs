using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Pdfium;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureTests : IDisposable
{
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
    private static readonly PdfVerifyOptions Offline = new(CheckRevocation: false);
    private static readonly Lazy<X509Certificate2> Signer = new(() => LoadId("สมชาย ใจดี", "somchai@example.com"));

    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-sign-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Signs_invisibly_and_verifies()
    {
        var signed = await PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(Signer.Value, Reason: "อนุมัติ", Location: "กรุงเทพฯ"));

        var result = Assert.Single(PdfSignatureVerifier.Verify(signed, Offline));
        Assert.True(result.IsIntact);
        Assert.True(result.CoversWholeDocument);
        Assert.Equal("สมชาย ใจดี", result.SignerName);
        Assert.Equal("อนุมัติ", result.Reason);
        Assert.Equal("กรุงเทพฯ", result.Location);
        Assert.Equal("ETSI.CAdES.detached", result.SubFilter);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity); // Self-signed.
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
        Assert.Equal(0, result.PageIndex);

        // The original bytes are untouched (incremental update) and PDFium still reads the file.
        var original = File.ReadAllBytes(SamplePath);
        Assert.True(signed.AsSpan(0, original.Length).SequenceEqual(original));
        var path = PathFor("invisible.pdf");
        File.WriteAllBytes(path, signed);
        await using var document = await _engine.OpenAsync(path);
        await using var unsigned = await _engine.OpenAsync(SamplePath);
        Assert.Equal(unsigned.PageCount, document.PageCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Places_a_visible_signature_where_asked_on_rotated_pages(int quarterTurns)
    {
        var source = PathFor($"rotated-{quarterTurns}.pdf");
        await using (var document = await _engine.OpenAsync(SamplePath))
        {
            await document.RotatePagesAsync([1], quarterTurns);
            await document.SaveAsync(source);
        }

        var bounds = new NormalizedRect(0.55, 0.7, 0.3, 0.08);
        var output = PathFor($"rotated-{quarterTurns}-signed.pdf");
        await PdfSigner.SignFileAsync(source, output, new PdfSignRequest(Signer.Value, new PdfSignatureAppearance(1, bounds, SolidImage(120, 32))));

        await using var signed = await _engine.OpenAsync(output);
        var field = Assert.Single(await signed.GetFormFieldsAsync(1), f => f.Type == PdfFormFieldType.Signature);
        Assert.Equal(bounds.X, field.Bounds.X, 2);
        Assert.Equal(bounds.Y, field.Bounds.Y, 2);
        Assert.Equal(bounds.Width, field.Bounds.Width, 2);
        Assert.Equal(bounds.Height, field.Bounds.Height, 2);

        var result = Assert.Single(await PdfSignatureVerifier.VerifyFileAsync(output, Offline));
        Assert.True(result.IsIntact);
        Assert.Equal(1, result.PageIndex);
    }

    [Fact]
    public async Task Builds_the_layered_appearance_acrobat_expects()
    {
        // Adobe Acrobat fails to validate a visible signature ("Expected a dict object") without /FRM, /n0 and /n2.
        var bounds = new NormalizedRect(0.1, 0.1, 0.3, 0.08);
        var signed = await PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(Signer.Value, new PdfSignatureAppearance(0, bounds, SolidImage(120, 32))));

        var document = CosDocument.Load(signed);
        var widget = Assert.IsType<CosDictionary>(document.Resolve(((CosArray)document.Resolve(document.GetPages()[0].Dictionary["Annots"])!).Items[^1]));
        CosDictionary XObjects(CosObject? form) =>
            (CosDictionary)document.Resolve(((CosDictionary)document.Resolve(((CosStream)document.Resolve(form)!).Dictionary["Resources"])!)["XObject"])!;
        var normal = ((CosDictionary)document.Resolve(widget["AP"])!)["N"];
        var frm = XObjects(normal)["FRM"];
        Assert.NotNull(XObjects(frm)["n0"]);
        Assert.NotNull(XObjects(XObjects(frm)["n2"])["Im0"]);

        // The picture still shows.
        var path = PathFor("layered.pdf");
        File.WriteAllBytes(path, signed);
        await using var opened = await _engine.OpenAsync(path);
        var size = opened.PageSizes[0];
        var bitmap = await opened.RenderPageAsync(0, (int)size.Width, (int)size.Height);
        var x = (int)((bounds.X + (bounds.Width / 2)) * bitmap.Width);
        var y = (int)((bounds.Y + (bounds.Height / 2)) * bitmap.Height);
        var blue = bitmap.Pixels[(y * bitmap.Stride) + (x * 4)];
        var red = bitmap.Pixels[(y * bitmap.Stride) + (x * 4) + 2];
        Assert.True(blue > red + 50, $"Expected the blue picture, got B={blue} R={red}");
    }

    [Fact]
    public async Task Detects_a_changed_byte()
    {
        var signed = await PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(Signer.Value));
        var index = Array.IndexOf(signed, (byte)'M', 200);
        signed[index] = (byte)'N';

        var result = Assert.Single(PdfSignatureVerifier.Verify(signed, Offline));
        Assert.False(result.IsIntact);
        Assert.Equal(PdfSignatureStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Keeps_earlier_signatures_valid_through_later_updates()
    {
        var first = await PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(Signer.Value, Reason: "first"));
        var second = await PdfSigner.SignAsync(first, new PdfSignRequest(LoadId("Second Signer", null), new PdfSignatureAppearance(0, new NormalizedRect(0.1, 0.8, 0.3, 0.1), SolidImage(60, 20)), Reason: "second"));

        // A comment added afterwards by PDFium's incremental save.
        var signedPath = PathFor("two.pdf");
        File.WriteAllBytes(signedPath, second);
        var commented = PathFor("two-commented.pdf");
        await using (var document = await _engine.OpenAsync(signedPath))
        {
            await document.AddAnnotationAsync(0, new PdfNoteSpec(new NormalizedPoint(0.5, 0.5), new PdfColor(255, 230, 0), "later", "Ken"));
            await document.SaveAsync(commented);
        }

        var results = await PdfSignatureVerifier.VerifyFileAsync(commented, Offline);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntact));
        Assert.All(results, r => Assert.True(r.ModifiedAfterSigning));
        Assert.Equal(["first", "second"], results.Select(r => r.Reason));
        Assert.Equal(["Signature1", "Signature2"], results.Select(r => r.FieldName));

        // The signed revision of the first signature is exactly the first signed file.
        Assert.Equal(first, PdfSignatureVerifier.GetSignedRevision(File.ReadAllBytes(commented), results[0]));
    }

    [Fact]
    public async Task Adds_a_trusted_timestamp()
    {
        var time = new DateTimeOffset(2026, 10, 17, 7, 30, 0, TimeSpan.Zero);
        var signed = await PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(Signer.Value, TimestampAuthority: new FakeTimestampAuthority(time)));

        var result = Assert.Single(PdfSignatureVerifier.Verify(signed, Offline));
        Assert.True(result.IsIntact);
        Assert.True(result.IsTimestamped);
        Assert.Equal(time, result.SigningTime);
    }

    [Fact]
    public async Task Signs_files_with_object_and_xref_streams()
    {
        var source = TestPdfFactory.CreateXrefStreamPdf(PathFor("xrefstream.pdf"));
        var output = PathFor("xrefstream-signed.pdf");
        await PdfSigner.SignFileAsync(source, output, new PdfSignRequest(Signer.Value, new PdfSignatureAppearance(0, new NormalizedRect(0.1, 0.1, 0.3, 0.1), SolidImage(30, 10))));

        var update = CosDocument.Load(File.ReadAllBytes(output));
        Assert.True(update.UsesXrefStream);
        Assert.Single(update.GetPages());

        await using var document = await _engine.OpenAsync(output);
        Assert.Equal(1, document.PageCount);
        Assert.Contains(await document.GetFormFieldsAsync(0), f => f.Type == PdfFormFieldType.Signature);
        Assert.True(Assert.Single(await PdfSignatureVerifier.VerifyFileAsync(output, Offline)).IsIntact);
    }

    [Fact]
    public async Task Keeps_existing_form_fields()
    {
        var source = TestPdfFactory.CreateFormPdf(PathFor("form.pdf"));
        var output = PathFor("form-signed.pdf");
        await PdfSigner.SignFileAsync(source, output, new PdfSignRequest(Signer.Value, new PdfSignatureAppearance(0, new NormalizedRect(0.5, 0.85, 0.3, 0.08), SolidImage(30, 10))));

        await using var document = await _engine.OpenAsync(output);
        var fields = await document.GetFormFieldsAsync(0);
        Assert.Equal(["name", "agree", "province", "id", "Signature1"], fields.Select(f => f.Name));
        Assert.Equal("John", fields[0].Value);
    }

    [Fact]
    public async Task Refuses_documents_certified_without_changes()
    {
        var bytes = File.ReadAllBytes(TestPdfFactory.CreateFormPdf(PathFor("certified.pdf")));
        var document = CosDocument.Load(bytes);
        var update = new CosIncrementalUpdate(document);
        var sig = update.Add(new CosDictionary()
            .Set("Type", new CosName("Sig"))
            .Set("Reference", new CosArray([new CosDictionary()
                .Set("TransformMethod", new CosName("DocMDP"))
                .Set("TransformParams", new CosDictionary().Set("P", new CosInteger(1)))])));
        update.Replace(new CosObjectId(1, 0), document.Catalog.Clone().Set("Perms", new CosDictionary().Set("DocMDP", sig)));
        var certified = update.Build().Bytes;

        var error = await Assert.ThrowsAsync<PdfException>(() => PdfSigner.SignAsync(certified, new PdfSignRequest(Signer.Value)));
        Assert.Contains("certified", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Certifies_a_document()
    {
        var source = File.ReadAllBytes(TestPdfFactory.CreateFormPdf(PathFor("to-certify.pdf")));
        var certified = await PdfSigner.SignAsync(source, new PdfSignRequest(Signer.Value, Certification: PdfCertification.FormFilling));

        var document = CosDocument.Load(certified);
        var perms = Assert.IsType<CosDictionary>(document.Resolve(document.Catalog["Perms"]));
        var signature = Assert.IsType<CosDictionary>(document.Resolve(perms["DocMDP"]));
        var reference = Assert.IsType<CosDictionary>(document.Resolve(((CosArray)document.Resolve(signature["Reference"])!)[0]));
        Assert.Equal(2, ((CosInteger)document.Resolve(((CosDictionary)document.Resolve(reference["TransformParams"])!)["P"])!).Value);
        Assert.NotNull(document.Resolve(document.Catalog["AcroForm"])); // The form survives the catalog change.

        var path = PathFor("certified-form.pdf");
        File.WriteAllBytes(path, certified);
        var result = Assert.Single(await PdfSignatureVerifier.VerifyFileAsync(path, Offline));
        Assert.True(result.IsCertification);
        Assert.True(result.IsIntact);

        // Signing is allowed after a certification that allows form filling, but certifying again is not.
        var signedAgain = await PdfSigner.SignAsync(certified, new PdfSignRequest(Signer.Value));
        File.WriteAllBytes(path, signedAgain);
        Assert.Equal(2, (await PdfSignatureVerifier.VerifyFileAsync(path, Offline)).Count);
        await Assert.ThrowsAsync<PdfException>(() => PdfSigner.SignAsync(certified, new PdfSignRequest(Signer.Value, Certification: PdfCertification.NoChanges)));
    }

    [Fact]
    public async Task A_certification_without_changes_refuses_more_signatures()
    {
        var certified = await PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(Signer.Value, Certification: PdfCertification.NoChanges));
        await Assert.ThrowsAsync<PdfException>(() => PdfSigner.SignAsync(certified, new PdfSignRequest(Signer.Value)));
    }

    [Fact]
    public async Task Adds_long_term_validation_data()
    {
        var (signer, crl) = IssuedId("http://crl.example.test/ca.crl");
        var source = new FakeRevocationSource(crl);
        var signed = await PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(signer, RevocationSource: source));

        Assert.Equal(["http://crl.example.test/ca.crl"], source.Requested.Select(u => u.ToString()));
        var document = CosDocument.Load(signed);
        var dss = Assert.IsType<CosDictionary>(document.Resolve(document.Catalog["DSS"]));
        var certificates = ((CosArray)document.Resolve(dss["Certs"])!).Items.Select(i => CosFilters.Decode((CosStream)document.Resolve(i)!, document.Resolve)).ToList();
        Assert.Contains(certificates, c => c.AsSpan().SequenceEqual(signer.RawData)); // The test CA is in no store, so the signer is all there is.
        var storedCrl = Assert.Single(((CosArray)document.Resolve(dss["CRLs"])!).Items);
        Assert.Equal(crl, CosFilters.Decode((CosStream)document.Resolve(storedCrl)!, document.Resolve));

        var path = PathFor("ltv.pdf");
        File.WriteAllBytes(path, signed);
        var result = Assert.Single(await PdfSignatureVerifier.VerifyFileAsync(path, Offline));
        Assert.True(result.IsIntact);
        Assert.False(result.ModifiedAfterSigning); // Validation data is no change to the document.

        // A real change after it still counts.
        var changed = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(changed);
        var page = changed.GetPages()[0];
        update.Replace(page.Id, page.Dictionary.Clone().Set("Rotate", new CosInteger(90)));
        File.WriteAllBytes(path, update.Build().Bytes);
        Assert.True(Assert.Single(await PdfSignatureVerifier.VerifyFileAsync(path, Offline)).ModifiedAfterSigning);
    }

    [Fact]
    public async Task Adds_long_term_validation_data_to_files_with_xref_streams()
    {
        var (signer, crl) = IssuedId("http://crl.example.test/ca.crl");
        var source = File.ReadAllBytes(TestPdfFactory.CreateXrefStreamPdf(PathFor("xrefstream-ltv-source.pdf")));
        var signed = await PdfSigner.SignAsync(source, new PdfSignRequest(signer, RevocationSource: new FakeRevocationSource(crl)));

        var path = PathFor("xrefstream-ltv.pdf");
        File.WriteAllBytes(path, signed);
        var result = Assert.Single(await PdfSignatureVerifier.VerifyFileAsync(path, Offline));
        Assert.True(result.IsIntact);
        Assert.False(result.ModifiedAfterSigning);
    }

    [Fact]
    public async Task Says_when_a_revocation_list_cannot_be_downloaded()
    {
        var (signer, _) = IssuedId("http://crl.example.test/missing.crl");
        var error = await Assert.ThrowsAsync<PdfException>(() =>
            PdfSigner.SignAsync(File.ReadAllBytes(SamplePath), new PdfSignRequest(signer, RevocationSource: new FakeRevocationSource(null))));
        Assert.Contains("missing.crl", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_strings_names_and_references()
    {
        var parser = new CosParser("<< /A (a\\(b\\)\\101) /B <48 49> /C [1 0 R 2 3.5 -4] /D#20E true /F null >>"u8.ToArray());

        var dictionary = Assert.IsType<CosDictionary>(parser.ParseObject());
        Assert.Equal("a(b)A", ((CosString)dictionary["A"]!).ToText());
        Assert.Equal("HI", ((CosString)dictionary["B"]!).ToText());
        var array = (CosArray)dictionary["C"]!;
        Assert.Equal(new CosObjectId(1, 0), ((CosReference)array[0]).Id);
        Assert.Equal(2, ((CosInteger)array[1]).Value);
        Assert.Equal(3.5, ((CosReal)array[2]).Value);
        Assert.Equal(-4, ((CosInteger)array[3]).Value);
        Assert.True(((CosBoolean)dictionary["D E"]!).Value);
        Assert.Null(dictionary["F"]);
    }

    [Fact]
    public void Creates_a_digital_id_that_can_sign()
    {
        var pfx = DigitalIdFactory.CreateSelfSigned("Test ID", "test@example.com", "YRK", "pass");
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, "pass");

        Assert.True(certificate.HasPrivateKey);
        Assert.Contains("CN=Test ID", certificate.Subject, StringComparison.Ordinal);
        Assert.Contains(certificate.Extensions.OfType<X509KeyUsageExtension>(), k => k.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation));
    }

    private string PathFor(string name) => Path.Combine(_folder.FullName, name);

    private static X509Certificate2 LoadId(string name, string? email) =>
        X509CertificateLoader.LoadPkcs12(DigitalIdFactory.CreateSelfSigned(name, email, null, "pw"), "pw", X509KeyStorageFlags.EphemeralKeySet);

    private static PdfImage SolidImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200; // Blue
            pixels[i + 3] = (byte)(i % 8 == 0 ? 255 : 128);
        }

        return new PdfImage(width, height, pixels);
    }

    /// <summary>A signing ID issued by a test CA whose certificate names <paramref name="crlUrl"/>, and that CA's (empty) CRL.</summary>
    private static (X509Certificate2 Signer, byte[] Crl) IssuedId(string crlUrl)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5));

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=สมหญิง ทดสอบ", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUrl]));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), [1, 2, 3, 4]);
        using var withKey = issued.CopyWithPrivateKey(key);
        var signer = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);

        var crl = new CertificateRevocationListBuilder().Build(ca, 1, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return (signer, crl);
    }

    /// <summary>Hands out one CRL (or fails, for null) and remembers what was asked for.</summary>
    private sealed class FakeRevocationSource(byte[]? crl) : IPdfRevocationSource
    {
        public List<Uri> Requested { get; } = [];

        public Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            Requested.Add(url);
            return crl is null ? Task.FromException<byte[]>(new System.Net.Http.HttpRequestException("404")) : Task.FromResult(crl);
        }
    }

    /// <summary>An in-process RFC 3161 service with its own self-signed time-stamping certificate.</summary>
    private sealed class FakeTimestampAuthority(DateTimeOffset time) : IPdfTimestampAuthority
    {
        private static readonly Lazy<X509Certificate2> Certificate = new(() =>
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1));
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
        });

        public Task<byte[]> RequestAsync(byte[] request, CancellationToken cancellationToken)
        {
            Assert.True(Rfc3161TimestampRequest.TryDecode(request, out var decoded, out _));
            var info = new Rfc3161TimestampTokenInfo(
                new Oid("1.2.3.4"), decoded!.HashAlgorithmId, decoded.GetMessageHash(), new byte[] { 1 }, time, nonce: decoded.GetNonce());
            var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
            var signer = new CmsSigner(Certificate.Value) { IncludeOption = X509IncludeOption.EndCertOnly };
            signer.SignedAttributes.Add(new AsnEncodedData(CadesSignature.SigningCertificateV2, CadesSignature.EncodeSigningCertificateV2(Certificate.Value)));
            token.ComputeSignature(signer);

            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteInteger(0); // granted
                }

                writer.WriteEncodedValue(token.Encode());
            }

            return Task.FromResult(writer.Encode());
        }
    }
}
