using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yrk.PdfStudio.Core.Pdf.Cos;
using Yrk.PdfStudio.Core.Pdf.Signatures;
using Yrk.PdfStudio.Core.Pdf.Text;

namespace Yrk.PdfStudio.Core.Pdf.Forms;

/// <summary>Text shaped by the caller, with glyph positions in points from the text's top-left (y down).</summary>
public sealed record PdfShapedText(IReadOnlyList<PdfGlyphRun> Runs, double Width, double Height);

/// <summary>
/// How one filled-in widget should look: its text in the chosen font and size, or the picture of an image
/// field (a push button, as Acrobat makes them). <see cref="AnnotIndex"/> is the widget's place in the page's
/// /Annots, as PDFium numbers annotations. <see cref="RemovePicture"/> empties an image field again.
/// </summary>
public sealed record PdfFieldLook(int PageIndex, int AnnotIndex, double FontSize, PdfShapedText? Text = null, PdfImage? Image = null, bool Multiline = false, PdfColor? Color = null,
    bool RemovePicture = false);

/// <summary>
/// Writes widget appearances ourselves, as an incremental update. PDFium draws field text with a stand-in
/// font that cannot shape Thai and ignores the size people choose; here the text is the app's shaped text in
/// an embedded font subset, and image fields get the chosen picture (also set as the button's icon, so
/// Acrobat keeps it). The field's /DA font size is updated too, for viewers that redraw fields themselves; with
/// a <see cref="PdfFormFont"/>, fields holding Thai also name that font there, and the form gets it once.
/// </summary>
public static partial class PdfFieldAppearance
{
    /// <summary>Space between the widget border and its text, in points (as Acrobat leaves it).</summary>
    private const double Inset = 2;

    /// <param name="thaiFont">A whole Thai font for viewers that redraw Thai fields; null to leave /DA fonts as they are.</param>
    public static byte[] Apply(byte[] pdf, IReadOnlyList<PdfFieldLook> looks, PdfFormFont? thaiFont = null)
    {
        if (looks.Count == 0)
        {
            return pdf;
        }

        var document = CosDocument.Load(pdf);
        if (document.IsEncrypted)
        {
            throw new PdfException("Password-protected PDFs cannot be edited yet. Save an unprotected copy first.");
        }

        var update = new CosIncrementalUpdate(document);
        var pages = document.GetPages();

        var thaiFields = false;

        // One embedded subset per font, holding the glyphs of every field that uses it.
        var fonts = new Dictionary<PdfEmbeddedFont, PdfTextWriter.FontObject>(ReferenceEqualityComparer.Instance);
        var runs = looks.Where(l => l.Text is not null).SelectMany(l => l.Text!.Runs).ToList();
        foreach (var font in runs.Select(r => r.Font).Distinct(ReferenceEqualityComparer.Instance).Cast<PdfEmbeddedFont>())
        {
            var glyphs = runs.Where(r => ReferenceEquals(r.Font, font)).SelectMany(r => r.Glyphs).Where(g => g.Text.Length > 0);
            fonts[font] = PdfTextWriter.AddFont(update, font, glyphs, fonts.Count);
        }

        foreach (var look in looks)
        {
            if (look.PageIndex < 0 || look.PageIndex >= pages.Count
                || document.Resolve(pages[look.PageIndex].Dictionary["Annots"]) is not CosArray annots
                || look.AnnotIndex < 0 || look.AnnotIndex >= annots.Items.Count
                || annots.Items[look.AnnotIndex] is not CosReference widgetRef
                || document.Resolve(widgetRef) is not CosDictionary widget
                || document.Resolve(widget["Rect"]) is not CosArray rect || rect.Items.Count < 4)
            {
                continue; // The widget is not where it was: leave the field as PDFium drew it.
            }

            var r = rect.Items.Select(i => Number(document.Resolve(i))).ToArray();
            var width = Math.Abs(r[2] - r[0]);
            var height = Math.Abs(r[3] - r[1]);
            if (width < 1 || height < 1)
            {
                continue;
            }

            // A widget turned with /MK /R draws its content upright in the turned box: swap the sides and
            // give the appearance the matching rotation (viewers fit the turned box to /Rect).
            var rotation = WidgetRotation(document, widget);
            if (rotation is 90 or 270)
            {
                (width, height) = (height, width);
            }

            var changed = widget.Clone();
            var resources = new CosDictionary();
            var content = new StringBuilder();
            if (look.Image is { } image)
            {
                var imageRef = update.Add(PdfSigner.BuildImage(update, image));
                resources.Set("XObject", new CosDictionary().Set("Im0", imageRef));

                // The picture fits inside the box and keeps its proportions, centred like Acrobat does.
                var scale = Math.Min((width - (2 * Inset)) / image.Width, (height - (2 * Inset)) / image.Height);
                double w = image.Width * scale, h = image.Height * scale;
                content.Append(CultureInfo.InvariantCulture, $"q {F(w)} 0 0 {F(h)} {F((width - w) / 2)} {F((height - h) / 2)} cm /Im0 Do Q\n");
            }
            else if (look.Text is { } text)
            {
                var names = new Dictionary<PdfEmbeddedFont, string>(ReferenceEqualityComparer.Instance);
                var fontResources = new CosDictionary();
                (string, Dictionary<(ushort, string), int>) FontOf(PdfEmbeddedFont font)
                {
                    if (!names.TryGetValue(font, out var name))
                    {
                        name = "YrkF" + names.Count.ToString(CultureInfo.InvariantCulture);
                        names[font] = name;
                        fontResources.Set(name, fonts[font].Reference);
                    }

                    return (name, fonts[font].Codes);
                }

                // Single-line text sits in the middle of the box, aligned by the field's /Q; multi-line from the top.
                var quadding = Quadding(document, widget);
                var room = width - (2 * Inset);
                var left = Inset + (quadding switch { 1 => (room - text.Width) / 2, 2 => room - text.Width, _ => 0 });
                var top = look.Multiline ? Inset : (height - text.Height) / 2;
                content.Append(CultureInfo.InvariantCulture, $"/Tx BMC q {F(1)} {F(1)} {F(width - 2)} {F(height - 2)} re W n\n");
                PdfTextWriter.WriteRuns(content, text.Runs, look.Color ?? new PdfColor(0, 0, 0), (x, y) => (left + x, height - (top + y)), FontOf);
                content.Append("Q EMC\n");
                resources.Set("Font", fontResources);
                var da = WithFontSize(DefaultAppearance(document, widget), look.FontSize);
                if (thaiFont is not null && text.Runs.Any(r => r.Glyphs.Any(g => g.Text.Any(IsThai))))
                {
                    da = WithFontName(da, PdfFormFont.ResourceName);
                    thaiFields = true;
                }

                changed["DA"] = new CosString(Encoding.Latin1.GetBytes(look.Color is { } color ? WithColor(da, color) : da));
            }
            else if (!look.RemovePicture)
            {
                continue;
            }

            var formDictionary = new CosDictionary()
                .Set("Type", new CosName("XObject"))
                .Set("Subtype", new CosName("Form"))
                .Set("BBox", CosArray.FromNumbers(0, 0, width, height))
                .Set("Resources", resources);
            if (rotation is 90 or 180 or 270)
            {
                formDictionary["Matrix"] = rotation switch
                {
                    90 => CosArray.FromNumbers(0, 1, -1, 0, 0, 0),
                    180 => CosArray.FromNumbers(-1, 0, 0, -1, 0, 0),
                    _ => CosArray.FromNumbers(0, -1, 1, 0, 0, 0),
                };
            }

            var form = update.Add(new CosStream(formDictionary, Encoding.Latin1.GetBytes(content.ToString())));
            changed["AP"] = new CosDictionary().Set("N", form);

            if (look.Image is not null)
            {
                // The icon is what Acrobat redraws the button from; "icon only" keeps a caption off it.
                var mk = (document.Resolve(widget["MK"]) as CosDictionary)?.Clone() ?? new CosDictionary();
                changed["MK"] = mk.Set("I", form).Set("TP", new CosInteger(1));
            }
            else if (look.RemovePicture && document.Resolve(widget["MK"]) is CosDictionary oldMk)
            {
                // An empty appearance, and no icon for viewers that redraw the button.
                var mk = oldMk.Clone();
                mk["I"] = null;
                mk["RI"] = null;
                mk["IX"] = null;
                changed["MK"] = mk;
            }

            update.Replace(widgetRef.Id, changed);
        }

        if (thaiFields)
        {
            AddFormFont(document, update, thaiFont!);
        }

        return update.Build().Bytes;
    }

    /// <summary>Puts the Thai font in the form's default resources (/DR /Font), once.</summary>
    private static void AddFormFont(CosDocument document, CosIncrementalUpdate update, PdfFormFont font)
    {
        var catalog = document.Catalog;
        var formValue = catalog["AcroForm"];
        if (document.Resolve(formValue) is not CosDictionary existing)
        {
            return; // Fields without a form dictionary: nothing names a default font.
        }

        var form = existing.Clone();
        var resources = (document.Resolve(form["DR"]) as CosDictionary)?.Clone() ?? new CosDictionary();
        var fonts = (document.Resolve(resources["Font"]) as CosDictionary)?.Clone() ?? new CosDictionary();
        if (fonts[PdfFormFont.ResourceName] is not null)
        {
            return; // Saved before with this font.
        }

        fonts[PdfFormFont.ResourceName] = font.AddTo(update);
        resources["Font"] = fonts;
        form["DR"] = resources;
        if (formValue is CosReference formRef)
        {
            update.Replace(formRef.Id, form);
        }
        else if (document.Trailer["Root"] is CosReference root)
        {
            update.Replace(root.Id, catalog.Clone().Set("AcroForm", update.Add(form)));
        }
    }

    private static bool IsThai(char c) => c is >= '฀' and <= '๿';

    /// <summary>
    /// How far the text of each turned widget runs from left-to-right as the page is shown, in degrees
    /// anticlockwise (90: reads upwards), keyed by page and annotation index. Widgets whose text runs normally
    /// are left out. Combines the widget's /MK /R with the page's /Rotate.
    /// </summary>
    public static IReadOnlyDictionary<(int PageIndex, int AnnotIndex), int> TextRotations(byte[] pdf)
    {
        var rotations = new Dictionary<(int, int), int>();
        CosDocument document;
        try
        {
            document = CosDocument.Load(pdf);
        }
        catch (Exception ex) when (ex is PdfException or FormatException or InvalidDataException)
        {
            return rotations;
        }

        if (document.IsEncrypted)
        {
            return rotations;
        }

        var pages = document.GetPages();
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            if (document.Resolve(pages[pageIndex].Dictionary["Annots"]) is not CosArray annots)
            {
                continue;
            }

            for (var annotIndex = 0; annotIndex < annots.Items.Count; annotIndex++)
            {
                if (document.Resolve(annots.Items[annotIndex]) is CosDictionary widget
                    && ((WidgetRotation(document, widget) - pages[pageIndex].Rotate) % 360 + 360) % 360 is var shown and not 0)
                {
                    rotations[(pageIndex, annotIndex)] = shown;
                }
            }
        }

        return rotations;
    }

    /// <summary>The widget's /MK /R: how far its content is turned anticlockwise (0, 90, 180 or 270).</summary>
    private static int WidgetRotation(CosDocument document, CosDictionary widget) =>
        document.Resolve(widget["MK"]) is CosDictionary mk && document.Resolve(mk["R"]) is CosInteger turn
            ? (int)(((turn.Value % 360) + 360) % 360)
            : 0;

    /// <summary>The widget's default appearance string, inherited from its field or the form if need be.</summary>
    private static string DefaultAppearance(CosDocument document, CosDictionary widget)
    {
        var current = widget;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (document.Resolve(current["DA"]) is CosString da)
            {
                return Encoding.Latin1.GetString(da.Bytes);
            }

            current = document.Resolve(current["Parent"]) as CosDictionary;
        }

        return document.Resolve(document.Catalog["AcroForm"]) is CosDictionary form && document.Resolve(form["DA"]) is CosString formDa
            ? Encoding.Latin1.GetString(formDa.Bytes)
            : "/Helv 0 Tf 0 g";
    }

    private static int Quadding(CosDocument document, CosDictionary widget)
    {
        var current = widget;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (document.Resolve(current["Q"]) is CosInteger q)
            {
                return (int)q.Value;
            }

            current = document.Resolve(current["Parent"]) as CosDictionary;
        }

        return 0;
    }

    /// <summary>"/Helv 12 Tf 0 g" with another size; a string without a font gets Helvetica.</summary>
    internal static string WithFontSize(string da, double size)
    {
        var sized = F(Math.Round(size, 2));
        return FontOperator().IsMatch(da)
            ? FontOperator().Replace(da, m => $"{m.Groups[1].Value} {sized} Tf", 1)
            : $"/Helv {sized} Tf {da}".Trim();
    }

    [GeneratedRegex(@"(/\S+)\s+[-+]?\d*\.?\d+\s+Tf")]
    private static partial Regex FontOperator();

    /// <summary>"/Helv 12 Tf 0 g" with the font named <paramref name="name"/> instead.</summary>
    internal static string WithFontName(string da, string name) =>
        FontOperator().IsMatch(da)
            ? FontOperator().Replace(da, m => "/" + name + m.Value[m.Groups[1].Length..], 1)
            : $"/{name} 0 Tf {da}".Trim();

    /// <summary>"/Helv 12 Tf 0 g" with its text colour (a g, rg or k operator) replaced by <paramref name="color"/>.</summary>
    internal static string WithColor(string da, PdfColor color) =>
        $"{ColorOperator().Replace(da, string.Empty).Trim()} {F(color.R / 255.0)} {F(color.G / 255.0)} {F(color.B / 255.0)} rg".Trim();

    [GeneratedRegex(@"(?:[-+]?\d*\.?\d+\s+){1,4}(?:g|rg|k)(?=\s|$)")]
    private static partial Regex ColorOperator();

    private static double Number(CosObject? value) => value switch
    {
        CosInteger i => i.Value,
        CosReal r => r.Value,
        _ => 0,
    };

    private static string F(double value) => CosWriter.FormatReal(value);
}
