using System.IO;
using System.Text.Json;

namespace LyricsFloat.App;

public sealed class AppSettings
{
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Floatify");

    private static readonly string LegacyDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyLyricsFloat");

    public static string LyricsCacheDirectory => Path.Combine(DataDirectory, "lyrics");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 256;

    /// <summary>How see-through the overlay is, 0 (solid) to 1 (clear).</summary>
    public double Transparency { get; set; } = 0.35;

    /// <summary>Size of the current lyric line; resizing the window no longer changes it.</summary>
    public double TextSize { get; set; } = 32;

    public bool ClickThrough { get; set; }

    /// <summary>Show the overlay only while Spotify is playing; hide it when Spotify closes or stays paused.</summary>
    public bool AutoHide { get; set; } = true;

    /// <summary>Whether the first-run welcome card has been dismissed.</summary>
    public bool OnboardingDone { get; set; }

    /// <summary>Per-track sync correction in seconds, keyed by TrackInfo.Key. Positive shows lyrics earlier.</summary>
    public Dictionary<string, double> TrackOffsets { get; set; } = [];

    /// <summary>Carries settings and the lyrics cache over from the folder used before the rename.</summary>
    public static void MigrateLegacyData()
    {
        try
        {
            if (Directory.Exists(LegacyDataDirectory) && !Directory.Exists(DataDirectory))
                Directory.Move(LegacyDataDirectory, DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting fresh is fine; the old folder stays where it is.
        }
    }

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
