using System.Reflection;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf.Pdfium;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Real PDFium and app save pipeline; no windows, personal signature store or recent-file writes.</summary>
internal sealed class AppTestHarness : IAsyncDisposable
{
    public string Folder { get; } = Directory.CreateDirectory(Path.Combine(PrivateTempDirectory.Path, "app-" + Guid.NewGuid().ToString("N"))).FullName;
    public PdfiumEngine Engine { get; } = new();
    public ScriptedDialogs Dialogs { get; } = new();
    public MainViewModel Model { get; }

    public AppTestHarness()
    {
        var store = DispatchProxy.Create<ISignatureStore, SignatureSecurityTests.TestProxy>();
        ((SignatureSecurityTests.TestProxy)(object)store).Handle = (method, _) => method.Name == "LoadAll"
            ? Array.Empty<SavedSignature>() : throw new InvalidOperationException(method.Name);
        Model = new MainViewModel(Engine, Dialogs.Service, store);
    }

    public string PathFor(string name) => Path.Combine(Folder, name);

    public string CreatePdf()
    {
        var path = PathFor("source.pdf");
        TestPdfFactory.CreateReportPdf(path);
        return path;
    }

    public Task<bool> OpenAsync(string path) => Invoke<Task<bool>>("OpenDocumentAsync", path, null, null, false, null, null);
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
        try { await Model.CloseTabAsync(); }
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
                return answers.Dequeue();
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
