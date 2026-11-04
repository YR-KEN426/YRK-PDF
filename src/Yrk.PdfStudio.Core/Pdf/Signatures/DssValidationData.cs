using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>Untrusted, bounded input from /DSS. Certificates here never become trust anchors.</summary>
internal sealed class DssValidationData : IDisposable
{
    internal X509Certificate2Collection Certificates { get; } = [];
    internal List<byte[]> Crls { get; } = [];

    internal static DssValidationData Read(CosDocument document)
    {
        var data = new DssValidationData();
        var remaining = 32 * 1024 * 1024;
        var items = 256;
        try
        {
            if (document.Resolve(document.Catalog["DSS"]) is CosDictionary dss)
            {
                ReadArray(dss["Certs"], certificates: true);
                ReadArray(dss["CRLs"], certificates: false);
                if (document.Resolve(dss["VRI"]) is CosDictionary vri)
                {
                    foreach (var value in vri.Entries.Take(256).Select(e => e.Value))
                    {
                        if (document.Resolve(value) is CosDictionary entry)
                        {
                            ReadArray(entry["Cert"], certificates: true);
                            ReadArray(entry["CRL"], certificates: false);
                        }
                    }
                }
            }
        }
        catch (PdfException)
        {
            // A damaged store supplies no additional evidence from the unreadable part.
        }
        return data;

        void ReadArray(CosObject? value, bool certificates)
        {
            if (document.Resolve(value) is not CosArray array)
            {
                return;
            }
            foreach (var item in array.Items)
            {
                if (--items < 0 || remaining <= 0)
                {
                    break;
                }
                try
                {
                    var limit = Math.Min(remaining, certificates ? 1024 * 1024 : CrlValidation.MaxCrlBytes);
                    if (document.Resolve(item) is not CosStream stream || stream.Data.Length > limit)
                    {
                        continue;
                    }
                    var bytes = CosFilters.Decode(stream, document.Resolve, limit);
                    remaining -= bytes.Length;
                    if (certificates)
                    {
                        data.Certificates.Add(X509CertificateLoader.LoadCertificate(bytes));
                    }
                    else
                    {
                        data.Crls.Add(bytes);
                    }
                }
                catch (Exception ex) when (ex is PdfException or CryptographicException or ArgumentException)
                {
                    // Ignore individual malformed or oversized streams, not other evidence.
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var certificate in Certificates)
        {
            certificate.Dispose();
        }
    }
}
