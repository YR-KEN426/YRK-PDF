using System.Windows.Media;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.App.ViewModels;

/// <summary>One signature in the Signatures panel.</summary>
public sealed class SignatureInfoViewModel(PdfSignatureInfo info)
{
    private static readonly Brush ValidBrush = Frozen(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xC1, 0x7C, 0x00));
    private static readonly Brush InvalidBrush = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));

    public PdfSignatureInfo Info { get; } = info;

    public string Title => Loc.F(Info.IsCertification ? "Certified by {0}" : "Signed by {0}", Info.SignerName.Length > 0 ? Info.SignerName : Loc.T("unknown signer"));

    public string Glyph => Info.Status switch
    {
        PdfSignatureStatus.Valid => "",          // Check mark
        PdfSignatureStatus.Invalid => "",        // Cancel
        _ => "",                                 // Warning
    };

    public Brush StatusBrush => Info.Status switch
    {
        PdfSignatureStatus.Valid => ValidBrush,
        PdfSignatureStatus.Invalid => InvalidBrush,
        _ => WarningBrush,
    };

    public string StatusText => Loc.T(Info.Status switch
    {
        PdfSignatureStatus.Valid => "Signature is valid.",
        PdfSignatureStatus.Invalid => Info.Problem ?? "Signature is invalid.",
        PdfSignatureStatus.Unsupported => Info.Problem ?? "This signature format can't be checked.",
        _ => Info.Identity switch
        {
            PdfSignerIdentity.Untrusted => "Signature is intact, but the signer's identity is unknown: the certificate isn't from an authority this computer trusts.",
            PdfSignerIdentity.Expired => "Signature is intact, but the signer's certificate has expired.",
            _ => "Signature is intact, but the signer's identity couldn't be checked.",
        },
    });

    public IReadOnlyList<string> Details
    {
        get
        {
            var lines = new List<string>();
            if (Info.SigningTime is { } time)
            {
                var when = DigitalSignatureRenderer.FormatTime(time.ToLocalTime());
                lines.Add(Info.IsTimestamped ? Loc.F("Signed: {0} (trusted timestamp)", when) : Loc.F("Signed: {0} (signer's clock)", when));
            }

            if (!string.IsNullOrWhiteSpace(Info.Reason))
            {
                lines.Add(Loc.F("Reason: {0}", Info.Reason));
            }

            if (!string.IsNullOrWhiteSpace(Info.Location))
            {
                lines.Add(Loc.F("Location: {0}", Info.Location));
            }

            if (Info.IsIntact)
            {
                lines.Add(Loc.T(Info.ModifiedAfterSigning
                    ? "The document was changed after this signature (for example filled in, commented or signed again). Use \"View signed version\" to see what was signed."
                    : "The document hasn't been changed since it was signed."));
            }

            if (Info.Identity == PdfSignerIdentity.Trusted && !Info.RevocationChecked)
            {
                lines.Add(Loc.T("Revocation wasn't checked (offline)."));
            }

            if (Info.PageIndex is { } page)
            {
                lines.Add(Loc.F("Field: {0} on page {1}", Info.FieldName, page + 1));
            }

            return lines;
        }
    }

    public bool HasCertificate => Info.Certificate is not null;

    public bool CanViewSignedVersion => Info.IsIntact && Info.ModifiedAfterSigning;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
