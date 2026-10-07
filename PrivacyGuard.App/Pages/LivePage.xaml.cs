using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PrivacyGuard.App.Services;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;

namespace PrivacyGuard.App.Pages;

public partial class LivePage : Page
{
    private readonly AppServices _services = AppServices.Current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _updating;

    public LivePage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();

        // Only poll while the page is on screen.
        Loaded += (_, _) =>
        {
            _services.LiveProtectionChanged += OnLiveChanged;
            Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _services.LiveProtectionChanged -= OnLiveChanged;
            _timer.Stop();
        };
    }

    private void OnLiveChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        _updating = true;
        try
        {
            var settings = _services.Settings;
            var stats = _services.Engine.GetStats();
            var on = stats.Running;

            LiveSwitch.IsChecked = on;
            TraySwitch.IsChecked = settings.CloseToTray;

            StatusIcon.Symbol = on ? SymbolRegular.ShieldCheckmark24 : SymbolRegular.Shield24;

            // On must be obvious at a glance, not only from the switch: green shield and border.
            var success = TryFindResource("SystemFillColorSuccessBrush") as System.Windows.Media.Brush;
            if (on && success is not null)
            {
                StatusIcon.Foreground = success;
                StatusCard.BorderBrush = success;
                StatusCard.BorderThickness = new Thickness(2);
            }
            else
            {
                StatusIcon.ClearValue(ForegroundProperty);
                StatusCard.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                StatusCard.BorderThickness = new Thickness(1);
            }

            // The numbers only mean something while protection runs.
            StatsGrid.Opacity = on ? 1 : 0.45;
            StatusTitle.Text = on ? "Protection is on" : "Protection is off";
            StatusDetail.Text = on
                ? "Watching your screen for anything that matches your rules."
                : "Turn it on to start covering sensitive text as it appears.";

            var hasRules = _services.Rules.Rules.Any(r => r.Enabled);
            NoRulesBar.IsOpen = !hasRules;

            ProblemBar.IsOpen = stats.Problem is not null;
            ProblemBar.Message = stats.Problem ?? "";

            var hiddenWindows = stats.WindowsHidden;
            ItemsValue.Text = on ? (stats.ItemsHidden + hiddenWindows).ToString() : "0";
            WindowsValue.Text = on ? stats.WindowsWatched.ToString() : "0";
            OcrValue.Text = on && stats.OcrAverageMs > 0 ? $"{stats.OcrAverageMs:F0} ms" : "-";

            using var me = Process.GetCurrentProcess();
            MemoryValue.Text = $"{me.PrivateMemorySize64 / (1024 * 1024)} MB";
        }
        finally
        {
            _updating = false;
        }
    }

    private void LiveSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var wanted = LiveSwitch.IsChecked == true;
        if (wanted == _services.Engine.IsRunning) return;

        if (_services.SetLiveProtection(wanted) is { } error)
        {
            System.Windows.MessageBox.Show("Live protection could not start.\n\n" + error, "PrivacyGuard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void TraySwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _services.Settings.CloseToTray = TraySwitch.IsChecked == true;
        _services.SaveSettings();
    }
}
