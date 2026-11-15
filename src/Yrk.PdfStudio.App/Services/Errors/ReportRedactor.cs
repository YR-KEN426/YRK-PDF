using System.Text;
using System.Text.RegularExpressions;

namespace Yrk.PdfStudio.App.Services;

/// <summary>Removes local identities and file locations from text a person may share.</summary>
public static class ReportRedactor
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
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
            @"|(?<email>[\p{L}\p{M}\p{N}._%+!#$&'*=/^`{|}~-]+@[\p{L}\p{M}\p{N}-]+(?:\.[\p{L}\p{M}\p{N}-]+)+)";
        // Longer identities win when names overlap. No word boundaries: even very short
        // names embedded in other text must be removed. Replacement markers are never rescanned.
        var identities = new[] { (Name: userName, Group: "user"), (Name: machineName, Group: "pc") };
        foreach (var identity in identities.OrderByDescending(i => i.Name.Length))
            if (identity.Name.Length > 0)
                pattern += "|(?<" + identity.Group + ">" + Regex.Escape(identity.Name) + ")";
        // Logs may contain long unbroken text. Keep matching linear even when no email is present.
        var sensitive = new Regex(pattern, Options | RegexOptions.NonBacktracking);
        var result = new StringBuilder(text.Length);
        var position = 0;
        while (sensitive.Match(text, position) is { Success: true } match)
        {
            result.Append(text, position, match.Index - position);
            position = match.Index + match.Length;
            if (match.Groups["path"].Success)
            {
                position = PathEnd(text, position);
                result.Append("<path>");
            }
            else result.Append(match.Groups["email"].Success ? "<email>" : match.Groups["user"].Success ? "<user>" : "<pc>");
        }
        result.Append(text, position, text.Length - position);
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
