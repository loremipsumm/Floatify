using Microsoft.Win32;

namespace LyricsFloat.App;

/// <summary>Starts the app with Windows through the per-user Run key (no admin rights needed).</summary>
public static class AutoStart
{
    /// <summary>Command-line flag for a quiet start: stay hidden until Spotify plays something.</summary>
    public const string BackgroundFlag = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Floatify";
    private const string LegacyValueName = "SpotifyLyricsFloat";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {BackgroundFlag}");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Moves an entry made before the rename over to the new name and executable.</summary>
    public static void MigrateLegacyEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is not string) return;

        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        Enabled = true;
    }
}
