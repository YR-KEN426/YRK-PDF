using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Yrk.PdfStudio.App.Localization;

/// <summary>
/// The UI language. Texts are written in English in XAML and code; the English text is the key, and the
/// Thai dictionary (Localization/th.json, embedded) gives the translation. A missing entry shows English.
/// Changing <see cref="Language"/> updates open windows at once (see <see cref="UiTranslator"/>).
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Thai = "th";

    private static readonly Dictionary<string, string> Empty = [];
    private static readonly Lazy<Dictionary<string, string>> ThaiTexts = new(LoadThai);
    private static string _language = English;

    /// <summary>Raised after the language changes.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>"en" or "th".</summary>
    public static string Language
    {
        get => _language;
        set
        {
            value = value == Thai ? Thai : English;
            if (value != _language)
            {
                _language = value;
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
        }
    }

    public static bool IsThai => _language == Thai;

    /// <summary>Thai when Windows is set to Thai, otherwise English.</summary>
    public static string SystemDefault => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Thai ? Thai : English;

    private static Dictionary<string, string> Texts => _language == Thai ? ThaiTexts.Value : Empty;

    /// <summary>The text in the current language.</summary>
    public static string T(string english) => Texts.TryGetValue(english, out var text) ? text : english;

    /// <summary>A composite format ("Saved {0}") in the current language, filled in.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>Whether a text has a translation in the current language (English always has).</summary>
    public static bool TryTranslate(string english, out string text)
    {
        if (Texts.TryGetValue(english, out var found))
        {
            text = found;
            return true;
        }

        text = english;
        return false;
    }

    /// <summary>All keys of the Thai dictionary (for the translation check).</summary>
    public static IReadOnlyCollection<string> ThaiKeys => ThaiTexts.Value.Keys;

    private static Dictionary<string, string> LoadThai()
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("Yrk.PdfStudio.App.Localization.th.json");
        if (stream is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
        }
        catch (JsonException ex)
        {
            App.LogMessage("Thai texts could not be read: " + ex.Message);
            return [];
        }
    }
}
