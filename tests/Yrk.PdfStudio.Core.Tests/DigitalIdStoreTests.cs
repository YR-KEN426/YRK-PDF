using System.Security.Cryptography;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class DigitalIdStoreTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-ids-");

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public void Deletes_an_id_file_only_with_its_password()
    {
        var store = new DigitalIdStore(_folder.FullName, File.Delete);
        var id = store.Create("Test Signer", null, null, "secret");

        Assert.ThrowsAny<CryptographicException>(() => store.Delete(id, "wrong"));
        Assert.True(File.Exists(id.FilePath));

        store.Delete(id, "secret");
        Assert.False(File.Exists(id.FilePath));
    }

    [Fact]
    public void Backs_up_an_id_that_still_opens_with_its_password()
    {
        var store = new DigitalIdStore(_folder.FullName, File.Delete);
        var id = store.Create("Test Signer", null, null, "secret");
        var target = Path.Combine(_folder.FullName, "backup.bin");

        Assert.ThrowsAny<CryptographicException>(() => store.BackUp(id, "wrong", target));
        store.BackUp(id, "secret", target);

        using var copy = store.Open(id with { FilePath = target }, "secret");
        Assert.True(copy.HasPrivateKey);
    }

    [Fact]
    public void Leaves_windows_certificates_alone()
    {
        var store = new DigitalIdStore(_folder.FullName, File.Delete);
        var windows = new DigitalId("Windows", "Issuer", DateTime.MaxValue, "ABC", null);

        Assert.Throws<InvalidOperationException>(() => store.Delete(windows, "secret"));
    }
}
