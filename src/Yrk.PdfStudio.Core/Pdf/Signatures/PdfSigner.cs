using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>
/// Adds a PAdES digital signature (ETSI.CAdES.detached) to a PDF as an incremental update, so the
/// original bytes and any earlier signatures stay intact.
/// </summary>
public static class PdfSigner
{
    // Bytes reserved for the CMS beyond the certificates it carries; a timestamp token adds its own chain.
    private const int BaseReserve = 4096;
    private const int TimestampReserve = 12288;

    /// <summary>Signs <paramref name="inputPath"/> into <paramref name="outputPath"/> (written atomically; may be the same file).</summary>
    public static async Task SignFileAsync(string inputPath, string outputPath, PdfSignRequest request, CancellationToken cancellationToken = default)
    {
        var input = await File.ReadAllBytesAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var output = await SignAsync(input, request, cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, output, cancellationToken).ConfigureAwait(false);
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static async Task<byte[]> SignAsync(byte[] input, PdfSignRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);

        var document = CosDocument.Load(input);
        if (document.IsEncrypted)
        {
            throw new PdfException("Password-protected PDFs cannot be signed yet. Save an unprotected copy first.");
        }

        EnsureChangesAllowed(document);
        if (request.Certification != PdfCertification.None && HasSignatures(document))
        {
            throw new PdfException("Only the first signature can certify a document, and this one is signed already.");
        }

        var reserve = BaseReserve + ChainLength(request.Certificate) + (request.TimestampAuthority is null ? 0 : TimestampReserve);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (bytes, byteRange, contents) = BuildUpdate(document, request, reserve);
            var contentsStart = (int)contents;
            var contentsEnd = contentsStart + (reserve * 2) + 2;

            // Fill in /ByteRange now that the layout is final, padded to the placeholder's width.
            var rangeText = string.Create(CultureInfo.InvariantCulture, $"[0 {contentsStart} {contentsEnd} {bytes.Length - contentsEnd}");
            var placeholderLength = ByteRangePlaceholder.Length;
            Encoding.ASCII.GetBytes(rangeText.PadRight(placeholderLength - 1) + "]").CopyTo(bytes, (int)byteRange);

            var signedData = new byte[bytes.Length - (contentsEnd - contentsStart)];
            bytes.AsSpan(0, contentsStart).CopyTo(signedData);
            bytes.AsSpan(contentsEnd).CopyTo(signedData.AsSpan(contentsStart));

            var cms = await CadesSignature.CreateAsync(signedData, request.Certificate, request.TimestampAuthority, cancellationToken).ConfigureAwait(false);
            if (cms.Length <= reserve)
            {
                Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(bytes, contentsStart + 1);
                return request.RevocationSource is { } source
                    ? await LongTermValidation.AddAsync(bytes, cms, source, cancellationToken).ConfigureAwait(false)
                    : bytes;
            }

            reserve = cms.Length + 2048;
        }

        throw new PdfException("The signature did not fit in the space reserved for it.");
    }

    private const string ByteRangePlaceholder = "[0 0000000000 0000000000 0000000000]";

    private static (byte[] Bytes, long ByteRangeOffset, long ContentsOffset) BuildUpdate(CosDocument document, PdfSignRequest request, int reserve)
    {
        var update = new CosIncrementalUpdate(document);
        var pages = document.GetPages();
        var appearance = request.Appearance;
        var pageIndex = appearance?.PageIndex ?? 0;
        if (pageIndex < 0 || pageIndex >= pages.Count)
        {
            throw new PdfException("The signature page does not exist.");
        }

        var page = pages[pageIndex];
        var signingTime = request.SigningTime ?? DateTimeOffset.Now;

        // Signature dictionary (§12.8.1).
        var byteRange = new CosPlaceholder(ByteRangePlaceholder);
        var contents = new CosPlaceholder("<" + new string('0', reserve * 2) + ">");
        var signature = new CosDictionary()
            .Set("Type", new CosName("Sig"))
            .Set("Filter", new CosName("Adobe.PPKLite"))
            .Set("SubFilter", new CosName("ETSI.CAdES.detached"))
            .Set("ByteRange", byteRange)
            .Set("Contents", contents)
            .Set("M", CosString.FromDate(signingTime))
            .Set("Name", CosString.FromText(request.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)));
        AddText(signature, "Reason", request.Reason);
        AddText(signature, "Location", request.Location);
        AddText(signature, "ContactInfo", request.ContactInfo);
        signature["Prop_Build"] = new CosDictionary().Set("App", new CosDictionary().Set("Name", new CosName("YRK_PDF_Studio")));
        if (request.Certification != PdfCertification.None)
        {
            // A certifying signature (§12.8.2.2): the changes still allowed, checked by viewers on later revisions.
            signature["Reference"] = new CosArray([
                new CosDictionary()
                    .Set("Type", new CosName("SigRef"))
                    .Set("TransformMethod", new CosName("DocMDP"))
                    .Set("TransformParams", new CosDictionary()
                        .Set("Type", new CosName("TransformParams"))
                        .Set("P", new CosInteger((int)request.Certification))
                        .Set("V", new CosName("1.2"))),
            ]);
        }

        var signatureRef = update.Add(signature);

        // The catalog changes at most once (new /AcroForm, /Perms), whichever step needs it first.
        var rootId = (document.Trailer["Root"] as CosReference)?.Id ?? throw new PdfException("The document has no catalog.");
        CosDictionary? catalogCopy = null;
        CosDictionary Catalog()
        {
            if (catalogCopy is null)
            {
                catalogCopy = document.Catalog.Clone();
                update.Replace(rootId, catalogCopy);
            }

            return catalogCopy;
        }

        if (request.Certification != PdfCertification.None)
        {
            Catalog()["Perms"] = new CosDictionary().Set("DocMDP", signatureRef);
        }

        // The field and its widget, merged into one dictionary.
        var widget = new CosDictionary()
            .Set("Type", new CosName("Annot"))
            .Set("Subtype", new CosName("Widget"))
            .Set("FT", new CosName("Sig"))
            .Set("T", CosString.FromText(UniqueFieldName(document)))
            .Set("V", signatureRef)
            .Set("F", new CosInteger(132)) // Print + Locked
            .Set("P", new CosReference(page.Id));

        if (appearance is null)
        {
            widget["Rect"] = CosArray.FromNumbers(0, 0, 0, 0);
        }
        else
        {
            var (rect, form) = BuildAppearance(update, page, appearance);
            widget["Rect"] = rect;
            widget["AP"] = new CosDictionary().Set("N", update.Add(form));
        }

        var widgetRef = update.Add(widget);

        AddToArray(document, update, page.Dictionary, page.Id, "Annots", widgetRef);
        AddField(document, update, widgetRef, Catalog);

        var (bytes, placeholders) = update.Build();
        return (bytes, placeholders[byteRange], placeholders[contents]);
    }

    /// <param name="catalog">The catalog copy that goes into the update (made on first use).</param>
    private static void AddField(CosDocument document, CosIncrementalUpdate update, CosReference field, Func<CosDictionary> catalog)
    {
        CosDictionary acroForm;
        switch (document.Catalog["AcroForm"])
        {
            case CosReference reference when document.Resolve(reference) is CosDictionary existing:
                acroForm = existing.Clone();
                update.Replace(reference.Id, acroForm);
                break;
            case CosDictionary direct:
                acroForm = direct.Clone();
                catalog()["AcroForm"] = acroForm;
                break;
            default:
                acroForm = new CosDictionary();
                catalog()["AcroForm"] = update.Add(acroForm);
                break;
        }

        // SignaturesExist | AppendOnly: viewers must save incrementally from now on.
        acroForm["SigFlags"] = new CosInteger(3);
        if (acroForm["Fields"] is CosReference fieldsRef && document.Resolve(fieldsRef) is CosArray fields)
        {
            var copy = fields.Clone();
            copy.Items.Add(field);
            update.Replace(fieldsRef.Id, copy);
        }
        else
        {
            var copy = (acroForm["Fields"] as CosArray)?.Clone() ?? new CosArray();
            copy.Items.Add(field);
            acroForm["Fields"] = copy;
        }
    }

    /// <summary>Appends to an array entry of an object, changing whichever object actually holds the array.</summary>
    private static void AddToArray(CosDocument document, CosIncrementalUpdate update, CosDictionary owner, CosObjectId ownerId, string key, CosObject item)
    {
        if (owner[key] is CosReference reference && document.Resolve(reference) is CosArray shared)
        {
            var copy = shared.Clone();
            copy.Items.Add(item);
            update.Replace(reference.Id, copy);
            return;
        }

        var array = (owner[key] as CosArray)?.Clone() ?? new CosArray();
        array.Items.Add(item);
        update.Replace(ownerId, owner.Clone().Set(key, array));
    }

    private static (CosArray Rect, CosStream Form) BuildAppearance(CosIncrementalUpdate update, CosPage page, PdfSignatureAppearance appearance)
    {
        var (x0, y0, x1, y1) = page.CropBox;
        var (pageWidth, pageHeight) = page.Rotate is 90 or 270 ? (y1 - y0, x1 - x0) : (x1 - x0, y1 - y0);
        var b = appearance.Bounds;
        var width = Math.Max(1, b.Width * pageWidth);
        var height = Math.Max(1, b.Height * pageHeight);

        // Corners as displayed; the form matrix rotates the appearance so it stays upright on rotated pages.
        var bottomLeft = page.ToUserSpace(b.X, b.Y + b.Height);
        var bottomRight = page.ToUserSpace(b.X + b.Width, b.Y + b.Height);
        var topLeft = page.ToUserSpace(b.X, b.Y);
        var topRight = page.ToUserSpace(b.X + b.Width, b.Y);
        double[] xs = [bottomLeft.X, bottomRight.X, topLeft.X, topRight.X];
        double[] ys = [bottomLeft.Y, bottomRight.Y, topLeft.Y, topRight.Y];
        var rect = CosArray.FromNumbers(xs.Min(), ys.Min(), xs.Max(), ys.Max());
        var matrix = CosArray.FromNumbers(
            (bottomRight.X - bottomLeft.X) / width,
            (bottomRight.Y - bottomLeft.Y) / width,
            (topLeft.X - bottomLeft.X) / height,
            (topLeft.Y - bottomLeft.Y) / height,
            0,
            0);

        // Adobe's layered signature appearance: N draws /FRM, which draws the background layer /n0 (blank) and the
        // signature layer /n2 (the picture). Adobe Acrobat looks for these layers when it validates a visible signature,
        // and fails with "Expected a dict object" when the appearance is one plain form.
        CosStream Form(string content, CosDictionary xobjects, CosArray? formMatrix = null)
        {
            var dictionary = new CosDictionary()
                .Set("Type", new CosName("XObject"))
                .Set("Subtype", new CosName("Form"))
                .Set("BBox", CosArray.FromNumbers(0, 0, width, height))
                .Set("Resources", new CosDictionary().Set("XObject", xobjects));
            if (formMatrix is not null)
            {
                dictionary["Matrix"] = formMatrix;
            }

            return new CosStream(dictionary, Encoding.ASCII.GetBytes(content));
        }

        var image = update.Add(BuildImage(update, appearance.Image));
        var picture = string.Create(CultureInfo.InvariantCulture, $"q {CosWriter.FormatReal(width)} 0 0 {CosWriter.FormatReal(height)} 0 0 cm /Im0 Do Q");
        var n0 = update.Add(Form("% DSBlank", new CosDictionary()));
        var n2 = update.Add(Form(picture, new CosDictionary().Set("Im0", image)));
        var frm = update.Add(Form("q /n0 Do Q q /n2 Do Q", new CosDictionary().Set("n0", n0).Set("n2", n2)));
        return (rect, Form("q /FRM Do Q", new CosDictionary().Set("FRM", frm), matrix));
    }

    /// <summary>An RGB image XObject with the alpha channel as a soft mask.</summary>
    internal static CosStream BuildImage(CosIncrementalUpdate update, PdfImage image)
    {
        var pixelCount = image.Width * image.Height;
        var rgb = new byte[pixelCount * 3];
        var alpha = new byte[pixelCount];
        var hasTransparency = false;
        for (var i = 0; i < pixelCount; i++)
        {
            rgb[(i * 3) + 0] = image.Pixels[(i * 4) + 2];
            rgb[(i * 3) + 1] = image.Pixels[(i * 4) + 1];
            rgb[(i * 3) + 2] = image.Pixels[(i * 4) + 0];
            alpha[i] = image.Pixels[(i * 4) + 3];
            hasTransparency |= alpha[i] != 255;
        }

        CosDictionary ImageDictionary(string colorSpace) => new CosDictionary()
            .Set("Type", new CosName("XObject"))
            .Set("Subtype", new CosName("Image"))
            .Set("Width", new CosInteger(image.Width))
            .Set("Height", new CosInteger(image.Height))
            .Set("ColorSpace", new CosName(colorSpace))
            .Set("BitsPerComponent", new CosInteger(8))
            .Set("Filter", new CosName("FlateDecode"));

        var dictionary = ImageDictionary("DeviceRGB");
        if (hasTransparency)
        {
            dictionary["SMask"] = update.Add(new CosStream(ImageDictionary("DeviceGray"), CosFilters.Deflate(alpha)));
        }

        return new CosStream(dictionary, CosFilters.Deflate(rgb));
    }

    private static string UniqueFieldName(CosDocument document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (document.Resolve(document.Catalog["AcroForm"]) is CosDictionary acroForm && document.Resolve(acroForm["Fields"]) is CosArray fields)
        {
            foreach (var field in fields.Items)
            {
                if (document.Resolve(field) is CosDictionary dictionary && document.Resolve(dictionary["T"]) is CosString name)
                {
                    names.Add(name.ToText());
                }
            }
        }

        for (var i = 1; ; i++)
        {
            var candidate = "Signature" + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Whether a signature field of the document is signed already.</summary>
    private static bool HasSignatures(CosDocument document)
    {
        if (document.Resolve(document.Catalog["AcroForm"]) is not CosDictionary acroForm || document.Resolve(acroForm["Fields"]) is not CosArray fields)
        {
            return false;
        }

        var pending = new Stack<CosObject>(fields.Items);
        var seen = new HashSet<CosObject>();
        while (pending.Count > 0)
        {
            if (document.Resolve(pending.Pop()) is not CosDictionary field || !seen.Add(field))
            {
                continue;
            }

            if ((document.Resolve(field["FT"]) as CosName)?.Value == "Sig" && document.Resolve(field["V"]) is CosDictionary)
            {
                return true;
            }

            if (document.Resolve(field["Kids"]) is CosArray kids)
            {
                kids.Items.ForEach(pending.Push);
            }
        }

        return false;
    }

    /// <summary>A certifying signature that forbids any change (DocMDP P=1) also forbids more signatures.</summary>
    private static void EnsureChangesAllowed(CosDocument document)
    {
        if (document.Resolve(document.Catalog["Perms"]) is not CosDictionary perms
            || document.Resolve(perms["DocMDP"]) is not CosDictionary certification
            || document.Resolve(certification["Reference"]) is not CosArray references)
        {
            return;
        }

        foreach (var reference in references.Items.Select(document.Resolve).OfType<CosDictionary>())
        {
            if (document.Resolve(reference["TransformParams"]) is CosDictionary parameters
                && document.Resolve(parameters["P"]) is CosInteger { Value: 1 })
            {
                throw new PdfException("This document is certified and its author does not allow any changes, including new signatures.");
            }
        }
    }

    private static void AddText(CosDictionary dictionary, string key, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            dictionary[key] = CosString.FromText(text.Trim());
        }
    }

    private static int ChainLength(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.Build(certificate);
        var length = chain.ChainElements.Sum(e => e.Certificate.RawData.Length);
        return Math.Max(length, certificate.RawData.Length);
    }
}
