using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PrivacyGuard.App.Services;

public static class ThemeService
{
    public static readonly string[] Choices = ["System", "Light", "Dark"];

    public static void Apply(string theme)
    {
        var dark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemPrefersDark(),
        };

        ApplicationThemeManager.Apply(
            dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.Mica,
            updateAccent: true);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
