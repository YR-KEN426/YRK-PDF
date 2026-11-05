using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>Downloads the revocation lists (CRLs) that certificates name.</summary>
public interface IPdfRevocationSource
{
    Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>Gets revocation lists over HTTP.</summary>
public sealed class HttpRevocationSource : IPdfRevocationSource
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _client;

    public HttpRevocationSource() : this(DefaultClient) { }

    internal HttpRevocationSource(HttpClient client) => _client = client;

    public async Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > CrlValidation.MaxCrlBytes)
        {
            throw new HttpRequestException("The revocation list is too large.");
        }
        // ResponseHeadersRead does not apply HttpClient.Timeout to the response body.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > CrlValidation.MaxCrlBytes)
            {
                throw new HttpRequestException("The revocation list is too large.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

/// <summary>
/// PAdES B-LT: after signing, the certificates of the signer and the timestamp authority and the current revocation
/// lists for them go into the document security store (/DSS, ETSI EN 319 142-1 §5.4), as one more incremental
/// update. The signature can then be checked years later without asking anyone, even after the certificates expire.
/// </summary>
internal static class LongTermValidation
{
    private const string CrlDistributionPoints = "2.5.29.31";

    public static async Task<byte[]> AddAsync(byte[] pdf, byte[] cms, IPdfRevocationSource source, string? password, CancellationToken cancellationToken)
    {
        var document = CosDocument.Load(pdf, password ?? string.Empty);
        using var existing = DssValidationData.Read(document);
        var (certificates, crls) = await CollectAsync(cms, source, cancellationToken, existing.Certificates).ConfigureAwait(false);
        var update = new CosIncrementalUpdate(document);
        var rootId = (document.Trailer["Root"] as CosReference)?.Id ?? throw new PdfException("The document has no catalog.");

        // A store from an earlier signature is kept and added to.
        var dss = (document.Resolve(document.Catalog["DSS"]) as CosDictionary)?.Clone() ?? new CosDictionary();
        Append(document, update, dss, "Certs", certificates);
        Append(document, update, dss, "CRLs", crls);
        update.Replace(rootId, document.Catalog.Clone().Set("DSS", update.Add(dss)));
        return update.Build().Bytes;
    }

    /// <summary>Every certificate the signature and its timestamp depend on, and a revocation list for each one that has one.</summary>
    internal static async Task<(List<byte[]> Certificates, List<byte[]> Crls)> CollectAsync(byte[] cms, IPdfRevocationSource source, CancellationToken cancellationToken,
        X509Certificate2Collection? extra = null)
    {
        var signed = new SignedCms();
        signed.Decode(cms);
        var pool = new X509Certificate2Collection(signed.Certificates);
        if (extra is not null)
        {
            pool.AddRange(extra);
        }
        var leaves = new List<X509Certificate2>();
        foreach (var signer in signed.SignerInfos)
        {
            if (signer.Certificate is { } certificate)
            {
                leaves.Add(certificate);
            }

            foreach (var attribute in signer.UnsignedAttributes)
            {
                if (attribute.Oid?.Value != CadesSignature.SignatureTimeStampToken)
                {
                    continue;
                }

                foreach (var value in attribute.Values)
                {
                    var token = new SignedCms();
                    token.Decode(value.RawData);
                    pool.AddRange(token.Certificates);
                    leaves.AddRange(token.SignerInfos.Cast<SignerInfo>().Select(s => s.Certificate).OfType<X509Certificate2>());
                }
            }
        }

        var certificates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var crls = new List<byte[]>();
        var checkedCertificates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            chain.ChainPolicy.ExtraStore.AddRange(pool);
            chain.Build(leaf);
            for (var i = 0; i < chain.ChainElements.Count; i++)
            {
                var certificate = chain.ChainElements[i].Certificate;
                certificates.TryAdd(certificate.Thumbprint, certificate.RawData);
                if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) || !checkedCertificates.Add(certificate.Thumbprint))
                {
                    continue; // A root vouches for itself; and each certificate is looked up once.
                }

                var urls = CrlUrls(certificate);
                if (urls.Count == 0)
                {
                    continue;
                }

                var issuer = i + 1 < chain.ChainElements.Count ? chain.ChainElements[i + 1].Certificate : null;
                crls.Add(await DownloadAsync(urls, source, certificate, issuer, cancellationToken).ConfigureAwait(false));
            }
        }

        return (certificates.Values.ToList(), crls);
    }

    private static async Task<byte[]> DownloadAsync(IReadOnlyList<Uri> urls, IPdfRevocationSource source,
        X509Certificate2 certificate, X509Certificate2? issuer, CancellationToken cancellationToken)
    {
        Exception? last = null;
        Exception? validationFailure = null;
        Uri? invalidUrl = null;
        foreach (var url in urls)
        {
            try
            {
                var crl = await source.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (issuer is null || !CrlValidation.TryCheck(crl, certificate, issuer, DateTimeOffset.UtcNow, out _))
                {
                    validationFailure = new InvalidDataException("The revocation list has an invalid signature, validity period, size or format, or its issuer is unavailable.");
                    invalidUrl = url;
                    continue;
                }
                return crl;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = ex; // Try the next place the certificate names.
            }
        }

        if (validationFailure is not null)
        {
            throw DownloadError(
                "The revocation list from {0} was downloaded but could not be validated. Sign without long-term validation, or try again later.",
                invalidUrl!, validationFailure);
        }
        throw DownloadError(
            "The revocation list at {0} could not be downloaded. Check the internet connection, or sign without long-term validation.", urls[0], last!);
    }

    private static PdfException DownloadError(string format, Uri url, Exception cause)
    {
        // Keep the existing exception type and English message; the UI translates the format before inserting the URL.
        var error = new PdfException(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, url), cause);
        error.Data["RevocationMessageFormat"] = format;
        error.Data["RevocationUrl"] = url;
        return error;
    }

    /// <summary>The http(s) addresses in a certificate's CRL distribution points.</summary>
    internal static IReadOnlyList<Uri> CrlUrls(X509Certificate2 certificate)
    {
        var urls = new List<Uri>();
        if (certificate.Extensions[CrlDistributionPoints] is not { } extension)
        {
            return urls;
        }

        var context0 = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        var uri = new Asn1Tag(TagClass.ContextSpecific, 6);
        try
        {
            var points = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                if (!point.HasData || !point.PeekTag().HasSameClassAndValue(context0))
                {
                    continue;
                }

                var name = point.ReadSequence(context0);                // distributionPoint [0] DistributionPointName
                if (!name.HasData || !name.PeekTag().HasSameClassAndValue(context0))
                {
                    continue;                                           // nameRelativeToCRLIssuer: not an address
                }

                var names = name.ReadSequence(context0);                // fullName [0] GeneralNames
                while (names.HasData)
                {
                    if (!names.PeekTag().HasSameClassAndValue(uri))
                    {
                        names.ReadEncodedValue();
                        continue;
                    }

                    var text = names.ReadCharacterString(UniversalTagNumber.IA5String, uri);
                    if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                    {
                        urls.Add(url);
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // A damaged extension: there is nothing to download.
        }

        return urls;
    }

    private static void Append(CosDocument document, CosIncrementalUpdate update, CosDictionary dss, string key, IEnumerable<byte[]> items)
    {
        var array = (document.Resolve(dss[key]) as CosArray)?.Clone() ?? new CosArray();
        var existing = array.Items.Select(document.Resolve).OfType<CosStream>().Select(s => Convert.ToHexString(SHA256.HashData(CosFilters.Decode(s, document.Resolve)))).ToHashSet();
        foreach (var item in items)
        {
            if (existing.Add(Convert.ToHexString(SHA256.HashData(item))))
            {
                array.Items.Add(update.Add(new CosStream(new CosDictionary().Set("Filter", new CosName("FlateDecode")), CosFilters.Deflate(item))));
            }
        }

        dss[key] = array;
    }
}
