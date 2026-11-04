using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>Validates complete, direct CRLs. Unsupported scopes/algorithms fail closed.</summary>
internal static class CrlValidation
{
    internal const int MaxCrlBytes = 8 * 1024 * 1024;

    internal static bool TryCheck(byte[] data, X509Certificate2 certificate, X509Certificate2 issuer,
        DateTimeOffset time, out bool revoked)
    {
        revoked = false;
        if (data.Length is 0 or > MaxCrlBytes
            || !certificate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData))
        {
            return false;
        }

        try
        {
            if (issuer.Extensions.OfType<X509KeyUsageExtension>().Any(e => !e.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign)))
            {
                return false;
            }
            var reader = new AsnReader(data, AsnEncodingRules.DER);
            var list = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            var tbsBytes = list.ReadEncodedValue();
            var algorithmBytes = list.ReadEncodedValue();
            var signature = list.ReadBitString(out var unused);
            list.ThrowIfNotEmpty();
            if (unused != 0 || !VerifySignature(tbsBytes.Span, algorithmBytes, signature, issuer))
            {
                return false;
            }

            var tbs = new AsnReader(tbsBytes, AsnEncodingRules.DER).ReadSequence();
            var v2 = tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer);
            if (v2 && tbs.ReadInteger() != 1)
            {
                return false;
            }
            if (!tbs.ReadEncodedValue().Span.SequenceEqual(algorithmBytes.Span)
                || !tbs.ReadEncodedValue().Span.SequenceEqual(issuer.SubjectName.RawData))
            {
                return false;
            }

            var thisUpdate = ReadTime(tbs);
            var nextUpdate = ReadTime(tbs); // Missing nextUpdate cannot establish freshness.
            if (thisUpdate > time || nextUpdate <= time || nextUpdate <= thisUpdate)
            {
                return false;
            }

            var serial = new BigInteger(certificate.SerialNumberBytes.Span, isUnsigned: true, isBigEndian: true);
            var found = false;
            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var entries = tbs.ReadSequence();
                while (entries.HasData)
                {
                    var entry = entries.ReadSequence();
                    var number = entry.ReadInteger();
                    if (number.Sign < 0)
                    {
                        return false;
                    }
                    var date = ReadTime(entry);
                    if (entry.HasData && (!v2 || !ReadExtensions(entry.ReadSequence(), entryExtensions: true)))
                    {
                        return false;
                    }
                    entry.ThrowIfNotEmpty();
                    found |= number == serial && date <= time;
                }
            }
            if (tbs.HasData)
            {
                var extensions = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                if (!v2 || !ReadExtensions(extensions.ReadSequence(), entryExtensions: false))
                {
                    return false;
                }
                extensions.ThrowIfNotEmpty();
            }
            tbs.ThrowIfNotEmpty();
            revoked = found; // Publish a result only after the entire CRL has been validated.
            return true;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    private static bool ReadExtensions(AsnReader extensions, bool entryExtensions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (extensions.HasData)
        {
            var extension = extensions.ReadSequence();
            var oid = extension.ReadObjectIdentifier();
            var critical = extension.HasData && extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean) && extension.ReadBoolean();
            extension.ReadOctetString();
            extension.ThrowIfNotEmpty();
            // Delta, scoped/indirect CRLs and certificateIssuer entries need additional processing.
            // Never interpret them as a complete list proving that a certificate is good (RFC 5280).
            if (!seen.Add(oid) || critical || oid is "2.5.29.27" or "2.5.29.28"
                || (entryExtensions && oid == "2.5.29.29"))
            {
                return false;
            }
        }
        return true;
    }

    private static bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlyMemory<byte> encodedAlgorithm,
        byte[] signature, X509Certificate2 issuer)
    {
        var algorithm = new AsnReader(encodedAlgorithm, AsnEncodingRules.DER).ReadSequence();
        var oid = algorithm.ReadObjectIdentifier();
        var hash = oid switch
        {
            "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
            "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => default,
        };
        if (hash == default)
        {
            return false;
        }
        if (oid.StartsWith("1.2.840.113549.1.1.", StringComparison.Ordinal))
        {
            if (algorithm.HasData)
            {
                algorithm.ReadNull();
            }
            algorithm.ThrowIfNotEmpty();
            using var rsa = issuer.GetRSAPublicKey();
            return rsa?.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1) == true;
        }
        algorithm.ThrowIfNotEmpty();
        using var ec = issuer.GetECDsaPublicKey();
        return ec?.VerifyData(data, signature, hash, DSASignatureFormat.Rfc3279DerSequence) == true;
    }
}
