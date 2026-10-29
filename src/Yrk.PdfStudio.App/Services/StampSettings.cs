using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yrk.PdfStudio.App.Imaging;

namespace Yrk.PdfStudio.App.Services;

/// <summary>The user's own rubber stamps (a company stamp, "รับเงินแล้ว โดย ...") and the one used last.</summary>
public sealed class StampSettings
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf", "stamps.json");

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public List<StampDesign> MyStamps { get; set; } = [];

    public StampDesign? Last { get; set; }

    public static StampSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<StampSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience.
        }
    }
}
