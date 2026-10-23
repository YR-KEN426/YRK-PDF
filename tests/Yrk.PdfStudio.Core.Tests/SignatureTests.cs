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
