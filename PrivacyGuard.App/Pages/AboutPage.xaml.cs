using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace PrivacyGuard.App.Pages;

public partial class AboutPage : Page
{
    private const string GitHub = "https://github.com/mitosisx";

    public AboutPage()
    {
        InitializeComponent();

        var assembly = typeof(AboutPage).Assembly;
        var version = assembly.GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";
        CopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        GitHubText.Text = "github.com/mitosisx";
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GitHub) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser could be opened. Copy the link instead so it is not a dead end.
            Clipboard.SetText(GitHub);
            MessageBox.Show($"The browser could not be opened, so the link was copied:\n\n{GitHub}", "PrivacyGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
