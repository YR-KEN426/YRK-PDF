using System.IO;
using System.Text.Json;

namespace Yrk.PdfStudio.App.Services;

/// <summary>The choices of the last digital signature, offered again next time.</summary>
public sealed class DigitalSignSettings
{
    public const string DefaultTimestampUrl = "http://timestamp.digicert.com";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "digital-sign.json");

    public string? LastId { get; set; }

    public string? Reason { get; set; }

    public string? Location { get; set; }

    public bool Visible { get; set; } = true;

    public string? HandwrittenId { get; set; }

    public bool Timestamp { get; set; }

    public string TimestampUrl { get; set; } = DefaultTimestampUrl;

    public bool LongTermValidation { get; set; }

    public static DigitalSignSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<DigitalSignSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }
}
