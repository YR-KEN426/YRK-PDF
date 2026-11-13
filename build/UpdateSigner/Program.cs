// YRK update signer.
//
//   new-key   Creates the signing key: the private key (encrypted with a password you choose) and the public
//             key that is built into the app. Run once; keep the private key file and its password safe.
//   sign      Signs a release package; checks first that the key matches the app's public key. Writes the
//             file signature (--out) and, with --content-out, the content signature that also matches a
//             package the updater rebuilt from a delta (see UpdateSignature).
//   verify    Checks the file signature and optional content signature using only a public key, and
//             prints its fingerprint. Defaults to the app's public key in the repository.
//
// Only new-key and sign ask for a password, or read it from YRK_UPDATE_KEY_PASSWORD.
using System.Security.Cryptography;
using System.Text;
using Yrk.PdfStudio.Core.Security;

return Run(args);

static int Run(string[] args)
{
    try
    {
        var options = Options(args.Skip(1).ToArray());
        switch (args.FirstOrDefault())
        {
            case "new-key":
                return NewKey(options["private"], options["public"]);
            case "sign":
                return Sign(options["private"], options["public"], options["package"], options["id"], options["version"], options["out"], options.GetValueOrDefault("content-out"));
            case "verify":
                return Verify(options["package"], options["id"], options["version"], options["sig"], options.GetValueOrDefault("content-sig"), options.GetValueOrDefault("public"));
            default:
                Console.Error.WriteLine("Usage: new-key --private <file> --public <file>");
                Console.Error.WriteLine("       sign --private <file> --public <file> --package <nupkg> --id <id> --version <x.y.z> --out <sig> [--content-out <sig>]");
                Console.Error.WriteLine("       verify --package <nupkg> --id <id> --version <x.y.z> --sig <file> [--content-sig <file>] [--public <pem>]");
                Console.Error.WriteLine("       verify defaults --public to src/Yrk.PdfStudio.App/Assets/update-public-key.pem (repository root or cwd).");
                return 2;
        }
    }
    catch (KeyNotFoundException ex)
    {
        Console.Error.WriteLine("Missing option: " + ex.Message);
        return 2;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or ArgumentException or InvalidDataException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static int Verify(string package, string id, string version, string signaturePath, string? contentSignaturePath, string? publicPath)
{
    var publicPem = File.ReadAllText(publicPath ?? DefaultPublicKeyPath());
    Console.WriteLine($"Fingerprint: {UpdateSignature.Fingerprint(publicPem)}");
    var sha = UpdateSignature.Sha256Hex(package);
    var valid = UpdateSignature.Verify(publicPem, id, version, Path.GetFileName(package), sha, File.ReadAllText(signaturePath));
    Console.WriteLine($"File signature: {(valid ? "PASS" : "FAIL")} ({sha})");

    if (contentSignaturePath is not null)
    {
        var digest = UpdateSignature.ContentDigest(package);
        var contentValid = UpdateSignature.VerifyContent(publicPem, id, version, digest, File.ReadAllText(contentSignaturePath));
        Console.WriteLine($"Content signature: {(contentValid ? "PASS" : "FAIL")} ({digest})");
        valid &= contentValid;
    }

    return valid ? 0 : 1;
}

static string DefaultPublicKeyPath()
{
    var relativePath = Path.Combine("src", "Yrk.PdfStudio.App", "Assets", "update-public-key.pem");
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(candidate))
            return candidate;
    }

    return relativePath;
}

static int NewKey(string privatePath, string publicPath)
{
    if (File.Exists(privatePath))
    {
        // Replacing the key would stop every installed copy from accepting updates.
        throw new InvalidOperationException($"{privatePath} already exists. It is not replaced: copies of the app trust that key.");
    }

    var password = AskPassword(confirm: true);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var encrypted = key.ExportEncryptedPkcs8PrivateKeyPem(
        password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePath))!);
    File.WriteAllText(privatePath, encrypted);
    var publicPem = key.ExportSubjectPublicKeyInfoPem();
    File.WriteAllText(publicPath, publicPem + "\n");

    Console.WriteLine($"Private key (keep it, back it up): {Path.GetFullPath(privatePath)}");
    Console.WriteLine($"Public key (built into the app):   {Path.GetFullPath(publicPath)}");
    Console.WriteLine($"Fingerprint: {UpdateSignature.Fingerprint(publicPem)}");
    return 0;
}

static int Sign(string privatePath, string publicPath, string package, string id, string version, string output, string? contentOutput)
{
    var publicPem = File.ReadAllText(publicPath);
    using var key = ECDsa.Create();
    key.ImportFromEncryptedPem(File.ReadAllText(privatePath), AskPassword(confirm: false));

    var fileName = Path.GetFileName(package);
    var sha = UpdateSignature.Sha256Hex(package);
    var signature = UpdateSignature.Sign(key, id, version, fileName, sha);
    if (!UpdateSignature.Verify(publicPem, id, version, fileName, sha, signature))
    {
        throw new InvalidOperationException("This private key does not match the public key built into the app. Nothing was signed.");
    }

    File.WriteAllText(output, signature);
    Console.WriteLine($"Signed {fileName} ({sha}) -> {Path.GetFileName(output)}");

    if (contentOutput is not null)
    {
        var digest = UpdateSignature.ContentDigest(package);
        var contentSignature = UpdateSignature.SignContent(key, id, version, digest);
        if (!UpdateSignature.VerifyContent(publicPem, id, version, digest, contentSignature))
        {
            throw new InvalidOperationException("The content signature does not verify. Nothing more was signed.");
        }

        File.WriteAllText(contentOutput, contentSignature);
        Console.WriteLine($"Signed the program files of {fileName} ({digest}) -> {Path.GetFileName(contentOutput)}");
    }

    return 0;
}

static string AskPassword(bool confirm)
{
    if (Environment.GetEnvironmentVariable("YRK_UPDATE_KEY_PASSWORD") is { Length: > 0 } fromEnvironment)
    {
        return fromEnvironment;
    }

    while (true)
    {
        var password = ReadHidden("Update key password: ");
        if (!confirm)
        {
            return password;
        }

        if (password.Length < 12)
        {
            Console.WriteLine("Use at least 12 characters.");
            continue;
        }

        if (ReadHidden("Type it again: ") == password)
        {
            return password;
        }

        Console.WriteLine("The two passwords are different. Try again.");
    }
}

static string ReadHidden(string prompt)
{
    Console.Write(prompt);
    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return text.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
            {
                text.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            text.Append(key.KeyChar);
        }
    }
}

static Dictionary<string, string> Options(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1]))
            throw new KeyNotFoundException(args[i]);
        options[args[i].TrimStart('-')] = args[i + 1];
    }

    return options;
}
