using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Public certificates trusted only by YRK on this computer.</summary>
public sealed class TrustedCertificateStore
{
    public static TrustedCertificateStore Default { get; } = new();
    private readonly string _folder;
    private readonly Action<string> _remove;
    public event EventHandler<EventArgs>? Changed;

    public TrustedCertificateStore(string? folder = null, Action<string>? remove = null)
    {
        _folder = Path.GetFullPath(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "TrustedCertificates"));
        _remove = remove ?? (path => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
    }

    public static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static string DisplayFingerprint(X509Certificate2 certificate)
    {
        var hex = Fingerprint(certificate);
        return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    private void CheckFolder()
    {
        // Do not follow junctions or symlinks to files outside this store.
        for (var dir = new DirectoryInfo(_folder); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The certificate folder must not be a link.");
    }

    public IReadOnlyList<X509Certificate2> LoadAll()
    {
        CheckFolder();
        var result = new List<X509Certificate2>();
        if (!Directory.Exists(_folder)) return result;
        foreach (var path in Directory.EnumerateFiles(_folder, "*.cer"))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var bytes = File.ReadAllBytes(path);
                using var cert = X509CertificateLoader.LoadCertificate(bytes);
                if (cert.HasPrivateKey || !bytes.AsSpan().SequenceEqual(cert.Export(X509ContentType.Cert))
                    || !string.Equals(Path.GetFileNameWithoutExtension(path), Fingerprint(cert), StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(X509CertificateLoader.LoadCertificate(bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
        }
        return result;
    }

    public void Add(X509Certificate2 certificate)
    {
        CheckFolder();
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, Fingerprint(certificate) + ".cer");
        // CreateNew never overwrites an existing user file, including a symlink.
        var bytes = certificate.Export(X509ContentType.Cert);
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new IOException("An existing file prevents this certificate from being added.");
            return;
        }
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Import(string path)
    {
        using var certificate = ReadImport(path);
        Add(certificate);
    }

    public static X509Certificate2 ReadImport(string path)
    {
        if (!new[] { ".cer", ".crt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new CryptographicException("Only .cer and .crt certificate files are accepted.");
        var bytes = File.ReadAllBytes(path);
        if (System.Text.Encoding.ASCII.GetString(bytes).Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new CryptographicException("Only public certificates are accepted.");
        if (X509Certificate2.GetCertContentType(bytes) != X509ContentType.Cert)
            throw new CryptographicException("Only public certificates are accepted.");
        var certificate = X509CertificateLoader.LoadCertificate(bytes);
        if (certificate.HasPrivateKey) { certificate.Dispose(); throw new CryptographicException("Only public certificates are accepted."); }
        return certificate;
    }

    public void Delete(X509Certificate2 certificate)
    {
        CheckFolder();
        var path = Path.Combine(_folder, Fingerprint(certificate) + ".cer");
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The certificate file must not be a link.");
        _remove(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
