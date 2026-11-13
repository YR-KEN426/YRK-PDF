using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Localization;

internal static class Program
{
    // Reviewed constructors/XAML: SupportDialog initializes UI only; TrustCertificateDialog
    // describes its certificate; TrustedCertificatesDialog calls the read-only LoadAll.
    // TrustCertificateDialog uses an in-memory fixture; TrustedCertificatesDialog
    // still requires a store argument and is not supported by Render.
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
        var xamlMode = args.Contains("--xaml", StringComparer.Ordinal);
        var allowAny = args.Contains("--allow-any-window", StringComparer.Ordinal) || xamlMode;
        var positional = args.Where(arg => arg is not ("--allow-any-window" or "--xaml")).ToArray();
        if (positional.Length != 3 || args.Length != 3 + (args.Contains("--allow-any-window", StringComparer.Ordinal) ? 1 : 0) + (xamlMode ? 1 : 0)
            || positional[1] is not ("th" or "en"))
        {
            Console.Error.WriteLine("Usage: WindowRenderer [--allow-any-window] <Views window class> <th|en> <output.png>");
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
            before = UiOnlyWindows.Contains(name) ? null : Snapshot(dataFolder);
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
            success = Render(name, positional[1], output);
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string Render(string name, string language, string output)
    {
        // A plain Application loads the App resources without running App.OnStartup/OnExit,
        // which start the main UI, updater, settings and recovery services.
        var assembly = typeof(Yrk.PdfStudio.App.App).Assembly;
        // Relative pack URIs (/Assets/x.jpg) resolve against Application.ResourceAssembly, which WPF has already fixed to this tool;
        // the setter then throws, so point the private field at App directly.
        typeof(Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, assembly);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assembly.GetName().Name};component/Themes/Styles.xaml")
        });
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Loc.Language = language;
        UiTranslator.Start();
        var xamlPath = name.StartsWith("XAML:", StringComparison.Ordinal) ? name[5..] : null;
        var fullName = name.Contains('.') ? name : "Yrk.PdfStudio.App.Views." + name;
        var type = xamlPath is null ? assembly.GetType(fullName, throwOnError: true)! : typeof(Window);
        if (xamlPath is null && (type.Namespace != "Yrk.PdfStudio.App.Views" || !typeof(Window).IsAssignableFrom(type)))
            throw new ArgumentException("Choose a Window class in App.Views.");
        var isCertificate = fullName == "Yrk.PdfStudio.App.Views.TrustCertificateDialog";
        if (xamlPath is null && !isCertificate && type.GetConstructor(Type.EmptyTypes) is null)
            throw new ArgumentException($"{name} needs constructor arguments; this tool supports parameterless windows only.");
        Window? window = null;
        try
        {
            // Ephemeral fixture: never read user certificates or import into a store.
            using var key = isCertificate ? RSA.Create(2048) : null;
            using var certificate = key is null ? null : new CertificateRequest(
                "CN=WindowRenderer Example, O=YRK Test", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1).CreateSelfSigned(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2036, 1, 1, 0, 0, 0, TimeSpan.Zero));
            if (xamlPath is not null)
            {
                // A loose XAML fragment (e.g. a toolbar copied from MainWindow) shown with the App styles.
                using var stream = File.OpenRead(xamlPath);
                window = new Window
                {
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStyle = WindowStyle.None,
                    Background = Brushes.White,
                    Content = System.Windows.Markup.XamlReader.Load(stream)
                };
            }
            else
            {
                window = (Window)(isCertificate
                    ? Activator.CreateInstance(type, certificate!)!
                    : Activator.CreateInstance(type)!);
            }
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            // Windows that fill themselves asynchronously (e.g. the scan dialog's demo pages): pump messages first.
            if (int.TryParse(Environment.GetEnvironmentVariable("YRK_RENDER_WAIT_MS"), out var waitMs) && waitMs is > 0 and <= 20000)
            {
                var until = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (DateTime.UtcNow < until)
                {
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    System.Threading.Thread.Sleep(50);
                }
            }
            window.Measure(new Size(window.ActualWidth, window.ActualHeight));
            window.Arrange(new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
            // Do not replace an existing screenshot accidentally.
            using var file = new FileStream(output, FileMode.CreateNew);
            png.Save(file);
            return $"PASS: {output} ({width}x{height}, {colors.Count} colors)";
        }
        finally
        {
            try { window?.Close(); }
            finally { app.Shutdown(); }
        }
    }
}
