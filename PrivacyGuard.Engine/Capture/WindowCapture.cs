using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace PrivacyGuard.Engine.Capture;

/// <summary>
/// Captures one window with Windows.Graphics.Capture. The window's own pixels never include
/// the overlay (it is a separate window), so the engine always sees the true text underneath.
/// Frames only arrive when the window changes, so idle windows cost nothing.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private const int BufferCount = 2;

    private readonly GpuReader _gpu;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private int _pending;

    // The latest frame is held so OCR can read full-resolution pixels from it later.
    private Direct3D11CaptureFrame? _held;
    private ID3D11Texture2D? _heldTexture;

    public IntPtr Hwnd { get; }
    public bool IsClosed { get; private set; }
    public ID3D11Texture2D? Texture => _heldTexture;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long LastProcessedMs { get; set; }

    /// <summary>When Windows captured the held frame, in milliseconds on the performance-counter clock.</summary>
    public double FrameTimeMs { get; private set; } = double.NaN;

    private WindowCapture(IntPtr hwnd, GpuReader gpu, GraphicsCaptureItem item, Action signal)
    {
        Hwnd = hwnd;
        _gpu = gpu;
        _item = item;
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, BufferCount, _poolSize);
        _pool.FrameArrived += (_, _) =>
        {
            Interlocked.Exchange(ref _pending, 1);
            signal();
        };
        _item.Closed += (_, _) =>
        {
            IsClosed = true;
            signal();
        };

        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch (Exception) { /* older Windows */ }
        try { _session.IsBorderRequired = false; } catch (Exception) { /* Windows 10 always draws a border */ }
        _session.StartCapture();
    }

    public static WindowCapture? TryCreate(IntPtr hwnd, GpuReader gpu, Action signal)
    {
        try
        {
            var item = GraphicsCaptureItem.TryCreateFromWindowId(new WindowId((ulong)hwnd));
            if (item is null || item.Size.Width <= 0 || item.Size.Height <= 0) return null;
            return new WindowCapture(hwnd, gpu, item, signal);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException
                                         or System.Runtime.InteropServices.COMException)
        {
            // Some system windows cannot be captured. They are simply skipped.
            return null;
        }
    }

    /// <summary>Takes the newest frame if one arrived. Returns false when there is nothing new.</summary>
    public bool TryAcquire()
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0) return false;

        Direct3D11CaptureFrame? latest = null;
        while (_pool.TryGetNextFrame() is { } frame)
        {
            latest?.Dispose();
            latest = frame;
        }
        if (latest is null) return false;

        var size = latest.ContentSize;
        FrameTimeMs = latest.SystemRelativeTime.TotalMilliseconds;
        _heldTexture?.Dispose();
        _held?.Dispose();
        _held = latest;
        _heldTexture = GpuReader.TextureFrom(latest.Surface);

        var desc = _heldTexture.Description;
        Width = Math.Min(size.Width, (int)desc.Width);
        Height = Math.Min(size.Height, (int)desc.Height);

        // The window was resized: the pool must match, or frames get clipped or padded.
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            _poolSize = size;
            _pool.Recreate(_gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, BufferCount, size);
        }

        return Width > 0 && Height > 0;
    }

    /// <summary>
    /// Stops capturing. Closing the capture session can block inside Windows indefinitely (seen
    /// while a window was being dragged out of view), which froze the whole engine: boxes hung in
    /// place and nothing new was covered. So the session is closed on a throwaway thread of its
    /// own; if Windows never returns, only that thread waits.
    /// </summary>
    public void Dispose()
    {
        IsClosed = true;
        _heldTexture?.Dispose();
        _held?.Dispose();
        _heldTexture = null;
        _held = null;

        var session = _session;
        var pool = _pool;
        new Thread(() =>
        {
            try
            {
                session.Dispose();
                pool.Dispose();
            }
            catch (Exception ex)
            {
                EngineLog.Error("capture close", ex);
            }
        })
        { IsBackground = true, Name = "PrivacyGuard capture close" }.Start();
    }
}
