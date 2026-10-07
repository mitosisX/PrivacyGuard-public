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

    // One copy per Windows account. Two would run two engines and draw two sets of boxes.
    private const string InstanceName = @"Local\PrivacyGuard.SingleInstance";
    private const string ShowSignalName = @"Local\PrivacyGuard.ShowWindow";
    private static Mutex? _instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!ClaimSingleInstance())
        {
            MessageBox.Show(
                "PrivacyGuard is already running.\n\nIt may be hidden in the system tray. Click OK to open it.",
                "PrivacyGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            SignalRunningInstance();
            // Leave before the main window, the engine or the tray icon are created.
            Environment.Exit(0);
            return;
        }

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

    /// <summary>
    /// True if this is the only copy running for this Windows account. The first copy also starts
    /// listening for later copies asking it to come to the front.
    /// </summary>
    private bool ClaimSingleInstance()
    {
        _instanceLock = new Mutex(initiallyOwned: true, InstanceName, out var createdNew);
        if (!createdNew)
        {
            _instanceLock.Dispose();
            _instanceLock = null;
            return false;
        }

        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        new Thread(() =>
        {
            while (showSignal.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        })
        { IsBackground = true, Name = "PrivacyGuard show requests" }.Start();
        return true;
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
                using (signal) signal.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // The running copy keeps going either way; it just is not brought forward.
        }
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
        _instanceLock?.ReleaseMutex();
        _instanceLock?.Dispose();
        _instanceLock = null;
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
