namespace PrivacyGuard.Core.Storage;

public static class AppPaths
{
    /// <summary>%APPDATA%\PrivacyGuard, created on first use.</summary>
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrivacyGuard");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
