using System.IO;
using PrivacyGuard.Core.Storage;

namespace PrivacyGuard.Engine;

/// <summary>
/// Minimal diagnostics log. It records error types and stack traces only, never screen text,
/// because screen text is exactly what the user wants kept private.
/// </summary>
public static class EngineLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "engine.log");

    public static void Error(string where, Exception ex) =>
        Write($"ERROR {where}: {ex.GetType().FullName}{Environment.NewLine}{ex.StackTrace}");

    public static void Info(string message) => Write("INFO " + message);

    private static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:s} {line}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the engine down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
