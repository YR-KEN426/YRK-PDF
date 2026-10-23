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
    private const string Extension = ".yrksig";
    // The app's former name; part of the encryption key, so it must not change.
    private static readonly byte[] Entropy = "YRK PDF Studio signature v1"u8.ToArray();

    private readonly string _folder;

    public SignatureStore(string? folder = null)
    {
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
                var data = ProtectedData.Unprotect(File.ReadAllBytes(file.FullName), Entropy, DataProtectionScope.CurrentUser);
                signatures.Add(new SavedSignature(Path.GetFileNameWithoutExtension(file.Name), SignatureArt.Deserialize(data)));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or NotSupportedException or FileFormatException or FormatException)
            {
                // Written by another user or damaged: skip it rather than failing the whole list.
            }
        }

        return signatures;
    }

    public SavedSignature Save(SignatureArt art)
    {
        Directory.CreateDirectory(_folder);
        var id = Guid.NewGuid().ToString("N");
        var encrypted = ProtectedData.Protect(art.Serialize(), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(_folder, id + Extension), encrypted);
        return new SavedSignature(id, art);
    }

    public void Delete(string id)
    {
        if (id.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw new ArgumentException("Invalid signature id.", nameof(id));
        }

        File.Delete(Path.Combine(_folder, id + Extension));
    }
}
