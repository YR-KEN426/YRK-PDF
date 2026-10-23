using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>Builds the detached CMS (CAdES) signature that a PAdES signature embeds (ETSI EN 319 142-1).</summary>
internal static class CadesSignature
{
    public const string Sha256 = "2.16.840.1.101.3.4.2.1";
    public const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    public const string SignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";

    /// <summary>
    /// Signs <paramref name="content"/> (the PDF bytes outside /Contents). Following PAdES baseline, the
    /// signed attributes are content-type, message-digest and signing-certificate-v2; the signing
    /// time lives in the signature dictionary (/M), not in a signing-time attribute.
    /// </summary>
    public static async Task<byte[]> CreateAsync(
        byte[] content,
        X509Certificate2 certificate,
        IPdfTimestampAuthority? timestampAuthority,
        CancellationToken cancellationToken)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new PdfException("The certificate has no private key, so it cannot sign.");
        }

        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid(Sha256),
            IncludeOption = X509IncludeOption.WholeChain,
        };
        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2, EncodeSigningCertificateV2(certificate)));

        try
        {
            // silent: false lets Windows ask for a smart card or token PIN.
            cms.ComputeSignature(signer, silent: false);
        }
        catch (CryptographicException ex)
        {
            throw new PdfException("Signing failed: " + ex.Message, ex);
        }

        if (timestampAuthority is not null)
        {
            var signerInfo = cms.SignerInfos[0];
            var nonce = RandomNumberGenerator.GetBytes(8);
            nonce[0] &= 0x7F; // Keep the nonce a positive INTEGER.
            var request = Rfc3161TimestampRequest.CreateFromSignerInfo(signerInfo, HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: nonce);
            byte[] response;
            try
            {
                response = await timestampAuthority.RequestAsync(request.Encode(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                throw new PdfException("The timestamp server could not be reached. Check the internet connection or sign without a timestamp.", ex);
            }

            Rfc3161TimestampToken token;
            try
            {
                token = request.ProcessResponse(response, out _);
            }
            catch (CryptographicException ex)
            {
                throw new PdfException("The timestamp server sent an invalid reply: " + ex.Message, ex);
            }

            signerInfo.AddUnsignedAttribute(new AsnEncodedData(SignatureTimeStampToken, token.AsSignedCms().Encode()));
        }

        return cms.Encode();
    }

    /// <summary>
    /// ESS signing-certificate-v2 (RFC 5035): binds the signer's certificate into the signed data, so it
    /// cannot be swapped for another certificate with the same key.
    /// </summary>
    internal static byte[] EncodeSigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())                      // SigningCertificateV2
        using (writer.PushSequence())                      // certs SEQUENCE OF ESSCertIDv2
        using (writer.PushSequence())                      // ESSCertIDv2 (hashAlgorithm omitted: SHA-256 is the default)
        {
            writer.WriteOctetString(SHA256.HashData(certificate.RawData));
            using (writer.PushSequence())                  // IssuerSerial
            {
                using (writer.PushSequence())              // GeneralNames
                {
                    var directoryName = new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true);
                    writer.PushSequence(directoryName);
                    writer.WriteEncodedValue(certificate.IssuerName.RawData);
                    writer.PopSequence(directoryName);
                }

                writer.WriteInteger(certificate.SerialNumberBytes.Span);
            }
        }

        return writer.Encode();
    }
}

/// <summary>Talks to an RFC 3161 time-stamping service over HTTP.</summary>
public sealed class HttpTimestampAuthority(Uri url) : IPdfTimestampAuthority
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Uri Url { get; } = url;

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        using var response = await Client.PostAsync(Url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
