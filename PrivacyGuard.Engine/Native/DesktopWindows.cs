using PrivacyGuard.Engine.Geometry;
using static PrivacyGuard.Engine.Native.NativeMethods;

namespace PrivacyGuard.Engine.Native;

/// <summary>A visible top-level window, as the user sees it.</summary>
public readonly record struct DesktopWindow(IntPtr Hwnd, RectI Bounds, bool Opaque, uint ProcessId);

/// <summary>Lists on-screen windows from top to bottom. Must be called from a per-monitor DPI aware thread.</summary>
public static class DesktopWindows
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ForegroundStaging", "MultitaskingViewFrame",
    };

    public static List<DesktopWindow> EnumerateTopToBottom(IntPtr ignore)
    {
        var list = new List<DesktopWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == ignore || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            var bounds = GetBounds(hwnd);
            if (bounds.Width < 8 || bounds.Height < 8) return true;

            var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            var seeThrough = (ex & WS_EX_LAYERED) != 0 || (ex & WS_EX_TRANSPARENT) != 0;

            GetWindowThreadProcessId(hwnd, out var pid);
            list.Add(new DesktopWindow(hwnd, bounds, !seeThrough, pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The window's visible frame, without the invisible resize border or shadow.</summary>
    public static RectI GetBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        return RectI.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static bool IsIgnoredClass(IntPtr hwnd) => IgnoredClasses.Contains(GetClass(hwnd));

    /// <summary>Title and app name of every visible window, for previewing app rules.</summary>
    public static List<(string Title, string AppName)> ListOpenWindows()
    {
        var names = new Dictionary<uint, string>();
        var result = new List<(string, string)>();
        foreach (var w in EnumerateTopToBottom(IntPtr.Zero))
        {
            if (IsIgnoredClass(w.Hwnd)) continue;
            if (!names.TryGetValue(w.ProcessId, out var app))
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)w.ProcessId);
                    app = p.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    app = "";
                }
                names[w.ProcessId] = app;
            }
            result.Add((GetTitle(w.Hwnd), app));
        }
        return result;
    }

    public static RectI VirtualScreen => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// For each window, the parts not hidden behind opaque windows above it. See-through
    /// windows never count as cover, so text under them stays protected.
    /// </summary>
    public static Dictionary<IntPtr, List<RectI>> VisibleRegions(IReadOnlyList<DesktopWindow> topToBottom, RectI screen)
    {
        var result = new Dictionary<IntPtr, List<RectI>>(topToBottom.Count);
        var occluders = new List<RectI>();
        foreach (var w in topToBottom)
        {
            var visible = new List<RectI> { w.Bounds.Intersect(screen) };
            foreach (var o in occluders)
            {
                if (!o.IntersectsWith(w.Bounds)) continue;
                visible = RegionMath.Subtract(visible, o);
                if (visible.Count == 0) break;
            }
            visible.RemoveAll(r => r.IsEmpty);
            result[w.Hwnd] = visible;
            if (w.Opaque) occluders.Add(w.Bounds);
        }
        return result;
    }
}
