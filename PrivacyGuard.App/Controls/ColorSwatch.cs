using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PrivacyGuard.App.Controls;

/// <summary>
/// A round colour swatch, shown with glow alone (no rings). Choosing it sends one soft pulse
/// out of the swatch: a glow in its own colour that swells and settles while the swatch swells
/// with it, then a gentle glow stays to mark the choice, along with the tick. Hover and keyboard
/// focus show a fainter glow, and focus also lifts the swatch slightly. With Windows animations
/// turned off there is no pulse and changes are immediate.
/// </summary>
public sealed class ColorSwatch : RadioButton
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    private Ellipse? _glow;
    private FrameworkElement? _disc, _check, _body;
    private bool _pulsing;

    /// <summary>Clicking opens a colour picker instead of selecting the swatch.</summary>
    public bool OpensPicker { get; set; }

    public event EventHandler? PickerRequested;

    private static bool Still => !SystemParameters.ClientAreaAnimation;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _glow = GetTemplateChild("PART_Glow") as Ellipse;
        _disc = GetTemplateChild("PART_Disc") as FrameworkElement;
        _check = GetTemplateChild("PART_Check") as FrameworkElement;
        _body = GetTemplateChild("PART_Body") as FrameworkElement;
        Settle(animate: false);
    }

    protected override void OnToggle()
    {
        if (OpensPicker) PickerRequested?.Invoke(this, EventArgs.Empty);
        else base.OnToggle();
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        if (IsLoaded && !Still) Pulse();
        else Settle(animate: false);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        Settle(IsLoaded && !Still);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Settle(!Still);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Scale(_body, 1, TimeSpan.FromMilliseconds(160));
        Settle(!Still);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Settle(!Still);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Settle(!Still);
    }

    // Press feedback: the whole swatch dips slightly while held.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Scale(_body, 0.92, TimeSpan.FromMilliseconds(90));
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        Scale(_body, 1, TimeSpan.FromMilliseconds(160));
        base.OnMouseLeftButtonUp(e);
    }

    // Resting glow: strongest for the chosen swatch, fainter for hover and keyboard focus.
    private double RestGlowOpacity => IsChecked == true ? 0.45 : IsMouseOver || IsKeyboardFocused ? 0.3 : 0;
    private double RestGlowScale => IsChecked == true ? 1.28 : IsMouseOver || IsKeyboardFocused ? 1.18 : 1;
    private double RestDiscScale => IsKeyboardFocused ? 1.08 : 1;

    /// <summary>Eases the glow, tick and swatch size to match the current state.</summary>
    private void Settle(bool animate)
    {
        if (_pulsing) return; // the pulse settles into the resting state itself
        var duration = animate ? TimeSpan.FromMilliseconds(220) : TimeSpan.Zero;
        Scale(_check, IsChecked == true ? 1 : 0, animate ? TimeSpan.FromMilliseconds(280) : TimeSpan.Zero);
        Fade(_glow, RestGlowOpacity, duration);
        Scale(_glow, RestGlowScale, duration);
        Scale(_disc, RestDiscScale, duration);
    }

    /// <summary>Out quickly, back slowly: up over the first 40% of 700 ms, then down to the resting glow.</summary>
    private void Pulse()
    {
        if (_glow is null || _disc is null) return;
        _pulsing = true;
        Scale(_check, 1, TimeSpan.FromMilliseconds(280));

        DoubleAnimationUsingKeyFrames Wave(double from, double peak, double rest) => new()
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)), EaseOut),
                new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), EaseInOut),
            },
        };

        var fade = Wave(_glow.Opacity, 0.8, RestGlowOpacity);
        fade.Completed += (_, _) =>
        {
            _pulsing = false;
            Settle(animate: true); // hover or focus may have changed during the pulse
        };
        _glow.BeginAnimation(OpacityProperty, fade);

        var glowScale = ScaleOf(_glow);
        glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, Wave(glowScale.ScaleX, 1.6, RestGlowScale));
        glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, Wave(glowScale.ScaleY, 1.6, RestGlowScale));
        var discScale = ScaleOf(_disc);
        discScale.BeginAnimation(ScaleTransform.ScaleXProperty, Wave(discScale.ScaleX, 1.08, RestDiscScale));
        discScale.BeginAnimation(ScaleTransform.ScaleYProperty, Wave(discScale.ScaleY, 1.08, RestDiscScale));
    }

    private static ScaleTransform ScaleOf(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform { IsFrozen: false } existing) return existing;
        var created = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = created;
        return created;
    }

    private static void Scale(UIElement? element, double to, TimeSpan duration)
    {
        if (element is null) return;
        var scale = ScaleOf(element);
        var animation = new DoubleAnimation(to, duration) { EasingFunction = EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void Fade(UIElement? element, double to, TimeSpan duration) =>
        element?.BeginAnimation(OpacityProperty, new DoubleAnimation(to, duration) { EasingFunction = EaseOut });

    /// <summary>Dark or light ink, whichever reads better on the given colour.</summary>
    public static Brush InkFor(Color c)
    {
        var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        return luminance > 0.6 ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)) : Brushes.White;
    }
}
