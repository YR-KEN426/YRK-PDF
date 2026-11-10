using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace Yrk.PdfStudio.App.Services;

public sealed record SavedSignature(string Id, SignatureArt Art)
{
    /// <summary>The signature as a transparent bitmap (lists, appearance of digital signatures).</summary>
    public BitmapSource Image => Art.Preview;
}

public interface ISignatureStore
{
    IReadOnlyList<SavedSignature> LoadAll();

    SavedSignature Save(SignatureArt art);

    void Delete(string id);
}

/// <summary>
/// Keeps signatures in %LOCALAPPDATA%\YRK\EasyPdf\Signatures, encrypted with Windows DPAPI (current user),
/// so other accounts on the same PC cannot read or reuse them.
/// </summary>
public sealed class SignatureStore : ISignatureStore
{
    private static volatile bool s_saveSuspended;
    internal static void SuspendSavingUntilRestart() => s_saveSuspended = true;
    private const string Extension = ".yrksig";
    private readonly Backup.IBackupSignatureProtection _protection;

    private readonly string _folder;
    private readonly Action<string> _log;

    public SignatureStore(string? folder = null, Action<string>? log = null)
        : this(folder, new Backup.DpapiBackupSignatureProtection(), log)
    {
    }

    internal SignatureStore(string? folder, Backup.IBackupSignatureProtection protection, Action<string>? log = null)
    {
        _protection = protection;
        _log = log ?? App.LogMessage;
        _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "Signatures");
    }

    public IReadOnlyList<SavedSignature> LoadAll()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        var signatures = new List<SavedSignature>();
        foreach (var file in new DirectoryInfo(_folder).GetFiles("*" + Extension).OrderByDescending(f => f.CreationTimeUtc))
        {
            try
            {
                var data = _protection.Unprotect(File.ReadAllBytes(file.FullName));
                try { signatures.Add(new SavedSignature(Path.GetFileNameWithoutExtension(file.Name), SignatureArt.Deserialize(data))); }
                finally { CryptographicOperations.ZeroMemory(data); }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or FormatException)
            {
                // Written by another user or damaged: skip it rather than failing the whole list.
                _log($"Skipped a damaged saved signature ({ex.GetType().Name}).");
            }
        }

        return signatures;
    }

    public SavedSignature Save(SignatureArt art)
    {
        if (s_saveSuspended) return new SavedSignature(Guid.NewGuid().ToString("N"), art);
        Directory.CreateDirectory(_folder);
        var id = Guid.NewGuid().ToString("N");
        var encrypted = _protection.Protect(art.Serialize());
        File.WriteAllBytes(Path.Combine(_folder, id + Extension), encrypted);
        return new SavedSignature(id, art);
    }

    public void Delete(string id)
    {
        if (s_saveSuspended) return;
        if (!Backup.BackupFormat.IsSignatureId(id))
        {
            throw new ArgumentException("Invalid signature id.", nameof(id));
        }

        File.Delete(Path.Combine(_folder, id + Extension));
    }
}
