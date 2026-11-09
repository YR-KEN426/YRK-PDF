using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Signatures;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Uri? tsa = null;
        if (args.Length != 0 && !(args.Length == 1 && args[0] == "--offline"))
        {
            if (args.Length != 2 || args[0] != "--timestamp"
                || !Uri.TryCreate(args[1], UriKind.Absolute, out tsa) || tsa.Scheme is not ("http" or "https"))
            {
                Console.Error.WriteLine("Usage: LtvTestSigner [--offline | --timestamp <http(s) TSA URL>]");
                return 2;
            }
        }
        var folder = Directory.CreateTempSubdirectory("yrk-ltv-test-");
        try
        {
            using var rootKey = RSA.Create(2048);
            using var leafKey = RSA.Create(2048);
            var now = DateTimeOffset.UtcNow;
            using var root = Request("Temporary YRK test CA " + Guid.NewGuid(), rootKey, true)
                .CreateSelfSigned(now.AddDays(-7), now.AddDays(7));
            using var publicLeaf = Request("Temporary LTV test signer", leafKey, false)
                .Create(root, now.AddDays(-2), now.AddDays(2), RandomNumberGenerator.GetBytes(16));
            using var signer = publicLeaf.CopyWithPrivateKey(leafKey);
            var crl = new CertificateRevocationListBuilder().Build(root, 1, now.AddDays(1),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, now.AddDays(-1));
            var source = new TestRevocationSource(crl, tsa is null ? null : new HttpRevocationSource());
            var input = CreatePdf(root.RawData);
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "sample.pdf"), input);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var signed = await PdfSigner.SignAsync(input, new PdfSignRequest(signer,
                Reason: "Temporary automated LTV check",
                TimestampAuthority: tsa is null ? null : new HttpTimestampAuthority(tsa),
                RevocationSource: source), timeout.Token);
            var signedPath = Path.Combine(folder.FullName, "signed.pdf");
            await File.WriteAllBytesAsync(signedPath, signed);

            // Same isolated trust mechanism as SignatureLtvTests; never install a root in Windows.
            var options = new PdfVerifyOptions(CheckRevocation: false);
            var trust = typeof(PdfVerifyOptions).GetProperty("CustomTrustRoots", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Core no longer exposes the isolated test trust option.");
            trust.SetValue(options, new X509Certificate2Collection(root));
            var results = PdfSignatureVerifier.Verify(await File.ReadAllBytesAsync(signedPath), options);
            if (results.Count != 1) throw new InvalidOperationException($"Expected one signature, found {results.Count}.");
            var result = results[0];
            Console.WriteLine($"Mode: {(tsa is null ? "offline (no network)" : "real timestamp: " + tsa)}");
            Console.WriteLine($"Status={result.Status}; Identity={result.Identity}; Intact={result.IsIntact}; " +
                $"WholeDocument={result.CoversWholeDocument}; RevocationChecked={result.RevocationChecked}; Timestamp={result.IsTimestamped}");
            if (!result.IsIntact || !result.CoversWholeDocument || !result.RevocationChecked
                || result.Status != PdfSignatureStatus.Valid || result.Identity != PdfSignerIdentity.Trusted
                || source.TestCalls != 1 || (tsa is not null && !result.IsTimestamped))
                throw new InvalidOperationException("LTV verification failed: " + result.Problem);

            // Negative control: embedding a test CA must not grant it system trust.
            var untrusted = PdfSignatureVerifier.Verify(signed, new PdfVerifyOptions(CheckRevocation: false)).Single();
            if (untrusted.Identity != PdfSignerIdentity.Untrusted || !untrusted.RevocationChecked)
                throw new InvalidOperationException("Unexpected result without the isolated trust root.");
            Console.WriteLine("PASS: Core verified the signature and embedded CRL; test CA trust remained local to this verification.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.GetBaseException().Message);
            return 1;
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static CertificateRequest Request(string name, RSA key, bool ca)
    {
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca
            ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (!ca) request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(
            [TestRevocationSource.TestUrl]));
        return request;
    }

    // Standalone one-page PDF with the test CA in DSS, following SignatureLtvTests.Store.
    // No test assets, real documents, certificate stores or private IDs are read.
    private static byte[] CreatePdf(byte[] certificate)
    {
        using var pdf = new MemoryStream();
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        void Object(int number, string body)
        {
            offsets.Add(pdf.Position);
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }
        Object(1, "<< /Type /Catalog /Pages 2 0 R /DSS 5 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents 4 0 R >>");
        const string content = "0.2 0.5 0.8 rg 50 700 300 60 re f\n";
        Object(4, $"<< /Length {content.Length} >>\nstream\n{content}endstream");
        Object(5, "<< /Certs [6 0 R] >>");
        offsets.Add(pdf.Position);
        Write($"6 0 obj\n<< /Length {certificate.Length} >>\nstream\n");
        pdf.Write(certificate);
        Write("\nendstream\nendobj\n");
        var xref = pdf.Position;
        Write("xref\n0 7\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return pdf.ToArray();
    }

    private sealed class TestRevocationSource(byte[] crl, IPdfRevocationSource? online) : IPdfRevocationSource
    {
        internal const string TestUrl = "https://crl.example.test/yrk-test.crl";
        internal int TestCalls { get; private set; }
        public Task<byte[]> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (url.AbsoluteUri == TestUrl)
            {
                TestCalls++;
                return Task.FromResult(crl);
            }
            return online?.GetAsync(url, cancellationToken)
                ?? throw new InvalidOperationException("Offline mode blocked unexpected URL: " + url);
        }
    }
}
