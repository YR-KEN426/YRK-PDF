using System.IO;
using System.Text.Json;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

/// <summary>Crash recovery copies: kept while changes are unsaved, offered back after a run that was cut off.</summary>
[Collection(nameof(RecoveryStoreTests))] // RecoveryStore.Folder is shared.
public sealed class RecoveryStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("yrk-recovery-");
    private readonly string _savedFolder = RecoveryStore.Folder;

    public RecoveryStoreTests()
    {
        RecoveryStore.Folder = Path.Combine(_root.FullName, "Recovery");
        Directory.CreateDirectory(RecoveryStore.Folder);
    }

    public void Dispose()
    {
        RecoveryStore.DeleteWritten();
        RecoveryStore.Folder = _savedFolder;
        _root.Delete(recursive: true);
    }

    [Fact]
    public void Does_not_offer_the_copies_of_the_running_program()
    {
        var pdf = Path.Combine(RecoveryStore.Folder, "x.tmp");
        File.WriteAllText(pdf, "%PDF-1.7");

        RecoveryStore.Keep("aaaa", pdf, Path.Combine(_root.FullName, "Quote.pdf"), "Quote.pdf");

        Assert.True(File.Exists(Path.Combine(RecoveryStore.Folder, "aaaa.pdf")));
        Assert.Empty(RecoveryStore.List());

        RecoveryStore.DeleteWritten();
        Assert.Empty(Directory.GetFiles(RecoveryStore.Folder));
    }

    [Fact]
    public void Puts_a_left_over_copy_next_to_its_original()
    {
        var original = Path.Combine(_root.FullName, "Quote.pdf");
        File.WriteAllText(original, "original");
        LeftOver("bbbb", original, "%PDF recovered");

        var entry = Assert.Single(RecoveryStore.List());
        var restored = RecoveryStore.Restore(entry);

        Assert.Equal(_root.FullName, Path.GetDirectoryName(restored));
        Assert.Contains("(recovered)", Path.GetFileName(restored), StringComparison.Ordinal);
        Assert.Equal("%PDF recovered", File.ReadAllText(restored));
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Empty(RecoveryStore.List());
    }

    [Fact]
    public void Deletes_left_over_copies_that_are_not_wanted()
    {
        LeftOver("cccc", Path.Combine(_root.FullName, "A.pdf"), "%PDF");

        RecoveryStore.DeleteAllButWritten();

        Assert.Empty(RecoveryStore.List());
        Assert.Empty(Directory.GetFiles(RecoveryStore.Folder));
    }

    /// <summary>A copy written by an earlier run of the program.</summary>
    private static void LeftOver(string id, string original, string content)
    {
        var entry = new RecoveryEntry(id, original, Path.GetFileName(original), DateTime.Now);
        File.WriteAllText(entry.PdfPath, content);
        File.WriteAllText(Path.Combine(RecoveryStore.Folder, id + ".json"), JsonSerializer.Serialize(entry));
    }
}
