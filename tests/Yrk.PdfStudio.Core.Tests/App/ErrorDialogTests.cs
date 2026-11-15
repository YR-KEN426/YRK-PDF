using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Yrk.PdfStudio.App;
using Yrk.PdfStudio.App.Localization;
using Yrk.PdfStudio.App.Services;
using Yrk.PdfStudio.App.Views;
using PdfApp = Yrk.PdfStudio.App.App;

namespace Yrk.PdfStudio.Core.Tests;

[Collection("WPF Application")]
public sealed class ErrorDialogTests
{
    [Fact]
    public void LogError_prefixes_payload_with_code_and_problem_report_redacts_the_log()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-error-log-");
        var previous = PdfApp.LogFolder;
        try
        {
            PdfApp.LogFolder = folder.FullName;
            var error = ErrorCatalog.ScannerNoDevice;
            var exception = new IOException(@"Cannot open C:\Users\PrivateName\secret.pdf; 'ลับ.pdf'; contact private@example.com");

            PdfApp.LogError(error, exception);

            var line = File.ReadLines(Assert.Single(Directory.GetFiles(folder.FullName))).First();
            var payload = line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
            Assert.StartsWith(error.Code + " " + exception, payload);
            var report = ProblemReport.Build(AppUpdater.AssemblyVersion, error, exception, folder.FullName, DateTime.Now);
            Assert.Contains(error.Code, report);
            Assert.Contains("<path>", report);
            Assert.Contains("<email>", report);
            Assert.Contains("'<file>'", report);
            Assert.DoesNotContain("ลับ.pdf", report);
            Assert.DoesNotContain("PrivateName", report);
            Assert.DoesNotContain("secret.pdf", report);
            Assert.DoesNotContain("private@example.com", report);

            PdfApp.LogError(error);
            Assert.Contains(error.Code + " " + error.Message, File.ReadAllText(Assert.Single(Directory.GetFiles(folder.FullName))));
        }
        finally
        {
            PdfApp.LogFolder = previous;
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Assembly_version_helper_reads_the_version_without_build_metadata()
    {
        var version = typeof(AppUpdater).Assembly.GetName().Version!;
        Assert.StartsWith($"{version.Major}.{version.Minor}.{version.Build}", AppUpdater.AssemblyVersion);
        Assert.DoesNotContain("+", AppUpdater.AssemblyVersion);
    }

    [Fact]
    public void Scripted_dialogs_record_both_error_overloads_and_arguments()
    {
        var dialogs = new ScriptedDialogs();
        var exception = new IOException("test");
        dialogs.Service.ShowError("old error");
        dialogs.Service.ShowError(ErrorCatalog.ScannerOther, exception, 3);
        var calls = dialogs.Named("ShowError");
        Assert.Equal(2, calls.Length);
        Assert.Equal("old error", calls[0].Arguments[0]);
        Assert.Same(ErrorCatalog.ScannerOther, calls[1].Arguments[0]);
        Assert.Same(exception, calls[1].Arguments[1]);
        Assert.Equal(3, Assert.IsType<object[]>(calls[1].Arguments[2])[0]);
    }

    [Fact]
    public void Dialog_starts_collapsed_and_copy_shows_feedback_without_crashing_on_clipboard_failure()
    {
        Sta.Run(() =>
        {
            void Reset() => typeof(MainWindowLayoutTests).GetMethod("ForgetApplicationFromEarlierTest",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            Reset();
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(MainWindow).Assembly);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/YrkEasyPdf;component/Themes/Styles.xaml", UriKind.Relative) });
            ErrorDialog? dialog = null;
            try
            {
                var exception = new IOException("Cannot open C:\\Users\\PrivateName\\secret.pdf; “สัญญา ลูกค้า.docx”");
                dialog = new ErrorDialog(new ErrorInfo("YRK-SCN-009", "Page {0}", "Try scanning again."), exception, 3);
                Assert.Equal(Loc.F("Page {0}", 3), ((TextBlock)dialog.FindName("MessageText")).Text);
                Assert.False(((Expander)dialog.FindName("DetailsExpander")).IsExpanded);
                var copy = (Button)dialog.FindName("CopyButton");
                Assert.True(copy.IsTabStop);
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(copy)));
                var ok = (Button)dialog.FindName("OkButton");
                Assert.True(ok.IsDefault);
                Assert.True(ok.IsCancel);
                var details = ((TextBox)dialog.FindName("DetailsText")).Text;
                Assert.Contains(nameof(IOException), details);
                Assert.Contains($"HRESULT: 0x{exception.HResult:X8}", details);
                Assert.Contains("<path>", details);
                Assert.Contains("“<file>”", details);
                Assert.DoesNotContain("สัญญา ลูกค้า.docx", details);
                Assert.DoesNotContain("secret.pdf", details);

                string? copied = null;
                dialog.CopyText = text => copied = text;
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("YRK-SCN-009", copied!);
                Assert.Contains(ReportRedactor.Redact(Feedback.ProgramInfo(AppUpdater.AssemblyVersion)), copied!);
                Assert.DoesNotContain("secret.pdf", copied!);
                Assert.Equal(Loc.T("Copied"), ((TextBlock)dialog.FindName("CopyFeedback")).Text);
                dialog.CopyText = _ => throw new System.Runtime.InteropServices.COMException("Clipboard busy");
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Loc.T("Could not copy. Try again."), ((TextBlock)dialog.FindName("CopyFeedback")).Text);
            }
            finally
            {
                dialog?.Close();
                application.Shutdown();
                Reset();
            }
            return true;
        });
    }
}
