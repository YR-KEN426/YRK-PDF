using System.Security.Cryptography.X509Certificates;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>What to sign with and how the signature looks.</summary>
/// <param name="Certificate">A certificate with an accessible private key (Windows store, smart card/USB token, or .pfx).</param>
/// <param name="Appearance">Where the visible signature goes; null for an invisible signature.</param>
/// <param name="TimestampAuthority">Adds a trusted timestamp (PAdES B-T) when set.</param>
/// <param name="Certification">Certifies the document (the first signature only): what may still change after it.</param>
public sealed record PdfSignRequest(
    X509Certificate2 Certificate,
    PdfSignatureAppearance? Appearance = null,
    string? Reason = null,
    string? Location = null,
    string? ContactInfo = null,
    IPdfTimestampAuthority? TimestampAuthority = null,
    DateTimeOffset? SigningTime = null,
    PdfCertification Certification = PdfCertification.None);

/// <summary>A certifying (author) signature and the changes it still allows (DocMDP /P).</summary>
public enum PdfCertification
{
    /// <summary>An ordinary approval signature.</summary>
    None = 0,

    /// <summary>No changes at all, not even more signatures.</summary>
    NoChanges = 1,

    /// <summary>Filling in forms and signing.</summary>
    FormFilling = 2,

    /// <summary>Filling in forms, signing and comments.</summary>
    FormFillingAndComments = 3,
}

/// <param name="Image">The finished appearance (BGRA; transparency is kept).</param>
public sealed record PdfSignatureAppearance(int PageIndex, NormalizedRect Bounds, PdfImage Image);

/// <summary>A time-stamping service (RFC 3161).</summary>
public interface IPdfTimestampAuthority
{
    /// <summary>Sends a DER-encoded TimeStampReq and returns the DER-encoded TimeStampResp.</summary>
    Task<byte[]> RequestAsync(byte[] request, CancellationToken cancellationToken);
}

public enum PdfSignatureStatus
{
    /// <summary>Unchanged since signing and the signer's certificate is trusted.</summary>
    Valid,

    /// <summary>Unchanged since signing, but the signer's identity could not be confirmed.</summary>
    IdentityUnknown,

    /// <summary>The signed bytes were changed, the signature is broken, or the certificate is revoked.</summary>
    Invalid,

    /// <summary>A signature format this version cannot check.</summary>
    Unsupported,
}

public enum PdfSignerIdentity
{
    Trusted,

    /// <summary>The certificate does not chain to a root this computer trusts (self-signed IDs, for example).</summary>
    Untrusted,

    Expired,
    Revoked,
    Unknown,
}

public sealed record PdfSignatureInfo(
    string FieldName,
    int? PageIndex,
    string SignerName,
    X509Certificate2? Certificate,
    DateTimeOffset? SigningTime,
    bool IsTimestamped,
    string? Reason,
    string? Location,
    string SubFilter,
    PdfSignatureStatus Status,
    bool IsIntact,
    PdfSignerIdentity Identity,
    bool RevocationChecked,
    bool CoversWholeDocument,
    bool IsCertification,
    long SignedLength,
    string? Problem)
{
    /// <summary>True when later revisions were appended (form filling, comments, more signatures).</summary>
    public bool ModifiedAfterSigning => !CoversWholeDocument;
}

public sealed record PdfVerifyOptions(bool CheckRevocation = true, TimeSpan? NetworkTimeout = null);
