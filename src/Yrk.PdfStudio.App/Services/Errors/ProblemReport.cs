using System.Globalization;
using System.IO;
using System.Text;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Builds a local, redacted report. It does not send or copy anything.</summary>
public static class ProblemReport
{
    public static string Build(string version, ErrorInfo? code, Exception? ex, string logFolder, DateTime now)
    {
        var report = new StringBuilder(Feedback.ProgramInfo(version));
        if (code is not null) report.Append("\nError code: ").Append(code.Code);
        if (ex is not null) report.Append("\nException: ").Append(ex.GetType().Name);

        var lines = ReadLogTail(logFolder, now);
        if (lines.Count > 0) report.Append("\n\nRecent log:\n").AppendJoin('\n', lines);

        return ReportRedactor.Redact(report.ToString());
    }

    private static Queue<string> ReadLogTail(string logFolder, DateTime now)
    {
        var lines = new Queue<string>(40);
        try
        {
            if ((File.GetAttributes(logFolder) & FileAttributes.ReparsePoint) != 0) return lines;
            var path = Path.Combine(logFolder, $"errors-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return lines;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == 40) lines.Dequeue();
                lines.Enqueue(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing, inaccessible or invalid logs must not prevent a report from being built.
        }

        return lines;
    }
}
