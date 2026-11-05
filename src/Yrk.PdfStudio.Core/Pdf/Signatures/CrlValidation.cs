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
                    if (entry.HasData && (!v2 || !ReadExtensions(entry.ReadSequence(), certificate, entryExtensions: true)))
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
                if (!v2 || !ReadExtensions(extensions.ReadSequence(), certificate, entryExtensions: false))
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

    private static bool ReadExtensions(AsnReader extensions, X509Certificate2 certificate, bool entryExtensions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (extensions.HasData)
        {
            var extension = extensions.ReadSequence();
            var oid = extension.ReadObjectIdentifier();
            var critical = false;
            if (extension.HasData && extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
            {
                // An explicit FALSE is not strict DER but means the same as an omitted one; 1.1.5 accepted it,
                // and rejecting it would break LTV for CAs that write it, with no gain in safety.
                critical = extension.ReadBoolean();
            }
            var value = extension.ReadOctetString();
            extension.ThrowIfNotEmpty();
            // Delta and indirect CRLs cannot prove complete, direct revocation coverage.
            if (!seen.Add(oid) || oid == "2.5.29.27"
                || (entryExtensions && oid == "2.5.29.29"))
            {
                return false;
            }
            if (oid == "2.5.29.28")
            {
                // Scope applies equally to critical and non-critical IDP extensions.
                if (entryExtensions || !CheckIssuingDistributionPoint(value, certificate))
                {
                    return false;
                }
            }
            else if (critical)
            {
                return false;
            }
        }
        return true;
    }

    private static bool CheckIssuingDistributionPoint(byte[] value, X509Certificate2 certificate)
    {
        var reader = new AsnReader(value, AsnEncodingRules.DER);
        var point = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        var urls = new List<Uri>();
        var usersOnly = false;
        var casOnly = false;
        var unsupportedScope = false;
        var previous = -1;
        while (point.HasData)
        {
            var tag = point.PeekTag();
            // SEQUENCE fields must occur once, in schema order, with no unknown fields.
            if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue <= previous || tag.TagValue > 5)
            {
                return false;
            }
            previous = tag.TagValue;
            if (tag.TagValue == 0)
            {
                urls.AddRange(ReadDistributionPointName(point));
            }
            else if (tag.TagValue == 3)
            {
                point.ReadBitString(out _, tag);
                unsupportedScope = true; // Even an empty reasons mask cannot establish full coverage.
            }
            else
            {
                if (!point.ReadBoolean(tag))
                {
                    continue; // An explicit FALSE (DEFAULT FALSE) narrows nothing, same as omitted.
                }
                switch (tag.TagValue)
                {
                    case 1: usersOnly = true; break;
                    case 2: casOnly = true; break;
                    case 4:
                    case 5: unsupportedScope = true; break;
                }
            }
        }
        if (urls.Count == 0 || unsupportedScope)
        {
            return false;
        }
        var constraints = certificate.Extensions["2.5.29.19"];
        var isCa = constraints is not null
            && new X509BasicConstraintsExtension(constraints, constraints.Critical).CertificateAuthority;
        if ((usersOnly && isCa) || (casOnly && !isCa))
        {
            return false;
        }
        var certificateUrls = ReadCertificateDistributionPoints(certificate);
        return urls.Any(certificateUrls.Contains);
    }

    private static List<Uri> ReadCertificateDistributionPoints(X509Certificate2 certificate)
    {
        var urls = new List<Uri>();
        var extensions = certificate.Extensions.Cast<X509Extension>().Where(e => e.Oid?.Value == "2.5.29.31").ToArray();
        if (extensions.Length != 1)
        {
            return urls;
        }
        var reader = new AsnReader(extensions[0].RawData, AsnEncodingRules.DER);
        var points = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        while (points.HasData)
        {
            var point = points.ReadSequence();
            var previous = -1;
            while (point.HasData)
            {
                var tag = point.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue <= previous || tag.TagValue > 2)
                {
                    throw new AsnContentException();
                }
                previous = tag.TagValue;
                switch (tag.TagValue)
                {
                    case 0: urls.AddRange(ReadDistributionPointName(point)); break;
                    case 1: point.ReadBitString(out _, tag); break;
                    case 2: ReadGeneralNameUris(point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2, true))); break;
                }
            }
        }
        return urls;
    }

    private static List<Uri> ReadDistributionPointName(AsnReader reader)
    {
        var context0 = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        var name = reader.ReadSequence(context0); // distributionPoint [0] EXPLICIT
        var urls = ReadGeneralNameUris(name.ReadSequence(context0)); // fullName [0] IMPLICIT GeneralNames
        name.ThrowIfNotEmpty();
        return urls;
    }

    private static List<Uri> ReadGeneralNameUris(AsnReader names)
    {
        var urls = new List<Uri>();
        if (!names.HasData)
        {
            throw new AsnContentException(); // GeneralNames SIZE (1..MAX).
        }
        while (names.HasData)
        {
            // Only URI names are supported for direct CRL distribution points.
            // Do not skip unrecognised or unsupported GeneralName encodings.
            var text = names.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
            {
                throw new AsnContentException();
            }
            urls.Add(url);
        }
        return urls;
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
