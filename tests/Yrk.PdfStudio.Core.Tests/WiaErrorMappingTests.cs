using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class WiaErrorMappingTests
{
    [Theory]
    [InlineData(ScanSource.Flatbed, 0, 100, 2, 1)]
    [InlineData(ScanSource.Feeder, 0, 100, 1, 0)]
    [InlineData(ScanSource.Duplex, 0, 100, 2, 0)]
    [InlineData(ScanSource.Feeder, 1, 100, 1, 1)]
    [InlineData(ScanSource.Duplex, 1, 100, 1, 2)]
    [InlineData(ScanSource.Duplex, 2, 100, 2, 2)]
    [InlineData(ScanSource.Duplex, 3, 100, 3, 6)]
    [InlineData(ScanSource.Duplex, 4, 100, 3, 4)]
    [InlineData(ScanSource.Duplex, int.MaxValue - 1, int.MaxValue, 2, int.MaxValue - 1)]
    public void Page_count_respects_source_range_and_step(ScanSource source, int min, int max, int step, int expected)
    {
        Assert.Equal(expected, WiaScannerService.SelectPagesPerTransfer(source, min, max, step));
    }

    [Theory]
    [InlineData(1, 100, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(3, 5, 3)]
    public void Duplex_rejects_ranges_without_a_supported_complete_sheet(int min, int max, int step)
        => Assert.Throws<NotSupportedException>(() => WiaScannerService.SelectPagesPerTransfer(ScanSource.Duplex, min, max, step));

    [Theory]
    [InlineData(ScanSource.Feeder, new[] { 1, 2, 0 }, 0)]
    [InlineData(ScanSource.Duplex, new[] { 1, 2, 0 }, 0)]
    [InlineData(ScanSource.Duplex, new[] { 1, 5, 4, 2 }, 2)]
    [InlineData(ScanSource.Feeder, new[] { 3, 5 }, 3)]
    [InlineData(ScanSource.Flatbed, new[] { 0, 2 }, 1)]
    public void Page_count_also_respects_enumerated_values(ScanSource source, int[] values, int expected)
        => Assert.Equal(expected, WiaScannerService.SelectPagesPerTransfer(source, values));

    [Fact]
    public void Duplex_rejects_enumerated_single_sides()
        => Assert.Throws<NotSupportedException>(() => WiaScannerService.SelectPagesPerTransfer(ScanSource.Duplex, [1, 3]));

    [Theory]
    [InlineData(0, 100, 2, 0)]
    [InlineData(2, 100, 2, 2)]
    [InlineData(1, 100, 1, 2)]
    public async Task Duplex_retains_both_sides_and_continues_until_paper_empty(int min, int max, int step, int expected)
    {
        var driver = new TestDevice(feeder: true, availablePages: 4, duplex: true,
            pages: new TestProperty(3096, min, 1) { SubTypeMin = min, SubTypeMax = max, SubTypeStep = step });
        driver.Acquisition.OnTransfer = () => Assert.Equal(expected, driver.Acquisition.Properties[4].Value);
        var scanner = new WiaScannerService(() => new TestManager(driver));
        var pages = new List<ScannedPage>();
        await scanner.ScanAsync(new("test-device", "Test"), new(Source: ScanSource.Duplex), new ScanSessionTests.Sink<ScannedPage>(pages.Add));
        Assert.Equal(4, pages.Count);
        Assert.Equal(5, driver.Acquisition.Transfers);
        Assert.Equal(5, driver.Properties[2].Value); // FEEDER | DUPLEX.
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, pages.Select(p => p.Bitmap.Pixels[0]).ToArray());
    }

    [Theory]
    [InlineData(0x80210001u, ScannerErrorKind.Other)]
    [InlineData(0x80210002u, ScannerErrorKind.PaperJam)]
    [InlineData(0x80210003u, ScannerErrorKind.FeederEmpty)]
    [InlineData(0x80210004u, ScannerErrorKind.PaperJam)]
    [InlineData(0x80210005u, ScannerErrorKind.Unavailable)]
    [InlineData(0x80210006u, ScannerErrorKind.DeviceBusy)]
    [InlineData(0x80210007u, ScannerErrorKind.DeviceBusy)]
    [InlineData(0x80210008u, ScannerErrorKind.Unavailable)]
    [InlineData(0x80210009u, ScannerErrorKind.NoDevice)]
    [InlineData(0x8021000Au, ScannerErrorKind.Unavailable)]
    [InlineData(0x8021000Bu, ScannerErrorKind.Other)]
    [InlineData(0x8021000Cu, ScannerErrorKind.Other)]
    [InlineData(0x8021000Du, ScannerErrorKind.DeviceBusy)]
    [InlineData(0x8021000Eu, ScannerErrorKind.Other)]
    [InlineData(0x8021000Fu, ScannerErrorKind.Other)]
    [InlineData(0x80210010u, ScannerErrorKind.Unavailable)] // SDK COVER_OPEN (decimal 16).
    [InlineData(0x80210011u, ScannerErrorKind.Unavailable)] // SDK LAMP_OFF (decimal 17).
    [InlineData(0x80210012u, ScannerErrorKind.Other)]
    [InlineData(0x80210013u, ScannerErrorKind.DeviceBusy)]
    [InlineData(0x80210014u, ScannerErrorKind.PaperJam)] // SDK MULTI_FEED (decimal 20).
    [InlineData(0x80210015u, ScannerErrorKind.NoDevice)]
    [InlineData(0x00210015u, ScannerErrorKind.NoDevice)]
    [InlineData(0x80210016u, ScannerErrorKind.Unavailable)]
    [InlineData(0x80210017u, ScannerErrorKind.Unavailable)]
    [InlineData(0x80210020u, ScannerErrorKind.PaperJam)] // MULTI_FEED, not USER_INTERVENTION.
    [InlineData(0x80210021u, ScannerErrorKind.Other)]
    [InlineData(0x800704C7u, ScannerErrorKind.Cancelled)]
    [InlineData(0x80004004u, ScannerErrorKind.Cancelled)]
    [InlineData(0x80040154u, ScannerErrorKind.Unavailable)]
    [InlineData(0x800401F3u, ScannerErrorKind.Unavailable)]
    [InlineData(0x800706BAu, ScannerErrorKind.Unavailable)]
    [InlineData(0x80070422u, ScannerErrorKind.Unavailable)]
    [InlineData(0x81234567u, ScannerErrorKind.Other)]
    public void Maps_WIA_and_COM_errors_and_preserves_hresult(uint code, ScannerErrorKind expected)
    {
        var mapped = WiaScannerService.MapError(unchecked((int)code));
        Assert.Equal(expected, mapped.Kind);
        Assert.Equal(unchecked((int)code), mapped.HResult);
        Assert.Contains($"0x{code:X8}", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_inventory_uses_factory_on_a_dedicated_STA()
    {
        var callingThread = Thread.CurrentThread;
        var scanner = new WiaScannerService(() =>
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.NotSame(callingThread, Thread.CurrentThread);
            return new EmptyManager();
        });
        Assert.Empty(await scanner.ListDevicesAsync());
        var error = await Assert.ThrowsAsync<ScannerException>(() => scanner.GetCapabilitiesAsync(new("missing", "Missing")));
        Assert.True(error.Kind == ScannerErrorKind.NoDevice, error.ToString());
    }

    [Fact]
    public async Task Missing_WIA_returns_a_typed_unavailable_error()
    {
        var scanner = new WiaScannerService(() => null);
        var error = await Assert.ThrowsAsync<ScannerException>(() => scanner.ListDevicesAsync());
        Assert.Equal(ScannerErrorKind.Unavailable, error.Kind);
    }

    [Fact]
    public async Task Cancelled_inventory_does_not_activate_COM()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scanner = new WiaScannerService(() => throw new InvalidOperationException("Factory must not run."));
        var error = await Assert.ThrowsAsync<ScannerException>(() => scanner.ListDevicesAsync(cancellation.Token));
        Assert.Equal(ScannerErrorKind.Cancelled, error.Kind);
    }

    [Fact]
    public async Task Feeder_negotiates_settings_reports_notices_and_finishes_on_paper_empty()
    {
        var driver = new TestDevice(feeder: true, availablePages: 2);
        var scanner = new WiaScannerService(() => new TestManager(driver));
        var device = Assert.Single(await scanner.ListDevicesAsync());
        var capabilities = await scanner.GetCapabilitiesAsync(device);
        Assert.Contains(ScanSource.Feeder, capabilities.Sources);
        Assert.Collection(capabilities.Dpi, dpi => Assert.Equal(300, dpi), dpi => Assert.Equal(600, dpi));
        Assert.DoesNotContain(ScanColorMode.BlackAndWhite, capabilities.ColorModes);
        var pages = new List<ScannedPage>();
        await scanner.ScanAsync(device, new(Dpi: 350, ColorMode: ScanColorMode.BlackAndWhite, Source: ScanSource.Feeder), new ScanSessionTests.Sink<ScannedPage>(pages.Add));
        Assert.Equal(2, pages.Count);
        Assert.Equal(3, driver.Acquisition.Transfers);
        Assert.All(pages, p =>
        {
            Assert.Equal(300, p.DpiX);
            Assert.Equal(300, p.DpiY);
            Assert.Contains(p.Notices!, n => n.Contains("DPI", StringComparison.Ordinal));
            Assert.Contains(p.Notices!, n => n.Contains("Color mode adjusted", StringComparison.Ordinal));
        });
        Assert.Equal(2, driver.Acquisition.Properties[1].Value); // Gray intent.
        Assert.Equal(1, driver.Properties[2].Value); // FEEDER selected at device root.
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(false, 2, 1)]
    public async Task Empty_feeder_finishes_normally_and_flatbed_acquires_only_one_page(bool feeder, int availablePages, int expected)
    {
        var driver = new TestDevice(feeder, availablePages);
        var scanner = new WiaScannerService(() => new TestManager(driver));
        var pages = new List<ScannedPage>();
        await scanner.ScanAsync(new("test-device", "Test"), new(Source: feeder ? ScanSource.Feeder : ScanSource.Flatbed), new ScanSessionTests.Sink<ScannedPage>(pages.Add));
        Assert.Equal(expected, pages.Count);
        Assert.Equal(1, driver.Acquisition.Transfers);
    }

    [Fact]
    public async Task Cancelling_during_transfer_never_reports_a_late_page()
    {
        using var cancellation = new CancellationTokenSource();
        var driver = new TestDevice(feeder: true, availablePages: 2);
        driver.Acquisition.OnTransfer = cancellation.Cancel;
        var scanner = new WiaScannerService(() => new TestManager(driver));
        var pages = new List<ScannedPage>();
        var error = await Assert.ThrowsAsync<ScannerException>(() => scanner.ScanAsync(new("test-device", "Test"),
            new(Source: ScanSource.Feeder), new ScanSessionTests.Sink<ScannedPage>(pages.Add), cancellation.Token));
        Assert.Equal(ScannerErrorKind.Cancelled, error.Kind);
        Assert.Empty(pages);
    }

    // These managed objects expose the small Automation surface consumed by the service. They
    // exercise its late-bound calls and setting negotiation without instantiating any COM object.
    public sealed class TestManager(TestDevice device)
    {
        public AutomationItems<TestInfo> DeviceInfos { get; } = new(new TestInfo(device));
    }

    public sealed class TestInfo(TestDevice device)
    {
        public int Type { get; } = 1;
        public string DeviceID { get; } = "test-device";
        public AutomationItems<TestProperty> Properties { get; } = new(new TestProperty(7, "Test scanner"));
        public TestDevice Connect() => device;
    }

    public sealed class TestDevice
    {
        public TestDevice(bool feeder, int availablePages, bool duplex = false, TestProperty? pages = null)
        {
            Acquisition = new TestAcquisition(availablePages, pages);
            Properties = new(new TestProperty(3086, duplex ? 5 : feeder ? 1 : 2), new TestProperty(3088, feeder ? 1 : 2));
            Items = new(Acquisition);
        }
        public TestAcquisition Acquisition { get; }
        public AutomationItems<TestProperty> Properties { get; }
        public AutomationItems<TestAcquisition> Items { get; }
    }

    public sealed class TestAcquisition(int availablePages, TestProperty? pages = null)
    {
        public AutomationItems<TestProperty> Properties { get; } = new(
            new TestProperty(6146, 1, 3, [1, 2]),
            new TestProperty(6147, 300, 1) { SubTypeMin = 300, SubTypeMax = 600, SubTypeStep = 300 },
            new TestProperty(6148, 300, 2, [300, 600]),
            pages ?? new TestProperty(3096, 1));
        public int Transfers { get; private set; }
        public Action? OnTransfer { get; set; }
        public TestImage Transfer(string format)
        {
            Assert.Equal("{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}", format);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            OnTransfer?.Invoke();
            if (Transfers++ >= availablePages)
                throw new ScannerException(ScannerErrorKind.FeederEmpty, "Empty feeder", hresult: unchecked((int)0x80210003));
            var encoder = new BmpBitmapEncoder();
            var pixels = new byte[16 * 16 * 3];
            Array.Fill(pixels, (byte)(Transfers - 1));
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 300, 300, PixelFormats.Bgr24, null, pixels, 16 * 3)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return new(stream.ToArray());
        }
    }

    public sealed class TestImage(byte[] data)
    {
        public TestVector FileData { get; } = new(data);
    }

    public sealed class TestVector(byte[] data)
    {
        public byte[] BinaryData { get; } = data;
    }

    public sealed class TestProperty(int id, object value, int subtype = 0, int[]? values = null)
    {
        public int PropertyID { get; } = id;
        public object Value { get; set; } = value;
        public bool IsReadOnly { get; }
        public int SubType { get; } = subtype;
        public AutomationItems<int> SubTypeValues { get; } = new(values ?? []);
        public int SubTypeMin { get; init; }
        public int SubTypeMax { get; init; }
        public int SubTypeStep { get; init; }
    }

    public sealed class AutomationItems<T>(params T[] items)
    {
        public int Count => items.Length;
        public T this[int index] => items[index - 1];
    }

    public sealed class EmptyManager
    {
        public EmptyDevices DeviceInfos { get; } = new();
    }

    public sealed class EmptyDevices
    {
        public int Count { get; }
    }
}
