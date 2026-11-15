using System.Text;
using System.Text.RegularExpressions;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Removes local identities and file locations from text a person may share.</summary>
public static class ReportRedactor
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string FileExtension = @"\.(?:pdf|doc|docx|xls|xlsx|txt|csv|rtf|png|jpg|jpeg|tif|tiff|bmp|gif|webp|heic|dwg|dxf|xml|json|p12|pfx|cer|crt|zip)";
    private static readonly Regex FileEnding = new(@"\.[\p{L}\p{N}]{1,16}(?=$|\s|\.(?=\s|$)|[)\]}])", Options);

    public static string Redact(string text) => Redact(text, Environment.UserName,
        Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string Redact(string text, string userName, string machineName, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(text);
        var roots = @"[a-z]:[\\/]|\\{1,2}[^\s\\/""'<>|:]+[\\/]|//[^\s/]+/|%USERPROFILE%";
        var profile = userProfile.TrimEnd('\\', '/');
        if (profile.Length > 0)
        {
            // Both separator styles occur in exception messages on Windows.
            roots += "|" + Regex.Escape(profile.Replace('\\', '/')).Replace("/", @"[\\/]");
        }

        var pattern = "(?<path>" + roots + ")" +
            @"|(?<email>[\p{L}\p{M}\p{N}._%+!#$&'*=/^`{|}~-]+@[\p{L}\p{M}\p{N}-]+(?:\.[\p{L}\p{M}\p{N}-]+)+)" +
            @"|(?<quotedFile>""[^""\\/\r\n]+" + FileExtension + @"""|'[^'\\/\r\n]+" + FileExtension +
            @"'|“[^“”\\/\r\n]+" + FileExtension + @"”)" +
            @"|(?<file>[^\\/\s""'“”‘’<>|?:;,!()\[\]{}]+" + FileExtension +
            @")(?<fileEnd>$|\.(?:$|\s)|[\\/\s""'“”‘’<>|?:;,!()\[\]{}])";
        // Match identities separately so an early identity cannot cause repeated scans of
        // a long possible email or filename. Longer identities win when names overlap.
        var identities = new[] { (Name: userName, Group: "user"), (Name: machineName, Group: "pc") };
        var identityPattern = string.Join("|", identities.OrderByDescending(i => i.Name.Length)
            .Where(i => i.Name.Length > 0).Select(i => "(?<" + i.Group + ">" + Regex.Escape(i.Name) + ")"));
        var identityRegex = identityPattern.Length == 0 ? null : new Regex(identityPattern, Options | RegexOptions.NonBacktracking);
        // Logs may contain long unbroken text. Keep matching linear even when no email is present.
        var sensitive = new Regex(pattern, Options | RegexOptions.NonBacktracking);
        var result = new StringBuilder(text.Length);
        void AppendText(int start, int length)
        {
            var plain = text.Substring(start, length);
            // Only original text is scanned; replacement markers never become input.
            result.Append(identityRegex is null ? plain : identityRegex.Replace(plain, match =>
            {
                var index = start + match.Index;
                var end = index + match.Length;
                if (match.Length < 3 &&
                    ((index > 0 && char.IsLetterOrDigit(text[index - 1])) ||
                     (end < text.Length && char.IsLetterOrDigit(text[end]))))
                    return match.Value;
                return match.Groups["user"].Success ? "<user>" : "<pc>";
            }));
        }
        var position = 0;
        while (sensitive.Match(text, position) is { Success: true } match)
        {
            AppendText(position, match.Index - position);
            position = match.Index + match.Length;
            if (match.Groups["path"].Success)
            {
                position = PathEnd(text, position);
                result.Append("<path>");
            }
            else if (match.Groups["quotedFile"].Success)
                result.Append(text[match.Index]).Append("<file>").Append(text[position - 1]);
            else if (match.Groups["file"].Success)
            {
                result.Append("<file>");
                // The delimiter establishes the extension boundary but remains original input.
                position -= match.Groups["fileEnd"].Length;
            }
            else result.Append("<email>");
        }
        AppendText(position, text.Length - position);
        return result.ToString();
    }

    private static int PathEnd(string text, int start)
    {
        var end = start;
        while (end < text.Length && !IsPathTerminator(text[end])) end++;

        // Spaces can belong to directories and filenames. Look for an extension only after
        // the last separator, so a directory such as "archive.pdf files" cannot leak its child.
        var fileStart = start;
        for (var i = start; i < end; i++)
            if (text[i] is '\\' or '/') fileStart = i + 1;
        var endings = FileEnding.Matches(text.Substring(fileStart, end - fileStart));
        var ending = endings.Count > 0 ? endings[^1] : null;
        if (ending is not null)
            return fileStart + ending.Index + ending.Length;

        var sentenceEnd = Regex.Match(text.Substring(fileStart, end - fileStart), @"\.(?=\s|$)");
        if (sentenceEnd.Success) return fileStart + sentenceEnd.Index;

        // Without a recognizable filename, redact the entire ambiguous span rather than
        // guess which words belong to the path. Keep trailing sentence punctuation.
        while (end > start && (char.IsWhiteSpace(text[end - 1]) || text[end - 1] is '.' or ')' or ']' or '}')) end--;
        return end;
    }

    private static bool IsPathTerminator(char value) => char.IsControl(value) ||
        value is '"' or '\'' or '<' or '>' or '|' or '?' or '*' or ':' or ';' or ',' or '!' or
            '“' or '”' or '‘' or '’' or '。' or '！' or '？' or '；' or '，';
}
