using System.IO;

namespace LyricsFloat.App;

/// <summary>Small file log in the data folder, for diagnosing sync or lookup problems.</summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();

    private static string FilePath => Path.Combine(AppSettings.DataDirectory, "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) file.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
