using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.App.Views;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class TrustedCertificateStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "trust-" + Guid.NewGuid().ToString("N"))).FullName;
    private string StoreFolder => Path.Combine(_folder, "store");
    private TrustedCertificateStore Store() => new(StoreFolder, File.Delete);
    public void Dispose() => Directory.Delete(_folder, true);

    private static X509Certificate2 Signer()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Trust test,O=Test organization,E=test@example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    }

    [Fact]
    public void Add_reload_delete_keeps_only_public_DER_and_never_changes_source()
    {
        using var signer = Signer();
        Assert.True(signer.HasPrivateKey);
        var details = TrustCertificateDialog.Describe(signer);
        Assert.Contains("test@example.test", details);
        Assert.Contains("Test organization", details);
        Assert.Contains(TrustedCertificateStore.Fingerprint(signer), details);
        var store = Store();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Add(signer);
        var path = Assert.Single(Directory.GetFiles(StoreFolder));
        Assert.Equal(TrustedCertificateStore.Fingerprint(signer) + ".cer", Path.GetFileName(path));
        Assert.Equal(signer.Export(X509ContentType.Cert), File.ReadAllBytes(path));
        using var loaded = Assert.Single(Store().LoadAll());
        Assert.False(loaded.HasPrivateKey);
        Assert.Equal(signer.RawData, loaded.RawData);
        store.Add(signer);
        Assert.Equal(1, changes);
        store.Delete(loaded);
        Assert.Empty(store.LoadAll());
        Assert.Equal(2, changes);
        Assert.True(signer.HasPrivateKey);
    }

    [Fact]
    public void Invalid_files_wrong_hash_PEM_and_disguised_PFX_are_skipped_on_load()
    {
        Directory.CreateDirectory(StoreFolder);
        using var signer = Signer();
        File.WriteAllText(Path.Combine(StoreFolder, "junk.cer"), "junk");
        File.WriteAllBytes(Path.Combine(StoreFolder, new string('0', 64) + ".cer"), signer.RawData);
        var hashPath = Path.Combine(StoreFolder, TrustedCertificateStore.Fingerprint(signer) + ".cer");
        File.WriteAllBytes(hashPath, signer.Export(X509ContentType.Pfx));
        Assert.Empty(Store().LoadAll());
        File.WriteAllText(hashPath, signer.ExportCertificatePem());
        Assert.Empty(Store().LoadAll());
        File.WriteAllBytes(hashPath, signer.RawData);
        using var loaded = Assert.Single(Store().LoadAll());
        Assert.False(loaded.HasPrivateKey);
    }

    [Theory]
    [InlineData(".cer", false)]
    [InlineData(".crt", true)]
    public void Imports_DER_and_PEM_without_changing_the_original(string extension, bool pem)
    {
        using var signer = Signer();
        var source = Path.Combine(_folder, "source" + extension);
        if (pem) File.WriteAllText(source, signer.ExportCertificatePem());
        else File.WriteAllBytes(source, signer.RawData);
        var original = File.ReadAllBytes(source);
        var store = Store();
        store.Import(source);
        using var loaded = Assert.Single(store.LoadAll());
        Assert.False(loaded.HasPrivateKey);
        store.Delete(loaded);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(".pfx")]
    [InlineData(".p12")]
    [InlineData(".txt")]
    public void Rejects_other_extensions_even_for_a_public_certificate(string extension)
    {
        using var signer = Signer();
        var path = Path.Combine(_folder, "source" + extension);
        File.WriteAllBytes(path, signer.RawData);
        Assert.Throws<CryptographicException>(() => Store().Import(path));
        Assert.False(Directory.Exists(StoreFolder));
    }

    [Fact]
    public void Rejects_PFX_renamed_to_cer_and_PEM_with_private_key()
    {
        using var signer = Signer();
        var path = Path.Combine(_folder, "source.cer");
        File.WriteAllBytes(path, signer.Export(X509ContentType.Pfx));
        Assert.Throws<CryptographicException>(() => Store().Import(path));
        using var key = signer.GetRSAPrivateKey();
        File.WriteAllText(path, signer.ExportCertificatePem() + key!.ExportPkcs8PrivateKeyPem());
        Assert.Throws<CryptographicException>(() => Store().Import(path));
    }

    [Fact]
    public void Add_does_not_overwrite_existing_file_and_delete_is_confined_to_store()
    {
        using var signer = Signer();
        Directory.CreateDirectory(StoreFolder);
        var name = TrustedCertificateStore.Fingerprint(signer) + ".cer";
        var outside = Path.Combine(_folder, name);
        var inside = Path.Combine(StoreFolder, name);
        File.WriteAllText(outside, "outside");
        File.WriteAllText(inside, "existing");
        Assert.Throws<IOException>(() => Store().Add(signer));
        Assert.Equal("existing", File.ReadAllText(inside));
        Store().Delete(signer);
        Assert.Equal("outside", File.ReadAllText(outside));
    }

    [Fact]
    public async Task App_sign_with_created_ID_trust_then_remove_refreshes_status()
    {
        await using var app = new AppTestHarness();
        var store = new TrustedCertificateStore(app.PathFor("trusted"), File.Delete);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainViewModel).GetField("_trustedCertificateStore", flags)!.SetValue(app.Model, store);
        var ids = new DigitalIdStore(app.PathFor("ids"), File.Delete);
        var id = ids.Create("App trust test", "test@example.test", "Test organization", "test-password");
        using var signer = ids.Open(id, "test-password");
        var signed = app.PathFor("signed.pdf");
        File.WriteAllBytes(signed, await PdfSigner.SignAsync(File.ReadAllBytes(app.CreatePdf()), new(signer)));
        Assert.True(await app.OpenAsync(signed));
        async Task Wait() => await ((Task)typeof(MainViewModel).GetField("_signatureLoadTask", flags)!.GetValue(app.Model)!).WaitAsync(TimeSpan.FromSeconds(30));
        await Wait();
        var unknown = Assert.Single(app.Model.Signatures);
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, unknown.Info.Status);
        Assert.True(unknown.CanTrustCertificate);
        Assert.False(new SignatureInfoViewModel(unknown.Info with { IsIntact = false }).CanTrustCertificate);
        Assert.False(new SignatureInfoViewModel(unknown.Info with { ModifiedAfterSigning = true }).CanTrustCertificate);
        app.Dialogs.Answer("ConfirmTrustCertificate", false, true);
        await app.Model.TrustCertificateCommand.ExecuteAsync(unknown);
        Assert.Empty(store.LoadAll());
        await app.Model.TrustCertificateCommand.ExecuteAsync(unknown);
        await Wait();
        var trusted = Assert.Single(app.Model.Signatures);
        Assert.Equal(PdfSignerIdentity.TrustedByUser, trusted.Info.Identity);
        Assert.Equal(PdfSignatureStatus.Valid, trusted.Info.Status);
        Assert.False(trusted.Info.RevocationChecked);
        Assert.False(trusted.CanTrustCertificate);
        Assert.Equal("Valid: you chose to trust this signer", trusted.StatusText);
        Assert.Contains(trusted.Details, s => s.Contains("only in YRK"));
        store.Delete(trusted.Info.Certificate!);
        await Wait();
        Assert.Equal(PdfSignatureStatus.IdentityUnknown, Assert.Single(app.Model.Signatures).Info.Status);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }
}
