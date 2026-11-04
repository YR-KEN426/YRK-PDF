using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Yrk.PdfStudio.Core.Pdf.Cos;

namespace Yrk.PdfStudio.Core.Pdf.Signatures;

/// <summary>Finds the signature fields of a PDF and checks each signature.</summary>
public static class PdfSignatureVerifier
{
    private const int MaxFieldDepth = 32;

    public static async Task<IReadOnlyList<PdfSignatureInfo>> VerifyFileAsync(string path, PdfVerifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Verify(bytes, options ?? new PdfVerifyOptions(), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file as it was when the signature was applied (the signed revision).</summary>
    public static byte[] GetSignedRevision(byte[] file, PdfSignatureInfo signature) =>
        file.AsSpan(0, (int)Math.Min(file.Length, signature.SignedLength)).ToArray();

    public static IReadOnlyList<PdfSignatureInfo> Verify(byte[] file, PdfVerifyOptions options, CancellationToken cancellationToken = default)
    {
        CosDocument document;
        try
        {
            document = CosDocument.Load(file);
        }
        catch (PdfException)
        {
            return [];
        }

        var results = new List<PdfSignatureInfo>();
        foreach (var (name, field) in FindSignatureFields(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Resolve(field["V"]) is CosDictionary signature)
            {
                results.Add(Check(document, file, name, field, signature, options));
            }
        }

        return results.OrderBy(r => r.SignedLength).ToList();
    }

    /// <summary>
    /// Whether the revisions after <paramref name="signed"/> only added long-term validation data (/DSS: certificates
    /// and revocation lists), which does not change the document. Every other object must be as it was when signed.
    /// </summary>
    private static bool OnlyValidationDataAfter(CosDocument document, CosDocument signed) =>
        OnlySignatureAndValidationDataAfter(document, signed, null, null);

    private static bool OnlySignatureAndValidationDataAfter(CosDocument document, CosDocument signed,
        HashSet<CosObjectId>? signingData, HashSet<int>? signingContent)
    {
        if ((document.Catalog["DSS"] is null && signingData is null) || document.Trailer["Root"] is not CosReference root)
        {
            return false;
        }

        if (!Same(document.Trailer["Root"], signed.Trailer["Root"])
            || !Same(document.Trailer["Info"], signed.Trailer["Info"])
            || !Same(document.Trailer["Encrypt"], signed.Trailer["Encrypt"]))
        {
            return false;
        }

        // An object used outside /DSS is document content, even if /DSS also references it.
        var content = signingContent ?? new HashSet<int>();
        if (signingContent is null && (!CollectContent(document, content) || !CollectContent(signed, content)))
        {
            return false;
        }

        var validationData = new HashSet<CosObjectId>();
        var budget = 10000;
        if (document.Catalog["DSS"] is { } dssValue && !ValidationObject(dssValue, "DSS", 0))
        {
            return false;
        }

        // Include deleted objects, too. Removing content is not a validation-data update.
        foreach (var id in document.ObjectIds.Union(signed.ObjectIds))
        {
            if (validationData.Contains(id))
            {
                continue;
            }

            var now = document.GetObject(id);
            var then = signed.GetObject(id);
            if (signingData?.Contains(id) == true && !content.Contains(id.Number))
            {
                continue;
            }
            if (!content.Contains(id.Number) && now is CosStream { Dictionary: var header } && (header["Type"] as CosName)?.Value is "XRef" or "ObjStm")
            {
                continue; // File structure: the objects an object stream holds are compared one by one.
            }

            if (id == root.Id)
            {
                // The catalog only gained (or changed) /DSS.
                if (now is not CosDictionary catalog || then is not CosDictionary signedCatalog)
                {
                    return false;
                }

                now = catalog.Clone();
                ((CosDictionary)now)["DSS"] = null;
                then = signedCatalog.Clone();
                ((CosDictionary)then)["DSS"] = null;
                if (signingData is not null && !content.Contains(id.Number))
                {
                    ((CosDictionary)now)["AcroForm"] = null;
                    ((CosDictionary)then)["AcroForm"] = null;
                }
            }

            if (!Same(now, then))
            {
                return false;
            }
        }

        return true;

        bool ValidationObject(CosObject? value, string kind, int depth)
        {
            if (--budget < 0 || depth > 32)
            {
                return false;
            }

            if (value is CosReference reference)
            {
                if (content.Contains(reference.Id.Number))
                {
                    return false;
                }

                validationData.Add(reference.Id);
                return ValidationObject(document.GetObject(reference.Id), kind, depth + 1);
            }

            return kind switch
            {
                "DSS" => value is CosDictionary dss && dss.Entries.All(e => e.Key switch
                {
                    "Type" => e.Value is CosName { Value: "DSS" },
                    "Certs" or "CRLs" or "OCSPs" => ValidationObject(e.Value, "Array", depth + 1),
                    "VRI" => ValidationObject(e.Value, "VRI", depth + 1),
                    _ => false,
                }),
                "VRI" => value is CosDictionary vri && vri.Entries.All(e =>
                    e.Key.Length == 40 && e.Key.All(Uri.IsHexDigit) && ValidationObject(e.Value, "Entry", depth + 1)),
                "Entry" => value is CosDictionary entry && entry.Entries.All(e => e.Key switch
                {
                    "Type" => e.Value is CosName { Value: "VRI" },
                    "Cert" or "CRL" or "OCSP" => ValidationObject(e.Value, "Array", depth + 1),
                    "TU" => e.Value is CosString,
                    "TS" => ValidationObject(e.Value, "Stream", depth + 1),
                    _ => false,
                }),
                "Array" => value is CosArray array && array.Items.All(i => ValidationObject(i, "Stream", depth + 1)),
                // Do not follow arbitrary stream dictionaries (page content, actions, external files, etc.).
                "Stream" => value is CosStream stream && stream.Dictionary.Entries.All(e => e.Key switch
                {
                    "Length" => ValidationObject(e.Value, "Length", depth + 1),
                    "Filter" => ValidationObject(e.Value, "Filter", depth + 1),
                    "DecodeParms" => ValidationObject(e.Value, "DecodeParms", depth + 1),
                    _ => false,
                }),
                "Length" => value is CosInteger { Value: >= 0 },
                "Filter" => value is CosName || value is CosArray filters && filters.Items.All(i => ValidationObject(i, "FilterName", depth + 1)),
                "FilterName" => value is CosName,
                "DecodeParms" => value is CosNull || value is CosArray parameters && parameters.Items.All(i => ValidationObject(i, "DecodeParms", depth + 1))
                    || value is CosDictionary parms && parms.Entries.All(e =>
                        e.Key is "Predictor" or "Colors" or "BitsPerComponent" or "Columns" or "EarlyChange"
                        && e.Value is CosInteger),
                _ => false,
            };
        }
    }

    /// <summary>
    /// Recognizes the containers a signing operation may change. Every other object is still
    /// compared by OnlyValidationDataAfter, including objects shared with document content.
    /// </summary>
    private static bool OnlySigningDataAfter(CosDocument document, CosDocument signed)
    {
        var allowed = new HashSet<CosObjectId>();
        var widgets = new HashSet<int>();
        var signedFields = new HashSet<int>();
        var budget = 10000;
        var seenFields = new HashSet<int>();
        var graphSeen = new HashSet<CosObjectId>();
        var graphActive = new HashSet<CosObjectId>();
        var pages = document.GetPages().Select(p => p.Id).ToHashSet();

        if (!Form() || signedFields.Count == 0)
        {
            return false;
        }

        foreach (var page in document.GetPages())
        {
            if (signed.GetObject(page.Id) is not CosDictionary oldPage
                || !SameExcept(page.Dictionary, oldPage, "Annots")
                || !AppendOnly(page.Dictionary["Annots"], oldPage["Annots"], value =>
                    value is CosReference r && widgets.Contains(r.Id.Number)))
            {
                return false;
            }
            allowed.Add(page.Id);
        }

        var content = new HashSet<int>();
        if (!ProtectedContent(document) || !ProtectedContent(signed))
        {
            return false;
        }
        return OnlySignatureAndValidationDataAfter(document, signed, allowed, content);

        bool Step(int depth) => --budget >= 0 && depth <= 32;

        void Allow(CosObject? value)
        {
            if (value is CosReference r)
            {
                allowed.Add(r.Id);
            }
        }

        bool Form()
        {
            if (document.Resolve(document.Catalog["AcroForm"]) is not CosDictionary form)
            {
                return false;
            }
            var oldForm = signed.Resolve(signed.Catalog["AcroForm"]) as CosDictionary ?? new CosDictionary();
            if (!SameExcept(form, oldForm, "Fields", "SigFlags")
                || document.Resolve(form["SigFlags"]) is not (null or CosInteger { Value: >= 0 and <= 3 }))
            {
                return false;
            }
            // Only the value of /SigFlags may change; its indirect object has no other role.
            Allow(document.Catalog["AcroForm"]);
            if (!Graph(form["SigFlags"], 0, streams: false))
            {
                return false;
            }
            return Fields(form["Fields"], oldForm["Fields"], null, 0);
        }

        bool Fields(CosObject? value, CosObject? oldValue, string? inheritedType, int depth)
        {
            if (!Step(depth) || document.Resolve(value) is not CosArray fields)
            {
                return false;
            }
            var old = signed.Resolve(oldValue) as CosArray ?? new CosArray();
            if (fields.Items.Count < old.Items.Count)
            {
                return false;
            }
            Allow(value);
            for (var i = 0; i < fields.Items.Count; i++)
            {
                var previous = i < old.Items.Count ? old[i] : null;
                if (previous is not null && !Same(fields[i], previous)
                    || !Field(fields[i], previous, inheritedType, depth + 1))
                {
                    return false;
                }
            }
            return true;
        }

        bool Field(CosObject value, CosObject? oldValue, string? inheritedType, int depth)
        {
            if (!Step(depth) || value is not CosReference reference || !seenFields.Add(reference.Id.Number)
                || document.Resolve(value) is not CosDictionary field)
            {
                return false;
            }
            var old = oldValue is null ? null : signed.Resolve(oldValue) as CosDictionary;
            var type = (document.Resolve(field["FT"]) as CosName)?.Value ?? inheritedType;
            if (type == "Sig" && field["V"] is CosReference signatureRef
                && (old is null || signed.Resolve(old["V"]) is null or CosNull))
            {
                if (signed.GetObject(signatureRef.Id) is not CosNull
                    || document.Resolve(signatureRef) is not CosDictionary signature
                    || signature["Type"] is not CosName { Value: "Sig" or "DocTimeStamp" }
                    || document.Resolve(signature["Contents"]) is not CosString { Bytes.Length: > 0 }
                    || document.Resolve(signature["ByteRange"]) is not CosArray { Items.Count: 4 }
                    || !Graph(signatureRef, depth + 1, streams: false, signatureRoot: true)
                    || old is not null && !SameExcept(field, old, "V", "AP", "Kids")
                    || old is null && !FieldShape(field))
                {
                    return false;
                }
                signedFields.Add(reference.Id.Number);
                Allow(value);
                if (!Appearance(field["AP"], depth + 1))
                {
                    return false;
                }
                if ((field["Subtype"] as CosName)?.Value == "Widget")
                {
                    if (!Widget(value, oldValue, merged: true, depth + 1))
                    {
                        return false;
                    }
                }
                if (document.Resolve(field["Kids"]) is CosArray kids)
                {
                    Allow(field["Kids"]);
                    var oldKids = old is null ? null : signed.Resolve(old["Kids"]) as CosArray;
                    if (kids.Items.Count < (oldKids?.Items.Count ?? 0))
                    {
                        return false;
                    }
                    for (var i = 0; i < kids.Items.Count; i++)
                    {
                        var previous = oldKids is not null && i < oldKids.Items.Count ? oldKids[i] : null;
                        if (previous is not null && !Same(kids[i], previous)
                            || !Widget(kids[i], previous, false, depth + 1))
                        {
                            return false;
                        }
                    }
                }
                else if (field["Kids"] is not null || old?["Kids"] is not null)
                {
                    return false;
                }
                return true;
            }

            // Existing fields retain their values and every other property. A field-tree parent
            // may only append signature fields to /Kids, following the same rule as /Fields.
            if (old is null || !SameExcept(field, old, "Kids"))
            {
                return false;
            }
            Allow(value);
            return field["Kids"] is null && old["Kids"] is null
                || Fields(field["Kids"], old["Kids"], type, depth + 1);
        }

        bool FieldShape(CosDictionary field) => field.Entries.All(e => e.Key switch
        {
            "V" or "AP" or "Kids" => true,
            "P" => e.Value is CosReference r && pages.Contains(r.Id),
            "Parent" => e.Value is CosReference,
            "Type" => e.Value is CosName { Value: "Annot" },
            "Subtype" => e.Value is CosName { Value: "Widget" },
            "FT" => e.Value is CosName { Value: "Sig" },
            "T" or "TU" or "TM" or "M" => e.Value is CosString,
            "F" or "Ff" => e.Value is CosInteger,
            "Rect" => e.Value is CosArray a && a.Items.Count == 4 && a.Items.All(n => n is CosInteger or CosReal),
            _ => false,
        });

        bool Widget(CosObject value, CosObject? oldValue, bool merged, int depth)
        {
            if (!Step(depth) || value is not CosReference r || document.Resolve(value) is not CosDictionary widget
                || widget["Subtype"] is not CosName { Value: "Widget" }
                || widget["P"] is CosReference p && !pages.Contains(p.Id))
            {
                return false;
            }
            var old = oldValue is null ? null : signed.Resolve(oldValue) as CosDictionary;
            if (!merged && (old is null ? !FieldShape(widget) || widget["V"] is not null || widget["Kids"] is not null
                : !Same(value, oldValue) || !SameExcept(widget, old, "AP")))
            {
                return false;
            }
            widgets.Add(r.Id.Number);
            Allow(value);
            return Appearance(widget["AP"], depth + 1);
        }

        bool Appearance(CosObject? value, int depth)
        {
            if (value is null)
            {
                return true;
            }
            if (!Step(depth) || document.Resolve(value) is not CosDictionary ap
                || ap.Entries.Any(e => e.Key is not ("N" or "R" or "D")))
            {
                return false;
            }
            Allow(value);
            return ap.Entries.Values.All(v => AppearanceState(v, depth + 1));
        }

        bool AppearanceState(CosObject value, int depth)
        {
            if (!Step(depth))
            {
                return false;
            }
            return document.Resolve(value) switch
            {
                CosStream stream => stream.Dictionary["Subtype"] is CosName { Value: "Form" }
                    && Graph(value, depth + 1, streams: true),
                CosDictionary states => Graph(value, depth + 1, streams: true)
                    && states.Entries.Values.All(v => document.Resolve(v) is CosStream s
                        && s.Dictionary["Subtype"] is CosName { Value: "Form" }),
                _ => false,
            };
        }

        bool Graph(CosObject? value, int depth, bool streams, bool signatureRoot = false)
        {
            if (!Step(depth))
            {
                return false;
            }
            if (value is CosReference r)
            {
                Allow(value);
                if (graphActive.Contains(r.Id))
                {
                    return false;
                }
                if (!graphSeen.Add(r.Id))
                {
                    return true;
                }
                graphActive.Add(r.Id);
                var valid = Graph(document.GetObject(r.Id), depth + 1, streams, signatureRoot);
                graphActive.Remove(r.Id);
                return valid;
            }
            return value switch
            {
                CosDictionary d => (signatureRoot || (d["Type"] as CosName)?.Value is not
                    ("Catalog" or "Pages" or "Page" or "Annot" or "Sig" or "DocTimeStamp"))
                    && d.Entries.All(e => e.Key == "Data" && d["Type"] is CosName { Value: "SigRef" }
                        && Same(e.Value, document.Trailer["Root"]) || Graph(e.Value, depth + 1, streams)),
                CosArray a => a.Items.All(v => Graph(v, depth + 1, streams)),
                CosStream s => streams && AppearanceStreamShape(s.Dictionary) && Graph(s.Dictionary, depth + 1, streams),
                null or CosNull or CosBoolean or CosName or CosString or CosInteger or CosReal => true,
                _ => false,
            };
        }

        bool AppearanceStreamShape(CosDictionary header) => header.Entries.All(e => e.Key is
            "Type" or "Subtype" or "Length" or "Filter" or "DecodeParms" or "DL"
            or "FormType" or "BBox" or "Matrix" or "Resources" or "Group"
            or "Width" or "Height" or "BitsPerComponent" or "ColorSpace" or "Intent"
            or "ImageMask" or "Mask" or "Decode" or "Interpolate" or "SMask" or "Matte"
            or "Length1" or "Length2" or "Length3")
            && (header["Resources"] is null || document.Resolve(header["Resources"]) is CosDictionary resources
                && resources.Entries.Keys.All(k => k is "ExtGState" or "ColorSpace" or "Pattern" or "Shading"
                    or "XObject" or "Font" or "ProcSet" or "Properties"));

        bool AppendOnly(CosObject? value, CosObject? oldValue, Func<CosObject, bool> added)
        {
            var resolved = document.Resolve(value);
            var previous = signed.Resolve(oldValue);
            if (resolved is not (null or CosNull or CosArray) || previous is not (null or CosNull or CosArray))
            {
                return false;
            }
            var array = resolved as CosArray ?? new CosArray();
            var old = previous as CosArray ?? new CosArray();
            Allow(value);
            return array.Items.Count >= old.Items.Count
                && old.Items.Select((v, i) => Same(v, array[i])).All(b => b)
                && array.Items.Skip(old.Items.Count).All(added);
        }

        bool ProtectedContent(CosDocument doc)
        {
            var pending = new Stack<(CosObject? Value, string Role, int Depth)>();
            var visited = new HashSet<(CosObjectId, string)>();
            var remaining = 100000;
            pending.Push((doc.Trailer["Root"], "Catalog", 0));
            pending.Push((doc.Trailer["Info"], "Content", 0));
            pending.Push((doc.Trailer["Encrypt"], "Content", 0));
            while (pending.TryPop(out var item))
            {
                if (--remaining < 0 || item.Depth > 64)
                {
                    return false;
                }
                void Push(CosObject? v, string role) => pending.Push((v, role, item.Depth + 1));
                if (item.Value is CosReference r)
                {
                    // Destinations and link annotations also refer to pages. A page retains all
                    // properties except the checked /Annots append, regardless of that route.
                    var effectiveRole = item.Role == "Content" && Same(r, doc.Trailer["Root"]) ? "Catalog"
                        : item.Role == "Content" && pages.Contains(r.Id) ? "Pages" : item.Role;
                    if (effectiveRole == "Content")
                    {
                        content.Add(r.Id.Number); // Generational aliases cannot hide shared content.
                    }
                    if (visited.Add((r.Id, effectiveRole)))
                    {
                        var role = effectiveRole == "Field" && (signedFields.Contains(r.Id.Number) || widgets.Contains(r.Id.Number)) ? "SignedField"
                            : effectiveRole == "Annotation" && widgets.Contains(r.Id.Number) ? "SignedField" : effectiveRole;
                        Push(doc.GetObject(r.Id), role);
                    }
                }
                else if (item.Value is CosArray a)
                {
                    foreach (var v in a.Items)
                    {
                        Push(v, item.Role == "Fields" ? "Field" : item.Role == "Annots" ? "Annotation"
                            : item.Role == "Pages" ? "Pages" : "Content");
                    }
                }
                else if (item.Value is CosDictionary d)
                {
                    foreach (var (key, v) in d.Entries)
                    {
                        var role = (item.Role, key) switch
                        {
                            ("Catalog", "DSS") or ("Form", "SigFlags") => "Skip",
                            ("Catalog", "AcroForm") => "Form",
                            ("Catalog", "Pages") or ("Pages", "Kids") => "Pages",
                            ("Pages", "Parent") => "Skip",
                            ("Pages", "Annots") => "Annots",
                            ("Form", "Fields") => "Fields",
                            ("SignedField", "V" or "AP") => "Skip",
                            ("SignedField" or "Field" or "Annotation", "P" or "Parent") => "Skip",
                            ("SignedField" or "Field", "Kids") => "Fields",
                            _ => "Content",
                        };
                        if (role != "Skip")
                        {
                            Push(v, role);
                        }
                    }
                }
                else if (item.Value is CosStream stream)
                {
                    Push(stream.Dictionary, "Content");
                }
            }
            return true;
        }
    }

    private static bool SameExcept(CosDictionary a, CosDictionary b, params string[] keys)
    {
        var x = a.Clone();
        var y = b.Clone();
        foreach (var key in keys)
        {
            x[key] = null;
            y[key] = null;
        }
        return Same(x, y);
    }

    // Check each xref revision, so changing content and restoring it in a later signing revision
    // cannot erase the warning. Select older xrefs explicitly without scanning arbitrary streams.
    private static (bool ValidationOnly, bool Allowed) LaterChanges(CosDocument document, CosDocument signed)
    {
        var current = document;
        var validationOnly = true;
        var remainingBytes = 256L * 1024 * 1024;
        for (var revision = 0; revision < 128; revision++)
        {
            if (current.WasReconstructed || signed.WasReconstructed
                || current.Trailer["Prev"] is not CosInteger previous
                || previous.Value < signed.StartXref || previous.Value >= current.StartXref)
            {
                return (false, false);
            }
            CosDocument before;
            if (previous.Value == signed.StartXref)
            {
                before = signed;
            }
            else
            {
                remainingBytes -= current.StartXref;
                if (remainingBytes < 0)
                {
                    return (false, false);
                }
                using var buffer = new MemoryStream();
                buffer.Write(document.Data.AsSpan(0, checked((int)current.StartXref)));
                buffer.Write(System.Text.Encoding.ASCII.GetBytes($"\nstartxref\n{previous.Value}\n%%EOF\n"));
                try
                {
                    before = CosDocument.Load(buffer.ToArray());
                }
                catch (PdfException)
                {
                    return (false, false);
                }
                if (before.WasReconstructed || before.StartXref != previous.Value)
                {
                    return (false, false);
                }
            }

            var validation = OnlyValidationDataAfter(current, before);
            validationOnly &= validation;
            if (!validation && !OnlySigningDataAfter(current, before))
            {
                return (false, false);
            }
            if (before == signed)
            {
                return (validationOnly, true);
            }
            current = before;
        }
        return (false, false);
    }

    private static bool CollectContent(CosDocument document, HashSet<int> content)
    {
        var visited = new HashSet<CosObjectId>();
        var catalog = document.Catalog.Clone();
        catalog["DSS"] = null;
        var pending = new Stack<(CosObject? Value, int Depth)>();
        pending.Push((catalog, 0));
        pending.Push((document.Trailer["Info"], 0));
        pending.Push((document.Trailer["Encrypt"], 0));
        var budget = 100000;
        while (pending.TryPop(out var item))
        {
            if (--budget < 0 || item.Depth > 64)
            {
                return false;
            }

            switch (item.Value)
            {
                case CosReference reference when visited.Add(reference.Id):
                    // CosDocument tolerates incorrect generations; aliases still identify content.
                    content.Add(reference.Id.Number);
                    // A back-reference to the catalog must not reintroduce /DSS.
                    pending.Push((Same(reference, document.Trailer["Root"]) ? catalog : document.GetObject(reference.Id), item.Depth + 1));
                    break;
                case CosDictionary dictionary:
                    foreach (var value in dictionary.Entries.Values)
                    {
                        pending.Push((value, item.Depth + 1));
                    }
                    break;
                case CosArray array:
                    foreach (var value in array.Items)
                    {
                        pending.Push((value, item.Depth + 1));
                    }
                    break;
                case CosStream stream:
                    pending.Push((stream.Dictionary, item.Depth + 1));
                    break;
            }
        }

        return true;
    }

    private static bool Same(CosObject? a, CosObject? b) => (a, b) switch
    {
        (null or CosNull, null or CosNull) => true,
        (CosBoolean x, CosBoolean y) => x.Value == y.Value,
        (CosInteger x, CosInteger y) => x.Value == y.Value,
        (CosReal x, CosReal y) => x.Value.Equals(y.Value),
        (CosInteger x, CosReal y) => x.Value == y.Value,
        (CosReal x, CosInteger y) => x.Value == y.Value,
        (CosName x, CosName y) => x.Value == y.Value,
        (CosString x, CosString y) => x.Bytes.AsSpan().SequenceEqual(y.Bytes),
        (CosReference x, CosReference y) => x.Id == y.Id,
        (CosArray x, CosArray y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Same(p.First, p.Second)),
        (CosDictionary x, CosDictionary y) => x.Entries.Count == y.Entries.Count
            && x.Entries.All(e => y.Entries.TryGetValue(e.Key, out var other) && Same(e.Value, other)),
        (CosStream x, CosStream y) => Same(x.Dictionary, y.Dictionary) && x.Data.Span.SequenceEqual(y.Data.Span),
        _ => false,
    };

    private static PdfSignatureInfo Check(CosDocument document, byte[] file, string fieldName, CosDictionary field, CosDictionary signature, PdfVerifyOptions options)
    {
        var subFilter = (document.Resolve(signature["SubFilter"]) as CosName)?.Value ?? string.Empty;
        var reason = (document.Resolve(signature["Reason"]) as CosString)?.ToText();
        var location = (document.Resolve(signature["Location"]) as CosString)?.ToText();
        var claimedTime = (document.Resolve(signature["M"]) as CosString)?.ToDate();
        var claimedName = (document.Resolve(signature["Name"]) as CosString)?.ToText();
        var pageIndex = FindPage(document, field);
        var certification = PdfCertification.None;
        var isCertification = false;
        var modifiedAfterSigning = true;

        PdfSignatureInfo Result(PdfSignatureStatus status, string? problem, long signedLength = 0, bool covers = false) => new(
            fieldName, pageIndex, claimedName ?? string.Empty, null, claimedTime, false, reason, location, subFilter,
            status, false, PdfSignerIdentity.Unknown, false, covers, isCertification, signedLength, problem)
        { Certification = certification, ModifiedAfterSigning = modifiedAfterSigning };

        // /ByteRange must cover the whole revision except exactly the /Contents hex string.
        var range = (document.Resolve(signature["ByteRange"]) as CosArray)?.Items.Select(i => (document.Resolve(i) as CosInteger)?.Value ?? -1).ToArray();
        if (range is not { Length: 4 } || range[0] != 0 || range[1] <= 0 || range[2] <= range[1] || range[3] < 0 || range[2] + range[3] > file.Length
            || file[range[1]] != '<' || file[range[2] - 1] != '>')
        {
            return Result(PdfSignatureStatus.Invalid, "The signature's byte range is invalid.");
        }

        var signedLength = range[2] + range[3];
        CosDocument signed;
        try
        {
            signed = CosDocument.Load(file.AsSpan(0, (int)signedLength).ToArray());
        }
        catch (PdfException)
        {
            return Result(PdfSignatureStatus.Invalid, "The signed revision could not be read.", signedLength);
        }

        var signedField = FindSignatureFields(signed).FirstOrDefault(f => f.Name == fieldName).Field;
        if (signedField is null || signed.Resolve(signedField["V"]) is not CosDictionary signedSignature
            || !Same(signedSignature["ByteRange"], signature["ByteRange"]))
        {
            return Result(PdfSignatureStatus.Invalid, "The signature is missing from the signed revision.", signedLength);
        }

        if (signed.Resolve(signedSignature["Reference"]) is CosArray references)
        {
            foreach (var reference in references.Items.Select(signed.Resolve).OfType<CosDictionary>())
            {
                if ((signed.Resolve(reference["TransformMethod"]) as CosName)?.Value == "DocMDP")
                {
                    isCertification = true;
                    if (signed.Resolve(reference["TransformParams"]) is CosDictionary parameters
                        && signed.Resolve(parameters["P"]) is CosInteger { Value: >= 1 and <= 3 } permission)
                    {
                        certification = (PdfCertification)permission.Value;
                    }
                }
            }
        }

        var noLaterRevision = file.AsSpan((int)signedLength).Trim("\r\n\0 \t"u8).IsEmpty;
        var changes = noLaterRevision ? (ValidationOnly: true, Allowed: true) : LaterChanges(document, signed);
        var covers = changes.ValidationOnly;
        var forbiddenChange = !covers && certification == PdfCertification.NoChanges;
        modifiedAfterSigning = !changes.Allowed || forbiddenChange;
        if (document.Resolve(signature["Contents"]) is not CosString { Bytes.Length: > 0 } contents)
        {
            return Result(PdfSignatureStatus.Invalid, "The signature has no contents.", signedLength, covers);
        }

        var signedData = new byte[range[1] + range[3]];
        file.AsSpan(0, (int)range[1]).CopyTo(signedData);
        file.AsSpan((int)range[2], (int)range[3]).CopyTo(signedData.AsSpan((int)range[1]));

        var detached = subFilter is "adbe.pkcs7.detached" or "ETSI.CAdES.detached";
        if (!detached && subFilter != "adbe.pkcs7.sha1")
        {
            return Result(PdfSignatureStatus.Unsupported, $"The signature format {subFilter} is not supported.", signedLength, covers);
        }

        SignedCms cms;
        try
        {
            cms = detached ? new SignedCms(new ContentInfo(signedData), detached: true) : new SignedCms();
            cms.Decode(TrimDer(contents.Bytes));
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException)
        {
            return Result(PdfSignatureStatus.Invalid, "The signature data is damaged.", signedLength, covers);
        }

        if (cms.SignerInfos.Count == 0)
        {
            return Result(PdfSignatureStatus.Invalid, "The signature has no signer.", signedLength, covers);
        }

        var signerInfo = cms.SignerInfos[0];
        var certificate = signerInfo.Certificate;
        var signerName = certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? claimedName ?? string.Empty;

        string? problem = null;
        var intact = true;
        try
        {
            signerInfo.CheckSignature(verifySignatureOnly: true);
            if (!detached && !cms.ContentInfo.Content.AsSpan().SequenceEqual(SHA1.HashData(signedData)))
            {
                intact = false;
            }
        }
        catch (CryptographicException)
        {
            intact = false;
        }

        if (!intact)
        {
            problem = "The document has been altered or corrupted since it was signed.";
        }
        else if (subFilter == "ETSI.CAdES.detached" && certificate is not null && !SigningCertificateMatches(signerInfo, certificate))
        {
            intact = false;
            problem = "The signer's certificate does not match the signed certificate reference.";
        }

        // A valid timestamp gives a trusted signing time; otherwise the time is the signer's claim.
        var (timestampTime, isTimestamped) = ReadTimestamp(signerInfo, options);
        var signingTime = timestampTime ?? ReadSigningTimeAttribute(signerInfo) ?? claimedTime;

        var identity = PdfSignerIdentity.Unknown;
        var revocationChecked = false;
        if (certificate is not null)
        {
            (identity, revocationChecked) = CheckIdentity(certificate, cms.Certificates, timestampTime, options);
        }

        var status = !intact || identity == PdfSignerIdentity.Revoked || forbiddenChange
            ? PdfSignatureStatus.Invalid
            : modifiedAfterSigning ? PdfSignatureStatus.Modified
            : identity == PdfSignerIdentity.Trusted ? PdfSignatureStatus.Valid : PdfSignatureStatus.IdentityUnknown;
        if (problem is null && identity == PdfSignerIdentity.Revoked)
        {
            problem = "The signer's certificate has been revoked.";
        }
        if (problem is null && forbiddenChange)
        {
            problem = "The document was changed after certification, which allows no changes.";
        }

        return new PdfSignatureInfo(
            fieldName, pageIndex, signerName, certificate, signingTime, isTimestamped, reason, location, subFilter,
            status, intact, identity, revocationChecked, covers, isCertification, signedLength, problem)
        { Certification = certification, ModifiedAfterSigning = modifiedAfterSigning };
    }

    private static (PdfSignerIdentity Identity, bool RevocationChecked) CheckIdentity(
        X509Certificate2 certificate,
        X509Certificate2Collection extra,
        DateTimeOffset? trustedTime,
        PdfVerifyOptions options)
    {
        using var chain = new X509Chain();
        if (options.CustomTrustRoots is { } roots)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        }
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        chain.ChainPolicy.RevocationMode = options.CheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.UrlRetrievalTimeout = options.NetworkTimeout ?? TimeSpan.FromSeconds(10);
        if (trustedTime is not null)
        {
            // With a timestamp the certificate only had to be valid when the document was signed.
            chain.ChainPolicy.VerificationTime = trustedTime.Value.UtcDateTime;
        }

        chain.Build(certificate);
        var flags = chain.ChainElements.SelectMany(e => e.ChainElementStatus).Select(s => s.Status)
            .Concat(chain.ChainStatus.Select(s => s.Status))
            .Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s);

        const X509ChainStatusFlags revocationUnknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        var revocationChecked = options.CheckRevocation && (flags & revocationUnknown) == 0;
        var identity = flags switch
        {
            _ when flags.HasFlag(X509ChainStatusFlags.Revoked) => PdfSignerIdentity.Revoked,
            _ when (flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0 => PdfSignerIdentity.Untrusted,
            _ when flags.HasFlag(X509ChainStatusFlags.NotTimeValid) => PdfSignerIdentity.Expired,
            _ when (flags & ~revocationUnknown) == 0 => PdfSignerIdentity.Trusted,
            _ => PdfSignerIdentity.Untrusted,
        };
        return (identity, revocationChecked);
    }

    private static (DateTimeOffset? Time, bool IsTimestamped) ReadTimestamp(SignerInfo signerInfo, PdfVerifyOptions options)
    {
        foreach (var attribute in signerInfo.UnsignedAttributes)
        {
            if (attribute.Oid.Value != CadesSignature.SignatureTimeStampToken)
            {
                continue;
            }

            foreach (var value in attribute.Values)
            {
                try
                {
                    if (!Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _)
                        || !token.VerifySignatureForSignerInfo(signerInfo, out var tsa) || tsa is null
                        || !tsa.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e =>
                            e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.8")))
                    {
                        continue;
                    }

                    using var chain = new X509Chain();
                    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.8"));
                    chain.ChainPolicy.ExtraStore.AddRange(token.AsSignedCms().Certificates);
                    chain.ChainPolicy.VerificationTime = token.TokenInfo.Timestamp.UtcDateTime;
                    chain.ChainPolicy.RevocationMode = options.CheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                    chain.ChainPolicy.DisableCertificateDownloads = !options.CheckRevocation;
                    chain.ChainPolicy.UrlRetrievalTimeout = options.NetworkTimeout ?? TimeSpan.FromSeconds(10);
                    if (chain.Build(tsa))
                    {
                        return (token.TokenInfo.Timestamp, true);
                    }
                }
                catch (Exception ex) when (ex is CryptographicException or AsnContentException)
                {
                    // An untrusted or malformed token supplies no trusted signing time.
                }
            }
        }

        return (null, false);
    }

    private static DateTimeOffset? ReadSigningTimeAttribute(SignerInfo signerInfo)
    {
        foreach (var attribute in signerInfo.SignedAttributes)
        {
            foreach (var value in attribute.Values)
            {
                if (value is Pkcs9SigningTime time)
                {
                    return new DateTimeOffset(time.SigningTime);
                }
            }
        }

        return null;
    }

    /// <summary>Checks the ESS signing-certificate(-v2) attribute, when present, against the signer's certificate.</summary>
    private static bool SigningCertificateMatches(SignerInfo signerInfo, X509Certificate2 certificate)
    {
        foreach (var attribute in signerInfo.SignedAttributes)
        {
            if (attribute.Oid.Value != CadesSignature.SigningCertificateV2)
            {
                continue;
            }

            try
            {
                var reader = new AsnReader(attribute.Values[0].RawData, AsnEncodingRules.DER);
                var certIds = reader.ReadSequence().ReadSequence();
                var first = certIds.ReadSequence();
                var algorithm = HashAlgorithmName.SHA256;
                if (first.PeekTag().TagValue == (int)UniversalTagNumber.Sequence)
                {
                    algorithm = first.ReadSequence().ReadObjectIdentifier() switch
                    {
                        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
                        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
                        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
                        _ => HashAlgorithmName.SHA256,
                    };
                }

                var expected = first.ReadOctetString();
                using var hash = IncrementalHash.CreateHash(algorithm);
                hash.AppendData(certificate.RawData);
                return hash.GetHashAndReset().AsSpan().SequenceEqual(expected);
            }
            catch (AsnContentException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>/Contents is zero-padded; cut it to the length of the DER structure.</summary>
    private static byte[] TrimDer(byte[] data)
    {
        try
        {
            AsnDecoder.ReadEncodedValue(data, AsnEncodingRules.BER, out _, out _, out var consumed);
            return data.AsSpan(0, consumed).ToArray();
        }
        catch (AsnContentException)
        {
            return data;
        }
    }

    private static IEnumerable<(string Name, CosDictionary Field)> FindSignatureFields(CosDocument document)
    {
        if (document.Resolve(document.Catalog["AcroForm"]) is not CosDictionary acroForm
            || document.Resolve(acroForm["Fields"]) is not CosArray fields)
        {
            yield break;
        }

        var visited = new HashSet<CosObjectId>();
        var stack = new Stack<(CosObject Node, string Parent, string? FieldType, int Depth)>();
        foreach (var field in fields.Items.AsEnumerable().Reverse())
        {
            stack.Push((field, string.Empty, null, 0));
        }

        while (stack.Count > 0)
        {
            var (node, parent, inheritedType, depth) = stack.Pop();
            if (depth > MaxFieldDepth || (node is CosReference r && !visited.Add(r.Id)) || document.Resolve(node) is not CosDictionary dictionary)
            {
                continue;
            }

            var partial = (document.Resolve(dictionary["T"]) as CosString)?.ToText();
            var name = partial is null ? parent : parent.Length == 0 ? partial : parent + "." + partial;
            var type = (document.Resolve(dictionary["FT"]) as CosName)?.Value ?? inheritedType;
            if (type == "Sig" && dictionary["V"] is not null)
            {
                yield return (name, dictionary);
                continue;
            }

            if (document.Resolve(dictionary["Kids"]) is CosArray kids)
            {
                foreach (var kid in kids.Items.AsEnumerable().Reverse())
                {
                    stack.Push((kid, name, type, depth + 1));
                }
            }
        }
    }

    private static int? FindPage(CosDocument document, CosDictionary field)
    {
        var pages = document.GetPages();
        if (field["P"] is CosReference pageRef)
        {
            var index = pages.ToList().FindIndex(p => p.Id == pageRef.Id);
            if (index >= 0)
            {
                return index;
            }
        }

        // Fall back to finding the widget (the field itself or its first kid) in the pages' /Annots.
        for (var i = 0; i < pages.Count; i++)
        {
            if (document.Resolve(pages[i].Dictionary["Annots"]) is CosArray annots
                && annots.Items.Any(a => ReferenceEquals(document.Resolve(a), field)))
            {
                return i;
            }
        }

        return null;
    }
}
