using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Printing;

namespace Yrk.PdfStudio.App.Services;

public enum PrintOrientation
{
    Auto,
    Portrait,
    Landscape,
}

public enum PrintQuality
{
    Draft = 150,
    Standard = 300,
    High = 600,
}

/// <summary>A paper size a printer offers. Sizes are in points; <see cref="Media"/> goes into the print ticket.</summary>
public sealed record PaperOption(string Name, double Width, double Height, PageMediaSize Media)
{
    public override string ToString() => Name;
}

/// <summary>Unprintable margins of a printer for the chosen paper, in points (portrait).</summary>
public readonly record struct PaperMargins(double Left, double Top, double Right, double Bottom);

public sealed record PrintSettings(
    string Printer,
    PaperOption Paper,
    PaperMargins Margins,
    PrintOrientation Orientation,
    IReadOnlyList<int> Pages,
    int Copies,
    bool Collate,
    PrintSizing Sizing,
    double CustomScale,
    bool AutoRotate,
    bool Grayscale,
    bool Comments,
    PrintQuality Quality,
    PagesPerSheet Layout = PagesPerSheet.One);

/// <summary>
/// Printing through the Windows print system (XPS). Each page is rendered by PDFium for printing at the
/// chosen resolution and placed on the paper with <see cref="PrintLayout"/>. The job runs on its own STA
/// thread, so the window stays responsive and System.Printing objects never cross threads.
/// </summary>
public static class PdfPrinting
{
    private const double PointsPerDip = 72.0 / 96.0;
    private const double MaxPixelsPerPage = 80_000_000;

    public static IReadOnlyList<string> PrinterNames(out string? defaultPrinter)
    {
        using var server = new LocalPrintServer();
        defaultPrinter = SafeDefault(server);
        using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
        return queues.Select(q => q.FullName).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The paper sizes of a printer, its default one, and its margins for each size.</summary>
    public static (IReadOnlyList<PaperOption> Papers, PaperOption? Default) Papers(string printer)
    {
        using var server = new LocalPrintServer();
        using var queue = Find(server, printer);
        var capabilities = queue.GetPrintCapabilities();
        var papers = capabilities.PageMediaSizeCapability
            .Where(m => m.Width is > 0 && m.Height is > 0)
            .Select(m => new PaperOption(PaperName(m), m.Width!.Value * PointsPerDip, m.Height!.Value * PointsPerDip, m))
            .GroupBy(p => p.Name)
            .Select(g => g.First())
            .ToList();
        var current = (queue.UserPrintTicket ?? queue.DefaultPrintTicket)?.PageMediaSize;
        var paper = papers.FirstOrDefault(p => current is not null && p.Media.PageMediaSizeName == current.PageMediaSizeName && current.PageMediaSizeName is not null)
            ?? papers.FirstOrDefault(p => p.Media.PageMediaSizeName == PageMediaSizeName.ISOA4);
        return (papers, paper ?? papers.FirstOrDefault());
    }

    public static PaperMargins Margins(string printer, PaperOption paper)
    {
        using var server = new LocalPrintServer();
        using var queue = Find(server, printer);
        var ticket = (queue.UserPrintTicket ?? queue.DefaultPrintTicket).Clone();
        ticket.PageMediaSize = paper.Media;
        ticket.PageOrientation = PageOrientation.Portrait;
        if (queue.GetPrintCapabilities(ticket).PageImageableArea is not { } area)
        {
            return default;
        }

        var left = area.OriginWidth * PointsPerDip;
        var top = area.OriginHeight * PointsPerDip;
        return new PaperMargins(
            left,
            top,
            Math.Max(0, paper.Width - left - (area.ExtentWidth * PointsPerDip)),
            Math.Max(0, paper.Height - top - (area.ExtentHeight * PointsPerDip)));
    }

    /// <summary>Whether the paper is used in landscape for these pages (turned against them for 2 or 6 a sheet and booklets).</summary>
    public static bool IsLandscape(PrintSettings settings, IPdfDocument document) => settings.Orientation switch
    {
        PrintOrientation.Landscape => true,
        PrintOrientation.Portrait => false,
        _ => (settings.Pages.Count > 0 && document.PageSizes[settings.Pages[0]] is var s && s.Width > s.Height) != PrintLayout.TurnsPaper(settings.Layout),
    };

    /// <summary>The sides of paper to print, each with its pages and where they go.</summary>
    public static IReadOnlyList<IReadOnlyList<SheetCell>> Sides(PrintSettings settings, PrintRect printable) =>
        settings.Layout == PagesPerSheet.One
            ? settings.Pages.Select(p => (IReadOnlyList<SheetCell>)[new SheetCell(p, printable)]).ToList()
            : PrintLayout.Sheets(settings.Pages, settings.Layout, printable);

    /// <summary>Where a page goes in its part of the sheet: as chosen when one to a sheet, else fitted.</summary>
    public static PrintPlacement Place(PrintSettings settings, PdfPageSize size, double sheetWidth, double sheetHeight, PrintRect area) =>
        settings.Layout == PagesPerSheet.One
            ? PrintLayout.Place(size.Width, size.Height, sheetWidth, sheetHeight, area, settings.Sizing, settings.CustomScale, settings.AutoRotate)
            : PrintLayout.Place(size.Width, size.Height, sheetWidth, sheetHeight, area, PrintSizing.Fit, 1, settings.AutoRotate && settings.Layout != PagesPerSheet.Booklet);

    /// <summary>Paper size and printable area in points, in the orientation printed (margins kept symmetric).</summary>
    public static (double Width, double Height, PrintRect Printable) Sheet(PrintSettings settings, bool landscape)
    {
        var m = settings.Margins;
        double marginX = Math.Max(m.Left, m.Right), marginY = Math.Max(m.Top, m.Bottom);
        var (width, height) = (settings.Paper.Width, settings.Paper.Height);
        if (landscape)
        {
            (width, height, marginX, marginY) = (height, width, marginY, marginX);
        }

        return (width, height, new PrintRect(marginX, marginY, width - (2 * marginX), height - (2 * marginY)));
    }

    public static Task PrintAsync(IPdfDocument document, PrintSettings settings, string jobName, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Print(document, settings, jobName, progress, cancellationToken);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(cancellationToken.IsCancellationRequested ? new OperationCanceledException(cancellationToken) : ex);
            }
        })
        {
            IsBackground = true,
            Name = "Print job",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void Print(IPdfDocument document, PrintSettings settings, string jobName, IProgress<int> progress, CancellationToken cancellationToken)
    {
        using var server = new LocalPrintServer();
        using var queue = Find(server, settings.Printer);
        var landscape = IsLandscape(settings, document);
        var baseTicket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
        var ticket = baseTicket.Clone();
        ticket.PageMediaSize = settings.Paper.Media;
        ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        ticket.CopyCount = settings.Copies;
        ticket.Collation = settings.Collate ? Collation.Collated : Collation.Uncollated;
        ticket.OutputColor = settings.Grayscale ? OutputColor.Grayscale : OutputColor.Color;
        if (settings.Layout == PagesPerSheet.Booklet)
        {
            // Both sides, flipped on the short edge, so the sheets fold into a booklet (a printer without it drops this).
            ticket.Duplexing = Duplexing.TwoSidedShortEdge;
        }

        var validated = queue.MergeAndValidatePrintTicket(baseTicket, ticket).ValidatedPrintTicket;

        queue.CurrentJobSettings.Description = jobName;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        writer.Write(new Paginator(document, settings, landscape, progress, cancellationToken), validated);
    }

    private static PrintQueue Find(LocalPrintServer server, string name)
    {
        using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
        foreach (var queue in queues)
        {
            if (queue.FullName == name)
            {
                return new PrintQueue(queue.HostingPrintServer, queue.Name);
            }
        }

        throw new PrintQueueException(Loc.F("The printer \"{0}\" is not available.", name));
    }

    private static string? SafeDefault(LocalPrintServer server)
    {
        try
        {
            return server.DefaultPrintQueue?.FullName;
        }
        catch (PrintSystemException)
        {
            return null;
        }
    }

    /// <summary>"A4 (210 × 297 mm)", "Letter (216 × 279 mm)".</summary>
    private static string PaperName(PageMediaSize media)
    {
        var name = media.PageMediaSizeName?.ToString() ?? string.Empty;
        foreach (var (prefix, replacement) in new[] { ("ISO", string.Empty), ("NorthAmerica", string.Empty), ("JIS", "JIS ") })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
            {
                name = replacement + name[prefix.Length..];
                break;
            }
        }

        var mm = string.Format(CultureInfo.CurrentCulture, "{0:0} × {1:0} mm", media.Width!.Value * 25.4 / 96, media.Height!.Value * 25.4 / 96);
        return name.Length == 0 || name == "Unknown" ? mm : $"{name} ({mm})";
    }

    /// <summary>Renders one page at a time as the print system asks for it.</summary>
    private sealed class Paginator(IPdfDocument document, PrintSettings settings, bool landscape, IProgress<int> progress, CancellationToken cancellationToken)
        : DocumentPaginator
    {
        private const double DipsPerPoint = 96.0 / 72.0;
        private readonly (double Width, double Height, PrintRect Printable) _sheet = Sheet(settings, landscape);

        public override bool IsPageCountValid => true;

        private IReadOnlyList<IReadOnlyList<SheetCell>>? _sides;

        private IReadOnlyList<IReadOnlyList<SheetCell>> SheetSides => _sides ??= Sides(settings, _sheet.Printable);

        public override int PageCount => SheetSides.Count;

        public override Size PageSize
        {
            get => new(_sheet.Width * DipsPerPoint, _sheet.Height * DipsPerPoint);
            set { }
        }

        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(pageNumber);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var paper = new Rect(0, 0, _sheet.Width * DipsPerPoint, _sheet.Height * DipsPerPoint);
                context.PushClip(new RectangleGeometry(paper));
                foreach (var cell in SheetSides[pageNumber])
                {
                    if (cell.PageIndex is not { } index)
                    {
                        continue; // A blank page of a booklet.
                    }

                    var size = document.PageSizes[index];
                    var placement = Place(settings, size, _sheet.Width, _sheet.Height, cell.Area);

                    // Pixels for the page at the print resolution, fewer for huge pages.
                    var dpi = (double)settings.Quality;
                    double imageWidth = size.Width * placement.Scale, imageHeight = size.Height * placement.Scale; // paper points
                    var pixels = imageWidth / 72 * dpi * (imageHeight / 72 * dpi);
                    if (pixels > MaxPixelsPerPage)
                    {
                        dpi *= Math.Sqrt(MaxPixelsPerPage / pixels);
                    }

                    // Started from the thread pool: this thread has a WPF dispatcher context that is blocked while we wait.
                    int pixelWidth = Math.Max(1, (int)Math.Round(imageWidth / 72 * dpi)), pixelHeight = Math.Max(1, (int)Math.Round(imageHeight / 72 * dpi));
                    var bitmap = Task.Run(
                        () => document.RenderPageForPrintAsync(index, pixelWidth, pixelHeight, settings.Grayscale, settings.Comments, cancellationToken),
                        cancellationToken).GetAwaiter().GetResult();
                    DrawPage(context, BitmapFactory.Create(bitmap), placement, DipsPerPoint);
                }

                context.Pop();
            }

            var size96 = PageSize;
            return new DocumentPage(visual, size96, new Rect(size96), new Rect(size96));
        }
    }

    /// <summary>Draws a rendered page where <paramref name="placement"/> puts it; <paramref name="unit"/> is drawing units per paper point.</summary>
    public static void DrawPage(DrawingContext context, BitmapSource image, PrintPlacement placement, double unit)
    {
        var target = placement.Target;
        if (placement.Rotated)
        {
            // Turned clockwise: the page's top edge runs down the right side of the target.
            context.PushTransform(new TranslateTransform((target.X + target.Width) * unit, target.Y * unit));
            context.PushTransform(new RotateTransform(90));
            context.DrawImage(image, new Rect(0, 0, target.Height * unit, target.Width * unit));
            context.Pop();
            context.Pop();
        }
        else
        {
            context.DrawImage(image, new Rect(target.X * unit, target.Y * unit, target.Width * unit, target.Height * unit));
        }
    }
}
