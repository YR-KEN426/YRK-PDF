using System.Reflection;
using System.Windows.Threading;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Security;

namespace Yrk.PdfStudio.Core.Tests;

public sealed partial class SavedItemEditingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Overlapping_password_cancel_and_failed_reopen_release_every_owner(bool transform, bool outerFinishesFirst)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var release = new TaskCompletionSource();
            var armed = false;
            var app = new AppTestHarness(wrapOpenedDocument: d =>
            {
                var proxy = DispatchProxy.Create<IPdfDocument, SignatureSecurityTests.TestProxy>();
                ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = (method, args) =>
                    method.Name == nameof(IAsyncDisposable.DisposeAsync) && armed
                        ? PauseRelease(d) : method.Invoke(d, args);
                return proxy;
            });
            try
            {
                var source = app.CreatePdf();
                var locked = app.PathFor("locked.pdf");
                File.WriteAllBytes(locked, PdfSecurity.Protect(File.ReadAllBytes(source),
                    new PdfProtection("user", "owner", PdfPermissions.All)));
                Assert.True(Await(app.OpenAsync(source)));
                var target = app.PathFor("failed-reopen.pdf");
                // Remove only the test output after replacement, making the outer reopen fail.
                app.Model.PropertyChanged += (_, _) =>
                {
                    if (!app.Model.HasDocument && File.Exists(target)) File.Delete(target);
                };
                armed = true;
                Task outer;
                if (transform)
                {
                    Func<IPdfDocument, byte[], CancellationToken, Task<byte[]?>> change = (_, bytes, _) => Task.FromResult<byte[]?>(bytes);
                    outer = (Task)typeof(MainViewModel).GetMethod("TransformToFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(app.Model, [target, "Testing", change])!;
                }
                else outer = app.SaveToAsync(target);
                PumpUntil(() => app.Model.IsReopeningDocument && !app.Model.HasDocument);
                var dialogs = (SignatureSecurityTests.TestProxy)(object)app.Dialogs.Service;
                var original = dialogs.Handle;
                dialogs.Handle = (method, args) =>
                {
                    if (method.Name != "PromptPassword") return original(method, args);
                    if (outerFinishesFirst)
                    {
                        release.SetResult();
                        Await(outer);
                    }
                    return null;
                };
                Assert.False(Await(app.OpenAsync(locked)));
                if (!outerFinishesFirst)
                {
                    Assert.True(app.Model.IsReopeningDocument);
                    release.SetResult();
                    Await(outer);
                }
                Assert.False(app.Model.HasDocument);
                Assert.False(app.Model.IsReopeningDocument);
                Assert.False(app.Model.ShowsDocument);
                Assert.Empty(app.Model.Pages);
                Assert.Equal("YRK PDF", app.Model.WindowTitle);
                Assert.False(app.Model.IsBusy);
            }
            finally
            {
                release.TrySetResult();
                Await(app.DisposeAsync().AsTask());
            }
            return true;

            async ValueTask PauseRelease(IPdfDocument d)
            {
                armed = false;
                await d.DisposeAsync();
                await release.Task;
            }
        });
    }
}
