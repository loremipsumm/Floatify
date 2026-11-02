using System.IO;
using System.Text.Json;

namespace LyricsFloat.App;

public sealed class AppSettings
{
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyLyricsFloat");

    public static string LyricsCacheDirectory => Path.Combine(DataDirectory, "lyrics");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 165;

    /// <summary>How dark the glass is tinted over the blurred backdrop, 0 (clear) to 1.</summary>
    public double GlassTint { get; set; } = 0.18;

    public bool ClickThrough { get; set; }

    /// <summary>Per-track sync correction in seconds, keyed by TrackInfo.Key. Positive shows lyrics earlier.</summary>
    public Dictionary<string, double> TrackOffsets { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Could not read settings, using defaults", ex);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
