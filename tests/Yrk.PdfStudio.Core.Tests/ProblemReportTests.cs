using System.Globalization;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class ProblemReportTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("yrk-problem-report-");
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0);
    private string TodayLog => Path.Combine(_root.FullName, "errors-2026-11-15.log");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(100)]
    public void Includes_only_the_last_forty_lines_of_todays_log(int count)
    {
        var structure = Enumerable.Range(0, count)
            .Select(i => $"[2026-11-15T12:00:00.{i:D7}] YRK-EDIT-005 System.IO.IOException").ToArray();
        var lines = structure.Select(line => line + ": Open Acme payroll.pdf failed.").ToArray();
        File.WriteAllLines(TodayLog, lines);
        File.WriteAllText(Path.Combine(_root.FullName, "errors-2026-11-14.log"), "yesterday must not appear");

        var report = ProblemReport.Build("1.2.3", null, null, _root.FullName, Now);

        var marker = ReportRedactor.Redact("\n\nRecent log:\n");
        var markerIndex = report.IndexOf(marker, StringComparison.Ordinal);
        var tail = markerIndex < 0 ? "" : report[(markerIndex + marker.Length)..];
        Assert.Equal(ReportRedactor.Redact(string.Join('\n', structure.TakeLast(40))), tail);
        Assert.True(tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 40);
        Assert.DoesNotContain("yesterday must not appear", report);
        Assert.DoesNotContain("Acme", report);
        if (count > 40) Assert.DoesNotContain(ReportRedactor.Redact(structure[0]), tail);
    }

    [Fact]
    public void Program_info_comes_first_followed_by_optional_code_and_exception_type_only()
    {
        var report = ProblemReport.Build("1.2.3", ErrorCatalog.AppDispatcherUnhandled,
            new InvalidOperationException("exception message must not be included", new Exception("inner exception")), _root.FullName, Now);

        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")
            + "\nError code: YRK-APP-001\nException: InvalidOperationException"), report);
        Assert.StartsWith(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")), report);
    }

    [Fact]
    public void Entire_report_including_program_info_and_log_is_redacted()
    {
        var version = $"1.2.3 person@example.com x{Environment.UserName.ToUpperInvariant()}x x{Environment.MachineName.ToLowerInvariant()}x";
        var line = $"[2026-11-15T12:00:00.0000000] YRK-FILE-002 System.IO.IOException: \"C:\\Program Files\\Private App\\ลับ ใหม่.pdf\" \"\\\\server\\share\\secret.pdf\" "
            + $"%USERPROFILE% \"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\" "
            + $"x{Environment.UserName.ToUpperInvariant()}x x{Environment.MachineName.ToLowerInvariant()}x mail@example.com";
        const string frame = @"   at Namespace.Type.Method(String path) in C:\Private Folder\Acme payroll.cs:line 42";
        File.WriteAllLines(TodayLog, [line, frame]);

        var report = ProblemReport.Build(version, null, null, _root.FullName, Now);

        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo(version)
            + "\n\nRecent log:\n[2026-11-15T12:00:00.0000000] YRK-FILE-002 System.IO.IOException"
            + "\n   at Namespace.Type.Method(String path) in <path>:line 42"), report);
        Assert.Contains("<path>", report);
        Assert.Contains("<user>", report);
        Assert.Contains("<pc>", report);
        Assert.Contains("<email>", report);
        Assert.DoesNotContain("ลับ ใหม่.pdf", report);
        Assert.DoesNotContain("secret.pdf", report);
        Assert.DoesNotContain("@example.com", report);
    }

    [Fact]
    public void Daily_log_name_uses_the_invariant_calendar()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            File.WriteAllText(TodayLog, "YRK-FILE-002 System.IO.IOException: invariant calendar log");
            File.WriteAllText(Path.Combine(_root.FullName, "errors-2569-11-15.log"), "YRK-EDIT-005 wrong calendar");

            var report = ProblemReport.Build("1.2.3", null, null, _root.FullName, Now);

            Assert.Contains("YRK-FILE-002 System.IO.IOException", report);
            Assert.DoesNotContain("YRK-EDIT-005", report);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Theory]
    [InlineData("Open Acme payroll.pdf failed.")]
    [InlineData("Open Customer private.yrkbackup failed.")]
    [InlineData("Acme Client, Bob Smith: confidential message")]
    [InlineData("[Acme payroll.pdf] arbitrary text")]
    [InlineData("Open Acme PayrollException.pdf failed. Message mentions System.IO.IOException")]
    [InlineData("Open Acme YRK-SIG-025.pdf failed.")]
    public void Free_form_log_lines_are_omitted_completely(string line)
    {
        File.WriteAllText(TodayLog, line);
        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")),
            ProblemReport.Build("1.2.3", null, null, _root.FullName, Now));
    }

    [Fact]
    public void Keeps_structure_but_discards_all_exception_messages_and_frame_suffixes()
    {
        File.WriteAllLines(TodayLog,
        [
            "[2026-11-15T12:00:00.0000000+07:00] YRK-SIG-025 System.IO.IOException: Open Acme payroll.pdf failed.",
            " ---> System.Net.Http.HttpRequestException: Bob private message",
            "InvalidOperationException: client name",
            "System.Exception: Open Acme PayrollException.pdf YRK-UPD-002 failed.",
            "   at Namespace.Type.Method(String path, Int32 count) in Acme payroll.cs:line 7 client name",
            "   at Namespace.Type.Other() client name",
            "   at Namespace.Type.Generic[T](T value) in relative folder/Acme private.cs:line 8",
            "arbitrary message without structure",
        ]);

        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3") + "\n\nRecent log:\n"
            + "[2026-11-15T12:00:00.0000000+07:00] YRK-SIG-025 System.IO.IOException\n"
            + "System.Net.Http.HttpRequestException\nInvalidOperationException\nSystem.Exception\n"
            + "   at Namespace.Type.Method(String path, Int32 count) in <path>:line 7\n"
            + "   at Namespace.Type.Other()\n   at Namespace.Type.Generic[T](T value) in <path>:line 8"),
            ProblemReport.Build("1.2.3", null, null, _root.FullName, Now));
    }

    [Fact]
    public void Forty_line_limit_counts_discarded_message_lines_too()
    {
        File.WriteAllLines(TodayLog, new[] { "YRK-APP-001 System.Exception: old error" }
            .Concat(Enumerable.Repeat("Open Acme payroll.pdf failed.", 40)));
        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")),
            ProblemReport.Build("1.2.3", null, null, _root.FullName, Now));
    }

    [Fact]
    public void Missing_log_file_or_folder_still_returns_program_info()
    {
        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")),
            ProblemReport.Build("1.2.3", null, null, _root.FullName, Now));
        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")),
            ProblemReport.Build("1.2.3", null, null, Path.Combine(_root.FullName, "missing"), Now));
    }

    [Fact]
    public void Unreadable_log_still_returns_program_info()
    {
        File.WriteAllText(TodayLog, "private log");
        using var locked = new FileStream(TodayLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")),
            ProblemReport.Build("1.2.3", null, null, _root.FullName, Now));
    }

    [Fact]
    public void Reparse_point_log_folder_is_ignored()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root.FullName, "target")).FullName;
        var targetLog = Path.Combine(target, "errors-2026-11-15.log");
        File.WriteAllText(targetLog, "linked-log-must-not-be-read");
        var junction = Path.Combine(_root.FullName, "junction");
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Equal(ReportRedactor.Redact(Feedback.ProgramInfo("1.2.3")),
                ProblemReport.Build("1.2.3", null, null, junction, Now));
        }
        finally { Directory.Delete(junction); }
        Assert.True(File.Exists(targetLog));
    }
}
