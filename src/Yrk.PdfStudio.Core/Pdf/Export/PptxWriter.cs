using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Yrk.PdfStudio.Core.Pdf.Export;

/// <summary>A page as a picture for a slide (JPEG), with the page's size in points.</summary>
public sealed record PptxSlide(byte[] Jpeg, double Width, double Height);

/// <summary>
/// Writes a PowerPoint presentation (.pptx) with one slide per PDF page: the page as a picture, exactly as it
/// looks. The slides take the first page's size; other pages are fitted in, centred. No library: the package is
/// a ZIP of a few XML parts.
/// </summary>
public static class PptxWriter
{
    private const string PresentationNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const long EmuPerPoint = 12700;

    public static void Write(Stream output, IReadOnlyList<PptxSlide> slides)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // PowerPoint accepts slides from 1 to 56 inches a side.
        var first = slides.Count > 0 ? slides[0] : new PptxSlide([], 720, 540);
        long cx = Math.Clamp((long)(first.Width * EmuPerPoint), 914400, 51206400);
        long cy = Math.Clamp((long)(first.Height * EmuPerPoint), 914400, 51206400);

        var types = new StringBuilder();
        var presentationRels = new StringBuilder($"<Relationship Id=\"rId1\" Type=\"{RelNs}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>");
        var slideIds = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            var n = i + 1;
            types.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/ppt/slides/slide{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
            presentationRels.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{n + 2}\" Type=\"{RelNs}/slide\" Target=\"slides/slide{n}.xml\"/>");
            slideIds.Append(CultureInfo.InvariantCulture, $"<p:sldId id=\"{255 + n}\" r:id=\"rId{n + 2}\"/>");
            OfficeXml.AddPart(zip, $"ppt/media/image{n}.jpeg", slides[i].Jpeg);
            OfficeXml.AddPart(zip, $"ppt/slides/slide{n}.xml", Slide(slides[i], n, cx, cy));
            OfficeXml.AddPart(zip, $"ppt/slides/_rels/slide{n}.xml.rels", Relationships(
                $"<Relationship Id=\"rId1\" Type=\"{RelNs}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                $"<Relationship Id=\"rId2\" Type=\"{RelNs}/image\" Target=\"../media/image{n}.jpeg\"/>"));
        }

        presentationRels.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId2\" Type=\"{RelNs}/theme\" Target=\"theme/theme1.xml\"/>");

        OfficeXml.AddPart(zip, "[Content_Types].xml", OfficeXml.Header +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" +
            types + "</Types>");
        OfficeXml.AddPart(zip, "_rels/.rels", Relationships(
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"ppt/presentation.xml\"/>"));
        OfficeXml.AddPart(zip, "ppt/presentation.xml", OfficeXml.Header +
            $"<p:presentation xmlns:a=\"{DrawingNs}\" xmlns:r=\"{RelNs}\" xmlns:p=\"{PresentationNs}\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            (slides.Count > 0 ? $"<p:sldIdLst>{slideIds}</p:sldIdLst>" : string.Empty) +
            FormattableString.Invariant($"<p:sldSz cx=\"{cx}\" cy=\"{cy}\"/><p:notesSz cx=\"{cy}\" cy=\"{cx}\"/>") +
            "</p:presentation>");
        OfficeXml.AddPart(zip, "ppt/_rels/presentation.xml.rels", Relationships(presentationRels.ToString()));

        OfficeXml.AddPart(zip, "ppt/slideMasters/slideMaster1.xml", OfficeXml.Header +
            $"<p:sldMaster xmlns:a=\"{DrawingNs}\" xmlns:r=\"{RelNs}\" xmlns:p=\"{PresentationNs}\">" +
            "<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>" + EmptyShapeTree + "</p:cSld>" +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
            "<p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>");
        OfficeXml.AddPart(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", Relationships(
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{RelNs}/theme\" Target=\"../theme/theme1.xml\"/>"));
        OfficeXml.AddPart(zip, "ppt/slideLayouts/slideLayout1.xml", OfficeXml.Header +
            $"<p:sldLayout xmlns:a=\"{DrawingNs}\" xmlns:r=\"{RelNs}\" xmlns:p=\"{PresentationNs}\" type=\"blank\" preserve=\"1\">" +
            "<p:cSld name=\"Blank\">" + EmptyShapeTree + "</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        OfficeXml.AddPart(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", Relationships(
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/>"));
        OfficeXml.AddPart(zip, "ppt/theme/theme1.xml", Theme);
    }

    private const string EmptyShapeTree =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree>";

    /// <summary>The page picture, as large as the slide allows and centred.</summary>
    private static string Slide(PptxSlide slide, int number, long cx, long cy)
    {
        var scale = Math.Min(cx / (slide.Width * EmuPerPoint), cy / (slide.Height * EmuPerPoint));
        long w = (long)(slide.Width * EmuPerPoint * scale), h = (long)(slide.Height * EmuPerPoint * scale);
        long x = (cx - w) / 2, y = (cy - h) / 2;
        return OfficeXml.Header +
            $"<p:sld xmlns:a=\"{DrawingNs}\" xmlns:r=\"{RelNs}\" xmlns:p=\"{PresentationNs}\"><p:cSld><p:spTree>" +
            "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
            "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
            FormattableString.Invariant($"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Page {number}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>") +
            "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
            FormattableString.Invariant($"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>") +
            "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private static string Relationships(string items) =>
        OfficeXml.Header + $"<Relationships xmlns=\"{PackageRelNs}\">{items}</Relationships>";

    /// <summary>A plain Office theme: PowerPoint needs one, though the slides only hold pictures.</summary>
    private static readonly string Theme = OfficeXml.Header +
        $"<a:theme xmlns:a=\"{DrawingNs}\" name=\"Office\"><a:themeElements>" +
        "<a:clrScheme name=\"Office\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2>" +
        "<a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
        "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Office\">" +
        "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
}
