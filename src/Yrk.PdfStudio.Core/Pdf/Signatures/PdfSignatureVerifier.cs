using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>Finds the signature fields of a PDF and checks each signature.</summary>
public static class PdfSignatureVerifier
{
    private const int MaxFieldDepth = 32;

    public static async Task<IReadOnlyList<PdfSignatureInfo>> VerifyFileAsync(string path, PdfVerifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Verify(bytes, options ?? new PdfVerifyOptions(), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file as it was when the signature was applied (the signed revision).</summary>
    public static byte[] GetSignedRevision(byte[] file, PdfSignatureInfo signature) =>
        file.AsSpan(0, (int)Math.Min(file.Length, signature.SignedLength)).ToArray();

    public static IReadOnlyList<PdfSignatureInfo> Verify(byte[] file, PdfVerifyOptions options, CancellationToken cancellationToken = default)
    {
        CosDocument document;
        try
        {
            document = CosDocument.Load(file);
        }
        catch (PdfException)
        {
            return [];
        }

        var results = new List<PdfSignatureInfo>();
        foreach (var (name, field) in FindSignatureFields(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Resolve(field["V"]) is CosDictionary signature)
            {
                results.Add(Check(document, file, name, field, signature, options));
            }
        }

        return results.OrderBy(r => r.SignedLength).ToList();
    }

    private static PdfSignatureInfo Check(CosDocument document, byte[] file, string fieldName, CosDictionary field, CosDictionary signature, PdfVerifyOptions options)
    {
        var subFilter = (document.Resolve(signature["SubFilter"]) as CosName)?.Value ?? string.Empty;
        var reason = (document.Resolve(signature["Reason"]) as CosString)?.ToText();
        var location = (document.Resolve(signature["Location"]) as CosString)?.ToText();
        var claimedTime = (document.Resolve(signature["M"]) as CosString)?.ToDate();
        var claimedName = (document.Resolve(signature["Name"]) as CosString)?.ToText();
        var pageIndex = FindPage(document, field);
        var isCertification = document.Resolve(signature["Reference"]) is CosArray references
            && references.Items.Select(document.Resolve).OfType<CosDictionary>().Any(r => (document.Resolve(r["TransformMethod"]) as CosName)?.Value == "DocMDP");

        PdfSignatureInfo Result(PdfSignatureStatus status, string? problem, long signedLength = 0, bool covers = false) => new(
            fieldName, pageIndex, claimedName ?? string.Empty, null, claimedTime, false, reason, location, subFilter,
            status, false, PdfSignerIdentity.Unknown, false, covers, isCertification, signedLength, problem);

        // /ByteRange must cover the whole revision except exactly the /Contents hex string.
        var range = (document.Resolve(signature["ByteRange"]) as CosArray)?.Items.Select(i => (document.Resolve(i) as CosInteger)?.Value ?? -1).ToArray();
        if (range is not { Length: 4 } || range[0] != 0 || range[1] <= 0 || range[2] <= range[1] || range[3] < 0 || range[2] + range[3] > file.Length
            || file[range[1]] != '<' || file[range[2] - 1] != '>')
        {
            return Result(PdfSignatureStatus.Invalid, "The signature's byte range is invalid.");
        }

        var signedLength = range[2] + range[3];
        var covers = file.AsSpan((int)signedLength).Trim("\r\n\0 \t"u8).IsEmpty;
        if (document.Resolve(signature["Contents"]) is not CosString { Bytes.Length: > 0 } contents)
        {
            return Result(PdfSignatureStatus.Invalid, "The signature has no contents.", signedLength, covers);
        }

        var signedData = new byte[range[1] + range[3]];
        file.AsSpan(0, (int)range[1]).CopyTo(signedData);
        file.AsSpan((int)range[2], (int)range[3]).CopyTo(signedData.AsSpan((int)range[1]));

        var detached = subFilter is "adbe.pkcs7.detached" or "ETSI.CAdES.detached";
        if (!detached && subFilter != "adbe.pkcs7.sha1")
        {
            return Result(PdfSignatureStatus.Unsupported, $"The signature format {subFilter} is not supported.", signedLength, covers);
        }

        SignedCms cms;
        try
        {
            cms = detached ? new SignedCms(new ContentInfo(signedData), detached: true) : new SignedCms();
            cms.Decode(TrimDer(contents.Bytes));
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException)
        {
            return Result(PdfSignatureStatus.Invalid, "The signature data is damaged.", signedLength, covers);
        }

        if (cms.SignerInfos.Count == 0)
        {
            return Result(PdfSignatureStatus.Invalid, "The signature has no signer.", signedLength, covers);
        }

        var signerInfo = cms.SignerInfos[0];
        var certificate = signerInfo.Certificate;
        var signerName = certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? claimedName ?? string.Empty;

        string? problem = null;
        var intact = true;
        try
        {
            signerInfo.CheckSignature(verifySignatureOnly: true);
            if (!detached && !cms.ContentInfo.Content.AsSpan().SequenceEqual(SHA1.HashData(signedData)))
            {
                intact = false;
            }
        }
        catch (CryptographicException)
        {
            intact = false;
        }

        if (!intact)
        {
            problem = "The document has been altered or corrupted since it was signed.";
        }
        else if (subFilter == "ETSI.CAdES.detached" && certificate is not null && !SigningCertificateMatches(signerInfo, certificate))
        {
            intact = false;
            problem = "The signer's certificate does not match the signed certificate reference.";
        }

        // A valid timestamp gives a trusted signing time; otherwise the time is the signer's claim.
        var (timestampTime, isTimestamped) = ReadTimestamp(signerInfo);
        var signingTime = timestampTime ?? ReadSigningTimeAttribute(signerInfo) ?? claimedTime;

        var identity = PdfSignerIdentity.Unknown;
        var revocationChecked = false;
        if (certificate is not null)
        {
            (identity, revocationChecked) = CheckIdentity(certificate, cms.Certificates, timestampTime, options);
        }

        var status = !intact || identity == PdfSignerIdentity.Revoked
            ? PdfSignatureStatus.Invalid
            : identity == PdfSignerIdentity.Trusted ? PdfSignatureStatus.Valid : PdfSignatureStatus.IdentityUnknown;
        if (problem is null && identity == PdfSignerIdentity.Revoked)
        {
            problem = "The signer's certificate has been revoked.";
        }

        return new PdfSignatureInfo(
            fieldName, pageIndex, signerName, certificate, signingTime, isTimestamped, reason, location, subFilter,
            status, intact, identity, revocationChecked, covers, isCertification, signedLength, problem);
    }

    private static (PdfSignerIdentity Identity, bool RevocationChecked) CheckIdentity(
        X509Certificate2 certificate,
        X509Certificate2Collection extra,
        DateTimeOffset? trustedTime,
        PdfVerifyOptions options)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        chain.ChainPolicy.RevocationMode = options.CheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.UrlRetrievalTimeout = options.NetworkTimeout ?? TimeSpan.FromSeconds(10);
        if (trustedTime is not null)
        {
            // With a timestamp the certificate only had to be valid when the document was signed.
            chain.ChainPolicy.VerificationTime = trustedTime.Value.UtcDateTime;
        }

        chain.Build(certificate);
        var flags = chain.ChainElements.SelectMany(e => e.ChainElementStatus).Select(s => s.Status)
            .Concat(chain.ChainStatus.Select(s => s.Status))
            .Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s);

        const X509ChainStatusFlags revocationUnknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        var revocationChecked = options.CheckRevocation && (flags & revocationUnknown) == 0;
        var identity = flags switch
        {
            _ when flags.HasFlag(X509ChainStatusFlags.Revoked) => PdfSignerIdentity.Revoked,
            _ when (flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0 => PdfSignerIdentity.Untrusted,
            _ when flags.HasFlag(X509ChainStatusFlags.NotTimeValid) => PdfSignerIdentity.Expired,
            _ when (flags & ~revocationUnknown) == 0 => PdfSignerIdentity.Trusted,
            _ => PdfSignerIdentity.Untrusted,
        };
        return (identity, revocationChecked);
    }

    private static (DateTimeOffset? Time, bool IsTimestamped) ReadTimestamp(SignerInfo signerInfo)
    {
        foreach (var attribute in signerInfo.UnsignedAttributes)
        {
            if (attribute.Oid.Value != CadesSignature.SignatureTimeStampToken)
            {
                continue;
            }

            foreach (var value in attribute.Values)
            {
                if (Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _)
                    && token.VerifySignatureForSignerInfo(signerInfo, out _))
                {
                    return (token.TokenInfo.Timestamp, true);
                }
            }
        }

        return (null, false);
    }

    private static DateTimeOffset? ReadSigningTimeAttribute(SignerInfo signerInfo)
    {
        foreach (var attribute in signerInfo.SignedAttributes)
        {
            foreach (var value in attribute.Values)
            {
                if (value is Pkcs9SigningTime time)
                {
                    return new DateTimeOffset(time.SigningTime);
                }
            }
        }

        return null;
    }

    /// <summary>Checks the ESS signing-certificate(-v2) attribute, when present, against the signer's certificate.</summary>
    private static bool SigningCertificateMatches(SignerInfo signerInfo, X509Certificate2 certificate)
    {
        foreach (var attribute in signerInfo.SignedAttributes)
        {
            if (attribute.Oid.Value != CadesSignature.SigningCertificateV2)
            {
                continue;
            }

            try
            {
                var reader = new AsnReader(attribute.Values[0].RawData, AsnEncodingRules.DER);
                var certIds = reader.ReadSequence().ReadSequence();
                var first = certIds.ReadSequence();
                var algorithm = HashAlgorithmName.SHA256;
                if (first.PeekTag().TagValue == (int)UniversalTagNumber.Sequence)
                {
                    algorithm = first.ReadSequence().ReadObjectIdentifier() switch
                    {
                        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
                        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
                        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
                        _ => HashAlgorithmName.SHA256,
                    };
                }

                var expected = first.ReadOctetString();
                using var hash = IncrementalHash.CreateHash(algorithm);
                hash.AppendData(certificate.RawData);
                return hash.GetHashAndReset().AsSpan().SequenceEqual(expected);
            }
            catch (AsnContentException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>/Contents is zero-padded; cut it to the length of the DER structure.</summary>
    private static byte[] TrimDer(byte[] data)
    {
        try
        {
            AsnDecoder.ReadEncodedValue(data, AsnEncodingRules.BER, out _, out _, out var consumed);
            return data.AsSpan(0, consumed).ToArray();
        }
        catch (AsnContentException)
        {
            return data;
        }
    }

    private static IEnumerable<(string Name, CosDictionary Field)> FindSignatureFields(CosDocument document)
    {
        if (document.Resolve(document.Catalog["AcroForm"]) is not CosDictionary acroForm
            || document.Resolve(acroForm["Fields"]) is not CosArray fields)
        {
            yield break;
        }

        var visited = new HashSet<CosObjectId>();
        var stack = new Stack<(CosObject Node, string Parent, string? FieldType, int Depth)>();
        foreach (var field in fields.Items.AsEnumerable().Reverse())
        {
            stack.Push((field, string.Empty, null, 0));
        }

        while (stack.Count > 0)
        {
            var (node, parent, inheritedType, depth) = stack.Pop();
            if (depth > MaxFieldDepth || (node is CosReference r && !visited.Add(r.Id)) || document.Resolve(node) is not CosDictionary dictionary)
            {
                continue;
            }

            var partial = (document.Resolve(dictionary["T"]) as CosString)?.ToText();
            var name = partial is null ? parent : parent.Length == 0 ? partial : parent + "." + partial;
            var type = (document.Resolve(dictionary["FT"]) as CosName)?.Value ?? inheritedType;
            if (type == "Sig" && dictionary["V"] is not null)
            {
                yield return (name, dictionary);
                continue;
            }

            if (document.Resolve(dictionary["Kids"]) is CosArray kids)
            {
                foreach (var kid in kids.Items.AsEnumerable().Reverse())
                {
                    stack.Push((kid, name, type, depth + 1));
                }
            }
        }
    }

    private static int? FindPage(CosDocument document, CosDictionary field)
    {
        var pages = document.GetPages();
        if (field["P"] is CosReference pageRef)
        {
            var index = pages.ToList().FindIndex(p => p.Id == pageRef.Id);
            if (index >= 0)
            {
                return index;
            }
        }

        // Fall back to finding the widget (the field itself or its first kid) in the pages' /Annots.
        for (var i = 0; i < pages.Count; i++)
        {
            if (document.Resolve(pages[i].Dictionary["Annots"]) is CosArray annots
                && annots.Items.Any(a => ReferenceEquals(document.Resolve(a), field)))
            {
                return i;
            }
        }

        return null;
    }
}
