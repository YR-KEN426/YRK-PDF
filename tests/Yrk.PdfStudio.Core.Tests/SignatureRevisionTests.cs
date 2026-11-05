using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Security;
using Yrk.PdfStudio.Core.Pdf.Signatures;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SignatureRevisionTests
{
    private static byte[] Source => SignatureFormFillTests.BlankDocument();
    private const string RangeText = "[0 0000000000 0000000000 0000000000]";
    private const int Reserve = 8192;

    // Same in-memory signing/trust setup as SignatureCoSigningTests; no PFX or certificate store.
    private static X509Certificate2 NewSigner()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Revision test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static PdfVerifyOptions Trust(X509Certificate2 signer) => new(CheckRevocation: false)
    { CustomTrustRoots = new X509Certificate2Collection(signer) };

    private static PdfSignatureInfo[] Verify(byte[] file, X509Certificate2 signer, string? password = null) =>
        PdfSignatureVerifier.Verify(file, Trust(signer), password: password).ToArray();

    private static void IsPrefix(byte[] file, byte[] expected, PdfRevisionBeforeSignatureResult result)
    {
        Assert.Null(result.FailureReason);
        Assert.NotNull(result.Bytes);
        Assert.Equal(expected, result.Bytes);
        Assert.True(file.AsSpan(0, result.Bytes.Length).SequenceEqual(result.Bytes));
        Assert.NotSame(file, result.Bytes);
        Assert.False(CosDocument.Load(result.Bytes).WasReconstructed);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task One_signature_returns_exact_original_including_EOF_line_ending(string eol)
    {
        using var signer = NewSigner();
        var original = Source[..^1].Concat(Encoding.ASCII.GetBytes(eol)).ToArray();
        var file = await PdfSigner.SignAsync(original, new(signer));
        var info = Assert.Single(Verify(file, signer));
        Assert.True(info.ContentsOffset > original.Length);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, info);
        IsPrefix(file, original, result);
        Assert.Equal([info.FieldName], result.LostSignatures.Select(s => s.FieldName));
        Assert.False(result.LosesLaterEdits);
    }

    [Fact]
    public async Task Second_signature_returns_first_signature_still_valid()
    {
        using var signer = NewSigner();
        var original = Source;
        var first = await PdfSigner.SignAsync(original, new(signer));
        var second = await PdfSigner.SignAsync(first, new(signer));
        var infos = Verify(second, signer);
        Assert.Equal(2, infos.Length);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(second, infos[1]);
        IsPrefix(second, first, result);
        Assert.Equal(PdfSignatureStatus.Valid, Assert.Single(Verify(result.Bytes!, signer)).Status);
        Assert.Equal([infos[1].FieldName], result.LostSignatures.Select(s => s.FieldName));
        Assert.False(result.LosesLaterEdits);

        result = PdfSignatureVerifier.GetRevisionBeforeSignature(second, infos[0]);
        IsPrefix(second, original, result);
        Assert.Equal(infos.Select(s => s.FieldName), result.LostSignatures.Select(s => s.FieldName));
        Assert.False(result.LosesLaterEdits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_edit_warns_even_if_restored_in_another_revision(bool restore)
    {
        using var signer = NewSigner();
        var original = Source;
        var signed = await PdfSigner.SignAsync(original, new(signer));
        var file = Rotate(signed, 90);
        if (restore) file = Rotate(file, null);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, Assert.Single(Verify(file, signer)));
        IsPrefix(file, original, result);
        Assert.True(result.LosesLaterEdits);
    }

    [Theory]
    [InlineData("DSS")]
    [InlineData("DocTimeStamp")]
    public async Task Later_validation_data_does_not_warn(string kind)
    {
        using var signer = NewSigner();
        var original = Source;
        var signed = await PdfSigner.SignAsync(original, new(signer));
        byte[] file;
        if (kind == "DSS")
        {
            var doc = CosDocument.Load(signed);
            var update = new CosIncrementalUpdate(doc);
            update.Replace(((CosReference)doc.Trailer["Root"]!).Id, doc.Catalog.Clone().Set("DSS",
                update.Add(new CosDictionary().Set("Certs", new CosArray([
                    update.Add(new CosStream(new CosDictionary(), signer.RawData))])))));
            file = update.Build().Bytes;
        }
        else file = await TestSignature(signed, signer, timestamp: true);
        var info = Verify(file, signer)[0];
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, info);
        IsPrefix(file, original, result);
        Assert.False(result.LosesLaterEdits);
        Assert.Equal(kind == "DSS" ? 1 : 2, result.LostSignatures.Count);
    }

    [Theory]
    [InlineData("reason", false)]
    [InlineData("stream", false)]
    [InlineData("reason", true)]
    [InlineData("stream", true)]
    public async Task Embedded_EOF_before_contents_is_not_a_cut(string placement, bool fakeFooter)
    {
        using var signer = NewSigner();
        var original = Source;
        // A fake footer can point to the old, valid xref: merely checking WasReconstructed is insufficient.
        var marker = fakeFooter ? $"\nstartxref\n{CosDocument.Load(original).StartXref}\n%%EOF\n" : "%%EOF\n";
        var file = await TestSignature(original, signer, placement, marker);
        var info = Assert.Single(Verify(file, signer));
        Assert.Equal(PdfSignatureStatus.Valid, info.Status);
        Assert.True(file.AsSpan(original.Length, (int)info.ContentsOffset - original.Length).IndexOf("%%EOF"u8) >= 0);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, info);
        IsPrefix(file, original, result);
    }

    [Fact]
    public async Task Signature_in_first_revision_has_no_earlier_revision()
    {
        using var signer = NewSigner();
        var file = await TestSignature(Source, signer, rewrite: true);
        var info = Assert.Single(Verify(file, signer));
        Assert.Equal(PdfSignatureStatus.Valid, info.Status);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, info);
        Assert.Null(result.Bytes);
        Assert.Equal(PdfRevisionFailureReason.NoEarlierRevision, result.FailureReason);
    }

    [Fact]
    public async Task Linearized_first_part_is_rejected_but_complete_revision_is_usable()
    {
        using var signer = NewSigner();
        var firstRevision = await TestSignature(Source, signer, rewrite: true, linearized: true);
        var firstEof = firstRevision.AsSpan().IndexOf("%%EOF"u8) + 6;
        // The first-part xref has a forward /Prev, as in linearized files. It cannot open normally.
        Assert.ThrowsAny<Exception>(() => CosDocument.Load(firstRevision[..firstEof]));
        Assert.False(CosDocument.Load(firstRevision).WasReconstructed);
        var info = Assert.Single(Verify(firstRevision, signer));
        Assert.Equal(PdfSignatureStatus.Valid, info.Status);
        Assert.True(firstEof < info.ContentsOffset);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(firstRevision, info);
        Assert.Null(result.Bytes);
        Assert.Equal(PdfRevisionFailureReason.NoEarlierRevision, result.FailureReason);

        var second = await PdfSigner.SignAsync(firstRevision, new(signer));
        result = PdfSignatureVerifier.GetRevisionBeforeSignature(second, Verify(second, signer)[1]);
        IsPrefix(second, firstRevision, result);
        Assert.Equal(PdfSignatureStatus.Valid, Assert.Single(Verify(result.Bytes!, signer)).Status);
    }

    [Fact]
    public async Task Encrypted_prefix_keeps_password_and_valid_earlier_signature()
    {
        using var signer = NewSigner();
        const string password = "synthetic-password";
        var original = PdfSecurity.Protect(Source, new(password, "synthetic-owner", PdfPermissions.All));
        var first = await PdfSigner.SignAsync(original, new(signer, Password: password));
        var second = await PdfSigner.SignAsync(first, new(signer, Password: password));
        var infos = Verify(second, signer, password);
        Assert.Equal(2, infos.Length);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(second, infos[1], password);
        IsPrefix(second, first, result);
        Assert.False(CosDocument.Load(result.Bytes!, password).IsLocked);
        Assert.Equal(PdfSignatureStatus.Valid, Assert.Single(Verify(result.Bytes!, signer, password)).Status);
        Assert.False(result.LosesLaterEdits);
        Assert.Throws<PdfPasswordRequiredException>(() => CosDocument.Load(result.Bytes!, "wrong"));
        result = PdfSignatureVerifier.GetRevisionBeforeSignature(second, infos[0], password);
        IsPrefix(second, original, result);
        Assert.False(CosDocument.Load(result.Bytes!, password).IsLocked);
    }

    [Theory]
    [InlineData(-1L, 2L, 3L)]
    [InlineData(999999L, 1000000L, 0L)]
    [InlineData(1L, long.MaxValue, long.MaxValue)]
    [InlineData(1L, 2L, -1L)]
    public async Task Broken_byte_range_returns_invalid_byte_range(long begin, long end, long length)
    {
        using var signer = NewSigner();
        var file = await PdfSigner.SignAsync(Source, new(signer));
        var originalInfo = Assert.Single(Verify(file, signer));
        var doc = CosDocument.Load(file);
        var form = (CosDictionary)doc.Resolve(doc.Catalog["AcroForm"])!;
        var field = (CosDictionary)doc.Resolve(((CosArray)doc.Resolve(form["Fields"])!)[0])!;
        var signature = (CosReference)field["V"]!;
        var update = new CosIncrementalUpdate(doc);
        // Explicit '+' signs are legal PDF integers and avoid the parser's Int32 reference lookahead,
        // so even Int64 overflow combinations reach the verifier's range checks.
        static string Integer(long value) => value >= 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
        update.Replace(signature.Id, ((CosDictionary)doc.Resolve(signature)!).Clone().Set("ByteRange",
            new CosPlaceholder($"[0 {Integer(begin)} {Integer(end)} {Integer(length)}]")));
        file = update.Build().Bytes;
        var broken = Assert.Single(Verify(file, signer));
        Assert.Equal(0, broken.ContentsOffset);
        Assert.Equal(PdfSignatureStatus.Invalid, broken.Status);
        foreach (var info in new[] { originalInfo, broken })
        {
            var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, info);
            Assert.Null(result.Bytes);
            Assert.Equal(PdfRevisionFailureReason.InvalidByteRange, result.FailureReason);
        }
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    public async Task Tries_at_most_64_EOF_candidates(int markers)
    {
        using var signer = NewSigner();
        var original = Source;
        var file = await TestSignature(original, signer, "reason", string.Concat(Enumerable.Repeat("%%EOF\n", markers)));
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, Assert.Single(Verify(file, signer)));
        if (markers == 63) IsPrefix(file, original, result);
        else
        {
            Assert.Null(result.Bytes);
            Assert.Equal(PdfRevisionFailureReason.NoEarlierRevision, result.FailureReason);
        }
    }

    [Fact]
    public async Task Candidate_with_a_different_page_count_is_rejected()
    {
        using var signer = NewSigner();
        var file = await TestSignature(Source, signer, extraPage: true);
        Assert.Equal(2, CosDocument.Load(file).GetPages().Count);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, Assert.Single(Verify(file, signer)));
        Assert.Null(result.Bytes);
        Assert.Equal(PdfRevisionFailureReason.NoEarlierRevision, result.FailureReason);
    }

    [Fact]
    public async Task Candidate_requiring_xref_reconstruction_is_rejected()
    {
        using var signer = NewSigner();
        var source = Source;
        var footer = source.AsSpan().LastIndexOf("startxref"u8);
        source = [.. source.AsSpan(0, footer), .. "startxref\n0\n%%EOF\n"u8];
        Assert.True(CosDocument.Load(source).WasReconstructed);
        var file = await PdfSigner.SignAsync(source, new(signer));
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(file, Assert.Single(Verify(file, signer)));
        Assert.Null(result.Bytes);
        Assert.Equal(PdfRevisionFailureReason.NoEarlierRevision, result.FailureReason);
    }

    [Fact]
    public async Task Xref_stream_revision_is_an_exact_prefix_with_a_valid_signature()
    {
        using var signer = NewSigner();
        var source = XrefStreamSource();
        Assert.True(CosDocument.Load(source).UsesXrefStream);
        var first = await PdfSigner.SignAsync(source, new(signer));
        var second = await PdfSigner.SignAsync(first, new(signer));
        var infos = Verify(second, signer);
        var result = PdfSignatureVerifier.GetRevisionBeforeSignature(second, infos[1]);
        IsPrefix(second, first, result);
        Assert.Equal(PdfSignatureStatus.Valid, Assert.Single(Verify(result.Bytes!, signer)).Status);
        result = PdfSignatureVerifier.GetRevisionBeforeSignature(second, infos[0]);
        IsPrefix(second, source, result);
    }

    private static byte[] Rotate(byte[] file, int? angle)
    {
        var doc = CosDocument.Load(file);
        var update = new CosIncrementalUpdate(doc);
        var page = doc.GetPages()[0];
        var dictionary = page.Dictionary.Clone();
        dictionary["Rotate"] = angle is { } value ? new CosInteger(value) : null;
        update.Replace(page.Id, dictionary);
        return update.Build().Bytes;
    }

    // Adapted from SignatureCoSigningTests.AppendSignature. Put marker-bearing objects before
    // /Contents so the backward search actually encounters them. CMS is generated by the real signer.
    private static async Task<byte[]> TestSignature(byte[] input, X509Certificate2 signer,
        string? placement = null, string marker = "", bool rewrite = false, bool timestamp = false, bool linearized = false,
        bool extraPage = false)
    {
        var doc = CosDocument.Load(input);
        var update = new CosIncrementalUpdate(doc);
        var catalog = doc.Catalog.Clone();
        if (placement == "stream")
            catalog["TestStream"] = update.Add(new CosStream(new CosDictionary(), Encoding.ASCII.GetBytes("q\n" + marker + "Q")));
        var range = new CosPlaceholder(RangeText);
        var contents = new CosPlaceholder("<" + new string('0', Reserve * 2) + ">");
        var signature = new CosDictionary().Set("Type", new CosName(timestamp ? "DocTimeStamp" : "Sig"))
            .Set("SubFilter", new CosName(timestamp ? "ETSI.RFC3161" : "ETSI.CAdES.detached"));
        if (placement == "reason") signature["Reason"] = new CosPlaceholder("(" + marker + ")");
        signature.Set("ByteRange", range).Set("Contents", contents);
        var signatureRef = update.Add(signature);
        var page = doc.GetPages()[0];
        if (extraPage)
        {
            var pages = (CosReference)doc.Catalog["Pages"]!;
            var tree = ((CosDictionary)doc.Resolve(pages)!).Clone();
            var kids = ((CosArray)doc.Resolve(tree["Kids"])!).Clone();
            kids.Items.Add(update.Add(page.Dictionary.Clone()));
            update.Replace(pages.Id, tree.Set("Kids", kids).Set("Count", new CosInteger(2)));
        }
        var field = update.Add(new CosDictionary().Set("Type", new CosName("Annot")).Set("Subtype", new CosName("Widget"))
            .Set("FT", new CosName("Sig")).Set("T", CosString.FromText(timestamp ? "Timestamp" : "Synthetic"))
            .Set("V", signatureRef).Set("Rect", CosArray.FromNumbers(0, 0, 0, 0)).Set("P", new CosReference(page.Id)));
        var annots = (doc.Resolve(page.Dictionary["Annots"]) as CosArray)?.Clone() ?? new CosArray();
        annots.Items.Add(field);
        update.Replace(page.Id, page.Dictionary.Clone().Set("Annots", annots));
        var form = (doc.Resolve(catalog["AcroForm"]) as CosDictionary)?.Clone() ?? new CosDictionary();
        var fields = (doc.Resolve(form["Fields"]) as CosArray)?.Clone() ?? new CosArray();
        fields.Items.Add(field);
        catalog["AcroForm"] = update.Add(form.Set("Fields", fields).Set("SigFlags", new CosInteger(3)));
        update.Replace(((CosReference)doc.Trailer["Root"]!).Id, catalog);
        var bytes = update.Build().Bytes;
        if (rewrite)
        {
            doc = CosDocument.Load(bytes);
            ((CosDictionary)doc.Resolve(signatureRef)!)["ByteRange"] = range;
            bytes = linearized ? LinearizedLike(doc, signatureRef.Id) : CosCompactWriter.Write(doc);
        }
        var begin = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(contents.Text));
        var end = begin + Reserve * 2 + 2;
        var rangeOffset = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(RangeText));
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"[0 {begin} {end} {bytes.Length - end}")
            .PadRight(RangeText.Length - 1) + "]").CopyTo(bytes, rangeOffset);
        var signedData = bytes[..begin].Concat(bytes[end..]).ToArray();
        byte[] cms;
        if (timestamp)
        {
            var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4"), new Oid(CadesSignature.Sha256),
                SHA256.HashData(signedData), new byte[] { 1 }, DateTimeOffset.UtcNow);
            var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
            token.ComputeSignature(new CmsSigner(signer));
            cms = token.Encode();
        }
        else cms = await CadesSignature.CreateAsync(signedData, signer, null, default);
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(bytes, begin + 1);
        return bytes;
    }

    private static byte[] XrefStreamSource()
    {
        var source = Source;
        var doc = CosDocument.Load(source);
        using var output = new MemoryStream();
        output.Write(source.AsSpan(0, (int)doc.StartXref));
        var writer = new CosWriter(output);
        var xref = writer.Position;
        var entries = new List<byte>();
        for (var number = 0; number <= doc.Size; number++)
        {
            var offset = number == 0 ? 0 : number == doc.Size ? (int)xref
                : source.AsSpan().IndexOf(Encoding.ASCII.GetBytes($"{number} 0 obj\n"));
            entries.Add(number == 0 ? (byte)0 : (byte)1);
            entries.AddRange([(byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
            entries.AddRange(number == 0 ? new byte[] { 255, 255 } : new byte[] { 0, 0 });
        }
        writer.WriteIndirectObject(new CosObjectId(doc.Size, 0), new CosStream(new CosDictionary()
            .Set("Type", new CosName("XRef")).Set("Size", new CosInteger(doc.Size + 1))
            .Set("Root", doc.Trailer["Root"]!).Set("W", CosArray.FromNumbers(1, 4, 2)), entries.ToArray()));
        writer.WriteRaw($"\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    // A two-part, single-revision layout with a front xref and forward /Prev to the main xref.
    // It models the relevant linearization structure without relying on an external PDF fixture.
    private static byte[] LinearizedLike(CosDocument doc, CosObjectId signature)
    {
        using var output = new MemoryStream();
        var writer = new CosWriter(output);
        writer.WriteRaw("%PDF-1.7\n");
        var offsets = new Dictionary<int, long>();
        var ids = doc.ObjectIds.ToArray();
        foreach (var id in ids.Where(id => id != signature))
        {
            offsets[id.Number] = writer.Position;
            writer.WriteIndirectObject(id, doc.GetObject(id)!);
        }
        var front = writer.Position;
        writer.WriteRaw("xref\n0 1\n0000000000 65535 f\r\ntrailer\n");
        var next = new CosPlaceholder("0000000000");
        writer.Write(new CosDictionary().Set("Size", new CosInteger(doc.Size)).Set("Root", doc.Trailer["Root"]!).Set("Prev", next));
        writer.WriteRaw($"\nstartxref\n{front}\n%%EOF\n");
        offsets[signature.Number] = writer.Position;
        writer.WriteIndirectObject(signature, doc.GetObject(signature)!);
        var main = writer.Position;
        writer.WriteRaw("xref\n");
        foreach (var (number, offset) in offsets.OrderBy(p => p.Key))
            writer.WriteRaw($"{number} 1\n{offset:D10} 00000 n\r\n");
        writer.WriteRaw("trailer\n");
        writer.Write(new CosDictionary().Set("Size", new CosInteger(doc.Size)).Set("Root", doc.Trailer["Root"]!));
        writer.WriteRaw($"\nstartxref\n{front}\n%%EOF\n");
        var bytes = output.ToArray();
        Encoding.ASCII.GetBytes(main.ToString("D10", CultureInfo.InvariantCulture)).CopyTo(bytes, (int)writer.PlaceholderOffsets[next]);
        return bytes;
    }
}
