using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.App.Services;

/// <summary>
/// A certificate the user can sign with: one in the Windows certificate store (including smart cards
/// and USB tokens, whose PIN Windows asks for) or a password-protected .pfx file kept by the app.
/// </summary>
public sealed record DigitalId(string Name, string? Issuer, DateTime? NotAfter, string? Thumbprint, string? FilePath)
{
    public bool IsFile => FilePath is not null;

    public bool IsExpired => NotAfter < DateTime.Now;

    public string Details => Issuer is null
        ? Loc.T("Digital ID file · details appear after you enter its password")
        : Loc.F(IsExpired ? "{0} · issued by {1} · expired {2:d}" : "{0} · issued by {1} · expires {2:d}", Loc.T(IsFile ? "Digital ID file" : "Windows certificate store"), Issuer, NotAfter);
}

public sealed class DigitalIdStore
{
    private readonly string _folder;
    private readonly Action<string> _remove;

    /// <param name="remove">Takes a file away (the Recycle Bin unless a test says otherwise).</param>
    public DigitalIdStore(string? folder = null, Action<string>? remove = null)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "DigitalIDs");
        _remove = remove ?? MoveToRecycleBin;
    }

    public IReadOnlyList<DigitalId> LoadAll()
    {
        var ids = new List<DigitalId>();
        if (Directory.Exists(_folder))
        {
            foreach (var path in Directory.EnumerateFiles(_folder, "*.pfx").Concat(Directory.EnumerateFiles(_folder, "*.p12")))
            {
                // The public part of a .pfx is usually readable without the password.
                ids.Add(ReadFileInfo(path) ?? new DigitalId(Path.GetFileNameWithoutExtension(path), null, null, null, path));
            }
        }

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        foreach (var certificate in store.Certificates)
        {
            using (certificate)
            {
                if (certificate.HasPrivateKey && CanSign(certificate))
                {
                    ids.Add(new DigitalId(
                        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
                        certificate.NotAfter,
                        certificate.Thumbprint,
                        null));
                }
            }
        }

        return ids.OrderBy(i => i.IsExpired).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Opens the certificate with its private key. Files need their password.</summary>
    /// <exception cref="CryptographicException">Wrong password or unreadable file.</exception>
    public X509Certificate2 Open(DigitalId id, string? password)
    {
        if (id.FilePath is { } path)
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, id.Thumbprint!, validOnly: false);
        return found.Count > 0 ? found[0] : throw new CryptographicException("The certificate is no longer in the Windows certificate store.");
    }

    /// <summary>Creates a self-signed ID and keeps it as a .pfx file.</summary>
    public DigitalId Create(string name, string? email, string? organization, string password)
    {
        var pfx = DigitalIdFactory.CreateSelfSigned(name, email, organization, password);
        return Save(pfx, name);
    }

    /// <summary>Copies an existing .pfx/.p12 into the app's folder after checking the password.</summary>
    public DigitalId Import(string path, string password)
    {
        using (X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet))
        {
        }

        return Save(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Copies an ID file elsewhere, for safe keeping; the copy stays protected by the ID's password.</summary>
    /// <exception cref="CryptographicException">Wrong password.</exception>
    public void BackUp(DigitalId id, string password, string target)
    {
        CheckPassword(id, password);
        File.Copy(id.FilePath!, target, overwrite: true);
    }

    /// <summary>Removes an ID file the app keeps. What it signed stays valid; it can sign nothing more.</summary>
    /// <exception cref="CryptographicException">Wrong password.</exception>
    public void Delete(DigitalId id, string password)
    {
        CheckPassword(id, password);
        _remove(id.FilePath!);
    }

    private void CheckPassword(DigitalId id, string password)
    {
        if (!id.IsFile)
        {
            throw new InvalidOperationException("Only ID files kept by the app can be backed up or deleted.");
        }

        using var certificate = Open(id, password);
    }

    private static void MoveToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    private DigitalId Save(byte[] pfx, string name)
    {
        Directory.CreateDirectory(_folder);
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var path = Path.Combine(_folder, (safe.Length > 0 ? safe : "Digital ID") + ".pfx");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(_folder, $"{safe} ({i}).pfx");
        }

        File.WriteAllBytes(path, pfx);
        return ReadFileInfo(path) ?? new DigitalId(Path.GetFileNameWithoutExtension(path), null, null, null, path);
    }

    private static DigitalId? ReadFileInfo(string path)
    {
        // Reads the certificate bags that are not encrypted, without the password (the MAC is not
        // checked here; Open checks it). The first certificate is the signer's in the files we write.
        try
        {
            var info = Pkcs12Info.Decode(File.ReadAllBytes(path), out _, skipCopy: true);
            var certificate = info.AuthenticatedSafe
                .Where(s => s.ConfidentialityMode == Pkcs12ConfidentialityMode.None)
                .SelectMany(s => s.GetBags())
                .OfType<Pkcs12CertBag>()
                .Where(b => b.IsX509Certificate)
                .Select(b => b.GetCertificate())
                .FirstOrDefault();
            if (certificate is null)
            {
                return null; // Encrypted certificate bag: the details appear once the password is known.
            }

            using (certificate)
            {
                return new DigitalId(
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
                    certificate.NotAfter,
                    null,
                    path);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Certificates meant for signing documents: non-repudiation, or an e-mail, document-signing or
    /// "any" purpose. This leaves out device and sign-in certificates (Wi-Fi, VPN, work accounts).
    /// </summary>
    private static bool CanSign(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages;
        if (usage is { } flags && (flags & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
        {
            return false;
        }

        if (usage is { } nr && nr.HasFlag(X509KeyUsageFlags.NonRepudiation))
        {
            return true;
        }

        var purposes = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault()?.EnhancedKeyUsages;
        return purposes is not null && purposes.Cast<Oid>().Any(p => p.Value is EmailProtection or DocumentSigning or AdobeAuthenticDocuments or AnyPurpose);
    }

    private const string EmailProtection = "1.3.6.1.5.5.7.3.4";
    private const string DocumentSigning = "1.3.6.1.4.1.311.10.3.12";
    private const string AdobeAuthenticDocuments = "1.2.840.113583.1.1.5";
    private const string AnyPurpose = "2.5.29.37.0";
}
