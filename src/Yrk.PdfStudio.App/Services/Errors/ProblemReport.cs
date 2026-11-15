using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Builds a local, redacted report. It does not send or copy anything.</summary>
public static class ProblemReport
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex Timestamp = new(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?\]", Options);
    private static readonly Regex ErrorCode = new(@"^(?<code>YRK-[A-Z]+-[0-9]{3})(?:\s|$)", Options);
    private static readonly Regex ExceptionType = new(@"^(?:--->\s+)?(?<type>(?:[A-Za-z_][A-Za-z0-9_]*[.+])*(?:[A-Za-z_][A-Za-z0-9_]*)?Exception)(?::|$)", Options);
    private static readonly Regex StackFrame = new(@"^   at [\p{L}\p{M}\p{N}_.,+`<>|\[\]]+\([\p{L}\p{M}\p{N}_.,+`<>\[\]&*?\s]*\)", Options);
    private static readonly Regex SourceLine = new(@"^ in [^\r\n]+:line (?<line>[0-9]+)", Options);

    public static string Build(string version, ErrorInfo? code, Exception? ex, string logFolder, DateTime now)
    {
        var report = new StringBuilder(Feedback.ProgramInfo(version));
        if (code is not null) report.Append("\nError code: ").Append(code.Code);
        if (ex is not null) report.Append("\nException: ").Append(ex.GetType().Name);

        var lines = ReadLogTail(logFolder, now).Where(line => line.Length > 0).ToArray();
        if (lines.Length > 0) report.Append("\n\nRecent log:\n").AppendJoin('\n', lines);

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
                lines.Enqueue(LogStructure(line));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing, inaccessible or invalid logs must not prevent a report from being built.
        }

        return lines;
    }

    private static string LogStructure(string line)
    {
        var frame = StackFrame.Match(line);
        if (frame.Success)
        {
            var source = SourceLine.Match(line[frame.Length..]);
            // Source paths may be relative or contain spaces and arbitrary extensions.
            return frame.Value + (source.Success ? " in <path>:line " + source.Groups["line"].Value : "");
        }

        var parts = new List<string>();
        var timestamp = Timestamp.Match(line);
        if (timestamp.Success) parts.Add(timestamp.Value);
        var remainder = line[timestamp.Length..].TrimStart();
        var code = ErrorCode.Match(remainder);
        if (code.Success)
        {
            parts.Add(code.Groups["code"].Value);
            remainder = remainder[code.Length..].TrimStart();
        }
        var exception = ExceptionType.Match(remainder);
        if (exception.Success) parts.Add(exception.Groups["type"].Value);
        return string.Join(' ', parts);
    }
}
