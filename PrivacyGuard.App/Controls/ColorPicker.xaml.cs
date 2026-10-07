using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PrivacyGuard.Core.Storage;

namespace PrivacyGuard.App.Controls;

/// <summary>Hue, saturation and brightness picker with a hex field. Works with mouse and keyboard.</summary>
public partial class ColorPicker : UserControl
{
    private double _hue, _saturation, _value;
    private bool _syncingHex;

    /// <summary>The chosen colour as 0xRRGGBB.</summary>
    public event EventHandler<uint>? Applied;

    public event EventHandler? Cancelled;

    public ColorPicker()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PlaceThumbs();
        Loaded += (_, _) => PlaceThumbs();
    }

    public uint Rgb => HsvToRgb(_hue, _saturation, _value);

    /// <summary>Starts the picker at a colour, given as 0xRRGGBB.</summary>
    public void SetColor(uint rgb)
    {
        (_hue, _saturation, _value) = RgbToHsv(rgb);
        Refresh(updateHex: true);
    }

    private void Refresh(bool updateHex)
    {
        HueFill.Fill = Solid(HsvToRgb(_hue, 1, 1));
        Preview.Fill = Solid(Rgb);
        if (updateHex)
        {
            _syncingHex = true;
            HexBox.Text = $"#{Rgb:X6}";
            _syncingHex = false;
            HexError.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = true;
        }
        PlaceThumbs();
    }

    private void PlaceThumbs()
    {
        Canvas.SetLeft(SvThumb, _saturation * SvArea.ActualWidth - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _value) * SvArea.ActualHeight - SvThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _hue / 360 * HueArea.ActualWidth - HueThumb.Width / 2);
    }

    private static SolidColorBrush Solid(uint rgb) => new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    // ---- mouse ---------------------------------------------------------------------

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SvArea.Focus();
        SvArea.CaptureMouse();
        PickSv(e.GetPosition(SvArea));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (SvArea.IsMouseCaptured) PickSv(e.GetPosition(SvArea));
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueArea.Focus();
        HueArea.CaptureMouse();
        PickHue(e.GetPosition(HueArea));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (HueArea.IsMouseCaptured) PickHue(e.GetPosition(HueArea));
    }

    private void Area_MouseUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void PickSv(Point p)
    {
        _saturation = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1);
        _value = Math.Clamp(1 - p.Y / SvArea.ActualHeight, 0, 1);
        Refresh(updateHex: true);
    }

    private void PickHue(Point p)
    {
        _hue = Math.Clamp(p.X / HueArea.ActualWidth, 0, 1) * 360;
        Refresh(updateHex: true);
    }

    // ---- keyboard ------------------------------------------------------------------

    private void Sv_KeyDown(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.02;
        switch (e.Key)
        {
            case Key.Left: _saturation = Math.Max(0, _saturation - step); break;
            case Key.Right: _saturation = Math.Min(1, _saturation + step); break;
            case Key.Up: _value = Math.Min(1, _value + step); break;
            case Key.Down: _value = Math.Max(0, _value - step); break;
            default: return;
        }
        e.Handled = true;
        Refresh(updateHex: true);
    }

    private void Hue_KeyDown(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30 : 5;
        switch (e.Key)
        {
            case Key.Left: _hue = Math.Max(0, _hue - step); break;
            case Key.Right: _hue = Math.Min(360, _hue + step); break;
            default: return;
        }
        e.Handled = true;
        Refresh(updateHex: true);
    }

    // ---- hex and buttons -----------------------------------------------------------

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingHex) return;
        var valid = AppSettings.TryParseColor(HexBox.Text, out var rgb);
        // Only complain once six characters are in; mid-typing is not an error.
        HexError.Visibility = valid || HexBox.Text.TrimStart('#').Length < 6 ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.IsEnabled = valid;
        if (!valid) return;
        (_hue, _saturation, _value) = RgbToHsv(rgb);
        Refresh(updateHex: false);
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Applied?.Invoke(this, Rgb);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    // ---- colour maths --------------------------------------------------------------

    internal static uint HsvToRgb(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        uint To(double channel) => (uint)Math.Round((channel + m) * 255);
        return (To(r) << 16) | (To(g) << 8) | To(b);
    }

    internal static (double H, double S, double V) RgbToHsv(uint rgb)
    {
        double r = (rgb >> 16 & 0xFF) / 255.0, g = (rgb >> 8 & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * ((g - b) / d % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
