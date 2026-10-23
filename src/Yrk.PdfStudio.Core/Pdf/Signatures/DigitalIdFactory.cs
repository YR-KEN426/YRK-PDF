using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>Creates self-signed digital IDs, like Acrobat's "Create a new Digital ID".</summary>
public static class DigitalIdFactory
{
    private const string DocumentSigning = "1.3.6.1.4.1.311.10.3.12";
    private const string EmailProtection = "1.3.6.1.5.5.7.3.4";

    /// <summary>Returns a password-protected PKCS#12 (.pfx) file with an RSA 3072 key.</summary>
    public static byte[] CreateSelfSigned(string name, string? email, string? organization, string password, int validYears = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var subject = new StringBuilder("CN=").Append(Escape(name.Trim()));
        if (!string.IsNullOrWhiteSpace(organization))
        {
            subject.Append(", O=").Append(Escape(organization.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            subject.Append(", E=").Append(Escape(email.Trim()));
        }

        using var key = RSA.Create(3072);
        var request = new CertificateRequest(new X500DistinguishedName(subject.ToString(), X500DistinguishedNameFlags.UseUTF8Encoding), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(DocumentSigning), new Oid(EmailProtection)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        if (!string.IsNullOrWhiteSpace(email))
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress(email.Trim());
            request.CertificateExtensions.Add(san.Build());
        }

        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var certificate = request.CreateSelfSigned(now, now.AddYears(validYears));

        // The certificate stays readable so ID lists can show its name and expiry; only the key is encrypted.
        var localKeyId = new Pkcs9LocalKeyId(SHA256.HashData(certificate.RawData));
        var certificates = new Pkcs12SafeContents();
        certificates.AddCertificate(certificate).Attributes.Add(localKeyId);
        var keys = new Pkcs12SafeContents();
        keys.AddShroudedKey(key, password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)).Attributes.Add(localKeyId);
        var builder = new Pkcs12Builder();
        builder.AddSafeContentsUnencrypted(certificates);
        builder.AddSafeContentsUnencrypted(keys);
        builder.SealWithMac(password, HashAlgorithmName.SHA256, 100_000);
        return builder.Encode();
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '+', '"', '\\', '<', '>', ';', '=']) >= 0 ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
}
