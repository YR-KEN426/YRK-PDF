using Yrk.PdfStudio.Core.Pdf.Cad;
using Yrk.PdfStudio.Core.Pdf.Measure;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class SnapTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("yrk-snap-");

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Delete(recursive: true);
    }

    [Fact]
    public async Task Line_ends_and_middles_of_a_drawing_are_snap_points()
    {
        // An L: a 30 m line along x and a 10 m line up from its start.
        var dxf = Path.Combine(_folder.FullName, "plan.dxf");
        var drawing = new ACadSharp.CadDocument();
        drawing.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        drawing.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(30000, 0, 0)));
        drawing.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(0, 10000, 0)));
        ACadSharp.IO.DxfWriter.Write(dxf, drawing);
        var path = Path.Combine(_folder.FullName, "plan.pdf");
        await File.WriteAllBytesAsync(path, CadToPdf.Convert(dxf).Pdf);

        await using var document = await _engine.OpenAsync(path);
        var points = await document.GetSnapPointsAsync(0);
        var ends = points.Where(p => p.Kind == PdfSnapKind.End).ToList();

        // The corner of the L is the left-most bottom end; the far end of the long line is to its right.
        var corner = ends.OrderBy(p => p.X).ThenByDescending(p => p.Y).First();
        var farEnd = ends.Where(p => Math.Abs(p.Y - corner.Y) < 1e-4).OrderByDescending(p => p.X).First();
        var top = ends.Where(p => Math.Abs(p.X - corner.X) < 1e-4).OrderBy(p => p.Y).First();
        Assert.True(farEnd.X > corner.X + 0.3);
        Assert.True(top.Y < corner.Y - 0.05); // Up on the page is a smaller y.
        Assert.Contains(points, p => p.Kind == PdfSnapKind.Middle
            && Math.Abs(p.X - ((corner.X + farEnd.X) / 2)) < 1e-4 && Math.Abs(p.Y - corner.Y) < 1e-4);

        // The index finds the corner from a few points away, and nothing far from any line.
        var size = document.PageSizes[0];
        var index = new PdfSnapIndex(points, size.Width, size.Height);
        var hit = index.Nearest(corner.X + (3 / size.Width), corner.Y - (2 / size.Height), radius: 6);
        Assert.Equal(corner, hit);
        Assert.Null(index.Nearest(corner.X + 0.15, corner.Y - 0.05, radius: 6));
    }
}
