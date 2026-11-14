using System.Globalization;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.App.Scanning;

public enum CropHandle { New, Move, NW, N, NE, E, SE, S, SW, W }
public enum CropAspect { Free, Paper, Original }
public readonly record struct CropBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Draft in source pixels. No controls or UI dependencies; committing is the caller's job.</summary>
public sealed class ScanCropFrame
{
    public double ImageWidth { get; }
    public double ImageHeight { get; }
    public NormalizedRect? Previous { get; }
    public CropBox Box { get; private set; }
    public CropBox? Detected { get; }
    public CropAspect Aspect { get; set; }
    public double PaperRatio { get; set; }
    private CropBox _start;
    private double _startX, _startY;
    private CropHandle _handle;
    private double MinWidth => Math.Min(3, ImageWidth);
    private double MinHeight => Math.Min(3, ImageHeight);

    public ScanCropFrame(double width, double height, NormalizedRect? previous, NormalizedRect? detected)
    {
        ImageWidth = Math.Max(1, width); ImageHeight = Math.Max(1, height); Previous = previous;
        CropBox Pixels(NormalizedRect r) => new(r.X * ImageWidth, r.Y * ImageHeight, r.Width * ImageWidth, r.Height * ImageHeight);
        Detected = detected is { } d ? Pixels(d) : null;
        Box = Clamp(previous is { } p ? Pixels(p) : Detected ?? new(0, 0, ImageWidth, ImageHeight));
    }
    public NormalizedRect Normalized => new(Box.X / ImageWidth, Box.Y / ImageHeight, Box.Width / ImageWidth, Box.Height / ImageHeight);
    public NormalizedRect? Cancel() => Previous;
    public static (double X, double Y) HandlePoint(CropBox b, CropHandle h) => h switch
    {
        CropHandle.NW => (b.X, b.Y), CropHandle.N => (b.X + b.Width / 2, b.Y), CropHandle.NE => (b.Right, b.Y),
        CropHandle.E => (b.Right, b.Y + b.Height / 2), CropHandle.SE => (b.Right, b.Bottom),
        CropHandle.S => (b.X + b.Width / 2, b.Bottom), CropHandle.SW => (b.X, b.Bottom), _ => (b.X, b.Y + b.Height / 2),
    };
    public CropHandle HitTest(double x, double y, double zoom)
    {
        foreach (var h in Enum.GetValues<CropHandle>().Skip(2))
        {
            var p = HandlePoint(Box, h);
            if (Math.Abs(x - p.X) * zoom <= 8 && Math.Abs(y - p.Y) * zoom <= 8) return h;
        }
        return x >= Box.X && x <= Box.Right && y >= Box.Y && y <= Box.Bottom ? CropHandle.Move : CropHandle.New;
    }
    public void Begin(double x, double y, CropHandle handle)
    { _start = Box; _startX = Math.Clamp(x, 0, ImageWidth); _startY = Math.Clamp(y, 0, ImageHeight); _handle = handle; }

    public double LockedRatio(bool shiftCorner)
    {
        var ratio = Aspect switch { CropAspect.Paper => PaperRatio, CropAspect.Original => ImageWidth / ImageHeight, _ => 0 };
        return ratio > 0 ? ratio : shiftCorner ? _start.Width / _start.Height : 0;
    }
    public static double MatchOrientation(double ratio, double width, double height) => (width > height) != (ratio > 1) ? 1 / ratio : ratio;

    public void Drag(double x, double y, double zoom, bool alt = false, bool shift = false)
    {
        var dx = x - _startX; var dy = y - _startY;
        if (_handle == CropHandle.Move)
        {
            var left = Math.Clamp(_start.X + dx, 0, ImageWidth - _start.Width);
            var top = Math.Clamp(_start.Y + dy, 0, ImageHeight - _start.Height);
            if (!alt)
            {
                var sl = Snap(left, true, zoom); var sr = Snap(left + _start.Width, true, zoom) - _start.Width;
                left = Math.Abs(sl - left) >= Math.Abs(sr - left) ? sl : sr;
                var st = Snap(top, false, zoom); var sb = Snap(top + _start.Height, false, zoom) - _start.Height;
                top = Math.Abs(st - top) >= Math.Abs(sb - top) ? st : sb;
            }
            Box = Clamp(new(left, top, _start.Width, _start.Height)); return;
        }
        var west = _handle is CropHandle.W or CropHandle.NW or CropHandle.SW;
        var north = _handle is CropHandle.N or CropHandle.NW or CropHandle.NE;
        var horizontal = _handle is not (CropHandle.N or CropHandle.S);
        var vertical = _handle is not (CropHandle.W or CropHandle.E);
        var fresh = _handle == CropHandle.New;
        var ax = fresh ? _startX : west ? _start.Right : _start.X;
        var ay = fresh ? _startY : north ? _start.Bottom : _start.Y;
        if (fresh) { west = x < ax; north = y < ay; }
        var ex = fresh ? x : west ? _start.X + dx : _start.Right + dx;
        var ey = fresh ? y : north ? _start.Y + dy : _start.Bottom + dy;
        if (!alt) { if (horizontal) ex = Snap(ex, true, zoom); if (vertical) ey = Snap(ey, false, zoom); }
        var w = horizontal ? Math.Max(MinWidth, west ? ax - ex : ex - ax) : _start.Width;
        var h = vertical ? Math.Max(MinHeight, north ? ay - ey : ey - ay) : _start.Height;
        var ratio = LockedRatio(shift && horizontal && vertical && !fresh);
        if (ratio > 0)
        {
            if (!horizontal) w = h * ratio;
            else if (!vertical || w / h > ratio) h = w / ratio;
            else w = h * ratio;
            var minimum = Math.Max(MinWidth / w, MinHeight / h);
            if (minimum > 1) { w *= minimum; h *= minimum; }
            var scale = Math.Min(1, Math.Min((west ? ax : ImageWidth - ax) / w, (north ? ay : ImageHeight - ay) / h));
            w *= scale; h *= scale;
        }
        else { w = Math.Min(w, west ? ax : ImageWidth - ax); h = Math.Min(h, north ? ay : ImageHeight - ay); }
        Box = Clamp(new(west ? ax - w : ax, north ? ay - h : ay, w, h));
    }
    private double Snap(double value, bool horizontal, double zoom)
    {
        var edges = new List<double> { 0, horizontal ? ImageWidth : ImageHeight };
        if (Detected is { } d) edges.AddRange(horizontal ? [d.X, d.Right] : [d.Y, d.Bottom]);
        var nearest = edges.MinBy(e => Math.Abs(e - value));
        return Math.Abs(nearest - value) * zoom <= 8 ? nearest : value;
    }
    private CropBox Clamp(CropBox b)
    {
        var x = Math.Clamp(b.X, 0, ImageWidth - MinWidth); var y = Math.Clamp(b.Y, 0, ImageHeight - MinHeight);
        return new(x, y, Math.Clamp(b.Width, MinWidth, ImageWidth - x), Math.Clamp(b.Height, MinHeight, ImageHeight - y));
    }
    public void SetBox(CropBox box) => Box = Clamp(box);
    public void Step(int x, int y, bool shift, bool resize)
    {
        var step = shift ? 10 : 1;
        if (resize) SetBox(Box with { Width = Box.Width + x * step, Height = Box.Height + y * step });
        else SetBox(Box with { X = Math.Clamp(Box.X + x * step, 0, ImageWidth - Box.Width), Y = Math.Clamp(Box.Y + y * step, 0, ImageHeight - Box.Height) });
    }
    public static double ToUnits(double pixels, double dpi, bool metric) => pixels / Math.Max(1, dpi) * (metric ? 25.4 : 1);
    public static double FromUnits(double units, double dpi, bool metric) => units * Math.Max(1, dpi) / (metric ? 25.4 : 1);
    public static string Format(double pixels, double dpi, bool metric) => ToUnits(pixels, dpi, metric).ToString(metric ? "0.0" : "0.00", CultureInfo.CurrentCulture);
    public static bool TryPixels(string text, double dpi, bool metric, out double pixels)
    {
        var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var units) && double.IsFinite(units);
        pixels = valid ? FromUnits(units, dpi, metric) : 0; return valid && double.IsFinite(pixels);
    }
}
