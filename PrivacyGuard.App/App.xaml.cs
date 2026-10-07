using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using PrivacyGuard.App.Services;
using PrivacyGuard.Core.Storage;

namespace PrivacyGuard.App;

public partial class App : Application
{
    private TaskbarIcon? _tray;
    private MenuItem? _liveMenuItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;

        // The shield keeps running in the tray after the window is closed.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var services = AppServices.Current;
        ThemeService.Apply(services.Settings.Theme);
        base.OnStartup(e);

        CreateTray();
        services.LiveProtectionChanged += (_, _) => Dispatcher.Invoke(UpdateTray);

        if (services.Settings.LiveProtection && services.SetLiveProtection(true) is { } error)
            MessageBox.Show("Live protection could not start.\n\n" + error, "PrivacyGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CreateTray()
    {
        var open = new MenuItem { Header = "Open PrivacyGuard" };
        open.Click += (_, _) => ShowMainWindow();

        _liveMenuItem = new MenuItem { Header = "Live protection", IsCheckable = true };
        _liveMenuItem.Click += (_, _) =>
        {
            var services = AppServices.Current;
            services.SetLiveProtection(!services.Engine.IsRunning);
        };

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(_liveMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _tray = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/PrivacyGuard.App;component/Assets/privacyguard.ico")),
            ContextMenu = menu,
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        _tray.ForceCreate();
        UpdateTray();
    }

    private void UpdateTray()
    {
        var on = AppServices.Current.Engine.IsRunning;
        if (_liveMenuItem is not null) _liveMenuItem.IsChecked = on;
        if (_tray is not null) _tray.ToolTipText = on ? "PrivacyGuard: live protection on" : "PrivacyGuard: live protection off";
    }

    public void ShowMainWindow()
    {
        if (MainWindow is MainWindow { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show();
            existing.Activate();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.Activate();
    }

    public void ExitApp()
    {
        AppServices.Current.Engine.Stop();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Log the type and stack only. Exception messages can echo rule text, which is secret.
        try
        {
            var line = $"{DateTime.Now:s} {e.Exception.GetType().FullName}{Environment.NewLine}{e.Exception.StackTrace}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "error.log"), line);
        }
        catch (IOException)
        {
            // Nothing more we can do if the log itself cannot be written.
        }

        MessageBox.Show(
            "Something went wrong and the last action was cancelled. Your rules are safe.\n\nDetails were saved to error.log in the PrivacyGuard data folder.",
            "PrivacyGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
