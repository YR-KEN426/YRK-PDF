using System.Reflection;
using System.Collections.ObjectModel;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.ViewModels;
using Yrk.PdfStudio.Core.Pdf;

namespace Yrk.PdfStudio.Core.Tests;

internal static class PageReviewTestSupport
{
    // Non-temporary bookmark paths are eligible for RecentFiles. Isolate its observable list
    // from UI tests, which may already have attached a Dispatcher-owned CollectionView.
    internal sealed class RecentFileScope : IDisposable
    {
        private static FieldInfo Items => typeof(RecentFiles).GetField("s_items", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object? _previous = Items.GetValue(null);
        internal RecentFileScope() => Items.SetValue(null, new ObservableCollection<RecentFile>());
        public void Dispose() => Items.SetValue(null, _previous);
    }

    internal static string Sample(AppTestHarness app, string name = "source.pdf")
    {
        var path = app.PathFor(name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf"), path);
        return path;
    }

    internal static IPdfDocument Document(AppTestHarness app) => (IPdfDocument)typeof(MainViewModel)
        .GetField("_document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app.Model)!;

    internal static async Task UndoRedo(AppTestHarness app, bool redo = false)
    {
        (redo ? app.Model.RedoCommand : app.Model.UndoCommand).Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (app.Model.IsBusy) await Task.Delay(5, timeout.Token);
        Assert.Empty(app.Dialogs.Named("ShowError"));
    }
}
