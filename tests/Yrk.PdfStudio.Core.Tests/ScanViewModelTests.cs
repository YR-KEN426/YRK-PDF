using System.Windows.Threading;
using Yrk.PdfStudio.App.Scanning;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Scan;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ScanViewModelTests
{
    [Theory]
    [InlineData(ScannerErrorKind.PaperJam)]
    [InlineData(ScannerErrorKind.Cancelled)]
    public void Scanner_failure_keeps_already_received_pages_and_finishes_thumbnails(ScannerErrorKind error)
    {
        Sta.Run(() =>
        {
            using var session = new ScanSession();
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var scanner = new FakeScannerService { SimulatedError = error, ErrorAfterPages = 1 };
            var model = new ScanViewModel(session, scanner, dispatcher) { Settings = new(Dpi: 150) };
            var task = model.ScanAsync(FakeScannerService.Device, () => { });
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
            timer.Start();
            Dispatcher.PushFrame(frame);
            var exception = Assert.Throws<ScannerException>(() => task.GetAwaiter().GetResult());
            Assert.Equal(error, exception.Kind);
            Assert.Single(session.Pages);
            Assert.Single(model.Pages);
            Assert.NotNull(model.Pages[0].Thumbnail);
            Assert.True(Directory.Exists(session.DirectoryPath));
            Assert.True(session.Read(session.Pages[0]).Bitmap.Width > 0);
            return true;
        });
    }
}
