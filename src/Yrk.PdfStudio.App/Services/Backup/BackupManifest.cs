using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yrk.PdfStudio.App.Services.Backup;

public sealed record BackupFile(string Path, long Size, string Sha256);

public sealed record BackupManifest(string AppVersion, DateTimeOffset CreatedUtc, IReadOnlyList<BackupFile> Files);

/// <summary>Owns the returned buffers; callers clear them after use. Tests can substitute a portable protector.</summary>
public interface IBackupSignatureProtection
{
    byte[] Unprotect(byte[] stored);
    byte[] Protect(byte[] portable);
}

/// <summary>Matches SignatureStore's existing on-disk format, including its legacy entropy.</summary>
public sealed class DpapiBackupSignatureProtection : IBackupSignatureProtection
{
    private static readonly byte[] Entropy = "YRK PDF Studio signature v1"u8.ToArray();
    public byte[] Unprotect(byte[] stored) => ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Protect(byte[] portable) => ProtectedData.Protect(portable, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// v1: ASCII magic (7), salt (16), nonce (12), little-endian PBKDF2 rounds (4), ZIP ciphertext, GCM tag (16).
/// The entire 39-byte header is authenticated as associated data. All limits include manifest.json,
/// except MaxFileCount, which counts data files only. No password is converted to a string.
/// </summary>
public static class BackupFormat
{
    public const int Pbkdf2Iterations = 600_000;
    public const int MaxPbkdf2Iterations = 10_000_000;
    public const int SaltBytes = 16;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int HeaderBytes = 7 + SaltBytes + NonceBytes + sizeof(int);
    public const int MaxFileCount = 1_024;
    public const int MaxFileBytes = 16 * 1_024 * 1_024;
    public const int MaxTotalUncompressedBytes = 64 * 1_024 * 1_024;
    public const int MaxBackupFileBytes = 80 * 1_024 * 1_024;
    public const int MaxManifestBytes = 1 * 1_024 * 1_024;
    internal const string ManifestPath = "manifest.json";
    internal const string ReadError = "The backup could not be read. The password is incorrect or the file is damaged.";
    internal const string WriteError = "The backup could not be created.";
    internal const string ImportError = "The backup could not be imported.";
    internal static readonly string[] Settings = ["settings.json", "stamps.json", "etax.json"];
    internal static readonly string[] Folders = ["Signatures", "DigitalIDs", "TrustedCertificates"];
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
    };

    internal static InvalidDataException Invalid() => new(Loc.T(ReadError));
    internal static bool IsInputError(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException
        or CryptographicException or JsonException or ArgumentException or FormatException or NotSupportedException or OverflowException or InvalidOperationException;

    internal static byte[] Header()
    {
        var header = new byte[HeaderBytes];
        "YRKBAK1"u8.CopyTo(header);
        RandomNumberGenerator.Fill(header.AsSpan(7, SaltBytes + NonceBytes));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(HeaderBytes - sizeof(int)), Pbkdf2Iterations);
        return header;
    }

    internal static byte[] Key(ReadOnlySpan<char> password, ReadOnlySpan<byte> header)
    {
        if (!header[..7].SequenceEqual("YRKBAK1"u8)) throw Invalid();
        var rounds = BinaryPrimitives.ReadInt32LittleEndian(header[(HeaderBytes - sizeof(int))..]);
        if (rounds < Pbkdf2Iterations || rounds > MaxPbkdf2Iterations) throw Invalid();
        return Rfc2898DeriveBytes.Pbkdf2(password, header.Slice(7, SaltBytes), rounds, HashAlgorithmName.SHA256, 32);
    }

    internal static bool IsSignature(string path) => path.StartsWith("Signatures/", StringComparison.Ordinal);
    internal static bool IsSettings(string path) => Settings.Contains(path, StringComparer.Ordinal);

    // Only canonical '/' paths are accepted. Never normalize an unsafe ZIP name into a safe one.
    internal static void CheckPath(string path, bool manifest = false)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 220 || path.Contains('\\') || path.Contains(':')
            || path.StartsWith('/') || Path.IsPathRooted(path)) throw Invalid();
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || c is '<' or '>' or '"' or '|' or '?' or '*' or '\u007f')) throw Invalid();
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "123456789¹²³".Contains(stem[3]))) throw Invalid();
        }
        if (manifest && path == ManifestPath || IsSettings(path)) return;
        if (parts.Length != 2) throw Invalid();
        var extension = Path.GetExtension(parts[1]);
        if (parts[0] == "DigitalIDs" && (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))) return;
        if (parts[0] == "Signatures" && extension == ".yrksig" && IsSignatureId(Path.GetFileNameWithoutExtension(parts[1]))) return;
        if (parts[0] == "TrustedCertificates" && extension == ".cer"
            && Path.GetFileNameWithoutExtension(parts[1]) is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit)) return;
        throw Invalid();
    }

    // SignatureStore uses these IDs for deletion too; imported duplicates remain usable there.
    internal static bool IsSignatureId(string id)
    {
        if (id.Length < 32 || id.Length > 100 || !id.Take(32).All(char.IsAsciiHexDigit)) return false;
        var suffix = id[32..];
        if (suffix.Length == 0 || suffix == " (นำเข้า)") return true;
        return suffix.StartsWith(" (นำเข้า) (", StringComparison.Ordinal) && suffix.EndsWith(')')
            && int.TryParse(suffix.AsSpan(" (นำเข้า) (".Length, suffix.Length - " (นำเข้า) (".Length - 1), out var n) && n >= 2;
    }

    internal static string Under(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        return result;
    }

    internal static void CheckNoLinks(string path)
    {
        // Check all ancestors, including a dangling file symlink (File.Exists alone would miss it).
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    // checkLinks: false only for the backup file the user picked. OneDrive folders (Desktop, Documents)
    // are reparse points, and a link the user chose is no attack on the app's own data folder.
    internal static byte[] ReadBounded(string path, int maximum, bool checkLinks = true)
    {
        if (checkLinks) CheckNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw Invalid();
        var bytes = new byte[checked((int)input.Length)];
        try
        {
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw Invalid();
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    internal static long CopyBounded(Stream input, Stream output, int maximum, ref long total)
    {
        var buffer = new byte[64 * 1_024];
        long size = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                size += read;
                total += read;
                if (size > maximum || total > MaxTotalUncompressedBytes) throw Invalid();
                output.Write(buffer, 0, read);
            }
            return size;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static void Clear(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
    }

    internal static void AtomicWrite(string path, ReadOnlySpan<byte> bytes, bool overwrite = false, bool checkLinks = true)
    {
        if (checkLinks) CheckNoLinks(path);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".yrk-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (checkLinks) CheckNoLinks(path);
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
