using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yrk.PdfStudio.App.Services.Backup;

public sealed class BackupWriter(IBackupSignatureProtection? signatures = null)
{
    private readonly IBackupSignatureProtection _signatures = signatures ?? new DpapiBackupSignatureProtection();

    /// <summary>Exports only known user stores. The destination must be new; no existing file is overwritten.</summary>
    public BackupManifest Write(string backupPath, string dataDirectory, ReadOnlySpan<char> password, string appVersion)
    {
        using var zipBytes = new MemoryStream();
        byte[]? key = null;
        try
        {
            BackupFormat.CheckNoLinks(dataDirectory);
            if (string.IsNullOrWhiteSpace(appVersion) || appVersion.Length > 128) throw BackupFormat.Invalid();
            var files = new List<BackupFile>();
            var manifest = new BackupManifest(appVersion, DateTimeOffset.UtcNow, files);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var path in Sources(dataDirectory))
                {
                    BackupFormat.CheckPath(path);
                    if (!names.Add(path) || files.Count >= BackupFormat.MaxFileCount) throw BackupFormat.Invalid();
                    var bytes = BackupFormat.ReadBounded(BackupFormat.Under(dataDirectory, path), BackupFormat.MaxFileBytes);
                    try
                    {
                        if (BackupFormat.IsSignature(path))
                        {
                            var portable = _signatures.Unprotect(bytes);
                            if (!ReferenceEquals(bytes, portable)) CryptographicOperations.ZeroMemory(bytes);
                            bytes = portable;
                        }
                        total += bytes.Length;
                        if (bytes.Length > BackupFormat.MaxFileBytes || total > BackupFormat.MaxTotalUncompressedBytes) throw BackupFormat.Invalid();
                        files.Add(new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
                        using var entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                        entry.Write(bytes);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                var json = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupFormat.Json);
                try
                {
                    if (json.Length > BackupFormat.MaxManifestBytes || total + json.Length > BackupFormat.MaxTotalUncompressedBytes) throw BackupFormat.Invalid();
                    using var entry = zip.CreateEntry(BackupFormat.ManifestPath, CompressionLevel.Optimal).Open();
                    entry.Write(json);
                }
                finally { CryptographicOperations.ZeroMemory(json); }
            }
            if (zipBytes.Length + BackupFormat.HeaderBytes + BackupFormat.TagBytes > BackupFormat.MaxBackupFileBytes) throw BackupFormat.Invalid();
            var header = BackupFormat.Header();
            key = BackupFormat.Key(password, header);
            var encrypted = new byte[checked(BackupFormat.HeaderBytes + (int)zipBytes.Length + BackupFormat.TagBytes)];
            header.CopyTo(encrypted, 0);
            using var aes = new AesGcm(key, BackupFormat.TagBytes);
            aes.Encrypt(header.AsSpan(7 + BackupFormat.SaltBytes, BackupFormat.NonceBytes),
                zipBytes.GetBuffer().AsSpan(0, (int)zipBytes.Length),
                encrypted.AsSpan(BackupFormat.HeaderBytes, (int)zipBytes.Length), encrypted.AsSpan(encrypted.Length - BackupFormat.TagBytes), header);
            BackupFormat.AtomicWrite(Path.GetFullPath(backupPath), encrypted, checkLinks: false);
            return manifest;
        }
        catch (Exception ex) when (BackupFormat.IsInputError(ex)) { throw new IOException(Loc.T(BackupFormat.WriteError)); }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            BackupFormat.Clear(zipBytes);
        }
    }

    private static IEnumerable<string> Sources(string root)
    {
        foreach (var name in BackupFormat.Settings)
            if (File.Exists(BackupFormat.Under(root, name))) yield return name;
        foreach (var folder in BackupFormat.Folders)
        {
            var full = BackupFormat.Under(root, folder);
            BackupFormat.CheckNoLinks(full);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.EnumerateFiles(full).Order(StringComparer.Ordinal))
            {
                var relative = folder + "/" + Path.GetFileName(file);
                // Unknown files, caches and subdirectories are never exported.
                try { BackupFormat.CheckPath(relative); }
                catch (InvalidDataException) { continue; }
                yield return relative;
            }
        }
    }
}
