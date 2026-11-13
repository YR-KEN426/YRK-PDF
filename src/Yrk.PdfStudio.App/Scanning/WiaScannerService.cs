using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Imaging;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.App.Scanning;

/// <summary>WIA Automation RCWs are created, used and released exclusively on a dedicated STA.</summary>
public sealed class WiaScannerService : IScannerService
{
    private const int Intent = 6146, XResolution = 6147, YResolution = 6148, Handling = 3088;
    private const string BmpFormat = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    private const string FlatbedCategory = "{FB607B1F-43F3-488B-855B-FB703EC342A6}";
    private const string FeederCategory = "{FE131934-F84C-42AD-8DA4-6129CDDD7288}";
    private readonly Func<object?> _deviceManagerFactory;

    public WiaScannerService() : this(CreateManager) { }

    // A managed factory lets tests exercise an empty inventory and unavailable WIA without COM.
    internal WiaScannerService(Func<object?> deviceManagerFactory) => _deviceManagerFactory = deviceManagerFactory;

    public Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default) => ScannerSta.RunAsync<IReadOnlyList<ScanDevice>>(() =>
    {
        using var objects = new ComObjects();
        var manager = Manager(objects);
        var infos = objects.Own(Get(manager, "DeviceInfos"));
        var devices = new List<ScanDevice>();
        for (var i = 1; i <= Number(Get(infos, "Count")); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var entry = new ComObjects();
            var info = entry.Own(Item(infos, i));
            if (Number(Get(info, "Type")) != 1) continue;
            var name = PropertyValue(info, 7, entry)?.ToString() ?? "Scanner";
            devices.Add(new(Get(info, "DeviceID").ToString()!, name));
        }
        return devices;
    }, cancellationToken, emptyInventoryOnNoDevice: true);

    public Task<ScanCapabilities> GetCapabilitiesAsync(ScanDevice device, CancellationToken cancellationToken = default) => ScannerSta.RunAsync(() =>
    {
        using var objects = new ComObjects();
        var connected = Connect(device, objects, cancellationToken);
        var sources = Sources(connected, objects);
        var item = ScanItem(connected, sources[0], objects);
        var x = FindProperty(item, XResolution, objects);
        var y = FindProperty(item, YResolution, objects);
        var dpi = SupportedNumbers(x).Intersect(SupportedNumbers(y)).Where(n => n > 0).Order().ToArray();
        if (dpi.Length == 0) dpi = [x is null ? 300 : Math.Max(1, Number(Get(x, "Value")))];
        return new ScanCapabilities(sources, dpi, Modes(FindProperty(item, Intent, objects)),
            ReadExtent(connected, 3074, 3075, objects), ReadExtent(connected, 3076, 3077, objects));
    }, cancellationToken);

    public Task ScanAsync(ScanDevice device, ScanSettings settings, IProgress<ScannedPage> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        settings.Validate();
        return ScannerSta.RunAsync(() =>
        {
            using var objects = new ComObjects();
            var connected = Connect(device, objects, cancellationToken);
            var notices = new List<string>();
            var sources = Sources(connected, objects);
            var source = sources.Contains(settings.Source) ? settings.Source : sources[0];
            if (source != settings.Source) notices.Add($"Paper source adjusted from {settings.Source} to {source}.");
            // WIA 1.x compatibility context must be set before reading item properties.
            var rootHandling = FindProperty(connected, Handling, objects);
            var handling = source switch { ScanSource.Flatbed => 2, ScanSource.Feeder => 1, _ => 1 | 4 };
            if (rootHandling is not null)
            {
                SetWithFallback(rootHandling, handling, "Paper source", notices);
                var actualHandling = Number(Get(rootHandling, "Value"));
                var actualSource = (actualHandling & 4) != 0 ? ScanSource.Duplex
                    : (actualHandling & 1) != 0 ? ScanSource.Feeder : ScanSource.Flatbed;
                if (actualSource != source) notices.Add($"Paper source adjusted from {source} to {actualSource}.");
                source = actualSource;
            }
            var item = ScanItem(connected, source, objects);
            if (rootHandling is null)
            {
                var itemHandling = FindProperty(item, Handling, objects);
                if (itemHandling is not null) SetWithFallback(itemHandling, handling, "Paper handling", notices);
            }
            var intent = FindProperty(item, Intent, objects);
            var modes = Modes(intent);
            var mode = modes.OrderBy(m => Math.Abs((int)m - (int)settings.ColorMode)).First();
            if (mode != settings.ColorMode) notices.Add($"Color mode adjusted from {settings.ColorMode} to {mode}.");
            if (intent is not null) SetWithFallback(intent, 1 << (int)mode, "Color intent", notices);
            else notices.Add("Scanner does not expose color intent; using the device default.");
            ConfigureResolution(item, XResolution, settings.Dpi, notices, objects);
            ConfigureResolution(item, YResolution, settings.Dpi, notices, objects);
            // Some drivers couple axes: setting Y can change X, so read both after all writes.
            var dpiX = Number(PropertyValue(item, XResolution, objects)!);
            var dpiY = Number(PropertyValue(item, YResolution, objects)!);
            if (dpiX <= 0 || dpiY <= 0) throw new ScannerException(ScannerErrorKind.Other, "The scanner returned an invalid DPI.");
            if (dpiX != settings.Dpi) notices.Add($"Actual horizontal DPI: {dpiX} (requested {settings.Dpi}).");
            if (dpiY != settings.Dpi) notices.Add($"Actual vertical DPI: {dpiY} (requested {settings.Dpi}).");
            var cropInSoftware = ConfigureArea(item, settings.Region);
            if (cropInSoftware) notices.Add(ScanRegion.FallbackNotice);
            // Request all feeder pages when supported, or a valid batch that includes both duplex sides.
            // Automation has no cancellable Transfer API:
            // cancellation is checked before/after each driver call, then all RCWs are released before completion.
            var pages = FindProperty(item, 3096, objects);
            if (pages is not null)
            {
                var count = Number(Get(pages, "SubType")) == 1
                    ? SelectPagesPerTransfer(source, Number(Get(pages, "SubTypeMin")), Number(Get(pages, "SubTypeMax")), Number(Get(pages, "SubTypeStep")))
                    : SelectPagesPerTransfer(source, SupportedNumbers(pages));
                SetWithFallback(pages, count, "Pages per transfer", notices);
                // A driver fallback to one side must not silently discard duplex backs.
                if (source == ScanSource.Duplex) SelectPagesPerTransfer(source, [Number(Get(pages, "Value"))]);
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var transfer = new ComObjects();
                    var image = transfer.Own(Call(item, "Transfer", BmpFormat));
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileData = transfer.Own(Get(image, "FileData"));
                    var bytes = (byte[])Get(fileData, "BinaryData");
                    using var stream = new MemoryStream(bytes, writable: false);
                    var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
                    foreach (var frame in frames)
                    {
                        var pixels = ImageConversion.ToPdfImage(frame);
                        cancellationToken.ThrowIfCancellationRequested();
                        var bitmap = new PdfBitmap(pixels.Width, pixels.Height, pixels.Width * 4, pixels.Pixels);
                        if (cropInSoftware && settings.Region is { } region) bitmap = ScanRegion.Crop(bitmap, region, cancellationToken);
                        progress.Report(new(bitmap, dpiX, dpiY, notices.ToArray()));
                    }
                    if (source == ScanSource.Flatbed) break;
                }
                catch (Exception ex) when (source != ScanSource.Flatbed && Unwrap(ex).HResult == unchecked((int)0x80210003))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    break; // An empty feeder (including before the first page) is normal end-of-input.
                }
            }
            return true;
        }, cancellationToken);
    }

    // Extent maxima are read after setting both resolutions: WIA expresses these values in pixels.
    internal static (int X, int Y, int Width, int Height) CalculateArea(NormalizedRect region, int maxWidth, int maxHeight)
        => ScanRegion.Pixels(region, maxWidth, maxHeight);

    internal static bool ConfigureArea(object item, NormalizedRect? region)
    {
        using var objects = new ComObjects();
        var x = FindProperty(item, 6149, objects);
        var y = FindProperty(item, 6150, objects);
        var w = FindProperty(item, 6151, objects);
        var h = FindProperty(item, 6152, objects);
        int Maximum(object property) => SupportedNumbers(property).DefaultIfEmpty(Number(Get(property, "Value"))).Max();
        // Range properties can have thousands of values; use the advertised maximum directly.
        int Extent(object property) => Number(Get(property, "SubType")) == 1 ? Number(Get(property, "SubTypeMax")) : Maximum(property);
        void Write(object? property, int value)
        {
            if (property is null) return;
            if (Number(Get(property, "Value")) == value) return;
            Set(property, "Value", value);
            if (Number(Get(property, "Value")) != value) throw new NotSupportedException("Scanner area was adjusted by the driver.");
        }
        // Some drivers reduce the extent limit when the origin is away from the glass edge.
        Write(x, 0); Write(y, 0);
        var maxW = w is null ? 0 : Extent(w);
        var maxH = h is null ? 0 : Extent(h);
        void FullGlass()
        {
            Write(x, 0); Write(y, 0);
            if (maxW > 0) Write(w, maxW);
            if (maxH > 0) Write(h, maxH);
        }
        if (region is null)
        {
            FullGlass();
            return false;
        }
        try
        {
            FullGlass();
            if (x is null || y is null || w is null || h is null || maxW <= 0 || maxH <= 0) return true;
            var area = CalculateArea(region.Value, maxW, maxH);
            Write(w, area.Width); Write(h, area.Height);
            Write(x, area.X); Write(y, area.Y);
            return false;
        }
        catch (Exception ex) when (Unwrap(ex) is COMException or NotSupportedException or ArgumentException)
        {
            // Never crop a partially configured scan: restoring full glass must succeed first.
            FullGlass();
            return true;
        }
    }

    internal static int SelectPagesPerTransfer(ScanSource source, int minimum, int maximum, int step)
    {
        if (source == ScanSource.Flatbed) return 1;
        if (minimum < 0 || maximum < minimum || step <= 0) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (minimum == 0) return 0; // WIA ALL_PAGES.
        var wanted = source == ScanSource.Duplex ? 2 : 1;
        var chosen = (long)minimum + Math.Max(0, ((long)wanted - minimum + step - 1) / step) * step;
        if (source == ScanSource.Duplex && chosen % 2 != 0)
        {
            if (step % 2 == 0) throw new NotSupportedException();
            chosen += step;
        }
        if (chosen > maximum) throw new NotSupportedException();
        return (int)chosen;
    }

    internal static int SelectPagesPerTransfer(ScanSource source, IReadOnlyList<int> values)
    {
        if (source == ScanSource.Flatbed) return 1;
        if (values.Contains(0)) return 0;
        var chosen = values.Where(n => n > 0 && (source != ScanSource.Duplex || n % 2 == 0)).Order().FirstOrDefault();
        return chosen > 0 ? chosen : throw new NotSupportedException();
    }

    public static ScannerException MapError(int hresult, Exception? innerException = null)
    {
        var (kind, message) = unchecked((uint)hresult) switch
        {
            0x80210015 or 0x00210015 or 0x80210009 => (ScannerErrorKind.NoDevice, "No scanner is available."),
            0x80210006 or 0x8021000D or 0x80210007 or 0x80210013 => (ScannerErrorKind.DeviceBusy, "The scanner is busy, locked, warming up or reserved."),
            0x80210002 or 0x80210004 or 0x80210014 or 0x80210020 => (ScannerErrorKind.PaperJam, "Check the feeder for a jam, paper problem or multiple feed."),
            0x80210003 => (ScannerErrorKind.FeederEmpty, "The document feeder is empty."),
            0x800704C7 or 0x80004004 => (ScannerErrorKind.Cancelled, "Scanning was cancelled."),
            // WiaDef.h numbers 16..21 are decimal. Older Learn tables also spell them as hex;
            // accept both sets. 0x15 is also WIA_S_NO_DEVICE_AVAILABLE (handled above).
            0x80210005 or 0x80210008 or 0x8021000A or 0x80210010 or 0x80210011 or 0x80210016 or 0x80210017
                or 0x80040154 or 0x800401F3 or 0x800706BA or 0x80070422 => (ScannerErrorKind.Unavailable, "The scanner or WIA service is unavailable; check its power, cover and connection."),
            0x80210001 or 0x8021000B or 0x8021000C or 0x8021000E or 0x8021000F
                or 0x80210012 or 0x80210021
                => (ScannerErrorKind.Other, "The scanner reported a driver, setting or transfer error."),
            _ => (ScannerErrorKind.Other, "Scanning failed."),
        };
        return new(kind, $"{message} (0x{hresult:X8})", innerException, hresult);
    }

    private static ScanExtent? ReadExtent(object device, int horizontal, int vertical, ComObjects objects)
    {
        try
        {
            var w = Number(PropertyValue(device, horizontal, objects) ?? 0);
            var h = Number(PropertyValue(device, vertical, objects) ?? 0);
            return w > 0 && h > 0 ? new(w * 25.4 / 1000, h * 25.4 / 1000) : null;
        }
        catch (Exception ex) when (Unwrap(ex) is COMException or ArgumentException) { return null; }
    }

    private static object? CreateManager()
    {
        var type = Type.GetTypeFromProgID("WIA.DeviceManager", throwOnError: false);
        return type is null ? null : Activator.CreateInstance(type);
    }

    private object Manager(ComObjects objects) => objects.Own(_deviceManagerFactory()
        ?? throw new ScannerException(ScannerErrorKind.Unavailable, "Windows Image Acquisition is not installed or available."));

    private object Connect(ScanDevice device, ComObjects objects, CancellationToken ct)
    {
        var infos = objects.Own(Get(Manager(objects), "DeviceInfos"));
        for (var i = 1; i <= Number(Get(infos, "Count")); i++)
        {
            ct.ThrowIfCancellationRequested();
            using var entry = new ComObjects();
            var info = entry.Own(Item(infos, i));
            if (Number(Get(info, "Type")) == 1 && string.Equals(Get(info, "DeviceID").ToString(), device.Id, StringComparison.Ordinal))
                return objects.Own(Call(info, "Connect"));
        }
        throw new ScannerException(ScannerErrorKind.NoDevice, "The selected scanner was not found.");
    }

    private static ScanSource[] Sources(object device, ComObjects objects)
    {
        var capabilities = Number(PropertyValue(device, 3086, objects) ?? 0);
        var sources = new HashSet<ScanSource>();
        if ((capabilities & 2) != 0) sources.Add(ScanSource.Flatbed);
        if ((capabilities & 1) != 0) sources.Add(ScanSource.Feeder);
        if ((capabilities & 4) != 0) { sources.Add(ScanSource.Feeder); sources.Add(ScanSource.Duplex); }
        var items = objects.Own(Get(device, "Items"));
        for (var i = 1; i <= Number(Get(items, "Count")); i++)
        {
            using var entry = new ComObjects();
            var item = entry.Own(Item(items, i));
            var category = PropertyValue(item, 4125, entry)?.ToString();
            if (string.Equals(category, FlatbedCategory, StringComparison.OrdinalIgnoreCase)) sources.Add(ScanSource.Flatbed);
            if (string.Equals(category, FeederCategory, StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(ScanSource.Feeder);
                var flags = FindProperty(item, Handling, entry);
                if (flags is not null && (SupportedMask(flags) & 4) != 0) sources.Add(ScanSource.Duplex);
            }
        }
        return sources.Count == 0 ? [ScanSource.Flatbed] : sources.Order().ToArray();
    }

    private static object ScanItem(object device, ScanSource source, ComObjects objects)
    {
        var items = objects.Own(Get(device, "Items"));
        var count = Number(Get(items, "Count"));
        if (count == 0) throw new ScannerException(ScannerErrorKind.Unavailable, "The scanner exposes no acquisition item.");
        var wanted = source == ScanSource.Flatbed ? FlatbedCategory : FeederCategory;
        for (var i = 1; i <= count; i++)
        {
            using var entry = new ComObjects();
            var item = entry.Own(Item(items, i));
            if (string.Equals(PropertyValue(item, 4125, entry)?.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                return objects.Own(Item(items, i));
        }
        return objects.Own(Item(items, 1)); // WIA 1.x scanners expose one context-dependent item.
    }

    private static ScanColorMode[] Modes(object? intent)
    {
        if (intent is null) return [ScanColorMode.Color];
        var mask = SupportedMask(intent) & 7;
        if (mask == 0) mask = Number(Get(intent, "Value")) & 7;
        return mask == 0 ? [ScanColorMode.Color] : Enum.GetValues<ScanColorMode>().Where(m => (mask & (1 << (int)m)) != 0).ToArray();
    }

    private static int SupportedMask(object property) => SupportedNumbers(property).Aggregate(0, (a, b) => a | b);

    private static int[] SupportedNumbers(object? property)
    {
        if (property is null) return [];
        var subtype = Number(Get(property, "SubType"));
        if (subtype is 2 or 3) // WiaList / WiaFlag. Vector indices are one-based.
        {
            using var objects = new ComObjects();
            var values = objects.Own(Get(property, "SubTypeValues"));
            var result = new int[Number(Get(values, "Count"))];
            for (var i = 0; i < result.Length; i++) result[i] = Number(Item(values, i + 1));
            return result;
        }
        if (subtype == 1)
        {
            var min = Number(Get(property, "SubTypeMin"));
            var max = Number(Get(property, "SubTypeMax"));
            var step = Math.Max(1, Number(Get(property, "SubTypeStep")));
            return new[] { min, max, Number(Get(property, "Value")), 75, 100, 150, 200, 300, 600, 1200 }
                .Where(n => n >= min && n <= max && (n - min) % step == 0).Distinct().Order().ToArray();
        }
        return [Number(Get(property, "Value"))];
    }

    private static int ConfigureResolution(object item, int id, int requested, List<string> notices, ComObjects objects)
    {
        var property = FindProperty(item, id, objects);
        if (property is null) throw new ScannerException(ScannerErrorKind.Other, "The scanner did not report its actual DPI.");
        var candidates = SupportedNumbers(property);
        var chosen = candidates.Where(n => n > 0).OrderBy(n => Math.Abs((long)n - requested)).FirstOrDefault();
        if (Number(Get(property, "SubType")) == 1)
        {
            var min = Number(Get(property, "SubTypeMin"));
            var max = Number(Get(property, "SubTypeMax"));
            var step = Math.Max(1, Number(Get(property, "SubTypeStep")));
            chosen = min + (int)Math.Clamp(Math.Round(((double)requested - min) / step), 0, (max - min) / step) * step;
        }
        SetWithFallback(property, chosen, $"DPI ({id})", notices);
        var actual = Number(Get(property, "Value"));
        if (actual <= 0) throw new ScannerException(ScannerErrorKind.Other, "The scanner returned an invalid DPI.");
        if (actual != requested) notices.Add($"DPI ({id}) adjusted from {requested} to {actual}.");
        return actual;
    }

    private static void SetWithFallback(object property, int requested, string label, List<string> notices)
    {
        var current = Number(Get(property, "Value"));
        try
        {
            if (!Convert.ToBoolean(Get(property, "IsReadOnly"), CultureInfo.InvariantCulture)) Set(property, "Value", requested);
        }
        catch (Exception ex) when (unchecked((uint)Unwrap(ex).HResult) is 0x80070057 or 0x8021000B or 0x8021000C)
        {
            notices.Add($"{label} {requested} is unsupported; using device value {current}.");
            return;
        }
        var actual = Number(Get(property, "Value"));
        if (actual != requested) notices.Add($"{label} adjusted from {requested} to {actual}.");
    }

    private static object? PropertyValue(object owner, int id, ComObjects objects)
    {
        var property = FindProperty(owner, id, objects);
        return property is null ? null : Get(property, "Value");
    }

    private static object? FindProperty(object owner, int id, ComObjects objects)
    {
        using var entries = new ComObjects();
        var properties = entries.Own(Get(owner, "Properties"));
        for (var i = 1; i <= Number(Get(properties, "Count")); i++)
        {
            using var entry = new ComObjects();
            var property = entry.Own(Item(properties, i));
            if (Number(Get(property, "PropertyID")) == id) return objects.Own(Item(properties, i));
        }
        return null;
    }

    private static int Number(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static object Get(object owner, string name) => owner.GetType().InvokeMember(name, BindingFlags.GetProperty, null, owner, null, CultureInfo.InvariantCulture)!;
    private static object Item(object owner, int index) => owner.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, owner, [index], CultureInfo.InvariantCulture)!;
    private static object Call(object owner, string name, params object[] args) => owner.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, owner, args, CultureInfo.InvariantCulture)!;
    private static void Set(object owner, string name, object value) => owner.GetType().InvokeMember(name, BindingFlags.SetProperty, null, owner, [value], CultureInfo.InvariantCulture);
    internal static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : ex;

    private sealed class ComObjects : IDisposable
    {
        private readonly List<object> _objects = [];
        public object Own(object value) { if (Marshal.IsComObject(value)) _objects.Add(value); return value; }
        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--) Marshal.ReleaseComObject(_objects[i]);
            _objects.Clear();
        }
    }
}

internal static class ScannerSta
{
    internal static Task<T> RunAsync<T>(Func<T> action, CancellationToken ct, bool emptyInventoryOnNoDevice = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = action();
                ct.ThrowIfCancellationRequested();
                completion.SetResult(result);
            }
            catch (Exception ex)
            {
                ex = WiaScannerService.Unwrap(ex);
                if (ct.IsCancellationRequested || ex is OperationCanceledException)
                    completion.SetException(new ScannerException(ScannerErrorKind.Cancelled, "Scanning was cancelled.", ex));
                else if (emptyInventoryOnNoDevice && unchecked((uint)ex.HResult) is 0x80210015 or 0x00210015)
                    completion.SetResult((T)(object)Array.Empty<ScanDevice>());
                else completion.SetException(ex is ScannerException ? ex : WiaScannerService.MapError(ex.HResult, ex));
            }
            finally
            {
                System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "YRK PDF scanner STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
