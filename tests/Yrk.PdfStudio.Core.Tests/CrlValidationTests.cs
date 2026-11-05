using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class CrlValidationTests
{
    private const string CdpUri = "https://crl.example.test/Test.crl";
    private static readonly DateTimeOffset VerificationTime = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0, true);

    [Fact]
    public void DigiCert_TSA_CRL_with_critical_IDP_covers_the_leaf()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestData", "DigiCertTsa2025");
        using var issuer = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, "ca1.cer"));
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, "tsa-leaf.cer"));
        Assert.True(CrlValidation.TryCheck(File.ReadAllBytes(Path.Combine(directory, "ca1.crl")),
            certificate, issuer, VerificationTime, out var revoked));
        Assert.False(revoked);
    }

    public static IEnumerable<object[]> IdpCases()
    {
        string[] accepted = ["matching", "scheme-host-case", "one-of-many"];
        string[] rejected = ["mismatch", "path-case", "reasons", "empty-reasons", "indirect", "attribute", "no-point", "no-point-users",
            "unknown-field", "unknown-name", "unknown-after-uri", "non-uri-after-uri", "invalid-uri-after-uri",
            "empty-name", "relative-name", "duplicate-field", "field-order",
            "trailing-data", "trailing-name", "noncanonical-true", "constructed-boolean", "invalid-boolean-length",
            "truncated", "ber-indefinite", "malformed-reasons", "invalid-uri", "wrong-wrapper", "extra-sequence"];
        foreach (var critical in new[] { false, true })
        {
            foreach (var kind in accepted) yield return [kind, critical, true];
            foreach (var kind in rejected) yield return [kind, critical, false];
        }
    }

    [Theory]
    [MemberData(nameof(IdpCases))]
    public void IDP_scope_and_DER_structure_are_checked_even_when_noncritical(string kind, bool critical, bool expected)
    {
        using var pki = new TestPki();
        var crl = CreateCrl(pki.Issuer, [new("2.5.29.28", Idp(kind), critical)], VerificationTime);
        Assert.Equal(expected, CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out var revoked));
        Assert.False(revoked);
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(1, null, true)] // Missing Basic Constraints means an end entity.
    [InlineData(2, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, null, false)]
    public void IDP_certificate_type_must_match_Basic_Constraints(int flag, bool? isCa, bool expected)
    {
        using var pki = new TestPki(isCa: isCa);
        var crl = CreateCrl(pki.Issuer, [new("2.5.29.28", Idp(flag: flag), true)], VerificationTime);
        Assert.Equal(expected, CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out var revoked));
        Assert.False(revoked);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void IDP_explicit_DEFAULT_FALSE_is_not_DER(int flag)
    {
        using var pki = new TestPki();
        var crl = CreateCrl(pki.Issuer, [new("2.5.29.28", Idp(flag: flag, flagValue: false), true)], VerificationTime);
        Assert.False(CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out _));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("trailing-data")]
    [InlineData("unknown-field")]
    public void IDP_requires_a_valid_certificate_CDP(string kind)
    {
        var cdp = CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([CdpUri]).RawData;
        cdp = kind switch
        {
            "malformed" => [0x30, 0x80, 0],
            "trailing-data" => [.. cdp, 0],
            "unknown-field" => [0x30, 0x05, 0x30, 0x03, 0x83, 0x01, 0],
            _ => cdp,
        };
        using var pki = new TestPki(includeCdp: kind != "absent", cdp: cdp);
        var crl = CreateCrl(pki.Issuer, [new("2.5.29.28", Idp(), true)], VerificationTime);
        Assert.False(CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out _));
    }

    [Fact]
    public void IDP_can_match_a_later_certificate_distribution_point()
    {
        var cdp = CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(
            ["https://other.example.test/other.crl", CdpUri]).RawData;
        using var pki = new TestPki(cdp: cdp);
        var crl = CreateCrl(pki.Issuer, [new("2.5.29.28", Idp(), true)], VerificationTime);
        Assert.True(CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out var revoked));
        Assert.False(revoked);
    }

    [Fact]
    public void Extension_critical_DEFAULT_FALSE_must_also_be_omitted_in_DER()
    {
        using var pki = new TestPki();
        var crl = CreateCrl(pki.Issuer, [new("2.5.29.28", Idp(), false)], VerificationTime, explicitCriticalFalse: true);
        Assert.False(CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out _));
    }

    [Theory]
    [InlineData("duplicate-idp", false)]
    [InlineData("duplicate-idp", true)]
    [InlineData("entry-idp", false)]
    [InlineData("entry-idp", true)]
    [InlineData("delta", false)]
    [InlineData("delta", true)]
    [InlineData("certificate-issuer", false)]
    [InlineData("certificate-issuer", true)]
    [InlineData("unknown", true)]
    public void Unsupported_or_duplicate_extensions_still_fail_closed(string kind, bool critical)
    {
        using var pki = new TestPki();
        var oid = kind switch
        {
            "delta" => "2.5.29.27",
            "certificate-issuer" => "2.5.29.29",
            "unknown" => "1.2.3.4.5.6",
            _ => "2.5.29.28",
        };
        X509Extension[] extensions = [new(oid, oid == "2.5.29.28" ? Idp() : new byte[] { 2, 1, 1 }, critical)];
        if (kind == "duplicate-idp") extensions = [extensions[0], extensions[0]];
        var crl = CreateCrl(pki.Issuer, extensions, VerificationTime,
            inEntry: kind is "entry-idp" or "certificate-issuer");
        Assert.False(CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out var revoked));
        Assert.False(revoked);
    }

    [Fact]
    public void IDP_with_conflicting_certificate_type_flags_is_rejected()
    {
        using var pki = new TestPki();
        var crl = CreateCrl(pki.Issuer, [new("2.5.29.28", Idp("conflicting-types", flag: 1), true)], VerificationTime);
        Assert.False(CrlValidation.TryCheck(crl, pki.Certificate, pki.Issuer, VerificationTime, out _));
    }

    internal static byte[] Idp(string kind = "matching", int? flag = null, bool flagValue = true)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            if (kind == "field-order") writer.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 1));
            if (kind is not ("no-point" or "no-point-users"))
            {
                using (writer.PushSequence(Context0))
                {
                    using (writer.PushSequence(kind == "relative-name" ? new Asn1Tag(TagClass.ContextSpecific, 1, true) : Context0))
                    {
                        if (kind == "extra-sequence") writer.PushSequence();
                        if (kind != "empty-name")
                        {
                            var uri = kind switch
                            {
                                "mismatch" => "https://crl.example.test/other.crl",
                                "path-case" => "https://crl.example.test/test.crl",
                                "scheme-host-case" => "HTTPS://CRL.EXAMPLE.TEST/Test.crl",
                                "ltv" => "https://crl.example.test/test",
                                "invalid-uri" => "not an absolute URI",
                                _ => CdpUri,
                            };
                            if (kind == "one-of-many") WriteUri(writer, "https://other.example.test/other.crl");
                            WriteUri(writer, uri, kind == "unknown-name" ? 9 : 6);
                            if (kind == "unknown-after-uri") WriteUri(writer, CdpUri, 9);
                            if (kind == "non-uri-after-uri") WriteUri(writer, "crl.example.test", 2);
                            if (kind == "invalid-uri-after-uri") WriteUri(writer, "not an absolute URI");
                        }
                        if (kind == "extra-sequence") writer.PopSequence();
                    }
                    if (kind == "trailing-name") writer.WriteNull();
                }
            }
            if (kind == "reasons") writer.WriteBitString(new byte[] { 0x40 }, 6, new Asn1Tag(TagClass.ContextSpecific, 3));
            if (kind == "empty-reasons") writer.WriteBitString([], 0, new Asn1Tag(TagClass.ContextSpecific, 3));
            if (kind == "no-point-users") flag = 1;
            if (kind == "indirect") flag = 4;
            if (kind == "attribute") flag = 5;
            if (flag is { } tag) writer.WriteBoolean(flagValue, new Asn1Tag(TagClass.ContextSpecific, tag));
            if (kind == "conflicting-types") writer.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 2));
            if (kind == "duplicate-field")
            {
                writer.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 1));
                writer.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 1));
            }
            if (kind == "unknown-field") writer.WriteBoolean(true, new Asn1Tag(TagClass.ContextSpecific, 6));
            if (kind == "noncanonical-true") writer.WriteEncodedValue(new byte[] { 0x81, 1, 1 });
            if (kind == "constructed-boolean") writer.WriteEncodedValue(new byte[] { 0xA1, 3, 1, 1, 0xFF });
            if (kind == "invalid-boolean-length") writer.WriteEncodedValue(new byte[] { 0x81, 2, 0xFF, 0xFF });
            if (kind == "malformed-reasons") writer.WriteEncodedValue(new byte[] { 0x83, 2, 8, 0 });
        }
        var bytes = writer.Encode();
        return kind switch
        {
            "trailing-data" => [.. bytes, 0],
            "truncated" => bytes[..^1],
            "ber-indefinite" => [0x30, 0x80, .. bytes[2..], 0, 0],
            "wrong-wrapper" => [0x30, (byte)(bytes.Length - 2), 0xA1, .. bytes[3..]],
            _ => bytes,
        };
    }

    private static void WriteUri(AsnWriter writer, string uri, int tag = 6) =>
        writer.WriteCharacterString(UniversalTagNumber.IA5String, uri, new Asn1Tag(TagClass.ContextSpecific, tag));

    // Build and sign the complete TBS, just like SignatureLtvTests.UnsupportedCrl.
    // Invalid extensions therefore exercise parsing rather than a failed signature.
    internal static byte[] CreateCrl(X509Certificate2 issuer, X509Extension[] extensions,
        DateTimeOffset? time = null, bool inEntry = false, bool explicitCriticalFalse = false)
    {
        var now = time ?? DateTimeOffset.UtcNow;
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
            tbs.WriteEncodedValue(issuer.SubjectName.RawData);
            tbs.WriteUtcTime(now.AddDays(-1));
            tbs.WriteUtcTime(now.AddDays(1));
            if (inEntry)
            {
                using (tbs.PushSequence())
                using (tbs.PushSequence())
                {
                    tbs.WriteInteger(42); // The target serial, to ensure failures never publish revoked=true.
                    tbs.WriteUtcTime(now.AddHours(-1));
                    WriteExtensions(tbs, extensions, explicitCriticalFalse);
                }
            }
            else
            {
                using (tbs.PushSequence(Context0)) WriteExtensions(tbs, extensions, explicitCriticalFalse);
            }
        }
        using var key = issuer.GetRSAPrivateKey()!;
        var list = new AsnWriter(AsnEncodingRules.DER);
        using (list.PushSequence())
        {
            list.WriteEncodedValue(tbs.Encode());
            list.WriteEncodedValue(algorithm.Encode());
            list.WriteBitString(key.SignData(tbs.Encode(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        return list.Encode();
    }

    private static void WriteExtensions(AsnWriter writer, X509Extension[] extensions, bool explicitCriticalFalse)
    {
        using (writer.PushSequence())
        {
            foreach (var extension in extensions)
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(extension.Oid!.Value!);
                    if (extension.Critical || explicitCriticalFalse) writer.WriteBoolean(extension.Critical);
                    writer.WriteOctetString(extension.RawData);
                }
            }
        }
    }

    private sealed class TestPki : IDisposable
    {
        internal X509Certificate2 Issuer { get; }
        internal X509Certificate2 Certificate { get; }

        internal TestPki(bool? isCa = false, bool includeCdp = true, byte[]? cdp = null)
        {
            using var issuerKey = RSA.Create(2048);
            using var leafKey = RSA.Create(2048);
            var request = new CertificateRequest("CN=IDP test issuer", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Issuer = request.CreateSelfSigned(VerificationTime.AddYears(-1), VerificationTime.AddYears(1));
            var target = new CertificateRequest("CN=IDP target", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (isCa is { } ca) target.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
            if (includeCdp) target.CertificateExtensions.Add(cdp is null
                ? CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([CdpUri])
                : new X509Extension("2.5.29.31", cdp, false));
            Certificate = target.Create(Issuer, VerificationTime.AddDays(-10), VerificationTime.AddDays(10), [42]);
        }

        public void Dispose() { Certificate.Dispose(); Issuer.Dispose(); }
    }
}
