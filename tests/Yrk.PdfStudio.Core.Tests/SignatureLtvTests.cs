using System.Formats.Asn1;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureLtvTests
{
    private static byte[] Source => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B_LT_uses_DSS_issuers_and_CRLs_without_a_network_revocation_source(bool onlineFallback)
    {
        using var pki = new TestPki();
        var source = new TestRevocationSource(pki.Crl(pki.Ca), pki.Crl(pki.Root));
        var pdf = await PdfSigner.SignAsync(Store(Source, [pki.Root, pki.Ca]),
            new(pki.Signer, RevocationSource: source));
        Assert.Equal(2, source.Calls);

        // The intermediate exists only in DSS, not CMS or a machine/user certificate store.
        var cms = new SignedCms();
        cms.Decode(SignatureContents(pdf));
        Assert.DoesNotContain(cms.Certificates.Cast<X509Certificate2>(), c => c.Thumbprint == pki.Ca.Thumbprint);
        var result = Assert.Single(PdfSignatureVerifier.Verify(pdf, pki.Options(onlineFallback)));
        Assert.True(result.IsIntact);
        Assert.True(result.CoversWholeDocument);
        Assert.True(result.RevocationChecked);
        Assert.Equal(PdfSignerIdentity.Trusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
        Assert.Equal(2, source.Calls);

        // Embedding the root does not itself grant trust.
        var untrusted = Assert.Single(PdfSignatureVerifier.Verify(pdf, new(CheckRevocation: false)));
        Assert.True(untrusted.RevocationChecked);
        Assert.Equal(PdfSignerIdentity.Untrusted, untrusted.Identity);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("wrong-key")]
    [InlineData("trailing-data")]
    [InlineData("missing-next-update")]
    [InlineData("signed-malformed")]
    [InlineData("delta")]
    [InlineData("scoped")]
    [InlineData("unknown-critical")]
    public async Task Invalid_DSS_CRLs_are_ignored(string kind)
    {
        using var pki = new TestPki();
        var signed = await PdfSigner.SignAsync(Source, new(pki.Signer));
        var pdf = Store(signed, [pki.Ca, pki.Root], pki.BadCrl(kind), pki.Crl(pki.Root));
        var result = Assert.Single(PdfSignatureVerifier.Verify(pdf, pki.Options()));
        Assert.True(result.IsIntact);
        Assert.False(result.RevocationChecked);
        Assert.Equal(PdfSignerIdentity.Trusted, result.Identity);

        // An invalid item must not prevent a later valid item being used.
        var withGood = Store(signed, [pki.Ca, pki.Root], pki.BadCrl(kind), pki.Crl(pki.Ca), pki.Crl(pki.Root));
        Assert.True(Assert.Single(PdfSignatureVerifier.Verify(withGood, pki.Options())).RevocationChecked);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("wrong-key")]
    [InlineData("signed-malformed")]
    public async Task LTV_signing_rejects_invalid_downloads(string kind)
    {
        using var pki = new TestPki();
        var error = await Assert.ThrowsAsync<PdfException>(() => PdfSigner.SignAsync(
            Store(Source, [pki.Ca, pki.Root]), new(pki.Signer, RevocationSource: new TestRevocationSource(pki.BadCrl(kind)))));
        Assert.Contains("revocation list", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DSS_detects_revoked_leaf_or_intermediate(bool intermediate)
    {
        using var pki = new TestPki();
        var signed = await PdfSigner.SignAsync(Source, new(pki.Signer));
        var leafCrl = pki.Crl(pki.Ca, revoked: intermediate ? null : pki.Signer);
        var caCrl = pki.Crl(pki.Root, revoked: intermediate ? pki.Ca : null);
        var result = Assert.Single(PdfSignatureVerifier.Verify(Store(signed, [pki.Ca, pki.Root], leafCrl, caCrl), pki.Options()));
        Assert.True(result.RevocationChecked);
        Assert.Equal(PdfSignerIdentity.Revoked, result.Identity);
        Assert.Equal(PdfSignatureStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Every_nonroot_requires_revocation_evidence()
    {
        using var pki = new TestPki();
        var signed = await PdfSigner.SignAsync(Source, new(pki.Signer));
        var result = Assert.Single(PdfSignatureVerifier.Verify(Store(signed, [pki.Ca, pki.Root], pki.Crl(pki.Ca)), pki.Options()));
        Assert.False(result.RevocationChecked);
    }

    [Fact]
    public async Task Existing_DSS_and_both_signatures_survive_LTV_cosigning()
    {
        using var pki = new TestPki();
        var original = Store(Source, [pki.Ca, pki.Root]);
        var first = await PdfSigner.SignAsync(original, new(pki.Signer,
            RevocationSource: new TestRevocationSource(pki.Crl(pki.Ca), pki.Crl(pki.Root))));
        var second = await PdfSigner.SignAsync(first, new(pki.Signer,
            RevocationSource: new TestRevocationSource(pki.Crl(pki.Ca), pki.Crl(pki.Root))));
        var results = PdfSignatureVerifier.Verify(second, pki.Options());
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsIntact);
            Assert.False(r.ModifiedAfterSigning);
            Assert.True(r.RevocationChecked);
            Assert.Equal(PdfSignatureStatus.Valid, r.Status);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HTTP_download_is_bounded_with_or_without_content_length(bool declaredLength)
    {
        using var input = new CountingStream(CrlValidation.MaxCrlBytes + 1);
        using var client = new HttpClient(new TestHandler(() =>
        {
            var content = new StreamContent(input);
            if (declaredLength)
            {
                content.Headers.ContentLength = CrlValidation.MaxCrlBytes + 1;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var source = new HttpRevocationSource(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetAsync(new Uri("https://crl.example.test/test"), default));
        Assert.InRange(input.BytesRead, 0, declaredLength ? 0 : CrlValidation.MaxCrlBytes + 1);
    }

    [Fact]
    public void Validates_ECDSA_issuer_signatures()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ECDSA test CA", caKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        using var leaf = new CertificateRequest("CN=ECDSA leaf", leafKey, HashAlgorithmName.SHA256)
            .Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), [42]);
        var crl = new CertificateRevocationListBuilder().Build(ca, 1, DateTimeOffset.UtcNow.AddDays(1), HashAlgorithmName.SHA256);
        Assert.True(CrlValidation.TryCheck(crl, leaf, ca, DateTimeOffset.UtcNow, out var revoked));
        Assert.False(revoked);
        crl[^1] ^= 1;
        Assert.False(CrlValidation.TryCheck(crl, leaf, ca, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void CRL_validity_uses_the_supplied_verification_time()
    {
        using var pki = new TestPki();
        var time = DateTimeOffset.UtcNow.AddDays(-3);
        var crl = pki.Crl(pki.Ca, thisUpdate: time.AddDays(-1), nextUpdate: time.AddDays(1));
        Assert.True(CrlValidation.TryCheck(crl, pki.Signer, pki.Ca, time, out var revoked));
        Assert.False(revoked);
        Assert.False(CrlValidation.TryCheck(crl, pki.Signer, pki.Ca, DateTimeOffset.UtcNow, out _));
    }

    private static byte[] SignatureContents(byte[] pdf)
    {
        var document = CosDocument.Load(pdf);
        var form = (CosDictionary)document.Resolve(document.Catalog["AcroForm"])!;
        var field = (CosDictionary)document.Resolve(((CosArray)document.Resolve(form["Fields"])!)[0])!;
        var contents = ((CosString)((CosDictionary)document.Resolve(field["V"])!)["Contents"]!).Bytes;
        AsnDecoder.ReadEncodedValue(contents, AsnEncodingRules.DER, out _, out _, out var length);
        return contents[..length];
    }

    private static byte[] Store(byte[] pdf, X509Certificate2[] certificates, params byte[][] crls)
    {
        var document = CosDocument.Load(pdf);
        var update = new CosIncrementalUpdate(document);
        CosArray Streams(IEnumerable<byte[]> values) => new(values.Select(b => (CosObject)update.Add(
            new CosStream(new CosDictionary().Set("Filter", new CosName("FlateDecode")), CosFilters.Deflate(b)))));
        var dss = new CosDictionary().Set("Certs", Streams(certificates.Select(c => c.RawData))).Set("CRLs", Streams(crls));
        update.Replace(((CosReference)document.Trailer["Root"]!).Id, document.Catalog.Clone().Set("DSS", update.Add(dss)));
        return update.Build().Bytes;
    }

    private sealed class TestPki : IDisposable
    {
        internal X509Certificate2 Root { get; }
        internal X509Certificate2 Ca { get; }
        internal X509Certificate2 Signer { get; }

        internal TestPki()
        {
            using var rootKey = RSA.Create(2048);
            using var caKey = RSA.Create(2048);
            using var leafKey = RSA.Create(2048);
            var root = Request("Root " + Guid.NewGuid(), rootKey, ca: true);
            Root = root.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(2));
            using var ca = Request("Intermediate " + Guid.NewGuid(), caKey, ca: true).Create(
                Root, DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddYears(1), [1]);
            Ca = ca.CopyWithPrivateKey(caKey);
            using var leaf = Request("LTV test signer", leafKey, ca: false).Create(
                Ca, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddMonths(1), [0x12, 0x34]);
            Signer = leaf.CopyWithPrivateKey(leafKey);
        }

        private static CertificateRequest Request(string name, RSA key, bool ca)
        {
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(ca
                ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["https://crl.example.test/test"]));
            return request;
        }

        internal PdfVerifyOptions Options(bool online = false) => new(CheckRevocation: online)
        {
            CustomTrustRoots = new X509Certificate2Collection(Root),
        };

        internal byte[] Crl(X509Certificate2 issuer, DateTimeOffset? thisUpdate = null, DateTimeOffset? nextUpdate = null,
            X509Certificate2? revoked = null)
        {
            var builder = new CertificateRevocationListBuilder();
            if (revoked is not null)
            {
                builder.AddEntry(revoked, DateTimeOffset.UtcNow.AddDays(-1));
            }
            return builder.Build(issuer, 1, nextUpdate ?? DateTimeOffset.UtcNow.AddDays(1), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1, thisUpdate ?? DateTimeOffset.UtcNow.AddDays(-2));
        }

        internal byte[] BadCrl(string kind)
        {
            var bytes = Crl(Ca);
            switch (kind)
            {
                case "tampered": bytes[^1] ^= 1; return bytes;
                case "expired": return Crl(Ca, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1));
                case "future": return Crl(Ca, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));
                case "malformed": return [0x30, 0x80, 0x01];
                case "oversized": return new byte[CrlValidation.MaxCrlBytes + 1];
                case "trailing-data": return [.. bytes, 0];
                case "missing-next-update":
                case "signed-malformed":
                case "delta":
                case "scoped":
                case "unknown-critical": return UnsupportedCrl(kind);
                case "wrong-key":
                    using (var key = RSA.Create(2048))
                    using (var impostor = Request(Ca.GetNameInfo(X509NameType.SimpleName, false), key, true)
                        .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-4), DateTimeOffset.UtcNow.AddDays(4)))
                    {
                        return Crl(impostor);
                    }
                default: throw new ArgumentException(kind);
            }
        }

        // These carry a genuine issuer signature. They must fail semantic/structural
        // validation, not merely the cryptographic signature check.
        private byte[] UnsupportedCrl(string kind)
        {
            var algorithm = new AsnWriter(AsnEncodingRules.DER);
            using (algorithm.PushSequence())
            {
                algorithm.WriteObjectIdentifier("1.2.840.113549.1.1.11");
                algorithm.WriteNull();
            }
            var tbs = new AsnWriter(AsnEncodingRules.DER);
            using (tbs.PushSequence())
            {
                tbs.WriteInteger(1);
                tbs.WriteEncodedValue(algorithm.Encode());
                tbs.WriteEncodedValue(Ca.SubjectName.RawData);
                tbs.WriteUtcTime(DateTimeOffset.UtcNow.AddDays(-1));
                if (kind != "missing-next-update")
                {
                    tbs.WriteUtcTime(DateTimeOffset.UtcNow.AddDays(1));
                }
                if (kind == "signed-malformed")
                {
                    using (tbs.PushSequence())
                    using (tbs.PushSequence())
                    {
                        tbs.WriteInteger(123); // Missing mandatory revocationDate.
                    }
                }
                else if (kind != "missing-next-update")
                {
                    using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    using (tbs.PushSequence())
                    using (tbs.PushSequence())
                    {
                        tbs.WriteObjectIdentifier(kind switch
                        {
                            "delta" => "2.5.29.27",
                            "scoped" => "2.5.29.28",
                            _ => "1.2.3.4.5.6",
                        });
                        tbs.WriteBoolean(true);
                        tbs.WriteOctetString(kind == "delta" ? new byte[] { 2, 1, 1 } : new byte[] { 0x30, 0 });
                    }
                }
            }
            using var key = Ca.GetRSAPrivateKey()!;
            var list = new AsnWriter(AsnEncodingRules.DER);
            using (list.PushSequence())
            {
                list.WriteEncodedValue(tbs.Encode());
                list.WriteEncodedValue(algorithm.Encode());
                list.WriteBitString(key.SignData(tbs.Encode(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
            return list.Encode();
        }

        public void Dispose() { Signer.Dispose(); Ca.Dispose(); Root.Dispose(); }
    }

    private sealed class TestRevocationSource(params byte[][] crls) : IPdfRevocationSource
    {
        internal int Calls { get; private set; }
        public Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(crls[Calls++]);
    }

    private sealed class TestHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private sealed class CountingStream(int size) : Stream
    {
        internal int BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, size - BytesRead);
            Array.Clear(buffer, offset, read);
            BytesRead += read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
