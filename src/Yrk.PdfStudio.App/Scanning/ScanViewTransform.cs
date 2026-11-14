using System.Windows;

namespace Yrk.PdfStudio.App.Scanning;

/// <summary>Image coordinates are source pixels; view coordinates are WPF DIPs.</summary>
public sealed class ScanViewTransform
{
    public Size Image { get; private set; } = new(1, 1);
    public Size View { get; private set; } = new(1, 1);
    public double Zoom { get; private set; } = 1;
    public Vector Offset { get; private set; }
    public bool IsFit { get; private set; } = true;
    public double FitScale => Math.Min(View.Width / Image.Width, View.Height / Image.Height);

    public void Resize(Size image, Size view)
    {
        Image = new(Math.Max(1, image.Width), Math.Max(1, image.Height));
        View = new(Math.Max(1, view.Width), Math.Max(1, view.Height));
        if (IsFit) Fit(); else ClampPan();
    }
    public void Fit() { IsFit = true; Zoom = Math.Clamp(FitScale, .05, 8); Offset = new(); ClampPan(); }
    public void ZoomAt(double zoom, Point anchor)
    {
        var imagePoint = ViewToImage(anchor);
        IsFit = false;
        Zoom = Math.Clamp(zoom, .05, 8);
        Offset = (Vector)anchor - (Vector)imagePoint * Zoom;
        ClampPan();
    }
    public void Pan(Vector delta) { Offset += delta; ClampPan(); }
    public Vector ScrollMaximum => new(Math.Max(0, Image.Width * Zoom - View.Width), Math.Max(0, Image.Height * Zoom - View.Height));
    public void ScrollTo(double x, double y) { Offset = new(-x, -y); ClampPan(); }
    public static bool IsPanGesture(bool middle, bool space, bool cropping) => middle || space || !cropping;
    public static Vector WheelPan(int delta, bool shift) => shift ? new(delta / 120.0 * 48, 0) : new(0, delta / 120.0 * 48);
    public static Vector AutoScroll(Point pointer, Size viewport)
    {
        static double Axis(double p, double length) => p < 28 ? Math.Min(20, (28 - p) * .6)
            : p > length - 28 ? -Math.Min(20, (p - length + 28) * .6) : 0;
        return new(Axis(pointer.X, viewport.Width), Axis(pointer.Y, viewport.Height));
    }
    private void ClampPan()
    {
        static double Axis(double offset, double image, double view) => image <= view
            ? (view - image) / 2 : Math.Clamp(offset, view - image, 0);
        Offset = new(Axis(Offset.X, Image.Width * Zoom, View.Width), Axis(Offset.Y, Image.Height * Zoom, View.Height));
    }
    public Point ImageToView(Point point) => (Point)((Vector)point * Zoom + Offset);
    public Point ViewToImage(Point point) => (Point)(((Vector)point - Offset) / Zoom);
    public Point ViewToNormalized(Point point)
    {
        var p = ViewToImage(point);
        return new(p.X / Image.Width, p.Y / Image.Height);
    }
    public Point NormalizedToView(Point point) => ImageToView(new(point.X * Image.Width, point.Y * Image.Height));
}

public sealed record ScanRenderPlan(int MaxSide, Rect Region, bool IsRegion, int ZoomBucket);

public static class ScanPreviewSizing
{
    public const int PixelCap = 24_000_000;
    public static ScanRenderPlan Plan(ScanViewTransform transform, double dpi)
    {
        // Bucket the cache key; raster dimensions still follow the actual displayed size and DPI.
        var bucket = (int)Math.Ceiling(transform.Zoom * dpi * 8);
        var scale = Math.Max(.001, transform.Zoom * dpi);
        var size = transform.Image;
        var whole = new Rect(0, 0, 1, 1);
        var topLeft = transform.ViewToNormalized(new(0, 0));
        var bottomRight = transform.ViewToNormalized(new(transform.View.Width, transform.View.Height));
        var visible = Rect.Intersect(whole, new Rect(topLeft, bottomRight));
        if (visible.IsEmpty) visible = whole;
        var region = visible.Width < .95 || visible.Height < .95;
        if (region)
        {
            // Keep the sharpening kernel outside the viewport to avoid a seam at tile edges.
            visible.Inflate(2 / size.Width, 2 / size.Height);
            visible.Intersect(whole);
        }
        var area = size.Width * size.Height * (region ? visible.Width * visible.Height : 1);
        var capScale = Math.Sqrt(PixelCap / Math.Max(1, area));
        var capped = scale > capScale;
        scale = Math.Min(scale, capScale);
        var side = Math.Max(size.Width * (region ? visible.Width : 1), size.Height * (region ? visible.Height : 1));
        var rasterSide = capped ? Math.Floor(side * scale) : Math.Ceiling(side * scale);
        return new(Math.Max(1, (int)rasterSide), region ? visible : whole, region, bucket);
    }
}
