using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using PrivacyGuard.Core.Matching;
using PrivacyGuard.Core.Models;
using PrivacyGuard.Engine.Capture;
using PrivacyGuard.Engine.Geometry;
using PrivacyGuard.Engine.Imaging;
using PrivacyGuard.Engine.Native;
using PrivacyGuard.Engine.Ocr;
using PrivacyGuard.Engine.Overlay;
using PrivacyGuard.Engine.Tracking;
using Windows.Graphics.Capture;
using static PrivacyGuard.Engine.Native.NativeMethods;

namespace PrivacyGuard.Engine;

public sealed class EngineOptions
{
    /// <summary>Also cover brand-new content until it has been read. Safer, with brief flashes.</summary>
    public bool StrictMode { get; set; }

    /// <summary>Protect PrivacyGuard's own windows too.</summary>
    public bool ProtectOwnWindows { get; set; } = true;

    /// <summary>How many windows are watched at once. Bounds memory use.</summary>
    public int MaxWindows { get; set; } = 8;

    /// <summary>Box colour as 0xRRGGBB.</summary>
    public uint BoxColor { get; set; } = 0x111111;
}

/// <summary>Per-window diagnostics. Counts and window classes only, never screen text.</summary>
public sealed record WindowDiagnostics(
    IntPtr Hwnd, string WindowClass, bool Captured, int Width, int Height,
    int Frames, int OcrRuns, int LastOcrLines, int LastDetections, int Boxes,
    int LostBoxes = 0, int RemovedOutside = 0, int RemovedExpired = 0, int RemovedByOcr = 0, double LastTrackScore = double.NaN,
    int NonZeroShifts = 0, string ShiftLog = "", int RestoredFromMemory = 0, long MotionSum = 0, int RecalledKnown = 0);

public sealed record EngineStats(
    bool Running,
    int WindowsWatched,
    int ItemsHidden,
    int WindowsHidden,
    double OcrAverageMs,
    int OcrPerSecond,
    int FramesPerSecond,
    string? Problem,
    double ReadMsPerSecond = 0,
    double TrackMsPerSecond = 0,
    double OcrMsPerSecond = 0);

/// <summary>
/// The live redaction engine. Two private threads:
/// the host thread owns the overlay window and listens for window events;
/// the worker thread owns the GPU, reads frames, tracks items and schedules OCR.
/// </summary>
public sealed class LiveEngine : IDisposable
{
    private readonly EngineOptions _options;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    private Thread? _host;
    private Thread? _worker;
    private Dispatcher? _dispatcher;
    private volatile bool _stopping;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ConcurrentQueue<OcrOutcome> _ocrResults = new();

    /// <summary>A finished OCR job. Times are on the performance-counter clock, for the latency trace.</summary>
    private sealed record OcrOutcome(OcrRequest Request, List<Detection> Detections, double Ms, int Lines,
        double FrameTimeMs, double StartedAtMs, int Slot);

    // Rules, replaced as a whole whenever they change.
    private volatile IReadOnlyList<Rule> _textRules = [];
    private volatile IReadOnlyList<Rule> _appRules = [];
    private Dictionary<Guid, string> _ruleSignatures = [];

    // Worker-thread state.
    private GpuReader? _gpu;
    private OcrService? _ocr;
    private readonly Dictionary<IntPtr, WindowCapture> _captures = [];
    private readonly Dictionary<IntPtr, WindowTracker> _trackers = [];
    private readonly Dictionary<IntPtr, long> _captureFailedAt = [];
    private readonly GrayImage _half = new(1, 1);
    private readonly GrayImage _strip = new(1, 1);
    private IntPtr _foreground;

    // OCR readers: one normally, a second for new content on PCs with cores to spare.
    private static readonly int OcrReaders = Environment.ProcessorCount <= 2 ? 1 : 2;
    private OcrService?[] _ocrPool = [];
    private bool[] _slotBusy = [];

    // Shared between threads.
    private volatile IReadOnlyDictionary<IntPtr, WindowSnapshot> _snapshot = new Dictionary<IntPtr, WindowSnapshot>();
    private volatile HashSet<IntPtr> _appCovered = [];
    private volatile List<RectI> _screenBoxes = [];
    private volatile string? _problem;
    private int _composeQueued;
    private int _refreshQueued;

    // Host-thread state.
    private ShieldOverlay? _overlay;
    private readonly List<IntPtr> _hooks = [];
    private WinEventProc? _winEventProc;
    private DispatcherTimer? _composeTimer;
    private DispatcherTimer? _refreshTimer;
    private readonly Dictionary<uint, string> _processNames = [];
    private volatile HashSet<IntPtr> _knownWindows = [];

    // Statistics.
    private double _ocrAverageMs;

    // Where the time goes, in milliseconds per second of wall time (100 = one full core).
    private double _readMsThisSecond, _trackMsThisSecond, _ocrMsThisSecond;
    private volatile float _readMsPerSecond, _trackMsPerSecond, _ocrMsPerSecond;
    private int _framesThisSecond, _ocrThisSecond, _framesPerSecond, _ocrPerSecond;
    private long _statsSecond;

    public LiveEngine(EngineOptions? options = null) => _options = options ?? new EngineOptions();

    public bool IsRunning { get; private set; }

    /// <summary>The boxes currently drawn, in physical screen pixels. Useful for diagnostics and tests.</summary>
    public IReadOnlyList<RectI> ScreenBoxes => _screenBoxes;

    // ---- public control ----------------------------------------------------------

    public void Start()
    {
        if (IsRunning) return;
        _stopping = false;
        _startedAtMs = Environment.TickCount64;
        _problem = null;

        Exception? hostError = null;
        using var ready = new ManualResetEventSlim();
        _host = new Thread(() =>
        {
            try { HostMain(ready); }
            catch (Exception ex) { hostError = ex; ready.Set(); }
        })
        { IsBackground = true, Name = "PrivacyGuard shield" };
        _host.SetApartmentState(ApartmentState.STA);
        _host.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        if (hostError is not null) throw new InvalidOperationException("The live shield could not start.", hostError);

        _worker = new Thread(WorkerMain) { IsBackground = true, Name = "PrivacyGuard engine", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _stopping = true;
        _signal.Set();
        _worker?.Join(TimeSpan.FromSeconds(3));
        _dispatcher?.InvokeShutdown();
        _host?.Join(TimeSpan.FromSeconds(3));
        _snapshot = new Dictionary<IntPtr, WindowSnapshot>();
        _screenBoxes = [];
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }

    public void UpdateRules(IEnumerable<Rule> rules)
    {
        var enabled = rules.Where(r => r.Enabled && RuleMatcher.Validate(r) is null).Select(r => r.Clone()).ToList();
        _textRules = enabled.Where(r => r.Kind != RuleKind.AppWindow).ToList();
        _appRules = enabled.Where(r => r.Kind == RuleKind.AppWindow).ToList();

        var signatures = enabled.ToDictionary(r => r.Id, Signature);
        var previous = _ruleSignatures;
        _ruleSignatures = signatures;

        // Keep boxes only for rules that still exist unchanged; rescan everything for the rest.
        var unchanged = signatures.Where(kv => previous.TryGetValue(kv.Key, out var old) && old == kv.Value)
                                  .Select(kv => kv.Key).ToHashSet();
        _commands.Enqueue(() =>
        {
            foreach (var t in _trackers.Values)
            {
                t.RemoveBoxesExcept(unchanged);
                t.RequestFullScan();
            }
            PublishSnapshot();
        });
        _signal.Set();
        QueueRefresh();
    }

    public void SetStrictMode(bool strict)
    {
        _options.StrictMode = strict;
        _commands.Enqueue(PublishSnapshot);
        _signal.Set();
    }

    /// <param name="color">Box colour as 0xRRGGBB. Applies right away if protection is running.</param>
    public void SetBoxColor(uint color)
    {
        _options.BoxColor = color;
        _dispatcher?.BeginInvoke(DispatcherPriority.Send, () => _overlay?.SetColor(color));
    }

    public EngineStats GetStats()
    {
        var snapshot = _snapshot;
        return new EngineStats(
            IsRunning,
            _knownCaptureCount,
            snapshot.Values.Sum(s => s.Boxes.Count),
            _appCovered.Count,
            Math.Round(_ocrAverageMs, 1),
            _ocrPerSecond,
            _framesPerSecond,
            _problem,
            _readMsPerSecond, _trackMsPerSecond, _ocrMsPerSecond);
    }

    private volatile int _knownCaptureCount;
    private long _startedAtMs;

    /// <summary>
    /// Windows that appear while protection is running get a strict-mode curtain until first
    /// read. Windows already open when it was switched on do not, so turning protection on
    /// never sweeps the screen black.
    /// </summary>
    private WindowTracker NewTracker(IntPtr hwnd) =>
        new(hwnd) { CurtainUntilFirstScan = Environment.TickCount64 - _startedAtMs > 3000 };

    private sealed class DiagCounter
    {
        public string WindowClass = "";
        public int Frames, OcrRuns, LastOcrLines, LastDetections;
    }

    private readonly Dictionary<IntPtr, DiagCounter> _diag = [];
    private volatile IReadOnlyList<WindowDiagnostics> _diagSnapshot = [];

    /// <summary>What the engine is doing per window. For the diagnostics view and tests.</summary>
    public IReadOnlyList<WindowDiagnostics> GetDiagnostics() => _diagSnapshot;

    private DiagCounter Diag(IntPtr hwnd)
    {
        if (!_diag.TryGetValue(hwnd, out var d))
            _diag[hwnd] = d = new DiagCounter { WindowClass = GetClass(hwnd) };
        return d;
    }

    private void UpdateDiagnostics()
    {
        foreach (var key in _diag.Keys.Where(k => !_trackers.ContainsKey(k) && !_captures.ContainsKey(k)).ToList())
            _diag.Remove(key);

        _diagSnapshot = _diag.Select(kv =>
        {
            _captures.TryGetValue(kv.Key, out var c);
            _trackers.TryGetValue(kv.Key, out var t);
            return new WindowDiagnostics(kv.Key, kv.Value.WindowClass, c is not null, c?.Width ?? 0, c?.Height ?? 0,
                kv.Value.Frames, kv.Value.OcrRuns, kv.Value.LastOcrLines, kv.Value.LastDetections, t?.Boxes.Count ?? 0,
                t?.LostCount ?? 0, t?.RemovedOutside ?? 0, t?.RemovedExpired ?? 0, t?.RemovedByOcr ?? 0, t?.LastTrackScore ?? double.NaN,
                t?.NonZeroShifts ?? 0, t?.ShiftLog ?? "", t?.RestoredFromMemory ?? 0, t?.MotionSumFull ?? 0, t?.RecalledFromMemory ?? 0);
        }).ToList();
    }

    private static string Signature(Rule r) =>
        $"{r.Kind}|{r.Pattern}|{r.BuiltIn}|{r.CaseSensitive}|{r.WholeWord}|{r.Fuzzy}|{r.FuzzyThreshold}";

    // ---- host thread: overlay and window events ----------------------------------

    private void HostMain(ManualResetEventSlim ready)
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _dispatcher = Dispatcher.CurrentDispatcher;

        _overlay = new ShieldOverlay();
        _overlay.Create(_options.BoxColor);

        _winEventProc = OnWinEvent;
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_REORDER);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_NAMECHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);

        _composeTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _composeTimer.Tick += (_, _) => Compose();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshWindows();
        _refreshTimer.Start();

        RefreshWindows();
        ready.Set();

        Dispatcher.Run();

        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        _composeTimer.Stop();
        _refreshTimer.Stop();
        _overlay.Dispose();
    }

    private void Hook(uint min, uint max)
    {
        var h = SetWinEventHook(min, max, IntPtr.Zero, _winEventProc!, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || hwnd == _overlay?.Handle) return;

        try
        {
            switch (eventType)
            {
                case EVENT_OBJECT_LOCATIONCHANGE:
                    // Window being dragged or resized: move the boxes with it right away.
                    if (_knownWindows.Contains(hwnd)) RequestCompose();
                    break;

                case EVENT_SYSTEM_FOREGROUND:
                    _commands.Enqueue(() => _foreground = hwnd);
                    _signal.Set();
                    RefreshWindows();
                    _overlay?.BringToTop();
                    break;

                case EVENT_OBJECT_NAMECHANGE:
                    if (_appRules.Count > 0 && GetAncestor(hwnd, 2) == hwnd) QueueRefresh();
                    break;

                default:
                    // Shown, hidden, destroyed, minimised, cloaked, reordered.
                    if (GetAncestor(hwnd, 2) == hwnd)
                    {
                        // A window matching an app rule must be covered the instant it appears.
                        if (eventType == EVENT_OBJECT_SHOW && _appRules.Count > 0) RefreshWindows();
                        else QueueRefresh();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            EngineLog.Error("window event", ex);
        }
    }

    private void QueueRefresh()
    {
        if (_dispatcher is null || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, RefreshWindows);
    }

    private void RequestCompose()
    {
        if (_dispatcher is null || Interlocked.Exchange(ref _composeQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Send, Compose);
    }

    /// <summary>Decides which windows to watch and which to hide entirely because of app rules.</summary>
    private void RefreshWindows()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        if (_overlay is null || _stopping) return;

        try
        {
            var windows = DesktopWindows.EnumerateTopToBottom(_overlay.Handle);
            var visible = DesktopWindows.VisibleRegions(windows, DesktopWindows.VirtualScreen);
            var appRules = _appRules;
            var covered = new HashSet<IntPtr>();
            var desired = new List<IntPtr>();

            foreach (var w in windows)
            {
                if (!_options.ProtectOwnWindows && w.ProcessId == _ownProcessId) continue;
                if (DesktopWindows.IsIgnoredClass(w.Hwnd)) continue;

                if (appRules.Count > 0 &&
                    AppRuleMatcher.MatchesAny(appRules, GetTitle(w.Hwnd), ProcessName(w.ProcessId)))
                {
                    covered.Add(w.Hwnd);
                    continue;
                }

                // Nothing to look for means nothing to capture: zero cost.
                if (_textRules.Count > 0 && desired.Count < _options.MaxWindows &&
                    visible.TryGetValue(w.Hwnd, out var parts) && RegionMath.TotalArea(parts) >= 2000)
                {
                    desired.Add(w.Hwnd);
                }
            }

            _appCovered = covered;
            _knownWindows = windows.Select(w => w.Hwnd).ToHashSet();

            // Always-on-top windows opened after the shield would otherwise sit above it.
            _overlay.BringToTop();

            var foreground = GetForegroundWindow();
            _commands.Enqueue(() => SetWatchedWindows(desired, foreground));
            _signal.Set();
            Compose();
        }
        catch (Exception ex)
        {
            EngineLog.Error("refresh windows", ex);
        }
    }

    private string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var name)) return name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = "";
        }
        if (_processNames.Count > 500) _processNames.Clear();
        _processNames[pid] = name;
        return name;
    }

    /// <summary>Turns every window's boxes into screen rectangles, clipped to what is actually visible.</summary>
    private void Compose()
    {
        Interlocked.Exchange(ref _composeQueued, 0);
        if (_overlay is null || _stopping) return;

        try
        {
            var snapshots = _snapshot;
            var covered = _appCovered;
            var rects = new List<RectI>();
            var moving = false;

            if (snapshots.Count > 0 || covered.Count > 0)
            {
                var strict = _options.StrictMode;
                var windows = DesktopWindows.EnumerateTopToBottom(_overlay.Handle);
                var visible = DesktopWindows.VisibleRegions(windows, DesktopWindows.VirtualScreen);

                // Each box is placed where its text will be when this update reaches the screen.
                var showAt = EngineClock.NowMs + PresentLagMs;

                foreach (var w in windows)
                {
                    if (!visible.TryGetValue(w.Hwnd, out var parts) || parts.Count == 0) continue;

                    if (covered.Contains(w.Hwnd))
                    {
                        rects.AddRange(parts);
                        continue;
                    }

                    if (!snapshots.TryGetValue(w.Hwnd, out var snap)) continue;
                    var offset = CaptureOffset(w.Hwnd, w.Bounds);
                    moving |= AddClipped(rects, snap.Boxes, snap, showAt, w.Bounds, offset, parts);
                    if (strict) moving |= AddClipped(rects, snap.Pending, snap, showAt, w.Bounds, offset, parts);
                }
            }

            _overlay.SetBoxes(rects);
            _screenBoxes = rects;

            // Redraw at 60 Hz while boxes are moving so they glide with the text; 30 Hz is enough
            // to follow window drags otherwise; stop entirely when nothing is on screen.
            var interval = TimeSpan.FromMilliseconds(moving ? 16 : 33);
            if (_composeTimer!.Interval != interval) _composeTimer.Interval = interval;
            if (rects.Count > 0 && !_composeTimer.IsEnabled) _composeTimer.Start();
            else if (rects.Count == 0 && _composeTimer.IsEnabled) _composeTimer.Stop();
        }
        catch (Exception ex)
        {
            EngineLog.Error("compose", ex);
        }
    }

    /// <summary>Lag from updating the overlay to it appearing; overridable for tuning on unusual displays.</summary>
    private static readonly double PresentLagMs =
        double.TryParse(Environment.GetEnvironmentVariable("PRIVACYGUARD_PRESENT_LAG_MS"), out var lag) ? lag : WindowTracker.PresentLagMs;

    /// <returns>True if any of the rectangles is moving.</returns>
    private static bool AddClipped(List<RectI> output, IReadOnlyList<MovingRect> frameRects, WindowSnapshot snap,
        double showAt, RectI windowBounds, (int X, int Y) frameOffset, List<RectI> visibleParts)
    {
        var moving = false;
        foreach (var m in frameRects)
        {
            moving |= m.VelocityPerMs != 0;
            var r = WindowTracker.PredictAt(m, snap.FrameTimeMs, snap.FrameIntervalMs, showAt);
            var screen = r.Offset(windowBounds.X + frameOffset.X, windowBounds.Y + frameOffset.Y).Intersect(windowBounds);
            if (!screen.IsEmpty) output.AddRange(RegionMath.ClipTo(screen, visibleParts));
        }
        return moving;
    }

    // ---- worker thread: frames, tracking and OCR ---------------------------------

    private void WorkerMain()
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            _gpu = new GpuReader();
        }
        catch (Exception ex)
        {
            _problem = "Screen capture is not available on this PC.";
            EngineLog.Error("gpu", ex);
            return;
        }

        _ocr = OcrService.TryCreate();
        if (_ocr is null) _problem = "No Windows OCR language is installed. Add English in Settings > Time & language.";
        _ocrPool = new OcrService?[OcrReaders];
        _ocrPool[0] = _ocr;
        _slotBusy = new bool[OcrReaders];

        // Lets Windows 11 capture without the yellow border. Fire and forget: it must never
        // delay protection, and it is harmless where unsupported.
        _ = Task.Run(async () =>
        {
            try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); }
            catch (Exception) { }
        });

        while (!_stopping)
        {
            _signal.WaitOne(30);
            try
            {
                WorkerTick();
            }
            catch (Exception ex)
            {
                EngineLog.Error("worker", ex);
            }
        }

        foreach (var c in _captures.Values) c.Dispose();
        _captures.Clear();
        _trackers.Clear();
        _gpu.Dispose();
        _gpu = null;
    }

    private void WorkerTick()
    {
        var now = Environment.TickCount64;
        var changed = false;

        while (_commands.TryDequeue(out var command)) command();

        foreach (var capture in _captures.Values.ToList())
        {
            if (capture.IsClosed)
            {
                capture.Dispose();
                _captures.Remove(capture.Hwnd);
                changed |= _trackers.Remove(capture.Hwnd);
                continue;
            }

            // Frame budget per window, to keep the engine light:
            //  - scrolling: every frame (60 fps), because boxes must keep up with the text;
            //  - otherwise: up to 30 fps, plenty for text that appears in place;
            //  - mostly animated (a game, a video) with nothing hidden: about 15 fps.
            if (_trackers.TryGetValue(capture.Hwnd, out var known))
            {
                var minInterval = known.IsScrolling(now) ? 0
                    : known.Boxes.Count == 0 && known.AnimatedFraction > 0.3 ? 66
                    : 33;
                if (now - capture.LastProcessedMs < minInterval) continue;
            }

            if (!capture.TryAcquire() || capture.Texture is null) continue;
            capture.LastProcessedMs = now;

            var stageStart = Stopwatch.GetTimestamp();
            _gpu!.ReadHalfGray(capture.Texture, capture.Width, capture.Height, _half);
            _gpu.ReadStripsGray(capture.Texture, capture.Width, capture.Height, _strip);
            _framesThisSecond++;
            Diag(capture.Hwnd).Frames++;
            if (!_trackers.TryGetValue(capture.Hwnd, out var tracker))
                _trackers[capture.Hwnd] = tracker = NewTracker(capture.Hwnd);
            var trackStart = Stopwatch.GetTimestamp();
            _readMsThisSecond += Stopwatch.GetElapsedTime(stageStart, trackStart).TotalMilliseconds;
            changed |= tracker.OnFrame(_half, capture.Width, capture.Height, now, capture.FrameTimeMs, _strip);
            _trackMsThisSecond += Stopwatch.GetElapsedTime(trackStart).TotalMilliseconds;
            DebugDump(capture.Hwnd, tracker);
        }

        while (_ocrResults.TryDequeue(out var result))
        {
            _slotBusy[result.Slot] = false;
            _ocrThisSecond++;
            if (StatsLogging) TraceOcr(result);
            var diag = Diag(result.Request.Tracker.Hwnd);
            diag.OcrRuns++;
            diag.LastOcrLines = result.Lines;
            diag.LastDetections = result.Detections.Count;
            _ocrAverageMs = _ocrAverageMs == 0 ? result.Ms : _ocrAverageMs * 0.8 + result.Ms * 0.2;
            _ocrMsThisSecond += result.Ms;
            if (_trackers.TryGetValue(result.Request.Tracker.Hwnd, out var t) && ReferenceEquals(t, result.Request.Tracker))
            {
                t.ApplyOcr(result.Request, result.Detections, now);
                changed = true;
            }
        }

        foreach (var t in _trackers.Values) changed |= t.SettleIfIdle(now);

        ScheduleOcr(now);
        if (changed) PublishSnapshot();

        if (now - _statsSecond >= 1000)
        {
            _framesPerSecond = _framesThisSecond;
            var seconds = Math.Max(0.5, (now - _statsSecond) / 1000.0);
            _readMsPerSecond = (float)(_readMsThisSecond / seconds);
            _trackMsPerSecond = (float)(_trackMsThisSecond / seconds);
            _ocrMsPerSecond = (float)(_ocrMsThisSecond / seconds);
            _readMsThisSecond = _trackMsThisSecond = _ocrMsThisSecond = 0;
            _ocrPerSecond = _ocrThisSecond;
            _framesThisSecond = _ocrThisSecond = 0;
            _statsSecond = now;
            _knownCaptureCount = _captures.Count;
            UpdateDiagnostics();
            LogStatsIfEnabled(now);
        }
    }

    // ---- diagnostics only ----------------------------------------------------------
    //
    // Saves the engine's own half-resolution frames so scroll estimation can be checked offline.
    // Off unless BOTH environment variables are set, and only for windows whose class starts with
    // the given prefix (the test harness window). Never used by the app: saving screen content
    // to disk is exactly what this product exists to prevent.
    private static readonly string? DumpDirectory = Environment.GetEnvironmentVariable("PRIVACYGUARD_DEBUG_DUMP_DIR");
    private static readonly string? DumpClassPrefix = Environment.GetEnvironmentVariable("PRIVACYGUARD_DEBUG_DUMP_CLASS");
    private int _dumpCount;

    private void DebugDump(IntPtr hwnd, WindowTracker tracker)
    {
        if (DumpDirectory is null || string.IsNullOrEmpty(DumpClassPrefix) || _dumpCount >= 600) return;
        if (!Diag(hwnd).WindowClass.StartsWith(DumpClassPrefix, StringComparison.Ordinal)) return;

        Directory.CreateDirectory(DumpDirectory);
        var path = Path.Combine(DumpDirectory, $"{_dumpCount++:00000}_{tracker.LastShiftFull}.gray");
        using var file = File.Create(path);
        file.Write(BitConverter.GetBytes(_strip.Width));
        file.Write(BitConverter.GetBytes(_strip.Height));
        file.Write(_strip.Pixels, 0, _strip.Width * _strip.Height);
    }

    // Opt-in performance log (PRIVACYGUARD_DEBUG_STATS=1): window classes and counts only, never text.
    private static readonly bool StatsLogging = Environment.GetEnvironmentVariable("PRIVACYGUARD_DEBUG_STATS") == "1";
    private long _lastStatsLogMs;

    private void LogStatsIfEnabled(long now)
    {
        if (!StatsLogging || now - _lastStatsLogMs < 5000) return;
        _lastStatsLogMs = now;
        var s = GetStats();
        EngineLog.Info($"stats read={s.ReadMsPerSecond:F0}ms/s track={s.TrackMsPerSecond:F0}ms/s ocr={s.OcrMsPerSecond:F0}ms/s fps={s.FramesPerSecond} ocr/s={s.OcrPerSecond} windows={s.WindowsWatched} items={s.ItemsHidden}");
        foreach (var d in _diagSnapshot.Where(d => d.Captured))
            EngineLog.Info($"  window {d.WindowClass} {d.Width}x{d.Height} frames={d.Frames} ocr={d.OcrRuns} boxes={d.Boxes} scrolls={d.NonZeroShifts}");
    }

    private void SetWatchedWindows(List<IntPtr> desired, IntPtr foreground)
    {
        if (_gpu is null) return;
        _foreground = foreground;
        var now = Environment.TickCount64;
        var wanted = desired.ToHashSet();

        foreach (var hwnd in desired)
        {
            if (_captures.ContainsKey(hwnd)) continue;
            if (_captureFailedAt.TryGetValue(hwnd, out var failed) && now - failed < 10_000) continue;

            var capture = WindowCapture.TryCreate(hwnd, _gpu, () => _signal.Set());
            if (capture is null)
            {
                _captureFailedAt[hwnd] = now;
                EngineLog.Info("capture unavailable for window class " + GetClass(hwnd));
                continue;
            }
            _captures[hwnd] = capture;
            Diag(hwnd);
            if (!_trackers.ContainsKey(hwnd)) _trackers[hwnd] = NewTracker(hwnd);
        }

        // Windows that went fully out of view stop being captured, but their boxes are kept
        // so they reappear instantly when the window comes back.
        foreach (var hwnd in _captures.Keys.Where(h => !wanted.Contains(h)).ToList())
        {
            _captures[hwnd].Dispose();
            _captures.Remove(hwnd);
        }

        var removed = false;
        foreach (var hwnd in _trackers.Keys.Where(h => !IsWindow(h)).ToList())
            removed |= _trackers.Remove(hwnd);
        foreach (var hwnd in _captureFailedAt.Keys.Where(h => !IsWindow(h)).ToList())
            _captureFailedAt.Remove(hwnd);

        if (_captures.Count == 0) _gpu.Trim();
        _knownCaptureCount = _captures.Count;
        if (removed) PublishSnapshot();
    }

    /// <summary>
    /// Starts OCR jobs while there are free readers. Reader 0 takes anything. Reader 1 only takes
    /// new content in the foreground window (never background re-reads or other windows), never
    /// while it is scrolling, and only while the PC has CPU to spare: Windows runs OCR on its own
    /// threads, so its priority cannot be lowered, and this is the guard instead.
    /// </summary>
    private void ScheduleOcr(long now)
    {
        if (_ocr is null || _gpu is null) return;

        for (var slot = 0; slot < _slotBusy.Length; slot++)
        {
            if (_slotBusy[slot]) continue;
            var extra = slot > 0;
            // Measured: during scrolling a second reader added CPU without covering text sooner,
            // so it only helps with text that appears while the first one is busy.
            if (extra && (SystemBusy(now) || ForegroundScrolling(now))) return;
            // The second reader is created the first time it is needed, so a quiet PC never pays
            // for it. Created before any job is taken, so a failure never loses a job.
            if ((_ocrPool[slot] ??= OcrService.TryCreate()) is null) return;
            var request = NextOcrRequest(now, urgentOnly: extra);
            if (request is null) return;
            StartOcr(request, slot, now);
        }
    }

    /// <param name="urgentOnly">
    /// For the second reader: only new content in the window being used. Background windows that
    /// keep changing (a log, a dashboard) would otherwise get read twice as often, for no benefit.
    /// </param>
    private OcrRequest? NextOcrRequest(long now, bool urgentOnly)
    {
        // Foreground window first, then anything with new content, then slow background rescans.
        var ordered = _captures.Values
            .Where(c => c.Texture is not null && _trackers.ContainsKey(c.Hwnd))
            .Where(c => !urgentOnly || c.Hwnd == _foreground)
            .OrderBy(c => c.Hwnd == _foreground ? 0 : 1)
            .ToList();

        foreach (var c in ordered)
        {
            var t = _trackers[c.Hwnd];
            if (t.HasPendingWork && t.TakeOcrRequest(now, allowPeriodic: false, urgentOnly) is { } urgent) return urgent;
        }
        if (urgentOnly) return null;
        foreach (var c in ordered)
            if (_trackers[c.Hwnd].TakeOcrRequest(now, allowPeriodic: true) is { } any) return any;
        return null;
    }

    private void StartOcr(OcrRequest request, int slot, long now)
    {
        var capture = _captures[request.Tracker.Hwnd];
        var crop = request.Crop.Intersect(new RectI(0, 0, capture.Width, capture.Height));
        if (crop.IsEmpty)
        {
            request.Tracker.ApplyOcr(request, [], now);
            return;
        }

        var ocr = _ocrPool[slot]!;
        var pixels = _gpu!.ReadCropGray(capture.Texture!, crop, request.Scale, out var width, out var height);
        var rules = _textRules;
        var frameTimeMs = capture.FrameTimeMs;
        var startedAtMs = EngineClock.NowMs;
        _slotBusy[slot] = true;

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var detections = new List<Detection>();
            var lineCount = 0;
            try
            {
                if (rules.Count > 0)
                {
                    var lines = await ocr.RecognizeAsync(pixels, width, height);
                    lineCount = lines.Count;
                    detections = DetectionMapper.Map(lines, rules, request.Scale, crop.X, crop.Y);
                }
            }
            catch (Exception ex)
            {
                EngineLog.Error("ocr", ex);
            }
            System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            _ocrResults.Enqueue(new OcrOutcome(request, detections, sw.Elapsed.TotalMilliseconds, lineCount, frameTimeMs, startedAtMs, slot));
            _signal.Set();
        });
    }

    // ---- where a captured image really sits on screen -------------------------------

    /// <summary>
    /// How far a window's captured image is shifted from its visible frame. Windows that draw
    /// their own title bar (File Explorer, browsers) extend their drawing area above the visible
    /// frame when maximized, and the capture starts there: every box landed 8 px too low.
    /// Restored windows are captured exactly.
    /// </summary>
    private static (int X, int Y) CaptureOffset(IntPtr hwnd, RectI frame)
    {
        if (!IsZoomed(hwnd) || !GetClientRect(hwnd, out _)) return (0, 0);
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return (0, 0);
        return (Math.Min(0, origin.X - frame.X), Math.Min(0, origin.Y - frame.Y));
    }

    private bool ForegroundScrolling(long now) =>
        _trackers.TryGetValue(_foreground, out var t) && t.IsScrolling(now);

    // ---- CPU load guard for the second reader ---------------------------------------

    private long _cpuSampledMs;
    private long _lastIdleTicks, _lastTotalTicks;
    private bool _systemBusy;

    /// <summary>True while the whole PC is over 75% busy. Sampled at most twice a second.</summary>
    private bool SystemBusy(long now)
    {
        if (now - _cpuSampledMs < 500) return _systemBusy;
        _cpuSampledMs = now;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return _systemBusy = false;

        // Kernel time includes idle time.
        var total = kernel + user;
        var dTotal = total - _lastTotalTicks;
        var dIdle = idle - _lastIdleTicks;
        _lastTotalTicks = total;
        _lastIdleTicks = idle;
        _systemBusy = dTotal > 0 && 1.0 - (double)dIdle / dTotal > 0.75;
        return _systemBusy;
    }

    /// <summary>Opt-in latency trace (PRIVACYGUARD_DEBUG_STATS=1): sizes and timings only, never text.</summary>
    private static void TraceOcr(OcrOutcome r)
    {
        var kind = r.Request.IsFullScan ? "full" : r.Request.Urgent ? "new" : "detail";
        var now = EngineClock.NowMs;
        EngineLog.Info($"ocr slot={r.Slot} kind={kind} crop={r.Request.Crop.Width}x{r.Request.Crop.Height}@{r.Request.Scale} " +
                       $"lines={r.Lines} hits={r.Detections.Count} frameAgeAtStart={r.StartedAtMs - r.FrameTimeMs:F0}ms " +
                       $"ocr={r.Ms:F0}ms frameAgeAtApply={now - r.FrameTimeMs:F0}ms");
    }

    private void PublishSnapshot()
    {
        var strict = _options.StrictMode;
        var dict = new Dictionary<IntPtr, WindowSnapshot>();
        foreach (var (hwnd, tracker) in _trackers)
        {
            var snap = tracker.Snapshot(strict);
            if (snap.Boxes.Count > 0 || snap.Pending.Count > 0) dict[hwnd] = snap;
        }
        _snapshot = dict;
        RequestCompose();
    }
}
