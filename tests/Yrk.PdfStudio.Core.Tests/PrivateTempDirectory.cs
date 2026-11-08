using System.Runtime.CompilerServices;

namespace Yrk.PdfStudio.Core.Tests;

internal static class PrivateTempDirectory
{
    public static string Path { get; private set; } = null!;

#pragma warning disable CA2255 // Redirect temp before any test can initialize PdfTemporaryFiles.Shared.
    [ModuleInitializer]
    internal static void Initialize()
#pragma warning restore CA2255
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yrk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Yrk.PdfStudio.App.Services.ReadingPositions.FilePath = System.IO.Path.Combine(Path, "settings", "reading-positions.json");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteTree(Path);
        Environment.SetEnvironmentVariable("TMP", Path, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", Path, EnvironmentVariableTarget.Process);
    }

    private static void DeleteTree(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
                File.Delete(path);
            else
            {
                // Delete junctions and directory symlinks themselves, never their targets.
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path)) DeleteTree(entry);
                Directory.Delete(path);
            }
        }
        catch (Exception) { } // Process-exit cleanup is best effort, including files still leased by Shared.
    }
}
