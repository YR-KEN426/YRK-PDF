using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureUserTrustTests
{
    private static byte[] Source => SignatureFormFillTests.BlankDocument();
    private static PdfVerifyOptions Trust(params X509Certificate2[] certificates) =>
        new(CheckRevocation: false) { UserTrustedCertificates = certificates };

    private static X509Certificate2 NewSigner(int notBeforeDays = -1, int notAfterDays = 7)
    {
        using var key = RSA.Create(2048);
        return Request(key).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(notBeforeDays),
            DateTimeOffset.UtcNow.AddDays(notAfterDays));
    }

    private static CertificateRequest Request(RSA key, bool ca = false, string name = "CN=User trust test",
        RSASignaturePadding? padding = null, HashAlgorithmName? hash = null)
    {
        var request = new CertificateRequest(name, key, hash ?? HashAlgorithmName.SHA256, padding ?? RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static PdfSignatureInfo Verify(byte[] signed, PdfVerifyOptions options) =>
        Assert.Single(PdfSignatureVerifier.Verify(signed, options));

    [Fact]
    public async Task Empty_list_keeps_self_signed_identity_unknown()
    {
        using var signer = NewSigner();
        var options = new PdfVerifyOptions(CheckRevocation: false);
        Assert.Empty(options.UserTrustedCertificates);
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), options);
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_public_certificate_is_trusted_by_user(bool checkRevocation)
    {
        using var signer = NewSigner();
        using var publicCertificate = X509CertificateLoader.LoadCertificate(signer.RawData);
        Assert.False(publicCertificate.HasPrivateKey);
        var options = Trust(publicCertificate) with { CheckRevocation = checkRevocation };
        var signed = await PdfSigner.SignAsync(Source, new(signer));
        var result = Verify(signed, options);
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.TrustedByUser, result.Identity);
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
        Assert.Equal(checkRevocation, result.RevocationChecked);

        // Removing the per-call list immediately removes trust; nothing was persisted.
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, Verify(signed, Trust()).Status);
    }

    [Fact]
    public async Task Same_subject_different_certificate_is_not_trusted()
    {
        using var signer = NewSigner();
        using var other = NewSigner();
        Assert.Equal(signer.SubjectName.RawData, other.SubjectName.RawData);
        Assert.NotEqual(signer.RawData, other.RawData);
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(other));
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Fact]
    public async Task Same_public_key_and_subject_with_different_DER_is_not_trusted()
    {
        using var key = RSA.Create(2048);
        var request = Request(key);
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        using var other = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(8));
        Assert.Equal(signer.PublicKey.ExportSubjectPublicKeyInfo(), other.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.NotEqual(signer.RawData, other.RawData);
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(other));
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Theory]
    [InlineData(-3, -1)]
    [InlineData(1, 7)]
    public async Task Time_invalid_certificate_stays_expired(int notBeforeDays, int notAfterDays)
    {
        using var signer = NewSigner(notBeforeDays, notAfterDays);
        var signed = await PdfSigner.SignAsync(Source, new(signer));
        Assert.Equal(PdfSignerIdentity.Expired, Verify(signed, Trust()).Identity);
        var result = Verify(signed, Trust(signer));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Expired, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Fact]
    public async Task Changed_signed_bytes_stay_invalid_with_user_trust()
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new(signer));
        var offset = signed.AsSpan().IndexOf("q Q"u8);
        Assert.True(offset >= 0);
        signed[offset + 2] = (byte)'q';
        var result = Verify(signed, Trust(signer));
        Assert.Equal(PdfSignerIdentity.TrustedByUser, result.Identity);
        Assert.False(result.IsIntact);
        Assert.Equal(PdfSignatureStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData(PdfCertification.None, PdfSignatureStatus.Modified)]
    [InlineData(PdfCertification.NoChanges, PdfSignatureStatus.Invalid)]
    public async Task Later_page_change_takes_precedence_over_user_trust(PdfCertification certification, PdfSignatureStatus expected)
    {
        using var signer = NewSigner();
        var signed = await PdfSigner.SignAsync(Source, new(signer, Certification: certification));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var page = document.GetPages()[0];
        update.Replace(page.Id, page.Dictionary.Clone().Set("Rotate", new CosInteger(90)));
        var result = Verify(update.Build().Bytes, Trust(signer));
        Assert.True(result.IsIntact);
        Assert.True(result.ModifiedAfterSigning);
        Assert.Equal(PdfSignerIdentity.TrustedByUser, result.Identity);
        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CA_issued_signer_is_not_trusted_by_listing_leaf_or_CA(bool listLeaf, bool listBoth)
    {
        using var caKey = RSA.Create(2048);
        using var ca = Request(caKey, ca: true, name: "CN=Untrusted CA")
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(8));
        using var key = RSA.Create(2048);
        using var issued = Request(key).Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), [1, 2, 3]);
        using var signer = issued.CopyWithPrivateKey(key);
        var signed = await PdfSigner.SignAsync(Source, new(signer));
        var options = listBoth ? Trust(ca, signer) : listLeaf ? Trust(signer) : Trust(ca);
        var result = Verify(signed, options);
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Fact]
    public async Task Matching_raw_issuer_and_subject_do_not_replace_self_signature_verification()
    {
        using var caKey = RSA.Create(2048);
        using var ca = Request(caKey, ca: true).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(8));
        using var key = RSA.Create(2048);
        using var issued = Request(key).Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), [4, 5, 6]);
        using var signer = issued.CopyWithPrivateKey(key);
        Assert.Equal(signer.IssuerName.RawData, signer.SubjectName.RawData);
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(ca, signer));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Fact]
    public async Task Damaged_certificate_self_signature_is_rejected_even_when_CMS_signature_is_intact()
    {
        using var key = RSA.Create(2048);
        using var original = Request(key).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var der = original.RawData;
        der[^1] ^= 1; // Change only the certificate signature, keeping its name and public key.
        using var damaged = X509CertificateLoader.LoadCertificate(der);
        using var signer = damaged.CopyWithPrivateKey(key);
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(damaged));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_key_or_enhanced_key_usage_is_not_promoted(bool enhanced)
    {
        using var key = RSA.Create(2048);
        var request = Request(key);
        if (enhanced)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        else
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(signer));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Fact]
    public async Task Other_chain_errors_are_not_ignored()
    {
        using var key = RSA.Create(2048);
        var request = Request(key);
        request.CertificateExtensions.Add(new X509Extension("1.2.3.4.5.6", [5, 0], true));
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(signer));
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Fact]
    public async Task Existing_system_trust_result_keeps_precedence()
    {
        using var signer = NewSigner();
        var options = Trust(signer) with { CustomTrustRoots = new X509Certificate2Collection(signer) };
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), options);
        Assert.Equal(PdfSignerIdentity.Trusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
    }

    [Theory]
    [InlineData("1.3.6.1.4.1.311.10.3.12", "1.3.6.1.5.5.7.3.4")] // YRK Digital ID
    [InlineData("1.2.840.113583.1.1.5", null)] // Adobe Authentic Documents
    [InlineData("1.3.6.1.5.5.7.3.4", null)] // Email Protection only
    public async Task Digital_ID_document_signing_usage_is_accepted(string first, string? second)
    {
        using var key = RSA.Create(2048);
        var request = Request(key);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var usages = new OidCollection { new Oid(first) };
        if (second is not null) usages.Add(new Oid(second));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(signer));
        Assert.Equal(PdfSignerIdentity.TrustedByUser, result.Identity);
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
    }

    [Fact]
    public async Task ECDSA_self_signature_is_verified()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ECDSA user trust test", key, HashAlgorithmName.SHA256);
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(signer));
        Assert.Equal(PdfSignerIdentity.TrustedByUser, result.Identity);
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA384")]
    [InlineData("SHA512")]
    public async Task PSS_self_signed_certificate_is_trusted_by_user(string hashName)
    {
        using var key = RSA.Create(2048);
        using var signer = Request(key, padding: RSASignaturePadding.Pss, hash: new HashAlgorithmName(hashName))
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        Assert.True(CertificateSelfSigned.IsSelfSigned(signer));
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(signer));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.TrustedByUser, result.Identity);
        Assert.Equal(PdfSignatureStatus.Valid, result.Status);
    }

    [Fact]
    public async Task PSS_bad_self_signature_is_not_trusted()
    {
        using var key = RSA.Create(2048);
        using var original = Request(key, padding: RSASignaturePadding.Pss)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var der = original.RawData;
        der[^1] ^= 1;
        using var damaged = X509CertificateLoader.LoadCertificate(der);
        using var signer = damaged.CopyWithPrivateKey(key);
        Assert.False(CertificateSelfSigned.IsSelfSigned(signer));
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(damaged));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Fact]
    public async Task PSS_certificate_issued_by_same_name_CA_is_not_trusted()
    {
        using var caKey = RSA.Create(2048);
        using var ca = Request(caKey, ca: true, padding: RSASignaturePadding.Pss)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(8));
        using var key = RSA.Create(2048);
        using var issued = Request(key, padding: RSASignaturePadding.Pss)
            .Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), [10, 11, 12]);
        using var signer = issued.CopyWithPrivateKey(key);
        Assert.Equal(signer.SubjectName.RawData, signer.IssuerName.RawData);
        Assert.False(CertificateSelfSigned.IsSelfSigned(signer));
        var result = Verify(await PdfSigner.SignAsync(Source, new(signer)), Trust(ca, signer));
        Assert.True(result.IsIntact);
        Assert.Equal(PdfSignerIdentity.Untrusted, result.Identity);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, result.Status);
    }

    [Theory]
    [InlineData("wrong salt")]
    [InlineData("wrong MGF hash")]
    [InlineData("missing parameters")]
    [InlineData("missing hash")]
    [InlineData("SHA1")]
    [InlineData("missing MGF")]
    [InlineData("wrong MGF algorithm")]
    [InlineData("missing salt")]
    [InlineData("wrong trailer")]
    public void PSS_unsupported_parameters_reject_an_otherwise_valid_self_signature(string variant)
    {
        using var key = RSA.Create(2048);
        using var certificate = PssCertificate(key, variant);
        // Verify the actual signature independently: only its declared parameters are unacceptable.
        // Removing the salt or MGF checks must make their respective cases fail this test.
        var sequence = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
        var tbs = sequence.ReadEncodedValue();
        sequence.ReadEncodedValue();
        var signature = sequence.ReadBitString(out _);
        Assert.True(key.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.False(CertificateSelfSigned.IsSelfSigned(certificate));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("explicit trailer")]
    [InlineData("absent hash NULL")]
    public void PSS_supported_parameters_accept_a_valid_self_signature(string variant)
    {
        using var key = RSA.Create(2048);
        using var certificate = PssCertificate(key, variant);
        Assert.True(CertificateSelfSigned.IsSelfSigned(certificate));
    }

    [Fact]
    public void PSS_inner_algorithm_must_match_the_outer_one()
    {
        // Both encodings are acceptable alone and the signature is valid; only the mismatch (RFC 5280 4.1.1.2) is wrong.
        using var key = RSA.Create(2048);
        using var certificate = PssCertificate(key, "standard", innerVariant: "absent hash NULL");
        var sequence = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
        var tbs = sequence.ReadEncodedValue();
        sequence.ReadEncodedValue();
        Assert.True(key.VerifyData(tbs.Span, sequence.ReadBitString(out _), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.False(CertificateSelfSigned.IsSelfSigned(certificate));
    }

    private static X509Certificate2 PssCertificate(RSA key, string variant, string? innerVariant = null)
    {
        using var original = Request(key, padding: RSASignaturePadding.Pss)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        var algorithm = new AsnWriter(AsnEncodingRules.DER);
        algorithm.PushSequence();
        algorithm.WriteObjectIdentifier("1.2.840.113549.1.1.10");
        if (variant != "missing parameters")
        {
            algorithm.PushSequence();
            if (variant != "missing hash")
            {
                var tag = new Asn1Tag(TagClass.ContextSpecific, 0, true);
                algorithm.PushSequence(tag);
                WriteHash(variant == "SHA1" ? "1.3.14.3.2.26" : "2.16.840.1.101.3.4.2.1");
                algorithm.PopSequence(tag);
            }
            if (variant != "missing MGF")
            {
                var tag = new Asn1Tag(TagClass.ContextSpecific, 1, true);
                algorithm.PushSequence(tag);
                algorithm.PushSequence();
                algorithm.WriteObjectIdentifier(variant == "wrong MGF algorithm" ? "1.2.3.4" : "1.2.840.113549.1.1.8");
                WriteHash(variant == "wrong MGF hash" ? "2.16.840.1.101.3.4.2.2" : "2.16.840.1.101.3.4.2.1");
                algorithm.PopSequence();
                algorithm.PopSequence(tag);
            }
            if (variant != "missing salt") WriteInteger(2, variant == "wrong salt" ? 31 : 32);
            if (variant is "explicit trailer" or "wrong trailer") WriteInteger(3, variant == "wrong trailer" ? 2 : 1);
            algorithm.PopSequence();
        }
        algorithm.PopSequence();

        // Replace both AlgorithmIdentifiers and sign the new TBS using standard PSS (32-byte salt).
        var originalTbs = new AsnReader(original.RawData, AsnEncodingRules.DER).ReadSequence().ReadSequence();
        var tbs = new AsnWriter(AsnEncodingRules.DER);
        tbs.PushSequence();
        tbs.WriteEncodedValue(originalTbs.ReadEncodedValue().Span); // Version
        tbs.WriteEncodedValue(originalTbs.ReadEncodedValue().Span); // Serial number
        originalTbs.ReadEncodedValue(); // Old signature algorithm
        if (innerVariant is null) tbs.WriteEncodedValue(algorithm.Encode());
        else
        {
            using var other = PssCertificate(key, innerVariant);
            var otherCertificate = new AsnReader(other.RawData, AsnEncodingRules.DER).ReadSequence();
            otherCertificate.ReadEncodedValue();
            tbs.WriteEncodedValue(otherCertificate.ReadEncodedValue().Span);
        }
        while (originalTbs.HasData) tbs.WriteEncodedValue(originalTbs.ReadEncodedValue().Span);
        tbs.PopSequence();
        var encodedTbs = tbs.Encode();
        var certificate = new AsnWriter(AsnEncodingRules.DER);
        certificate.PushSequence();
        certificate.WriteEncodedValue(encodedTbs);
        certificate.WriteEncodedValue(algorithm.Encode());
        certificate.WriteBitString(key.SignData(encodedTbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        certificate.PopSequence();
        return X509CertificateLoader.LoadCertificate(certificate.Encode());

        void WriteHash(string oid)
        {
            algorithm.PushSequence();
            algorithm.WriteObjectIdentifier(oid);
            if (variant != "absent hash NULL") algorithm.WriteNull();
            algorithm.PopSequence();
        }

        void WriteInteger(int tagNumber, int value)
        {
            var tag = new Asn1Tag(TagClass.ContextSpecific, tagNumber, true);
            algorithm.PushSequence(tag);
            algorithm.WriteInteger(value);
            algorithm.PopSequence(tag);
        }
    }

    [Fact]
    public async Task Revoked_signer_stays_revoked_even_when_leaf_and_CA_are_listed()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = Request(caKey, ca: true, name: "CN=Revocation user trust test CA");
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(8));
        using var key = RSA.Create(2048);
        var request = Request(key);
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["https://crl.example.test/user-trust"]));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(7), [7, 8, 9]);
        using var signer = issued.CopyWithPrivateKey(key);
        var builder = new CertificateRevocationListBuilder();
        builder.AddEntry(signer, DateTimeOffset.UtcNow.AddDays(-1));
        var crl = builder.Build(ca, 1, DateTimeOffset.UtcNow.AddDays(1), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddHours(-1));

        var signed = await PdfSigner.SignAsync(Source, new(signer));
        var document = CosDocument.Load(signed);
        var update = new CosIncrementalUpdate(document);
        var dss = new CosDictionary()
            .Set("Certs", new CosArray([update.Add(new CosStream(new CosDictionary(), ca.RawData))]))
            .Set("CRLs", new CosArray([update.Add(new CosStream(new CosDictionary(), crl))]));
        update.Replace(((CosReference)document.Trailer["Root"]!).Id, document.Catalog.Clone().Set("DSS", update.Add(dss)));

        var result = Verify(update.Build().Bytes, Trust(signer, ca));
        Assert.True(result.IsIntact);
        Assert.True(result.RevocationChecked);
        Assert.Equal(PdfSignerIdentity.Revoked, result.Identity);
        Assert.Equal(PdfSignatureStatus.Invalid, result.Status);
    }
}
