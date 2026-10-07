using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PrivacyGuard.App.Controls;
using PrivacyGuard.App.Services;
using PrivacyGuard.Core.Storage;
using Wpf.Ui.Controls;

namespace PrivacyGuard.App.Pages;

public partial class SettingsPage : Page
{
    private static readonly (string Name, string Hex)[] BoxColors =
    [
        ("Black", AppSettings.DefaultBoxColor),
        ("Charcoal", "#3A3A3A"),
        ("White", "#F2F2F2"),
        ("Red", "#C42B1C"),
        ("Orange", "#CA5010"),
        ("Green", "#107C10"),
        ("Blue", "#0F6CBD"),
        ("Purple", "#6B3FA0"),
        ("Pink", "#BF0077"),
    ];

    // Shown on the custom swatch until a custom colour is in use.
    private static readonly Brush Rainbow = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xFF, 0x4D, 0x4D), 0), new(Color.FromRgb(0xFF, 0xD2, 0x3F), 0.3),
            new(Color.FromRgb(0x3F, 0xD1, 0x7A), 0.55), new(Color.FromRgb(0x3F, 0x8C, 0xFF), 0.8),
            new(Color.FromRgb(0xC0, 0x4D, 0xFF), 1),
        }, new Point(0, 0), new Point(1, 1)));

    private readonly AppServices _services = AppServices.Current;
    private readonly AppSettings _settings = AppServices.Current.Settings;
    private readonly List<ColorSwatch> _swatches = [];
    private readonly ColorSwatch _customSwatch;
    private readonly SolidColorBrush _previewBrush = new();
    private bool _updating = true;

    public SettingsPage()
    {
        InitializeComponent();

        ThemeBox.ItemsSource = ThemeService.Choices;
        ThemeBox.SelectedItem = ThemeService.Choices.Contains(_settings.Theme) ? _settings.Theme : "System";
        PathText.Text = AppPaths.DataDirectory;

        foreach (var (name, hex) in BoxColors)
        {
            var color = ColorOf(hex);
            var swatch = NewSwatch(name, $"{name} ({hex})");
            swatch.Background = Freeze(new SolidColorBrush(color));
            swatch.Foreground = ColorSwatch.InkFor(color);
            swatch.Tag = hex;
            swatch.Checked += Swatch_Checked;
        }

        _customSwatch = NewSwatch("Custom colour", "Pick any colour");
        _customSwatch.OpensPicker = true;
        _customSwatch.Content = new SymbolIcon { Symbol = SymbolRegular.Eyedropper20, FontSize = 16, Foreground = Brushes.White };
        _customSwatch.PickerRequested += (_, _) => OpenPicker();

        Picker.Applied += (_, rgb) =>
        {
            PickerPopup.IsOpen = false;
            ApplyBoxColor($"#{rgb:X6}");
        };
        Picker.Cancelled += (_, _) => PickerPopup.IsOpen = false;

        PreviewBox.Background = _previewBrush;
        ShowBoxColor($"#{_settings.BoxColorRgb:X6}", animate: false);

        // Selections above must not count as user changes.
        _updating = false;
    }

    private ColorSwatch NewSwatch(string name, string tip)
    {
        var swatch = new ColorSwatch { Style = (Style)Resources["Swatch"], ToolTip = tip };
        AutomationProperties.SetName(swatch, name);
        _swatches.Add(swatch);
        SwatchPanel.Children.Add(swatch);
        return swatch;
    }

    private static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    private static Color ColorOf(string hex)
    {
        AppSettings.TryParseColor(hex, out var rgb);
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>Reflects a colour in the swatches, label and preview, without saving.</summary>
    private void ShowBoxColor(string hex, bool animate)
    {
        var wasUpdating = _updating;
        _updating = true;

        var color = ColorOf(hex);
        var preset = BoxColors.FirstOrDefault(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase));
        var isCustom = preset.Name is null;

        _customSwatch.Background = isCustom ? Freeze(new SolidColorBrush(color)) : Rainbow;
        _customSwatch.Foreground = isCustom ? ColorSwatch.InkFor(color) : Brushes.White;
        _customSwatch.ToolTip = isCustom ? $"Custom ({hex}). Click to change." : "Pick any colour";
        foreach (var s in _swatches)
            s.IsChecked = s == _customSwatch ? isCustom : string.Equals(s.Tag as string, hex, StringComparison.OrdinalIgnoreCase);

        CurrentText.Text = $"· {(isCustom ? "Custom" : preset.Name)} {hex}";

        // The preview box eases into the new colour rather than snapping.
        var duration = animate && SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(280) : TimeSpan.Zero;
        _previewBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        _updating = wasUpdating;
    }

    private void ApplyBoxColor(string hex)
    {
        if (_services.SetBoxColor(hex)) ShowBoxColor(_settings.BoxColor, animate: true);
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        if (!_updating && sender is ColorSwatch { Tag: string hex }) ApplyBoxColor(hex);
    }

    private void OpenPicker()
    {
        Picker.SetColor(_settings.BoxColorRgb);
        PickerPopup.PlacementTarget = _customSwatch;
        PickerPopup.IsOpen = true;
        Picker.Focus();
    }

    private void Picker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        PickerPopup.IsOpen = false;
        _customSwatch.Focus();
        e.Handled = true;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ThemeBox.SelectedItem is not string theme) return;

        _settings.Theme = theme;
        ThemeService.Apply(theme);
        Save();
    }

    private void Save()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show("Settings could not be saved. Check free space and folder permissions.",
                "PrivacyGuard", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
}
