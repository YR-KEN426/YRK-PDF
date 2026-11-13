using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Yrk.PdfStudio.Core.Security;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class UpdateSignerVerifyTests : IClassFixture<UpdateSignerBuild>
{
    private const string Id = "YRK.EasyPdf";
    private const string Version = "1.2.2";
    private readonly UpdateSignerBuild _build;

    public UpdateSignerVerifyTests(UpdateSignerBuild build) => _build = build;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_signatures_return_zero(bool content)
    {
        using var fixture = new SignedPackage();
        var result = await Verify(fixture, content);
        Assert.Equal(0, result.Code);
        Assert.Contains("File signature: PASS", result.Output);
        Assert.Contains($"Fingerprint: {UpdateSignature.Fingerprint(fixture.PublicPem)}", result.Output);
        if (content)
            Assert.Contains("Content signature: PASS", result.Output);
        else
            Assert.DoesNotContain("Content signature:", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_one_byte_returns_one(bool content)
    {
        using var fixture = new SignedPackage();
        // The entry is uncompressed: change exactly one payload byte while keeping a readable ZIP.
        var bytes = File.ReadAllBytes(fixture.Package);
        var offset = bytes.AsSpan().IndexOf("fake program payload"u8);
        Assert.True(offset >= 0);
        bytes[offset] ^= 1;
        File.WriteAllBytes(fixture.Package, bytes);
        var result = await Verify(fixture, content);
        Assert.Equal(1, result.Code);
        Assert.Contains("File signature: FAIL", result.Output);
        if (content)
            Assert.Contains("Content signature: FAIL", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_public_key_returns_one(bool content)
    {
        using var fixture = new SignedPackage();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.Public, other.ExportSubjectPublicKeyInfoPem());
        var result = await Verify(fixture, content);
        Assert.Equal(1, result.Code);
        Assert.Contains("File signature: FAIL", result.Output);
        if (content)
            Assert.Contains("Content signature: FAIL", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Invalid_content_signature_fails_even_when_file_signature_passes()
    {
        using var fixture = new SignedPackage();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.ContentSignature, UpdateSignature.SignContent(other, Id, Version, UpdateSignature.ContentDigest(fixture.Package)));
        var result = await Verify(fixture, content: true);
        Assert.Equal(1, result.Code);
        Assert.Contains("File signature: PASS", result.Output);
        Assert.Contains("Content signature: FAIL", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("sig")]
    public async Task Missing_required_option_returns_two(string missing)
    {
        using var fixture = new SignedPackage();
        var args = Arguments(fixture, content: false);
        var index = args.IndexOf("--" + missing);
        args.RemoveRange(index, 2);
        var result = await _build.Run(args, fixture.Folder);
        Assert.Equal(2, result.Code);
        Assert.Contains("Missing option:", result.Error);
    }

    [Theory]
    [InlineData("--public")]
    [InlineData("--content-sig")]
    public async Task Option_without_value_returns_two(string option)
    {
        using var fixture = new SignedPackage();
        var args = Arguments(fixture, content: false);
        args.Add(option);
        var result = await _build.Run(args, fixture.Folder);
        Assert.Equal(2, result.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_public_key_is_found_from_cwd_or_repository_ancestor(bool nested)
    {
        using var fixture = new SignedPackage();
        var assets = Path.Combine(fixture.Folder, "src", "Yrk.PdfStudio.App", "Assets");
        Directory.CreateDirectory(assets);
        File.Copy(fixture.Public, Path.Combine(assets, "update-public-key.pem"));
        var cwd = nested ? Directory.CreateDirectory(Path.Combine(fixture.Folder, "build", "UpdateSigner")).FullName : fixture.Folder;
        var args = Arguments(fixture, content: true);
        args.RemoveRange(args.IndexOf("--public"), 2);
        var result = await _build.Run(args, cwd);
        Assert.Equal(0, result.Code);
        Assert.Contains("Content signature: PASS", result.Output);
    }

    private Task<(int Code, string Output, string Error)> Verify(SignedPackage fixture, bool content) =>
        _build.Run(Arguments(fixture, content), fixture.Folder);

    private static List<string> Arguments(SignedPackage fixture, bool content)
    {
        var args = new List<string> { "verify", "--package", fixture.Package, "--id", Id, "--version", Version,
            "--sig", fixture.Signature, "--public", fixture.Public };
        if (content)
            args.AddRange(["--content-sig", fixture.ContentSignature]);
        return args;
    }

    private sealed class SignedPackage : IDisposable
    {
        public string Folder { get; } = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "verify-" + Guid.NewGuid().ToString("N"))).FullName;
        public string Package => Path.Combine(Folder, $"{Id}-{Version}-full.nupkg");
        public string Signature => Path.Combine(Folder, "file.sig");
        public string ContentSignature => Path.Combine(Folder, "content.sig");
        public string Public => Path.Combine(Folder, "public.pem");
        public string PublicPem { get; }

        public SignedPackage()
        {
            using (var zip = ZipFile.Open(Package, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("lib/app/program.dll", CompressionLevel.NoCompression).Open())
                stream.Write("fake program payload"u8);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            PublicPem = key.ExportSubjectPublicKeyInfoPem();
            File.WriteAllText(Public, PublicPem);
            File.WriteAllText(Signature, UpdateSignature.Sign(key, Id, Version, Path.GetFileName(Package), UpdateSignature.Sha256Hex(Package)));
            File.WriteAllText(ContentSignature, UpdateSignature.SignContent(key, Id, Version, UpdateSignature.ContentDigest(Package)));
        }

        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }
}

public sealed class UpdateSignerBuild : IAsyncLifetime
{
    private readonly string _output = Path.Combine(PrivateTempDirectory.Path, "update-signer");

    public async Task InitializeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build", "UpdateSigner", "UpdateSigner.csproj")))
            root = root.Parent;
        Assert.NotNull(root);
        var result = await Execute(["build", Path.Combine(root.FullName, "build", "UpdateSigner", "UpdateSigner.csproj"),
            "--output", _output, "--nologo"], root.FullName);
        Assert.True(result.Code == 0, result.Output + result.Error);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public Task<(int Code, string Output, string Error)> Run(IEnumerable<string> args, string cwd) =>
        Execute(new[] { Path.Combine(_output, "UpdateSigner.dll") }.Concat(args), cwd);

    private static async Task<(int Code, string Output, string Error)> Execute(IEnumerable<string> args, string cwd)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
