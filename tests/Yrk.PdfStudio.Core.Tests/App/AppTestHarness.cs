using System.Reflection;
using System.Runtime.ExceptionServices;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Real PDFium and app save pipeline; no windows, personal signature store or recent-file writes.</summary>
internal sealed class AppTestHarness : IAsyncDisposable
{
    public string Folder { get; } = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "app-" + Guid.NewGuid().ToString("N"))).FullName;
    public PdfiumEngine Engine { get; } = new();
    public ScriptedDialogs Dialogs { get; } = new();
    public MainViewModel Model { get; }
    public TrustedCertificateStore TrustedCertificates { get; }
    public string? FailedWritePath { get; private set; }
    public bool PartialFileExistedAtFailure { get; private set; }

    public AppTestHarness(bool failWriteMidway = false, Func<IPdfDocument, IPdfDocument>? wrapOpenedDocument = null)
    {
        var store = DispatchProxy.Create<ISignatureStore, SignatureSecurityTests.TestProxy>();
        ((SignatureSecurityTests.TestProxy)(object)store).Handle = (method, _) => method.Name == "LoadAll"
            ? Array.Empty<SavedSignature>() : throw new InvalidOperationException(method.Name);
        var modelEngine = failWriteMidway || wrapOpenedDocument is not null
            ? Forward<IPdfEngine>((method, args) =>
            {
                var result = ForwardCall(Engine, method, args);
                return result is Task<IPdfDocument> opening ? WrapDocumentAsync(opening, wrapOpenedDocument) : result;
            })
            : Engine;
        TrustedCertificates = new TrustedCertificateStore(PathFor("trusted-certificates"), File.Delete);
        Model = new MainViewModel(modelEngine, Dialogs.Service, store, TrustedCertificates);
    }

    private async Task<IPdfDocument> WrapDocumentAsync(Task<IPdfDocument> opening, Func<IPdfDocument, IPdfDocument>? wrapOpenedDocument)
    {
        var document = await opening;
        if (wrapOpenedDocument is not null) return wrapOpenedDocument(document);
        return Forward<IPdfDocument>((method, args) =>
            method.Name == nameof(IPdfDocument.SaveAsync)
                ? FailWriteAsync((string)args![0]!, (CancellationToken)args[1]!)
                : ForwardCall(document, method, args));
    }

    private async Task FailWriteAsync(string path, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(path, new byte[] { 0x25, 0x50, 0x44, 0x46 }, cancellationToken);
        FailedWritePath = path;
        PartialFileExistedAtFailure = File.Exists(path) && new FileInfo(path).Length > 0;
        throw new IOException("Simulated failure after writing a partial PDF.");
    }

    private static T Forward<T>(Func<MethodInfo, object?[]?, object?> handle) where T : class
    {
        var proxy = DispatchProxy.Create<T, SignatureSecurityTests.TestProxy>();
        ((SignatureSecurityTests.TestProxy)(object)proxy).Handle = handle;
        return proxy;
    }

    private static object? ForwardCall(object target, MethodInfo method, object?[]? args)
    {
        try
        {
            // Return Task/ValueTask objects unchanged, preserving their result and exception types.
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    public string PathFor(string name) => Path.Combine(Folder, name);

    public string CreatePdf()
    {
        var path = PathFor("source.pdf");
        TestPdfFactory.CreateReportPdf(path);
        return path;
    }

    public Task<bool> OpenAsync(string path) => Invoke<Task<bool>>("OpenDocumentAsync", path, null, null, false, null, null);
    public object CaptureViewState() => Invoke<object>("CaptureViewState");
    public Task<bool> OpenWithViewStateAsync(string path, object state) => Invoke<Task<bool>>("OpenDocumentAsync", path, null, state, false, null, null);
    public Task<bool> SaveToAsync(string path) => Invoke<Task<bool>>("SaveToAsync", path);

    private T Invoke<T>(string name, params object?[] args) =>
        (T)typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Model, args)!;

    public async Task RotateAsync()
    {
        await Model.RotatePagesAsync([0], 1);
        Assert.True(Model.IsDirty);
        Assert.False(Model.IsBusy);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A freshly reopened version may still be reading signatures from this test's PDF.
            if (typeof(MainViewModel).GetField("_signatureLoadTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Model) is Task signatures) await signatures;
            await Model.CloseTabAsync();
        }
        finally
        {
            Engine.Dispose();
            Directory.Delete(Folder, recursive: true);
        }
    }
}

internal sealed class ScriptedDialogs
{
    public sealed record Call(string Name, object?[] Arguments);
    private readonly Dictionary<string, Queue<object?>> _answers = [];
    public List<Call> Calls { get; } = [];
    public IDialogService Service { get; }

    public ScriptedDialogs()
    {
        Service = DispatchProxy.Create<IDialogService, SignatureSecurityTests.TestProxy>();
        ((SignatureSecurityTests.TestProxy)(object)Service).Handle = (method, args) =>
        {
            Calls.Add(new(method.Name, args?.ToArray() ?? []));
            if (_answers.TryGetValue(method.Name, out var answers) && answers.Count > 0)
            {
                var answer = answers.Dequeue();
                return answer is Func<object?[], object?> callback ? callback(args ?? []) : answer;
            }
            if (method.Name is "ShowError" or "ShowInfo") return null;
            throw new InvalidOperationException($"No scripted answer for {method.Name}");
        };
    }

    // Method names cover both PickSavePdfFile overloads and all other IDialogService questions,
    // including PromptPassword, ConfirmPassword, AskOwnerPassword and AskSaveChanges.
    public void Answer(string method, params object?[] answers)
    {
        if (!_answers.TryGetValue(method, out var queue)) _answers[method] = queue = new();
        foreach (var answer in answers) queue.Enqueue(answer);
    }

    public Call[] Named(string name) => Calls.Where(call => call.Name == name).ToArray();
}
