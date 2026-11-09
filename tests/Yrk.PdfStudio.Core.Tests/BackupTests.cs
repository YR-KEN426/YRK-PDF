using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Services.Backup;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "backup-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private static ReadOnlySpan<char> Password => ['t', 'e', 's', 't', '-', 'ก', 'ข', 'ค'];
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");
    private string Temp => Path.Combine(_root, "staging");
    private string Archive => Path.Combine(_root, "backup.yrkbackup");
    private const string SignatureId = "0123456789abcdef0123456789abcdef";
    private const string SignaturePath = "Signatures/" + SignatureId + ".yrksig";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("portable signature test data กขค");

    // This portable test substitute never invokes this machine's DPAPI.
    private sealed class Protector(byte account) : IBackupSignatureProtection
    {
        public int ProtectedCount { get; private set; }
        public int UnprotectedCount { get; private set; }
        public bool FailProtect { get; init; }
        public byte[] Unprotect(byte[] stored)
        {
            UnprotectedCount++;
            if (stored.Length == 0 || stored[0] != account) throw new CryptographicException();
            return stored[1..];
        }
        public byte[] Protect(byte[] portable)
        {
            ProtectedCount++;
            if (FailProtect) throw new CryptographicException();
            return [account, .. portable];
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Put(string root, string name, byte[] bytes)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void Export(Protector? protector = null) => new BackupWriter(protector ?? new Protector(1)).Write(Archive, Source, Password, "1.2.0-test");
    private BackupImportResult Import(bool replace = false, Protector? protector = null) => new BackupReader(protector ?? new Protector(1)).Import(Archive, Target, Temp, Password, replace);

    private void AssertClean()
    {
        if (Directory.Exists(Target)) Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
        if (Directory.Exists(Temp)) Assert.Empty(Directory.EnumerateFileSystemEntries(Temp));
    }

    private InvalidDataException Reject(ReadOnlySpan<char> password)
    {
        // Both a nonexistent and an existing empty destination must remain untouched.
        Directory.CreateDirectory(Target);
        InvalidDataException error;
        try
        {
            new BackupReader(new Protector(1)).Import(Archive, Target, Temp, password);
            throw new Xunit.Sdk.XunitException("Expected an invalid backup to be rejected.");
        }
        catch (InvalidDataException ex) { error = ex; }
        Assert.Null(error.InnerException);
        Assert.Equal(Loc.T(BackupFormat.ReadError), error.Message);
        AssertClean();
        Directory.Delete(Target);
        Assert.Throws<InvalidDataException>(() => Import());
        Assert.False(Directory.Exists(Target));
        AssertClean();
        return error;
    }

    [Fact]
    public void Round_trip_preserves_all_allowlisted_bytes_and_excludes_history_and_cache()
    {
        Put(Source, "settings.json", "{\"Language\":\"th\"}"u8.ToArray());
        Put(Source, "stamps.json", "{\"MyStamps\":[],\"Last\":null}"u8.ToArray());
        Put(Source, "etax.json", "{\"Seller\":null}"u8.ToArray());
        Put(Source, SignaturePath, [1, .. Payload]);
        Put(Source, "DigitalIDs/test.pfx", [0, 1, 255, 18, 19]);
        Put(Source, "DigitalIDs/test.p12", [42, 0, 127]);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Backup test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certPath = "TrustedCertificates/" + TrustedCertificateStore.Fingerprint(certificate) + ".cer";
        Put(Source, certPath, certificate.RawData);
        var excluded = new[] { "recent.json", "reading-positions.json", "digital-sign.json", "Recovery/doc.pdf", "logs/errors.log",
            "tessdata/tha.traineddata", "Backups/old.yrkbackup", "DigitalIDs/cache.json", "unknown.json", "Signatures/cache.png" };
        foreach (var name in excluded) Put(Source, name, "never include this"u8.ToArray());
        var protector = new Protector(1);
        Export(protector);
        Assert.Equal(1, protector.UnprotectedCount);
        var exported = File.ReadAllBytes(Archive);
        Assert.Equal("YRKBAK1", Encoding.ASCII.GetString(exported, 0, 7));
        Assert.False(exported.AsSpan().IndexOf(Payload) >= 0);
        var result = Import(protector: protector);
        Assert.Equal("1.2.0-test", result.Manifest.AppVersion);
        Assert.NotEqual(default, result.Manifest.CreatedUtc);
        Assert.Equal(7, result.ImportedPaths.Count);
        Assert.Equal(1, protector.ProtectedCount);
        foreach (var file in result.Manifest.Files)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(Source, file.Path)), File.ReadAllBytes(Path.Combine(Target, file.Path)));
            var portable = file.Path == SignaturePath ? Payload : File.ReadAllBytes(Path.Combine(Source, file.Path));
            Assert.Equal(portable.Length, file.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(portable)), file.Sha256);
        }
        foreach (var name in excluded) Assert.False(File.Exists(Path.Combine(Target, name)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Temp));
        var second = Path.Combine(_root, "second.yrkbackup");
        new BackupWriter(protector).Write(second, Source, Password, "1.2.0-test");
        Assert.False(exported.AsSpan(7, 16).SequenceEqual(File.ReadAllBytes(second).AsSpan(7, 16)));
        Assert.False(exported.AsSpan(23, 12).SequenceEqual(File.ReadAllBytes(second).AsSpan(23, 12)));
    }

    [Fact]
    public void Signatures_are_reprotected_for_the_destination_account()
    {
        Put(Source, SignaturePath, [1, .. Payload]);
        Export(new Protector(1));
        Import(protector: new Protector(2));
        var stored = File.ReadAllBytes(Path.Combine(Target, SignaturePath));
        Assert.Equal(new byte[] { 2 }.Concat(Payload), stored);
        Assert.Equal(Payload, new Protector(2).Unprotect(stored));
    }

    [Fact]
    public void Wrong_password_and_every_modified_header_component_ciphertext_and_tag_share_one_error()
    {
        Put(Source, "DigitalIDs/id.pfx", Payload);
        Export();
        var original = File.ReadAllBytes(Archive);
        // Wrong-password check separately: Reject also retries with the normal password.
        Directory.CreateDirectory(Target);
        var wrong = Assert.Throws<InvalidDataException>(() => new BackupReader(new Protector(1)).Import(Archive, Target, Temp, ['w', 'r', 'o', 'n', 'g']));
        AssertClean();
        foreach (var offset in new[] { 0, 6, 7, 22, 23, 34, 35, 38, BackupFormat.HeaderBytes, original.Length - 17, original.Length - 1 })
        {
            var changed = original.ToArray();
            changed[offset] ^= 1;
            File.WriteAllBytes(Archive, changed);
            Assert.Equal(wrong.Message, Reject(Password).Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(34)]
    [InlineData(39)]
    [InlineData(54)]
    [InlineData(-1)]
    public void Truncated_files_are_rejected_without_writes(int length)
    {
        Put(Source, "etax.json", "{}"u8.ToArray());
        Export();
        var bytes = File.ReadAllBytes(Archive);
        File.WriteAllBytes(Archive, bytes[..(length < 0 ? bytes.Length - 1 : length)]);
        Reject(Password);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(599999)]
    [InlineData(10000001)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void Unsafe_PBKDF2_rounds_are_rejected_before_derivation(int rounds)
    {
        // Even a valid tag made with too few rounds must be rejected.
        var zip = MakeZip([("DigitalIDs/id.pfx", Payload)]);
        Encrypt(zip, rounds: rounds, deriveRounds: rounds is > 0 and < 600000 ? rounds : 1);
        Reject(Password);
    }

    [Theory]
    [InlineData("..\\escape.pfx")]
    [InlineData("../escape.pfx")]
    [InlineData("DigitalIDs/../escape.pfx")]
    [InlineData("/DigitalIDs/id.pfx")]
    [InlineData("C:/DigitalIDs/id.pfx")]
    [InlineData("C:escape.pfx")]
    [InlineData("\\\\server\\share\\id.pfx")]
    [InlineData("//server/share/id.pfx")]
    [InlineData("DigitalIDs\\id.pfx")]
    [InlineData("DigitalIDs//id.pfx")]
    [InlineData("DigitalIDs/./id.pfx")]
    [InlineData("DigitalIDs/id.pfx:stream")]
    [InlineData("DigitalIDs/NUL.pfx")]
    [InlineData("DigitalIDs/com1.pfx")]
    [InlineData("DigitalIDs/LPT².pfx")]
    [InlineData("DigitalIDs/CON .pfx")]
    [InlineData("DigitalIDs/id.pfx.")]
    [InlineData("DigitalIDs/id.pfx ")]
    [InlineData("DigitalIDs/id?.pfx")]
    [InlineData("DigitalIDs/folder/id.pfx")]
    [InlineData("DigitalIDs/")]
    [InlineData("recent.json")]
    [InlineData("reading-positions.json")]
    [InlineData("digital-sign.json")]
    [InlineData("Recovery/id.pfx")]
    [InlineData("Backups/id.pfx")]
    [InlineData("DigitalIDs/id.exe")]
    [InlineData("Signatures/unknown.yrksig")]
    [InlineData("TrustedCertificates/unknown.cer")]
    public void Hostile_or_non_allowlisted_names_are_rejected(string name)
    {
        Encrypt(MakeZip([(name, Payload)]));
        Reject(Password);
        Assert.False(File.Exists(Path.Combine(_root, "escape.pfx")));
    }

    [Theory]
    [InlineData("DigitalIDs/id.pfx", "DigitalIDs/id.pfx")]
    [InlineData("DigitalIDs/id.pfx", "DigitalIDs/ID.pfx")]
    [InlineData("manifest.json", "MANIFEST.JSON")]
    public void Duplicate_ZIP_names_are_rejected_case_insensitively(string first, string second)
    {
        Encrypt(MakeZip([(first, Payload), (second, Payload)]));
        Reject(Password);
    }

    [Fact]
    public void Missing_unlisted_and_duplicate_manifest_files_and_wrong_hash_or_size_are_rejected()
    {
        var valid = new BackupFile("DigitalIDs/id.pfx", Payload.Length, Convert.ToHexString(SHA256.HashData(Payload)));
        var cases = new[]
        {
            Array.Empty<BackupFile>(),
            new[] { valid with { Path = "DigitalIDs/missing.pfx" } },
            new[] { valid, valid },
            new[] { valid with { Sha256 = new string('0', 64) } },
            new[] { valid with { Sha256 = "bad" } },
            new[] { valid with { Size = -1 } },
            new[] { valid with { Size = Payload.Length - 1 } },
        };
        foreach (var files in cases)
        {
            Encrypt(MakeZip([("DigitalIDs/id.pfx", Payload)], files));
            Reject(Password);
        }
        Encrypt(MakeZip([("DigitalIDs/id.pfx", Payload), ("DigitalIDs/extra.pfx", Payload)], [valid]));
        Reject(Password);
    }

    [Fact]
    public void Late_hash_failure_or_protection_failure_never_writes_an_earlier_valid_file()
    {
        var files = new[] { ("DigitalIDs/a.pfx", Payload), (SignaturePath, Payload) };
        var manifest = files.Select(f => new BackupFile(f.Item1, f.Item2.Length, Convert.ToHexString(SHA256.HashData(f.Item2)))).ToArray();
        manifest[1] = manifest[1] with { Sha256 = new string('0', 64) };
        Encrypt(MakeZip(files, manifest));
        Reject(Password);
        Encrypt(MakeZip(files));
        Assert.Throws<InvalidDataException>(() => Import(protector: new Protector(1) { FailProtect = true }));
        AssertClean();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Oversized_file_is_rejected_even_when_ZIP_declares_one_byte(bool lie)
    {
        var zip = MakeZip([("DigitalIDs/huge.pfx", new byte[BackupFormat.MaxFileBytes + 1])], declaredSize: lie ? 1 : null);
        if (lie) PatchDeclaredSizes(zip, 1);
        Encrypt(zip);
        Reject(Password);
    }

    [Fact]
    public void Total_uncompressed_size_file_count_manifest_size_and_encrypted_file_size_are_bounded()
    {
        var big = new byte[BackupFormat.MaxFileBytes];
        Encrypt(MakeZip(Enumerable.Range(0, 4).Select(i => ($"DigitalIDs/{i}.pfx", big)).ToArray()));
        Reject(Password); // Exactly 64 MiB data plus the manifest exceeds the total limit.
        Encrypt(MakeZip(Enumerable.Range(0, BackupFormat.MaxFileCount + 1).Select(i => ($"DigitalIDs/{i}.pfx", Array.Empty<byte>())).ToArray()));
        Reject(Password);
        var dishonestCount = MakeZip(Enumerable.Range(0, BackupFormat.MaxFileCount + 1).Select(i => ($"DigitalIDs/{i}.pfx", Array.Empty<byte>())).ToArray());
        BinaryPrimitives.WriteUInt16LittleEndian(dishonestCount.AsSpan(dishonestCount.Length - 22 + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dishonestCount.AsSpan(dishonestCount.Length - 22 + 10), 1);
        Encrypt(dishonestCount);
        Reject(Password);
        Encrypt(RawZip([(BackupFormat.ManifestPath, new byte[BackupFormat.MaxManifestBytes + 1])]));
        Reject(Password);
        using (var oversized = File.Create(Archive)) oversized.SetLength(BackupFormat.MaxBackupFileBytes + 1L);
        Reject(Password);
    }

    [Fact]
    public void Malformed_or_missing_manifest_and_non_zip_payloads_share_the_same_error()
    {
        foreach (var plain in new[]
        {
            Array.Empty<byte>(), Payload,
            RawZip([("DigitalIDs/id.pfx", Payload)]),
            RawZip([("manifest.json", "null"u8.ToArray())]),
            RawZip([("manifest.json", "{}"u8.ToArray())]),
            RawZip([("manifest.json", "{\"appVersion\":\"x\",\"createdUtc\":\"2026-11-10T00:00:00Z\",\"files\":[null]}"u8.ToArray())]),
        })
        {
            Encrypt(plain);
            Reject(Password);
        }
    }

    [Fact]
    public void Duplicate_imports_keep_original_bytes_and_suffix_both_IDs_and_signatures()
    {
        Put(Source, "DigitalIDs/id.pfx", Payload);
        Put(Source, SignaturePath, [1, .. Payload]);
        Export();
        Import();
        Import();
        Import();
        foreach (var relative in new[] { "DigitalIDs/id.pfx", SignaturePath })
        {
            var folder = Path.Combine(Target, Path.GetDirectoryName(relative)!);
            var stem = Path.GetFileNameWithoutExtension(relative);
            var extension = Path.GetExtension(relative);
            var original = File.ReadAllBytes(Path.Combine(Target, relative));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(folder, stem + " (นำเข้า)" + extension)));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(folder, stem + " (นำเข้า) (2)" + extension)));
        }
        // The app must still be able to delete an imported signature with its new ID.
        new SignatureStore(Path.Combine(Target, "Signatures")).Delete(SignatureId + " (นำเข้า)");
        Assert.False(File.Exists(Path.Combine(Target, "Signatures", SignatureId + " (นำเข้า).yrksig")));
    }

    [Fact]
    public void Settings_require_flag_to_replace_and_stamp_designs_merge_with_import_suffix()
    {
        var stamp = "{\"MyStamps\":[{\"Text\":\"Approved\",\"Detail\":null,\"Ink\":\"Red\",\"Shape\":\"Box\",\"Date\":\"None\"}],\"Last\":null,\"Language\":\"English\"}"u8.ToArray();
        Put(Source, "settings.json", "{\"Language\":\"en\"}"u8.ToArray());
        Put(Source, "stamps.json", stamp);
        Put(Source, "etax.json", "{\"VatRate\":7}"u8.ToArray());
        Export();
        Put(Target, "settings.json", "{\"Language\":\"th\"}"u8.ToArray());
        Put(Target, "stamps.json", stamp);
        Put(Target, "etax.json", "{\"VatRate\":0}"u8.ToArray());
        var before = Directory.GetFiles(Target).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
        Assert.Empty(Import().ImportedPaths);
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Target, pair.Key!)));
        Import(replace: true);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "settings.json")), File.ReadAllBytes(Path.Combine(Target, "settings.json")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "etax.json")), File.ReadAllBytes(Path.Combine(Target, "etax.json")));
        var merged = StampSettings.Load(Path.Combine(Target, "stamps.json"));
        Assert.Equal(new[] { "Approved", "Approved (นำเข้า)" }, merged.MyStamps.Select(s => s.Text));
        Import(replace: true);
        Assert.Equal(new[] { "Approved", "Approved (นำเข้า)", "Approved (นำเข้า) (2)" }, StampSettings.Load(Path.Combine(Target, "stamps.json")).MyStamps.Select(s => s.Text));
    }

    [Fact]
    public void Destination_obstructions_and_unsafe_staging_are_checked_before_any_data_write()
    {
        Encrypt(MakeZip([("settings.json", "{}"u8.ToArray()), ("DigitalIDs/id.pfx", Payload)]));
        Directory.CreateDirectory(Target);
        Put(Target, "DigitalIDs", Payload);
        Assert.Throws<InvalidDataException>(() => Import());
        Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(Target, "DigitalIDs")));
        Assert.False(File.Exists(Path.Combine(Target, "settings.json")));
        File.Delete(Path.Combine(Target, "DigitalIDs"));
        Assert.Throws<InvalidDataException>(() => new BackupReader(new Protector(1)).Import(Archive, Target, Path.Combine(Target, "temp"), Password));
        AssertClean();
    }

    [Fact]
    public void Export_never_overwrites_and_rejects_oversized_source_without_leftover_temp_files()
    {
        Put(Source, "DigitalIDs/id.pfx", Payload);
        Export();
        var original = File.ReadAllBytes(Archive);
        Assert.Throws<IOException>(() => Export());
        Assert.Equal(original, File.ReadAllBytes(Archive));
        File.Delete(Archive);
        using (var huge = File.Create(Path.Combine(Source, "DigitalIDs/id.pfx"))) huge.SetLength(BackupFormat.MaxFileBytes + 1L);
        Assert.Throws<IOException>(() => Export());
        Assert.False(File.Exists(Archive));
        Assert.Empty(Directory.EnumerateFiles(_root, ".yrk-backup-*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("temp")]
    public void Junctions_are_rejected_without_following_their_targets(string location)
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Put(outside, "keep.pfx", Payload);
        var junction = location switch { "source" => Path.Combine(Source, "DigitalIDs"), "target" => Path.Combine(Target, "DigitalIDs"), _ => Temp };
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        try
        {
            if (location == "source") Assert.Throws<IOException>(() => Export());
            else
            {
                Encrypt(MakeZip([("settings.json", "{}"u8.ToArray()), ("DigitalIDs/id.pfx", Payload)]));
                Assert.Throws<InvalidDataException>(() => Import());
                Assert.False(File.Exists(Path.Combine(Target, "settings.json")));
            }
            Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(outside, "keep.pfx")));
            Assert.Single(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(junction); } // Remove the junction itself, never its target.
        AssertClean();
    }

    [Fact]
    public void Backup_file_in_a_reparse_folder_chosen_by_the_user_works()
    {
        // OneDrive's Desktop and Documents are reparse points; the user's own backup location must not be refused.
        var real = Directory.CreateDirectory(Path.Combine(_root, "cloud-real")).FullName;
        var linked = Path.Combine(_root, "cloud-link");
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linked}\" \"{real}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        try
        {
            Put(Source, "settings.json", "{\"a\":1}"u8.ToArray());
            var archive = Path.Combine(linked, "backup.yrkbackup");
            new BackupWriter(new Protector(1)).Write(archive, Source, Password, "1.2.0-test");
            new BackupReader(new Protector(1)).Import(archive, Target, Temp, Password, true);
            Assert.Equal("{\"a\":1}"u8.ToArray(), File.ReadAllBytes(Path.Combine(Target, "settings.json")));
        }
        finally { Directory.Delete(linked); } // Remove the junction itself, never its target.
    }

    [Fact]
    public void ZIP_symlinks_are_rejected_before_data_is_written()
    {
        var bytes = MakeZip([("DigitalIDs/id.pfx", Payload)]);
        for (var i = 0; i + 46 < bytes.Length; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50u)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i + 38), unchecked((int)0xa1ff0000));
                break;
            }
        Encrypt(bytes);
        Reject(Password);
    }

    [Fact]
    public void Trusted_certificate_duplicates_are_deduplicated_and_conflicts_reject_the_entire_import()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Backup trust test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = "TrustedCertificates/" + TrustedCertificateStore.Fingerprint(certificate) + ".cer";
        Encrypt(MakeZip([("DigitalIDs/id.pfx", Payload), (path, certificate.RawData)]));
        Import();
        Assert.Single(Import().ImportedPaths); // Only the additional Digital ID is added.
        Assert.Single(Directory.GetFiles(Path.Combine(Target, "TrustedCertificates")));
        Directory.Delete(Path.Combine(Target, "DigitalIDs"), true);
        Put(Target, path, Payload);
        Assert.Throws<InvalidDataException>(() => Import());
        Assert.False(Directory.Exists(Path.Combine(Target, "DigitalIDs")));
        Assert.Equal(Payload, File.ReadAllBytes(Path.Combine(Target, path)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Temp));
        // A certificate whose content does not match its fingerprint name is rejected on an empty target too.
        Directory.Delete(Target, true);
        Encrypt(MakeZip([("TrustedCertificates/" + new string('0', 64) + ".cer", certificate.RawData)]));
        Reject(Password);
    }

    [Fact]
    public void Maximum_file_count_is_supported_and_export_checks_the_same_count_limit()
    {
        var files = Enumerable.Range(0, BackupFormat.MaxFileCount).Select(i => ($"DigitalIDs/{i}.pfx", Array.Empty<byte>())).ToArray();
        Encrypt(MakeZip(files));
        Assert.Equal(BackupFormat.MaxFileCount, Import().ImportedPaths.Count);
        foreach (var file in files) Put(Source, file.Item1, file.Item2);
        Put(Source, "DigitalIDs/extra.pfx", []);
        File.Delete(Archive);
        Assert.Throws<IOException>(() => Export());
        Assert.False(File.Exists(Archive));
        Assert.Empty(Directory.EnumerateFiles(_root, ".yrk-backup-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Planned_collision_names_and_reimported_signature_names_remain_unique()
    {
        Encrypt(MakeZip([("DigitalIDs/id.pfx", Payload), ("DigitalIDs/id (นำเข้า).pfx", [9]),
            (SignaturePath, Payload), ("Signatures/" + SignatureId + " (นำเข้า).yrksig", [8])]));
        Put(Target, "DigitalIDs/id.pfx", [7]);
        Put(Target, SignaturePath, [1, 6]);
        var result = Import();
        Assert.Equal(4, result.ImportedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(Target, "DigitalIDs/id.pfx")));
        Assert.Equal(new byte[] { 1, 6 }, File.ReadAllBytes(Path.Combine(Target, SignaturePath)));
        Assert.Equal(Payload, File.ReadAllBytes(result.ImportedPaths["DigitalIDs/id.pfx"]));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(result.ImportedPaths["DigitalIDs/id (นำเข้า).pfx"]));
        foreach (var name in Directory.EnumerateFiles(Path.Combine(Target, "Signatures")))
            Assert.True(BackupFormat.IsSignatureId(Path.GetFileNameWithoutExtension(name)));
    }

    private static byte[] MakeZip((string Name, byte[] Data)[] files, BackupFile[]? manifestFiles = null, long? declaredSize = null)
    {
        var manifest = new BackupManifest("test", DateTimeOffset.UtcNow, manifestFiles ?? files.Select(f =>
            new BackupFile(f.Name, declaredSize ?? f.Data.Length, Convert.ToHexString(SHA256.HashData(f.Data)))).ToArray());
        return RawZip([.. files, ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, BackupFormat.Json))]);
    }

    private static byte[] RawZip((string Name, byte[] Data)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal).Open();
                entry.Write(file.Data);
            }
        return memory.ToArray();
    }

    private void Encrypt(byte[] zip, int rounds = BackupFormat.Pbkdf2Iterations, int? deriveRounds = null)
    {
        // Construct adversarial archives independently of BackupWriter's source allowlist.
        var header = new byte[39];
        "YRKBAK1"u8.CopyTo(header);
        RandomNumberGenerator.Fill(header.AsSpan(7, 28));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(35), rounds);
        var key = Rfc2898DeriveBytes.Pbkdf2(Password, header.AsSpan(7, 16), deriveRounds ?? rounds, HashAlgorithmName.SHA256, 32);
        try
        {
            var bytes = new byte[39 + zip.Length + 16];
            header.CopyTo(bytes, 0);
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(header.AsSpan(23, 12), zip, bytes.AsSpan(39, zip.Length), bytes.AsSpan(39 + zip.Length, 16), header);
            File.WriteAllBytes(Archive, bytes);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void PatchDeclaredSizes(byte[] zip, uint size)
    {
        // Patch only the data file's local and central uncompressed sizes; keep its compressed stream intact.
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(22), size);
        for (var i = 0; i + 46 < zip.Length; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x02014b50u)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 24), size);
                return;
            }
        throw new Xunit.Sdk.XunitException("Missing central directory.");
    }
}
