using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Threading;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using PdfApp = Yrk.PdfStudio.App.App;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class CrashHandlerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("yrk-crash-handlers-");
    private readonly string _previousLogFolder = PdfApp.LogFolder;
    private readonly string _previousRecoveryFolder = RecoveryStore.Folder;
    private readonly Action<string> _previousMessageBox = PdfApp.CrashMessageBox;
    private readonly string _previousLanguage = Loc.Language;
    private readonly List<string> _messages = [];

    public CrashHandlerTests()
    {
        PdfApp.LogFolder = Path.Combine(_root.FullName, "logs");
        RecoveryStore.Folder = Path.Combine(_root.FullName, "recovery");
        PdfApp.CrashMessageBox = _messages.Add;
    }

    public void Dispose()
    {
        PdfApp.LogFolder = _previousLogFolder;
        RecoveryStore.Folder = _previousRecoveryFolder;
        PdfApp.CrashMessageBox = _previousMessageBox;
        Loc.Language = _previousLanguage;
        _root.Delete(recursive: true);
    }

    [Fact]
    public void Dispatcher_uses_catalog_dialog_logs_once_and_marks_exception_handled()
    {
        var exception = new IOException("Fake dispatcher failure with private details");
        var dialogs = LoggingDialogs();

        Assert.True(HandleDispatcher(exception, dialogs.Service));

        var call = Assert.Single(dialogs.Named("ShowError"));
        Assert.Same(ErrorCatalog.AppDispatcherUnhandled, call.Arguments[0]);
        Assert.Same(exception, call.Arguments[1]);
        var log = ReadLog();
        Assert.Contains("YRK-APP-001 " + exception, log);
        Assert.Equal(1, log.Split("YRK-APP-001", StringSplitOptions.None).Length - 1);
        Assert.Empty(_messages);
    }

    [Theory]
    [InlineData(Loc.English)]
    [InlineData(Loc.Thai)]
    public void Dispatcher_falls_back_to_fixed_localized_message_when_dialog_throws(string language)
    {
        Loc.Language = language;
        var exception = new IOException(@"Private C:\Users\Someone\secret.pdf");
        var dialogs = ThrowingDialogs();

        Assert.True(HandleDispatcher(exception, dialogs.Service));

        Assert.Contains("YRK-APP-001 " + exception, ReadLog());
        const string key = "Something went wrong. Try again. If the problem continues, restart the app.";
        var message = Assert.Single(_messages);
        Assert.Equal(Loc.T(key), message);
        Assert.DoesNotContain(exception.Message, message);
        if (language == Loc.Thai) Assert.NotEqual(key, message);
    }

    [Fact]
    public void Dispatcher_swallows_fallback_failure_and_releases_guard()
    {
        PdfApp.CrashMessageBox = _ => throw new InvalidOperationException("Fake MessageBox failure");
        Assert.True(HandleDispatcher(new IOException("first"), ThrowingDialogs().Service));

        PdfApp.CrashMessageBox = _messages.Add;
        Assert.True(HandleDispatcher(new IOException("second"), ThrowingDialogs().Service));

        Assert.Single(_messages);
        Assert.Contains("YRK-APP-001", ReadLog());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispatcher_reentrancy_in_dialog_or_fallback_does_not_open_another_dialog(bool duringFallback)
    {
        var nestedDialogs = new ScriptedDialogs();
        var nested = new IOException("Fake nested dispatcher failure");
        var dialogs = new ScriptedDialogs();
        void Reenter() => Assert.True(HandleDispatcher(nested, nestedDialogs.Service));
        dialogs.Answer("ShowError", (Func<object?[], object?>)(args =>
        {
            PdfApp.LogError((ErrorInfo)args[0]!, (Exception)args[1]!);
            if (duringFallback) throw new InvalidOperationException("Fake dialog failure");
            Reenter();
            return null;
        }));
        PdfApp.CrashMessageBox = message =>
        {
            _messages.Add(message);
            Reenter();
        };

        Assert.True(HandleDispatcher(new IOException("outer"), dialogs.Service));

        Assert.Single(dialogs.Named("ShowError"));
        Assert.Empty(nestedDialogs.Calls);
        Assert.Equal(duringFallback ? 1 : 0, _messages.Count);
        Assert.Contains("YRK-APP-001 " + nested, ReadLog());
    }

    [Theory]
    [InlineData(Loc.English)]
    [InlineData(Loc.Thai)]
    public void App_domain_logs_original_exception_and_shows_short_localized_message(string language)
    {
        Loc.Language = language;
        var exception = new IOException("Fake fatal failure with private details");

        PdfApp.OnAppDomainUnhandledException(null, new UnhandledExceptionEventArgs(exception, isTerminating: true));

        Assert.Contains("YRK-APP-002 " + exception, ReadLog());
        var message = Assert.Single(_messages);
        Assert.Equal(Loc.T(ErrorCatalog.AppDomainUnhandled.Message), message);
        Assert.DoesNotContain(exception.Message, message);
        if (language == Loc.Thai) Assert.NotEqual(ErrorCatalog.AppDomainUnhandled.Message, message);
    }

    [Fact]
    public void App_domain_swallows_message_box_failure_and_releases_guard()
    {
        var args = new UnhandledExceptionEventArgs(new IOException("Fake fatal failure"), isTerminating: true);
        PdfApp.CrashMessageBox = _ => throw new InvalidOperationException("Fake MessageBox failure");

        PdfApp.OnAppDomainUnhandledException(null, args);
        PdfApp.CrashMessageBox = _messages.Add;
        PdfApp.OnAppDomainUnhandledException(null, args);

        Assert.Single(_messages);
        Assert.Contains("YRK-APP-002", ReadLog());
    }

    [Fact]
    public void Unobserved_task_logs_aggregate_marks_observed_and_never_shows_a_dialog()
    {
        var exception = new AggregateException(new IOException("Fake task failure"));
        var args = new UnobservedTaskExceptionEventArgs(exception);
        Assert.False(args.Observed);
        PdfApp.OnTaskUnobservedException(null, args);

        Assert.True(args.Observed);
        Assert.Contains("YRK-APP-003 " + exception, ReadLog());
        Assert.Empty(_messages);
    }

    [Fact]
    public void Handlers_do_not_create_or_modify_files_outside_the_log_folder()
    {
        var documents = Directory.CreateDirectory(Path.Combine(_root.FullName, "documents"));
        File.WriteAllText(Path.Combine(documents.FullName, "user.pdf"), "%PDF-1.7\noriginal user content");
        var recovery = Directory.CreateDirectory(RecoveryStore.Folder);
        File.WriteAllText(Path.Combine(recovery.FullName, "unsaved.pdf"), "%PDF-1.7\nexisting recovery copy");
        var filesBefore = SnapshotFiles();
        var directoriesBefore = SnapshotDirectories();
        var exception = new IOException("Fake crash involving " + Path.Combine(documents.FullName, "user.pdf"));

        void AssertUnchanged()
        {
            Assert.Equal(filesBefore, SnapshotFiles());
            Assert.Equal(directoriesBefore, SnapshotDirectories());
        }

        Assert.True(HandleDispatcher(exception, LoggingDialogs().Service));
        AssertUnchanged();
        Assert.True(HandleDispatcher(exception, ThrowingDialogs().Service));
        AssertUnchanged();
        PdfApp.OnAppDomainUnhandledException(null, new UnhandledExceptionEventArgs(exception, true));
        AssertUnchanged();
        PdfApp.OnTaskUnobservedException(null, new UnobservedTaskExceptionEventArgs(new AggregateException(exception)));
        AssertUnchanged();
        var log = ReadLog();
        Assert.Contains("YRK-APP-001", log);
        Assert.Contains("YRK-APP-002", log);
        Assert.Contains("YRK-APP-003", log);
    }

    private string[] SnapshotFiles() => Directory.GetFiles(_root.FullName, "*", SearchOption.AllDirectories)
        .Where(path => !IsLogPath(path))
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{Path.GetRelativePath(_root.FullName, path)}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .ToArray();

    private string[] SnapshotDirectories() => Directory.GetDirectories(_root.FullName, "*", SearchOption.AllDirectories)
        .Where(path => !IsLogPath(path))
        .Select(path => Path.GetRelativePath(_root.FullName, path))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private static bool IsLogPath(string path) => path.Equals(PdfApp.LogFolder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(PdfApp.LogFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static ScriptedDialogs LoggingDialogs()
    {
        var dialogs = new ScriptedDialogs();
        // Match the real ShowError contract: it logs before opening any UI.
        dialogs.Answer("ShowError", (Func<object?[], object?>)(args =>
        {
            PdfApp.LogError((ErrorInfo)args[0]!, (Exception)args[1]!);
            return null;
        }));
        return dialogs;
    }

    private static ScriptedDialogs ThrowingDialogs()
    {
        var dialogs = new ScriptedDialogs();
        dialogs.Answer("ShowError", (Func<object?[], object?>)(_ => throw new InvalidOperationException("Fake dialog failure")));
        return dialogs;
    }

    private static bool HandleDispatcher(Exception exception, IDialogService dialogs) => Sta.Run(() =>
    {
        var args = (DispatcherUnhandledExceptionEventArgs)Activator.CreateInstance(
            typeof(DispatcherUnhandledExceptionEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [Dispatcher.CurrentDispatcher], culture: null)!;
        typeof(DispatcherUnhandledExceptionEventArgs).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(args, [exception, false]);
        PdfApp.HandleDispatcherUnhandledException(args, dialogs);
        return args.Handled;
    });

    private static string ReadLog() => File.ReadAllText(Assert.Single(Directory.GetFiles(PdfApp.LogFolder, "*.log")));
}
