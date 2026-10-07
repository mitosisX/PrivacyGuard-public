using System.Windows;
using PrivacyGuard.App.Pages;
using PrivacyGuard.App.Services;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PrivacyGuard.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        RootNavigation.SetPageProviderService(new PageProvider());

        // Only follow Windows theme changes live when the user chose "System".
        if (AppServices.Current.Settings.Theme == "System")
            SystemThemeWatcher.Watch(this);

        // The window can be closed and reopened from the tray, so unsubscribe on close.
        AppServices.Current.Rules.SaveFailed += OnSaveFailed;
        Closed += (_, _) => AppServices.Current.Rules.SaveFailed -= OnSaveFailed;

        Closing += (_, _) =>
        {
            if (!AppServices.Current.Settings.CloseToTray) ((App)Application.Current).ExitApp();
        };

        Loaded += (_, _) =>
        {
            KeepScrollingInPages();
            RootNavigation.Navigate(typeof(LivePage));
        };
    }

    /// <summary>
    /// WPF UI wraps every page in its own scroll viewer of unlimited height. The pages' own
    /// scroll viewers then never scroll, yet still swallow the mouse wheel, so nothing scrolls.
    /// Switching the wrapper off after each navigation lets every page scroll itself.
    /// </summary>
    private void KeepScrollingInPages()
    {
        var presenter = FindDescendant<NavigationViewContentPresenter>(RootNavigation);
        if (presenter is null) return;

        void Disable() => presenter.SetCurrentValue(NavigationViewContentPresenter.IsDynamicScrollViewerEnabledProperty, false);
        presenter.Navigated += (_, _) => Dispatcher.BeginInvoke(Disable, System.Windows.Threading.DispatcherPriority.Loaded);
        Disable();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void OnSaveFailed(string message) =>
        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
            this, message, "PrivacyGuard", MessageBoxButton.OK, MessageBoxImage.Warning));

    /// <summary>Creates each page once and reuses it, so scroll position and input survive navigation.</summary>
    private sealed class PageProvider : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, object> _pages = [];

        public object? GetPage(Type pageType)
        {
            if (!_pages.TryGetValue(pageType, out var page))
            {
                page = Activator.CreateInstance(pageType)!;
                _pages[pageType] = page;
            }
            return page;
        }
    }
}
