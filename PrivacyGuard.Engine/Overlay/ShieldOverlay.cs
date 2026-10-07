using System.Runtime.InteropServices;
using PrivacyGuard.Engine.Geometry;
using static PrivacyGuard.Engine.Native.NativeMethods;

namespace PrivacyGuard.Engine.Overlay;

/// <summary>
/// The redaction layer: one borderless, click-through, always-on-top window that spans every
/// monitor. Its shape is set to exactly the redaction boxes (a window region), so Windows only
/// draws those boxes. There is no full-screen transparent bitmap, which keeps it nearly free.
/// Screen recorders using display capture, such as OBS, record the boxes like any other window.
/// </summary>
internal sealed class ShieldOverlay : IDisposable
{
    private readonly WndProc _wndProc;
    private readonly string _className = "PrivacyGuardShield_" + Guid.NewGuid().ToString("N");
    private IntPtr _hwnd;
    private IntPtr _brush;
    private RectI _screen;
    private List<RectI> _current = [];

    public ShieldOverlay() => _wndProc = WindowProc;

    public IntPtr Handle => _hwnd;

    /// <param name="color">Fill colour as 0xRRGGBB.</param>
    public void Create(uint color)
    {
        var instance = GetModuleHandle(null);
        _brush = CreateSolidBrush(ToColorRef(color));

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hbrBackground = _brush,
            lpszClassName = _className,
        };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("Could not register the overlay window class.");

        _screen = Native.DesktopWindows.VirtualScreen;
        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            _className, "PrivacyGuard Shield", WS_POPUP,
            _screen.X, _screen.Y, _screen.Width, _screen.Height,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the overlay window.");

        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        SetWindowRgn(_hwnd, CreateRectRgn(0, 0, 0, 0), false);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    // Win32 colours are 0xBBGGRR.
    private static uint ToColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    /// <param name="color">Fill colour as 0xRRGGBB. Takes effect immediately.</param>
    public void SetColor(uint color)
    {
        if (_hwnd == IntPtr.Zero) return;
        var old = _brush;
        _brush = CreateSolidBrush(ToColorRef(color));
        SetClassLongPtr(_hwnd, GCLP_HBRBACKGROUND, _brush);
        InvalidateRect(_hwnd, IntPtr.Zero, true);
        if (old != IntPtr.Zero) DeleteObject(old);
    }

    /// <summary>Sets the boxes to draw, in physical screen pixels. Cheap when nothing changed.</summary>
    public void SetBoxes(List<RectI> screenRects)
    {
        if (_hwnd == IntPtr.Zero || SameAs(screenRects)) return;
        _current = screenRects;

        var region = CreateRectRgn(0, 0, 0, 0);
        foreach (var r in screenRects)
        {
            var local = r.Offset(-_screen.X, -_screen.Y);
            var piece = CreateRectRgn(local.X, local.Y, local.Right, local.Bottom);
            CombineRgn(region, region, piece, RGN_OR);
            DeleteObject(piece);
        }

        // Windows takes ownership of the region.
        SetWindowRgn(_hwnd, region, true);
    }

    private bool SameAs(List<RectI> rects)
    {
        if (rects.Count != _current.Count) return false;
        for (var i = 0; i < rects.Count; i++)
            if (rects[i] != _current[i]) return false;
        return true;
    }

    /// <summary>Keeps the shield above windows that were made topmost after it.</summary>
    public void BringToTop()
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private void FitToScreens()
    {
        _screen = Native.DesktopWindows.VirtualScreen;
        SetWindowPos(_hwnd, HWND_TOPMOST, _screen.X, _screen.Y, _screen.Width, _screen.Height, SWP_NOACTIVATE);
        var rects = _current;
        _current = [];
        SetBoxes(rects);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return HTTRANSPARENT;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_DISPLAYCHANGE:
                FitToScreens();
                return IntPtr.Zero;
            case WM_DPICHANGED:
                // Stay exactly where we are; the shield is drawn in physical pixels.
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        UnregisterClass(_className, GetModuleHandle(null));
        if (_brush != IntPtr.Zero) DeleteObject(_brush);
        _brush = IntPtr.Zero;
    }
}
