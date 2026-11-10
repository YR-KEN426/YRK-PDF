using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yrk.PdfStudio.App.Services.Backup;

public sealed record BackupImportResult(BackupManifest Manifest, IReadOnlyDictionary<string, string> ImportedPaths);

public sealed class BackupReader(IBackupSignatureProtection? signatures = null)
{
    private readonly IBackupSignatureProtection _signatures = signatures ?? new DpapiBackupSignatureProtection();

    /// <summary>Authenticates and validates all entries in memory; never writes to disk.</summary>
    public BackupManifest Inspect(string backupPath, ReadOnlySpan<char> password, bool checkLinks = false)
    {
        byte[]? bytes = null;
        try
        {
            bytes = Decrypt(backupPath, password, checkLinks);
            CheckZipDirectory(bytes);
            using var memory = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            return Extract(zip, null);
        }
        catch (Exception ex) when (BackupFormat.IsInputError(ex)) { throw BackupFormat.Invalid(); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// Authenticates, extracts, hashes and prepares every file before touching dataDirectory.
    /// Existing settings are skipped unless replaceSettings is true. Stamps then merge, keeping both designs.
    /// Atomicity is per file during commit; an OS/disk failure during commit can leave a partial import.
    /// The caller in step 5 is responsible for backing up existing data before calling this method.
    /// </summary>
    public BackupImportResult Import(string backupPath, string dataDirectory, string tempDirectory,
        ReadOnlySpan<char> password, bool replaceSettings = false)
    {
        string? staging = null;
        byte[]? zipBytes = null;
        try
        {
            BackupManifest manifest;
            List<PendingFile> pending;
            try
            {
                var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
                var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempDirectory));
                BackupFormat.CheckNoLinks(data);
                BackupFormat.CheckNoLinks(temp);
                if (File.Exists(data) || File.Exists(temp)) throw BackupFormat.Invalid();
                // Staging must never create anything in the data tree, even on a rejected archive.
                if (temp.Equals(data, StringComparison.OrdinalIgnoreCase)
                    || temp.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw BackupFormat.Invalid();
                zipBytes = Decrypt(backupPath, password);
                CheckZipDirectory(zipBytes);
                staging = BackupFormat.Under(temp, "yrk-backup-" + Guid.NewGuid().ToString("N"));
                CreatePrivateStaging(staging);
                using (var memory = new MemoryStream(zipBytes, writable: false))
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
                    manifest = Extract(zip, staging);
                CryptographicOperations.ZeroMemory(zipBytes);
                zipBytes = null;
                // Includes DPAPI conversion, JSON merging and every destination check. No data writes yet.
                pending = Prepare(manifest, staging, data, replaceSettings);
            }
            catch (Exception ex) when (BackupFormat.IsInputError(ex))
            {
                throw BackupFormat.Invalid(); // No inner exception: do not reveal authentication vs archive failures.
            }
            try
            {
                var imported = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in pending)
                {
                    var bytes = BackupFormat.ReadBounded(file.StagedPath, BackupFormat.MaxFileBytes);
                    try { BackupFormat.AtomicWrite(file.TargetPath, bytes, file.Overwrite); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    imported.Add(file.RelativePath, file.TargetPath);
                }
                return new(manifest, imported);
            }
            catch (Exception ex) when (BackupFormat.IsInputError(ex)) { throw new IOException(Loc.T(BackupFormat.ImportError)); }
        }
        finally
        {
            if (zipBytes is not null) CryptographicOperations.ZeroMemory(zipBytes);
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void CreatePrivateStaging(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    private static void CheckZipDirectory(ReadOnlySpan<byte> zip)
    {
        // Bound metadata BEFORE ZipArchive materializes its entry collection. v1 never writes ZIP64,
        // split archives, self-extracting prefixes or archive comments; none are needed at these limits.
        const int endBytes = 22;
        if (zip.Length < endBytes) throw BackupFormat.Invalid();
        var end = zip[^endBytes..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50
            || BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != 0) throw BackupFormat.Invalid();
        var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        if (count > BackupFormat.MaxFileCount + 1 || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count) throw BackupFormat.Invalid();
        var length = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if ((ulong)offset + length != (ulong)(zip.Length - endBytes)) throw BackupFormat.Invalid();
        var directory = zip.Slice(checked((int)offset), checked((int)length));
        var actualCount = 0;
        while (!directory.IsEmpty)
        {
            if (++actualCount > count || directory.Length < 46 || BinaryPrimitives.ReadUInt32LittleEndian(directory) != 0x02014b50
                || BinaryPrimitives.ReadUInt16LittleEndian(directory[34..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(directory[20..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(directory[24..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(directory[42..]) >= offset) throw BackupFormat.Invalid();
            var recordBytes = 46 + BinaryPrimitives.ReadUInt16LittleEndian(directory[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(directory[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(directory[32..]);
            if (recordBytes > directory.Length) throw BackupFormat.Invalid();
            directory = directory[recordBytes..];
        }
        if (actualCount != count) throw BackupFormat.Invalid();
    }

    private static byte[] Decrypt(string path, ReadOnlySpan<char> password, bool checkLinks = false)
    {
        var encrypted = BackupFormat.ReadBounded(path, BackupFormat.MaxBackupFileBytes, checkLinks: checkLinks);
        byte[]? key = null;
        byte[]? plain = null;
        try
        {
            if (encrypted.Length < BackupFormat.HeaderBytes + BackupFormat.TagBytes) throw BackupFormat.Invalid();
            var header = encrypted.AsSpan(0, BackupFormat.HeaderBytes);
            key = BackupFormat.Key(password, header);
            plain = new byte[encrypted.Length - BackupFormat.HeaderBytes - BackupFormat.TagBytes];
            using var aes = new AesGcm(key, BackupFormat.TagBytes);
            aes.Decrypt(header.Slice(7 + BackupFormat.SaltBytes, BackupFormat.NonceBytes),
                encrypted.AsSpan(BackupFormat.HeaderBytes, plain.Length), encrypted.AsSpan(encrypted.Length - BackupFormat.TagBytes), plain, header);
            return plain;
        }
        catch
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            throw;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static BackupManifest Extract(ZipArchive zip, string? staging)
    {
        if (zip.Entries.Count > BackupFormat.MaxFileCount + 1) throw BackupFormat.Invalid();
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            BackupFormat.CheckPath(entry.FullName, manifest: true);
            // No directory entries, symlinks, devices or other Unix special files.
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType != 0 && unixType != 0x8000 || (entry.ExternalAttributes & (int)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !entries.TryAdd(entry.FullName, entry)) throw BackupFormat.Invalid();
        }
        if (!entries.Remove(BackupFormat.ManifestPath, out var manifestEntry)
            || manifestEntry.FullName != BackupFormat.ManifestPath || manifestEntry.Length > BackupFormat.MaxManifestBytes) throw BackupFormat.Invalid();
        long total = 0;
        BackupManifest manifest;
        using (var json = new MemoryStream())
        {
            try
            {
                using var source = manifestEntry.Open();
                var size = BackupFormat.CopyBounded(source, json, BackupFormat.MaxManifestBytes, ref total);
                if (size != manifestEntry.Length) throw BackupFormat.Invalid();
                manifest = JsonSerializer.Deserialize<BackupManifest>(json.GetBuffer().AsSpan(0, (int)json.Length), BackupFormat.Json) ?? throw BackupFormat.Invalid();
            }
            finally { BackupFormat.Clear(json); }
        }
        if (string.IsNullOrWhiteSpace(manifest.AppVersion) || manifest.AppVersion.Length > 128 || manifest.CreatedUtc == default
            || manifest.Files.Count != entries.Count || manifest.Files.Count > BackupFormat.MaxFileCount) throw BackupFormat.Invalid();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null) throw BackupFormat.Invalid();
            BackupFormat.CheckPath(file.Path);
            if (!listed.Add(file.Path) || !entries.TryGetValue(file.Path, out var entry) || entry.FullName != file.Path
                || file.Size < 0 || file.Size > BackupFormat.MaxFileBytes || file.Size != entry.Length
                || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit)) throw BackupFormat.Invalid();
            if (staging is null)
            {
                using var content = new MemoryStream();
                try
                {
                    using var source = entry.Open();
                    var size = BackupFormat.CopyBounded(source, content, BackupFormat.MaxFileBytes, ref total);
                    if (size != file.Size || !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(content.GetBuffer().AsSpan(0, (int)content.Length)), Convert.FromHexString(file.Sha256)))
                        throw BackupFormat.Invalid();
                }
                finally { BackupFormat.Clear(content); }
                continue;
            }
            var target = BackupFormat.Under(staging, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var source = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var actualSize = BackupFormat.CopyBounded(source, output, BackupFormat.MaxFileBytes, ref total);
                if (actualSize != file.Size) throw BackupFormat.Invalid();
            }
            using var staged = File.OpenRead(target);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(staged), Convert.FromHexString(file.Sha256))) throw BackupFormat.Invalid();
        }
        return manifest;
    }

    private sealed record PendingFile(string RelativePath, string StagedPath, string TargetPath, bool Overwrite);

    private List<PendingFile> Prepare(BackupManifest manifest, string staging, string data, bool replaceSettings)
    {
        var result = new List<PendingFile>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var staged = BackupFormat.Under(staging, file.Path);
            var target = BackupFormat.Under(data, file.Path);
            BackupFormat.CheckNoLinks(target);
            if (File.Exists(Path.GetDirectoryName(target))) throw BackupFormat.Invalid();
            var exists = File.Exists(target) || Directory.Exists(target);
            var settings = BackupFormat.IsSettings(file.Path);
            if (settings && exists && !replaceSettings) continue;
            if (Directory.Exists(target)) throw BackupFormat.Invalid();
            if (settings && exists && file.Path == "stamps.json") MergeStamps(staged, target);
            if (file.Path.StartsWith("TrustedCertificates/", StringComparison.Ordinal))
            {
                var bytes = BackupFormat.ReadBounded(staged, BackupFormat.MaxFileBytes);
                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificate(bytes);
                    if (certificate.HasPrivateKey || !certificate.RawData.AsSpan().SequenceEqual(bytes)
                        || !TrustedCertificateStore.Fingerprint(certificate).Equals(Path.GetFileNameWithoutExtension(target), StringComparison.OrdinalIgnoreCase)) throw BackupFormat.Invalid();
                    // Fingerprint names cannot be renamed: the real store would ignore them.
                    if (exists)
                    {
                        var existing = BackupFormat.ReadBounded(target, BackupFormat.MaxFileBytes);
                        try { if (!existing.AsSpan().SequenceEqual(bytes)) throw BackupFormat.Invalid(); }
                        finally { CryptographicOperations.ZeroMemory(existing); }
                        continue;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            if (!settings)
            {
                // A collision with another planned name matters even if neither file exists yet.
                var original = target;
                for (var n = 1; File.Exists(target) || Directory.Exists(target) || reserved.Contains(target); n++)
                {
                    var stem = Path.GetFileNameWithoutExtension(original);
                    // Re-importing an already imported signature still yields an ID SignatureStore can delete.
                    if (BackupFormat.IsSignature(file.Path)) stem = stem[..32];
                    var suffix = n == 1 ? " (นำเข้า)" : $" (นำเข้า) ({n})";
                    target = Path.Combine(Path.GetDirectoryName(original)!, stem + suffix + Path.GetExtension(original));
                    BackupFormat.CheckNoLinks(target);
                }
            }
            reserved.Add(target);
            if (BackupFormat.IsSignature(file.Path))
            {
                var portable = BackupFormat.ReadBounded(staged, BackupFormat.MaxFileBytes);
                byte[]? protectedBytes = null;
                try
                {
                    protectedBytes = _signatures.Protect(portable);
                    if (protectedBytes.Length > BackupFormat.MaxFileBytes) throw BackupFormat.Invalid();
                    BackupFormat.AtomicWrite(staged, protectedBytes, overwrite: true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(portable);
                    if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            result.Add(new(file.Path, staged, target, settings && exists && replaceSettings));
        }
        return result;
    }

    private static void MergeStamps(string staged, string existing)
    {
        // Use the actual StampSettings schema; retain unknown settings and the existing designs.
        var currentBytes = BackupFormat.ReadBounded(existing, BackupFormat.MaxFileBytes);
        var importedBytes = BackupFormat.ReadBounded(staged, BackupFormat.MaxFileBytes);
        try
        {
            var current = JsonNode.Parse(currentBytes)?.AsObject() ?? throw BackupFormat.Invalid();
            var imported = JsonNode.Parse(importedBytes)?.AsObject() ?? throw BackupFormat.Invalid();
            var stamps = current["MyStamps"]?.AsArray() ?? throw BackupFormat.Invalid();
            var incoming = imported["MyStamps"]?.AsArray() ?? throw BackupFormat.Invalid();
            var names = new HashSet<string>(stamps.Select(s => s?["Text"]?.GetValue<string>() ?? throw BackupFormat.Invalid()), StringComparer.OrdinalIgnoreCase);
            foreach (var stamp in incoming)
            {
                var copy = stamp?.DeepClone().AsObject() ?? throw BackupFormat.Invalid();
                var name = copy["Text"]?.GetValue<string>() ?? throw BackupFormat.Invalid();
                var chosen = name;
                for (var n = 1; !names.Add(chosen); n++) chosen = name + (n == 1 ? " (นำเข้า)" : $" (นำเข้า) ({n})");
                copy["Text"] = chosen;
                stamps.Add(copy);
            }
            imported["MyStamps"] = stamps.DeepClone();
            var merged = JsonSerializer.SerializeToUtf8Bytes(imported);
            try
            {
                if (merged.Length > BackupFormat.MaxFileBytes) throw BackupFormat.Invalid();
                BackupFormat.AtomicWrite(staged, merged, overwrite: true);
            }
            finally { CryptographicOperations.ZeroMemory(merged); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentBytes);
            CryptographicOperations.ZeroMemory(importedBytes);
        }
    }
}
