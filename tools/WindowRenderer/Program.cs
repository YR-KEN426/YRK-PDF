using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    // Preserve the small default allowlist. --allow-any-window/--all use the reviewed construction table.
    private static readonly HashSet<string> ReadOnlyWindows = new(StringComparer.Ordinal)
    {
        "Yrk.PdfStudio.App.Views.SupportDialog",
        "Yrk.PdfStudio.App.Views.BackupDialog",
        "Yrk.PdfStudio.App.Views.ImportDialog",
        "Yrk.PdfStudio.App.Views.PrivacyDialog",
        "Yrk.PdfStudio.App.Views.TrustCertificateDialog",
        "Yrk.PdfStudio.App.Views.TrustedCertificatesDialog"
    };

    // These reviewed parameterless constructors initialize controls only. Do not even enumerate
    // personal stores for their previews: the renderer can run with no access to those stores.
    private static readonly HashSet<string> UiOnlyWindows = new(StringComparer.Ordinal)
    {
        "Yrk.PdfStudio.App.Views.BackupDialog",
        "Yrk.PdfStudio.App.Views.ImportDialog",
        "Yrk.PdfStudio.App.Views.PrivacyDialog"
    };

    private sealed record FileSnapshot(long Length, DateTime LastWriteTimeUtc);
    private sealed record FolderSnapshot(bool Exists, DateTime CreationTimeUtc,
        Dictionary<string, FileSnapshot> Files);

    [STAThread]
    private static int Main(string[] args)
    {
        var theme = "light";
        var themeIndex = Array.IndexOf(args, "--theme");
        if (themeIndex >= 0)
        {
            if (themeIndex + 1 >= args.Length || !Yrk.PdfStudio.Testing.WindowCatalog.Themes.Contains(args[themeIndex + 1])
                || args.Count(arg => arg == "--theme") != 1)
            {
                Console.Error.WriteLine("Usage: --theme light|dark (default: light)");
                return 2;
            }
            theme = args[themeIndex + 1];
            args = args.Take(themeIndex).Concat(args.Skip(themeIndex + 2)).ToArray();
        }
        if (args is ["--all", var folder]) return RenderAll(folder, theme);
        if (args is ["--list"])
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Yrk.PdfStudio.Testing.WindowCatalog.Cases
                .Select(c => new { c.Name, c.Fixture, c.Unavailable })));
            return 0;
        }
        var xamlMode = args.Contains("--xaml", StringComparer.Ordinal);
        var allowAny = args.Contains("--allow-any-window", StringComparer.Ordinal) || xamlMode;
        var positional = args.Where(arg => arg is not ("--allow-any-window" or "--xaml")).ToArray();
        if (positional.Length != 3 || args.Length != 3 + (args.Contains("--allow-any-window", StringComparer.Ordinal) ? 1 : 0) + (xamlMode ? 1 : 0)
            || positional[1] is not ("th" or "en"))
        {
            Console.Error.WriteLine("Usage: WindowRenderer [--theme light|dark] [--allow-any-window] <window class> <th|en> <output.png> | --all <folder> [--theme light|dark] | --list");
            return 2;
        }

        var name = xamlMode ? "XAML:" + Path.GetFullPath(positional[0]) : positional[0].Contains('.') ? positional[0] : "Yrk.PdfStudio.App.Views." + positional[0];
        if (!allowAny && !ReadOnlyWindows.Contains(name))
        {
            Console.Error.WriteLine($"FAIL: {name} is not in the reviewed read-only allowlist; use --allow-any-window to opt in.");
            return 2;
        }

        string dataFolder;
        string output;
        FolderSnapshot? before;
        try
        {
            dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf");
            output = Path.GetFullPath(positional[2]);
            if (output.Equals(dataFolder, StringComparison.OrdinalIgnoreCase)
                || output.StartsWith(dataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("PNG output must be outside the App data folder.");
            // Read metadata only. Never create the folder, copy files or restore user data.
            // Catalog factories use synthetic document/certificate stores and have no data-writing actions.
            // Do not enumerate unrelated personal folders (including DigitalIDs) for these reviewed fixtures.
            before = UiOnlyWindows.Contains(name) || Yrk.PdfStudio.Testing.WindowCatalog.Cases.Any(c =>
                c.Name == name.Split('.').Last() && c.Create is not null) ? null : Snapshot(dataFolder);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.GetBaseException().Message);
            return 1;
        }
        var result = 0;
        string? success = null;
        try
        {
            // Keep App references behind this boundary until the initial snapshot is complete.
            success = Render(name, positional[1], output, theme);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.GetBaseException().Message);
            result = 1;
        }
        finally
        {
            try
            {
                var differences = before is null ? [] : Compare(before, Snapshot(dataFolder));
                if (differences.Count != 0)
                {
                    Console.Error.WriteLine("FAIL: data folder changed");
                    foreach (var difference in differences) Console.Error.WriteLine("  " + difference);
                    result = 4;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: data folder snapshot could not be verified: " + ex.GetBaseException().Message);
                result = 4;
            }
        }
        if (result == 0) Console.WriteLine(success);
        return result;
    }

    private static int RenderAll(string folder, string theme)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var results = new List<object>();
        var failures = 0;
        foreach (var entry in Yrk.PdfStudio.Testing.WindowCatalog.Cases)
        foreach (var language in new[] { "th", "en" })
        {
            var output = Path.Combine(folder, entry.Name + "." + language + ".png");
            string detail;
            int exitCode;
            if (entry.Create is null) { detail = entry.Unavailable!; exitCode = 3; }
            else
            {
                var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                foreach (var arg in new[] { "--theme", theme, "--allow-any-window", entry.Name, language, output }) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(45000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); exitCode = 124; }
                else exitCode = process.ExitCode;
                detail = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                if (exitCode == 124) detail += "Timed out after 45 seconds.";
            }
            var captured = File.Exists(output);
            results.Add(new { Window = entry.Name, Language = language, Theme = theme, entry.Fixture, Captured = captured, ExitCode = exitCode, Detail = detail });
            File.WriteAllText(Path.Combine(folder, entry.Name + "." + language + ".log"), detail);
            Console.WriteLine($"{entry.Name} [{language}, {theme}]: {(captured ? "captured" : "unavailable")}, exit {exitCode}");
            if (exitCode != 0) failures++;
            File.WriteAllText(Path.Combine(folder, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(results,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        return failures == 0 ? 0 : 1;
    }

    private static FolderSnapshot Snapshot(string folder)
    {
        var files = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        var directory = new DirectoryInfo(folder);
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.Directory) == 0)
                throw new IOException("App data path is not a directory.");
        }
        catch (DirectoryNotFoundException) { return new(false, default, files); }
        catch (FileNotFoundException) { return new(false, default, files); }
        var created = directory.CreationTimeUtc;
        foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = 0
        }))
        {
            var file = new FileInfo(path);
            files.Add(Path.GetRelativePath(folder, path), new(file.Length, file.LastWriteTimeUtc));
        }
        return new(true, created, files);
    }

    private static List<string> Compare(FolderSnapshot before, FolderSnapshot after)
    {
        var differences = new List<string>();
        if (before.Exists != after.Exists)
            differences.Add(after.Exists ? "folder created: ." : "folder removed: .");
        else if (before.Exists && before.CreationTimeUtc != after.CreationTimeUtc)
            differences.Add("folder recreated: . (CreationTimeUtc changed)");
        foreach (var path in before.Files.Keys.Union(after.Files.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.Files.TryGetValue(path, out var oldFile)) differences.Add($"added: {path}");
            else if (!after.Files.TryGetValue(path, out var newFile)) differences.Add($"removed: {path}");
            else if (oldFile != newFile)
                differences.Add($"changed: {path} (size {oldFile.Length} -> {newFile.Length}; LastWriteTimeUtc {oldFile.LastWriteTimeUtc:O} -> {newFile.LastWriteTimeUtc:O})");
        }
        return differences;
    }

    private static string Render(string name, string language, string output, string theme)
    {
        using var app = new Yrk.PdfStudio.Testing.WpfTestApp(language, theme);
        using var fixture = new Yrk.PdfStudio.Testing.WindowFixture();
        Window window;
        if (name.StartsWith("XAML:", StringComparison.Ordinal))
        {
            using var stream = File.OpenRead(name[5..]);
            window = new Window { SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                Background = Brushes.White, Content = System.Windows.Markup.XamlReader.Load(stream) };
        }
        else
        {
            var shortName = name.Split('.').Last();
            var entry = Yrk.PdfStudio.Testing.WindowCatalog.Cases.Single(c => c.Name == shortName);
            if (entry.Create is null) throw new NotSupportedException(entry.Unavailable);
            window = entry.Create(fixture);
        }
        var closed = false;
        try
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000; window.Top = -32000;
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Show();
            Yrk.PdfStudio.Testing.WpfTestApp.Layout(window);
            var popupCount = Yrk.PdfStudio.Testing.WpfTestApp.ExercisePopups(window);
            Yrk.PdfStudio.Testing.WpfTestApp.Layout(window);
            var width = checked((int)Math.Ceiling(window.ActualWidth));
            var height = checked((int)Math.Ceiling(window.ActualHeight));
            if (width <= 0 || height <= 0 || width > 10000 || height > 10000)
                throw new InvalidOperationException("Invalid render dimensions.");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var pixels = new byte[checked(width * height * 4)];
            bitmap.CopyPixels(pixels, width * 4, 0);
            var colors = new HashSet<uint>();
            for (var i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 3] != 0) colors.Add(BitConverter.ToUInt32(pixels, i));
            if (colors.Count < 8) throw new InvalidOperationException("Blank image: fewer than eight visible colors.");
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = new FileStream(output, FileMode.CreateNew);
            png.Save(file);
            window.Close(); closed = true;
            Yrk.PdfStudio.Testing.WpfTestApp.Pump();
            // Keep baseline evidence even when a real binding defect is found, but return failure visibly.
            if (app.Errors.Count != 0) throw new InvalidOperationException("Image saved with WPF diagnostics: " + string.Join("\n", app.Errors));
            return $"PASS: {output} ({theme}, {width}x{height}, {colors.Count} colors, {popupCount} popups)";
        }
        finally { if (!closed) window.Close(); Yrk.PdfStudio.Testing.WpfTestApp.Pump(); }
    }
}
