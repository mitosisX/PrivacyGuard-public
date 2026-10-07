using PrivacyGuard.Engine.Geometry;
using PrivacyGuard.Engine.Imaging;
using PrivacyGuard.Engine.Ocr;

namespace PrivacyGuard.Engine.Tracking;

/// <summary>A redacted item on one window, followed from frame to frame.</summary>
public sealed class RedactionBox
{
    public required Guid RuleId { get; init; }

    /// <summary>Top-left of the template in the half-resolution frame.</summary>
    public int HalfX { get; set; }
    public int HalfY { get; set; }

    /// <summary>Size and sub-pixel offset of the box in full-resolution frame pixels.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    public Template? Template { get; set; }

    /// <summary>Vertical speed in full-resolution pixels per millisecond, from frame capture times.</summary>
    public double VelocityPerMs { get; set; }
    public int LastShiftHalf { get; set; }

    /// <summary>When tracking last failed, or null while the box is locked onto its text.</summary>
    public long? LostSinceMs { get; set; }
    public bool RescanRequested { get; set; }

    /// <summary>
    /// Set when the window was resized. The layout may have moved, and the template can lock onto
    /// look-alike text (Explorer's "Desktop" above "Downloads"), so only OCR on the new layout
    /// can confirm the box. It is removed after two OCR reads of its spot fail to find it.
    /// </summary>
    public bool NeedsOcrConfirm { get; set; }
    public int OcrMisses { get; set; }

    public RectI Rect => new(HalfX * 2 + OffsetX, HalfY * 2 + OffsetY, Width, Height);
}

/// <summary>An OCR job for part of one window.</summary>
public sealed class OcrRequest
{
    public required WindowTracker Tracker { get; init; }
    public required RectI Crop { get; init; }
    public required int Scale { get; init; }
    public required long MotionSumFull { get; init; }
    public required GrayImage HalfSnapshot { get; init; }
    public required RectI HalfRegion { get; init; }
    public required bool IsFullScan { get; init; }
    public required int Id { get; init; }

    /// <summary>Which page the request belongs to; results for an earlier page are discarded.</summary>
    public int PageGeneration { get; init; }

    /// <summary>Which window size the request read. Only a read of the current size can confirm boxes after a resize.</summary>
    public int LayoutEpoch { get; init; }

    /// <summary>New content or a whole new page, as opposed to a background re-read.</summary>
    public bool Urgent { get; init; }
}

/// <summary>A rectangle and how fast it is moving, so the overlay can place it for the moment it is shown.</summary>
public readonly record struct MovingRect(RectI Rect, double VelocityPerMs);

/// <summary>What one window wants covered, in its own frame pixels, as of the frame captured at FrameTimeMs.</summary>
public sealed record WindowSnapshot(
    IntPtr Hwnd, IReadOnlyList<MovingRect> Boxes, IReadOnlyList<MovingRect> Pending, double FrameTimeMs, double FrameIntervalMs);

/// <summary>
/// All real-time logic for one window: follows known items as the content scrolls, finds new
/// content that needs OCR, and folds OCR results back in. Pure logic, so it is fully testable.
/// Coordinates: tracking runs on a half-resolution image; boxes are reported in full resolution.
/// </summary>
public sealed class WindowTracker
{
    public const double AcceptScore = 0.75;
    public const long LostTimeoutMs = 1500;

    /// <summary>
    /// A box may only jump far from where it was expected on a very strong match. With the normal
    /// threshold, boxes whose text had gone latched onto look-alike text elsewhere and "jumped".
    /// </summary>
    public const double WideAcceptScore = 0.9;
    private const int MaxBandHeight = 600;

    private GrayImage _latest = new(1, 1);
    private GrayImage _scratch = new(1, 1);
    private int[] _prevProfile = [];
    private int[] _curProfile = [];
    private bool _hasFrame;
    private long _motionSumFull;
    private double _windowVelocity;
    private long _lastFrameMs;
    private long _lastFullScanMs;
    private bool _needsFullScan = true;
    private bool _hasScanned;
    private int _detailY = -1;
    private int _nextRequestId;

    // Full-resolution vertical strips: scroll is measured on these when the engine provides them.
    private GrayImage _stripLatest = new(1, 1);
    private GrayImage _stripScratch = new(1, 1);
    private int[] _stripPrevProfile = [];
    private int[] _stripCurProfile = [];
    private bool _hasStrip;

    // Frame timing, from the capture timestamps Windows attaches to every frame.
    private double _frameTimeMs = double.NaN;
    private double _frameIntervalMs = 16.7;
    private double _velocityPerMs;

    private readonly List<RedactionBox> _boxes = [];
    private readonly List<RectI> _pending = [];

    /// <summary>The changed areas each in-flight OCR job is reading (not the whole strip), for strict-mode curtains.</summary>
    private readonly Dictionary<int, List<RectI>> _inflight = [];

    // Heat map over 16 px blocks of the half-resolution frame. Regions that keep changing in
    // place (video, games, animations) heat up. They get no strict-mode curtain, so a playing
    // video is not blacked out, and they are re-read at most twice a second, which saves CPU.
    private byte[] _heat = [];
    private bool[] _dirtyGrid = [];
    private int _gridColumns, _gridRows;
    private long _lastHotOcrMs;
    private long _heatUpdatedMs;
    private const byte HotLevel = 120;
    private const long HotOcrIntervalMs = 500;

    /// <summary>A full-resolution distance in half-resolution pixels, rounded away from zero.</summary>
    private static int ToHalf(int full) => (int)Math.Round(full / 2.0, MidpointRounding.AwayFromZero);

    public WindowTracker(IntPtr hwnd) => Hwnd = hwnd;

    public IntPtr Hwnd { get; }
    public int FullWidth { get; private set; }
    public int FullHeight { get; private set; }
    public IReadOnlyList<RedactionBox> Boxes => _boxes;
    public bool HasScanned => _hasScanned;

    /// <summary>
    /// Show a full-window curtain in strict mode until the first read. Only for windows that
    /// appear while protection is running: windows already open when it is switched on must
    /// not all flash black.
    /// </summary>
    public bool CurtainUntilFirstScan { get; init; }

    // Diagnostics only.
    public int RemovedOutside { get; private set; }
    public int RemovedExpired { get; private set; }
    public int RemovedByOcr { get; private set; }
    public double LastTrackScore { get; private set; } = double.NaN;
    public int LostCount => _boxes.Count(b => b.LostSinceMs is not null);
    public int NonZeroShifts { get; private set; }
    public string ShiftLog => string.Join(",", _shiftLog);
    private readonly Queue<int> _shiftLog = new();
    public bool HasPendingWork => _hasFrame && (_needsFullScan || _pending.Count > 0 || _detailY >= 0);
    public long LastFrameMs => _lastFrameMs;
    public long MotionSumFull => _motionSumFull;
    public int LastShiftFull { get; private set; }
    private long _lastScrollMs = long.MinValue / 2;

    /// <summary>True while the content is scrolling. Only then is the full frame rate worth its cost.</summary>
    public bool IsScrolling(long nowMs) => nowMs - _lastScrollMs < 500;

    private RectI FullBounds => new(0, 0, FullWidth, FullHeight);

    // ---- frames -----------------------------------------------------------------

    /// <summary>Feeds a new half-resolution frame. Returns true if anything visible may have changed.</summary>
    /// <param name="frameTimeMs">When Windows captured the frame, in milliseconds on the performance-counter clock.</param>
    /// <param name="strip">Optional full-resolution vertical strips of the same frame, for exact scroll measurement.</param>
    public bool OnFrame(GrayImage half, int fullWidth, int fullHeight, long nowMs, double frameTimeMs = double.NaN, GrayImage? strip = null)
    {
        _lastFrameMs = nowMs;
        if (double.IsNaN(frameTimeMs)) frameTimeMs = nowMs;
        var elapsed = double.IsNaN(_frameTimeMs) ? double.NaN : frameTimeMs - _frameTimeMs;
        var timed = elapsed is > 0.5 and < 250;
        if (timed) _frameIntervalMs = _frameIntervalMs * 0.8 + elapsed * 0.2;
        _frameTimeMs = frameTimeMs;

        if (!_hasFrame || half.Width != _latest.Width || half.Height != _latest.Height)
        {
            var resized = _hasFrame;
            _latest.CopyFrom(half);
            _prevProfile = new int[half.Height];
            _curProfile = new int[half.Height];
            Motion.RowProfile(_latest, _prevProfile);
            _hasFrame = true;
            FullWidth = fullWidth;
            FullHeight = fullHeight;
            _needsFullScan = true;
            _pending.Clear();
            _offscreen.Clear();
            _readSpans.Clear();
            _gridColumns = ChangeDetector.GridColumns(half.Width);
            _gridRows = ChangeDetector.GridRows(half.Height);
            _heat = new byte[_gridColumns * _gridRows];
            _dirtyGrid = new bool[_gridColumns * _gridRows];
            KeepStrip(strip);

            // After a resize old positions are guesses; keep them (safe) until OCR confirms them
            // on the new layout. Tracking alone cannot, as it may lock onto look-alike text.
            if (resized)
            {
                _layoutEpoch++;
                foreach (var b in _boxes)
                {
                    b.LostSinceMs ??= nowMs;
                    b.NeedsOcrConfirm = true;
                    b.OcrMisses = 0;
                }
            }
            return true;
        }

        _scratch.CopyFrom(half);
        if (IsSame(_latest, _scratch))
        {
            // Nothing moved in this frame: whatever was scrolling has stopped.
            var wasMoving = _velocityPerMs != 0 || _boxes.Any(b => b.VelocityPerMs != 0);
            _velocityPerMs = 0;
            foreach (var b in _boxes) b.VelocityPerMs = 0;
            return wasMoving;
        }

        Motion.RowProfile(_scratch, _curProfile);

        // Scroll is measured and accumulated in full-resolution pixels so it never drifts;
        // box positions live in half-resolution pixels, where template matching runs. The
        // full-resolution strips give exact answers; half-size frames are the fallback.
        var expected = (int)Math.Round(_windowVelocity);
        int shiftFull;
        if (strip is not null && _hasStrip && strip.Width == _stripLatest.Width && strip.Height == _stripLatest.Height)
        {
            _stripScratch.CopyFrom(strip);
            Motion.RowProfile(_stripScratch, _stripCurProfile);
            shiftFull = Motion.EstimateVerticalShift(_stripLatest, _stripScratch, _stripPrevProfile, _stripCurProfile,
                maxShiftHalf: strip.Height * 2 / 3, expectedShift: expected, scale: 1);
            (_stripLatest, _stripScratch) = (_stripScratch, _stripLatest);
            (_stripPrevProfile, _stripCurProfile) = (_stripCurProfile, _stripPrevProfile);
        }
        else
        {
            shiftFull = Motion.EstimateVerticalShift(_latest, _scratch, _prevProfile, _curProfile,
                maxShiftHalf: _scratch.Height * 2 / 3, expectedShift: expected);
            KeepStrip(strip);
        }
        _motionSumFull += shiftFull;
        if (shiftFull != 0)
        {
            NonZeroShifts++;
            _shiftLog.Enqueue(shiftFull);
            if (_shiftLog.Count > 12) _shiftLog.Dequeue();
            _lastScrollMs = nowMs;
        }
        _windowVelocity = shiftFull;
        _velocityPerMs = timed ? shiftFull / elapsed : 0;
        LastShiftFull = shiftFull;

        var changes = ChangeDetector.FindNewContent(_latest, _scratch, ToHalf(shiftFull), grid: _dirtyGrid);

        if (shiftFull == 0 && IsPageSwitch())
        {
            // A different page (tab switch, navigation): old boxes cover nothing useful and must
            // not go hunting for look-alike text. Start over and read the new page first.
            StartNewPage(nowMs);
            // Switching back to a page seen before: its secrets are covered on this very frame.
            RecallKnown(_scratch, changedHalf: null, nowMs);
            // The first frame of a new page may not be fully drawn yet; keep checking briefly.
            _recallUntilMs = nowMs + RecallAfterSwitchMs;
            UpdateHeat(shiftFull, nowMs);
        }
        else
        {
            TrackBoxes(_scratch, shiftFull, nowMs, timed ? elapsed : double.NaN);
            if (shiftFull != 0) RestoreReturningItems(shiftFull, nowMs);
            // Something new appeared, perhaps a secret seen before: a panel re-opened, or a page
            // switch that looked like a scroll (two pages with the same line spacing). Only
            // remembered items inside the changed areas are checked, so this stays cheap.
            if (nowMs < _recallUntilMs) RecallKnown(_scratch, changedHalf: null, nowMs);
            else if (changes.Count > 0) RecallKnown(_scratch, changes, nowMs);
            UpdateHeat(shiftFull, nowMs);
            ShiftPending(shiftFull);
            foreach (var c in changes) QueueChange(c.Scale(2), shiftFull);
        }

        (_latest, _scratch) = (_scratch, _latest);
        (_prevProfile, _curProfile) = (_curProfile, _prevProfile);
        return true;
    }

    // ---- page areas already read ----------------------------------------------------
    //
    // Row ranges of the page that OCR has already read with the current rules, in page
    // coordinates (frame row minus the accumulated scroll). Scrolling back over them needs no
    // new OCR and, in streamer mode, no curtain: that is what removed the black sweeps when
    // scrolling up and down. Any in-place change to a range forgets it again.

    private readonly List<(long Top, long Bottom)> _readSpans = [];
    private const int MaxReadSpans = 64;

    private void MarkRead(long top, long bottom)
    {
        _readSpans.Add((top, bottom));
        _readSpans.Sort((a, b) => a.Top.CompareTo(b.Top));
        for (var i = _readSpans.Count - 1; i > 0; i--)
        {
            if (_readSpans[i].Top <= _readSpans[i - 1].Bottom)
            {
                _readSpans[i - 1] = (_readSpans[i - 1].Top, Math.Max(_readSpans[i - 1].Bottom, _readSpans[i].Bottom));
                _readSpans.RemoveAt(i);
            }
        }
        while (_readSpans.Count > MaxReadSpans) _readSpans.RemoveAt(0);
    }

    private void ForgetRead(long top, long bottom)
    {
        for (var i = _readSpans.Count - 1; i >= 0; i--)
        {
            var (t, b) = _readSpans[i];
            if (b <= top || t >= bottom) continue;
            _readSpans.RemoveAt(i);
            if (t < top) _readSpans.Add((t, top));
            if (b > bottom) _readSpans.Add((bottom, b));
        }
    }

    /// <summary>The parts of rows [top, bottom) in page coordinates that have not been read yet.</summary>
    private List<(long Top, long Bottom)> Unread(long top, long bottom)
    {
        var parts = new List<(long, long)> { (top, bottom) };
        foreach (var (t, b) in _readSpans)
        {
            var next = new List<(long, long)>();
            foreach (var (pt, pb) in parts)
            {
                if (b <= pt || t >= pb) { next.Add((pt, pb)); continue; }
                if (pt < t) next.Add((pt, t));
                if (pb > b) next.Add((b, pb));
            }
            parts = next;
        }
        return parts;
    }

    /// <summary>
    /// Queues a changed area for OCR. Content scrolling in at the leading edge is skipped where
    /// it was already read; anything else is a change in place and forgets earlier reads.
    /// </summary>
    private void QueueChange(RectI rect, int shiftFull)
    {
        var top = rect.Y - _motionSumFull;
        var bottom = rect.Bottom - _motionSumFull;

        var enteringEdge = shiftFull < 0
            ? new RectI(0, FullHeight + shiftFull - 32, FullWidth, -shiftFull + 32)
            : shiftFull > 0 ? new RectI(0, 0, FullWidth, shiftFull + 32) : default;

        if (shiftFull != 0 && enteringEdge.Contains(rect.Intersect(FullBounds)))
        {
            foreach (var (t, b) in Unread(top, bottom))
                AddPending(RectI.FromLTRB(rect.X, (int)(t + _motionSumFull), rect.Right, (int)(b + _motionSumFull)));
            return;
        }

        // A narrow change (a moving scrollbar, a small widget) is read, but does not make whole
        // rows count as unread: during scrolling the scrollbar changes every frame.
        if (rect.Width >= FullWidth / 4) ForgetRead(top, bottom);
        AddPending(rect);
    }

    // ---- page switches ----------------------------------------------------------------

    /// <summary>Share of the window that must change in place, without scrolling, to count as a new page.</summary>
    private const double PageSwitchShare = 0.35;

    private int _pageGeneration;
    private int _layoutEpoch;
    public int PageSwitches { get; private set; }

    /// <summary>
    /// True when a large part of the window changed in place. Animated areas (video, games) do
    /// not count, so a playing video never wipes the boxes beside it.
    /// </summary>
    private bool IsPageSwitch()
    {
        if (_dirtyGrid.Length == 0) return false;
        var changed = 0;
        for (var i = 0; i < _dirtyGrid.Length; i++)
            if (_dirtyGrid[i] && _heat[i] < HotLevel) changed++;
        return changed >= _dirtyGrid.Length * PageSwitchShare;
    }

    private void StartNewPage(long nowMs)
    {
        PageSwitches++;
        _pageGeneration++;              // OCR results for the old page are ignored when they arrive

        // Remember where this page's secrets were, so switching back covers them at once.
        foreach (var b in _boxes) Learn(b, nowMs);
        _boxes.Clear();
        _offscreen.Clear();
        _readSpans.Clear();
        _pending.Clear();
        _inflight.Clear();
        _detailY = -1;
        _needsFullScan = true;
        _velocityPerMs = 0;
        _windowVelocity = 0;
    }

    private void KeepStrip(GrayImage? strip)
    {
        _hasStrip = strip is not null;
        if (strip is null) return;
        _stripLatest.CopyFrom(strip);
        _stripPrevProfile = new int[strip.Height];
        _stripCurProfile = new int[strip.Height];
        Motion.RowProfile(_stripLatest, _stripPrevProfile);
    }

    private void UpdateHeat(int shift, long nowMs)
    {
        // Heat halves every 250 ms of real time. A video repainting 30 times a second stays hot;
        // a balance or chat line updating every second or so never does.
        var elapsed = Math.Max(0, nowMs - _heatUpdatedMs);
        _heatUpdatedMs = nowMs;
        var keep = (int)(256 * Math.Pow(0.5, elapsed / 250.0));

        for (var i = 0; i < _heat.Length; i++)
        {
            var h = (_heat[i] * keep) >> 8;
            // Only in-place changes heat a block. Content scrolling in is new, not animated.
            if (_dirtyGrid[i] && shift == 0) h = Math.Min(255, h + 48);
            _heat[i] = (byte)h;
        }
    }

    /// <summary>True when a region keeps changing in place, like a video or a game.</summary>
    public bool IsHot(RectI fullRect)
    {
        if (_heat.Length == 0) return false;
        var half = fullRect.Scale(0.5);
        var c0 = Math.Clamp(half.X / ChangeDetector.BlockSize, 0, _gridColumns - 1);
        var c1 = Math.Clamp((half.Right - 1) / ChangeDetector.BlockSize, 0, _gridColumns - 1);
        var r0 = Math.Clamp(half.Y / ChangeDetector.BlockSize, 0, _gridRows - 1);
        var r1 = Math.Clamp((half.Bottom - 1) / ChangeDetector.BlockSize, 0, _gridRows - 1);

        long sum = 0;
        var n = 0;
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
            {
                sum += _heat[r * _gridColumns + c];
                n++;
            }
        return n > 0 && sum / n >= HotLevel;
    }

    /// <summary>
    /// True if an area of the latest frame contains anything that could be text. Blank space
    /// needs no streamer-mode curtain; skipping it removes most of the black flashing.
    /// </summary>
    public bool HasInk(RectI fullRect)
    {
        var r = fullRect.Scale(0.5).Intersect(_latest.Bounds);
        if (r.IsEmpty) return false;
        var px = _latest.Pixels;
        var w = _latest.Width;
        var edges = 0;
        var samples = 0;
        for (var y = r.Y; y < r.Bottom; y += 2)
        {
            var row = y * w;
            for (var x = r.X; x < r.Right - 1; x++)
            {
                if (Math.Abs(px[row + x + 1] - px[row + x]) > 24) edges++;
                samples++;
            }
        }
        // Text runs at roughly 5 to 15% sharp edges; 3% ignores blank areas and their borders.
        return samples > 0 && edges * 100 >= samples * 3;
    }

    /// <summary>Share of the window that is animated. A mostly animated window with nothing hidden can be sampled less often.</summary>
    public double AnimatedFraction => _heat.Length == 0 ? 0 : _heat.Count(h => h >= HotLevel) / (double)_heat.Length;

    private static bool IsSame(GrayImage a, GrayImage b)
    {
        var pa = a.Pixels;
        var pb = b.Pixels;
        var n = a.Width * a.Height;
        for (var i = 0; i < n; i += 7)
            if (pa[i] != pb[i]) return false;
        return true;
    }

    private void TrackBoxes(GrayImage cur, int shiftFull, long nowMs, double elapsedMs)
    {
        double PerMs(double pixels) => double.IsNaN(elapsedMs) ? 0 : pixels / elapsedMs;

        var shift = ToHalf(shiftFull);
        var shiftFloor = (int)Math.Floor(shiftFull / 2.0);
        var shiftCeiling = (int)Math.Ceiling(shiftFull / 2.0);
        var wideBudget = 6;
        for (var i = _boxes.Count - 1; i >= 0; i--)
        {
            var b = _boxes[i];
            var best = MatchResult.None;

            if (b.Template is { IsTextured: true } t)
            {
                foreach (var candidate in new[] { 0, shiftFloor, shiftCeiling, b.LastShiftHalf }.Distinct())
                {
                    var m = TemplateMatcher.SearchLocal(cur, t, b.HalfX, b.HalfY + candidate, 3, 3);
                    if (m.Score > best.Score) best = m;
                }

                if (best.Score < AcceptScore && wideBudget-- > 0)
                {
                    var m = TemplateMatcher.SearchWide(cur, t, b.HalfX, b.HalfY + shift, 8, Math.Min(cur.Height, 400));
                    if (m.Score >= WideAcceptScore && m.Score > best.Score) best = m;
                }
            }

            LastTrackScore = best.Score;
            if (best.Score >= AcceptScore)
            {
                var moved = best.Y - b.HalfY;
                b.LastShiftHalf = moved;
                b.VelocityPerMs = PerMs(moved * 2);
                b.HalfX = best.X;
                b.HalfY = best.Y;
                b.LostSinceMs = null;
                b.RescanRequested = false;
            }
            else
            {
                // Could not see the text: follow the page scroll, the best guess.
                b.HalfY += shift;
                b.LastShiftHalf = shift;
                b.VelocityPerMs = PerMs(shiftFull);

                // Half-scrolled past the edge, the text cannot be matched but is still partly
                // visible. That is expected, not lost: keep covering it.
                var templateRect = new RectI(b.HalfX, b.HalfY, b.Template?.Width ?? b.Width / 2, b.Template?.Height ?? b.Height / 2);
                // While the page is scrolling the measured scroll is exact, so a box that briefly
                // fails to match (small text is hard to match at half size) is still on its text.
                if (shiftFull == 0 && cur.Bounds.Contains(templateRect))
                {
                    b.LostSinceMs ??= nowMs;
                    if (!b.RescanRequested)
                    {
                        AddPending(b.Rect.Inflate(8, 16));
                        b.RescanRequested = true;
                    }
                }
            }

            var outside = !b.Rect.IntersectsWith(FullBounds);
            var expired = b.LostSinceMs is { } since && nowMs - since > LostTimeoutMs;
            if (outside)
            {
                RemovedOutside++;
                Remember(b, nowMs);
            }
            else if (expired)
            {
                RemovedExpired++;
                // Re-read the spot in case the text is still there but looks different.
                AddPending(b.Rect.Inflate(8, 16));
            }
            if (outside || expired) _boxes.RemoveAt(i);
        }
    }

    // ---- items that scrolled out of view ----------------------------------------------

    private sealed record Remembered(RedactionBox Box, long ContentY, long ForgetAtMs);

    private readonly List<Remembered> _offscreen = [];
    private const int MaxRemembered = 300;
    private const long RememberMs = 120_000;

    public int RestoredFromMemory { get; private set; }

    /// <summary>
    /// Keeps an item that scrolled out of view, in page coordinates. If the user scrolls back,
    /// the box reappears on the very first frame, with no OCR round trip.
    /// </summary>
    private void Remember(RedactionBox box, long nowMs)
    {
        if (box.Template is null) return;
        _offscreen.Add(new Remembered(box, box.Rect.Y - _motionSumFull, nowMs + RememberMs));
        if (_offscreen.Count > MaxRemembered) _offscreen.RemoveAt(0);
    }

    private void RestoreReturningItems(int shiftFull, long nowMs)
    {
        for (var i = _offscreen.Count - 1; i >= 0; i--)
        {
            var r = _offscreen[i];
            if (nowMs > r.ForgetAtMs)
            {
                _offscreen.RemoveAt(i);
                continue;
            }

            var box = r.Box;
            box.HalfY = (int)Math.Round((r.ContentY + _motionSumFull - box.OffsetY) / 2.0);
            if (!box.Rect.IntersectsWith(FullBounds)) continue;

            _offscreen.RemoveAt(i);
            if (_boxes.Any(b => b.RuleId == box.RuleId && b.Rect.IntersectsWith(box.Rect))) continue;

            box.LostSinceMs = null;
            box.RescanRequested = false;
            box.LastShiftHalf = ToHalf(shiftFull);
            box.VelocityPerMs = _velocityPerMs;
            _boxes.Add(box);
            RestoredFromMemory++;
        }
    }

    // ---- secrets already found --------------------------------------------------------
    //
    // Small grey pictures of items OCR has confirmed, kept in memory only (never written to disk).
    // When one reappears where it was last seen (switching back to a tab, a panel shown again) it
    // is covered on the very frame it appears, before OCR has read anything. OCR still reads the
    // area and removes the box if it was wrong (NeedsOcrConfirm). The check costs a few
    // microseconds per item and is capped per frame, so it can never slow tracking down.

    private sealed class KnownItem
    {
        public required Guid RuleId { get; init; }
        public required Template Template { get; set; }
        public int HalfX, HalfY, Width, Height, OffsetX, OffsetY;
        public long LastSeenMs;
    }

    private readonly List<KnownItem> _known = [];
    private const int MaxKnown = 32;
    // Measured: a remembered item scores 1.00 when its page is redrawn identically, but 0.81-0.89
    // when the same text is redrawn with slightly different anti-aliasing. A wrong recall only
    // costs a brief extra box: OCR removes it (see NeedsOcrConfirm).
    private const double RecallScore = 0.85;
    private const double RecallBudgetMs = 2;
    private const long RecallAfterSwitchMs = 400;
    private long _recallUntilMs = long.MinValue;

    public int RecalledFromMemory { get; private set; }

    /// <summary>Remembers a confirmed box, or refreshes where it was last seen.</summary>
    private void Learn(RedactionBox box, long nowMs)
    {
        if (box.NeedsOcrConfirm || box.LostSinceMs is not null || box.Template is not { IsTextured: true } t) return;

        // The same item: same pixels, or confirmed again at the same spot (then the newer picture
        // replaces the older one, which may have been taken mid-animation).
        var item = _known.FirstOrDefault(k => k.RuleId == box.RuleId &&
            (SameItem(k.Template, t) ||
             Math.Abs(k.HalfX - box.HalfX) <= 2 && Math.Abs(k.HalfY - box.HalfY) <= 2 && Math.Abs(k.Template.Width - t.Width) <= 2));
        if (item is null)
        {
            if (_known.Count >= MaxKnown) _known.Remove(_known.MinBy(k => k.LastSeenMs)!);
            item = new KnownItem { RuleId = box.RuleId, Template = t };
            _known.Add(item);
        }
        item.Template = t;
        item.HalfX = box.HalfX;
        item.HalfY = box.HalfY;
        item.Width = box.Width;
        item.Height = box.Height;
        item.OffsetX = box.OffsetX;
        item.OffsetY = box.OffsetY;
        item.LastSeenMs = nowMs;
    }

    /// <summary>
    /// Covers remembered items that are back where they were last seen. Only items touching a
    /// changed area are checked, unless <paramref name="changedHalf"/> is null (a whole new page).
    /// </summary>
    private void RecallKnown(GrayImage cur, IReadOnlyList<RectI>? changedHalf, long nowMs)
    {
        if (_known.Count == 0) return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var trace = TraceRecall ? new System.Text.StringBuilder() : null;

        foreach (var k in _known.OrderByDescending(k => k.LastSeenMs))
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds > RecallBudgetMs) break;

            var at = new RectI(k.HalfX, k.HalfY, k.Template.Width, k.Template.Height);
            if (changedHalf is not null && !changedHalf.Any(c => c.IntersectsWith(at))) { trace?.Append(" unchanged"); continue; }
            var full = new RectI(k.HalfX * 2 + k.OffsetX, k.HalfY * 2 + k.OffsetY, k.Width, k.Height);
            // A box that is on its text already covers it. One that is not (lost, or dragged along by
            // a page switch that looked like a scroll) is only a guess, and gives way to the exact match.
            var there = _boxes.Where(b => b.RuleId == k.RuleId && b.Rect.IntersectsWith(full)).ToList();
            if (there.Any(b => b.LostSinceMs is null && IsOnItsText(cur, b))) { trace?.Append(" covered"); continue; }

            // One exact check first: most remembered items are simply not on this page.
            var exact = TemplateMatcher.Score(cur, k.Template, k.HalfX, k.HalfY);
            var match = exact >= RecallScore ? new MatchResult(k.HalfX, k.HalfY, exact)
                      : exact >= 0.6 ? TemplateMatcher.SearchLocal(cur, k.Template, k.HalfX, k.HalfY, 2, 2)
                      : MatchResult.None;
            trace?.Append($" {exact:F2}/{match.Score:F2}");
            if (match.Score < RecallScore) continue;

            _boxes.RemoveAll(there.Contains);
            _boxes.Add(new RedactionBox
            {
                RuleId = k.RuleId,
                HalfX = match.X,
                HalfY = match.Y,
                Width = k.Width,
                Height = k.Height,
                OffsetX = k.OffsetX,
                OffsetY = k.OffsetY,
                Template = k.Template,
                NeedsOcrConfirm = true,
            });
            k.LastSeenMs = nowMs;
            RecalledFromMemory++;
        }

        if (trace is not null)
            EngineLog.Info($"recall {(changedHalf is null ? "page" : $"changes={changedHalf.Count}")} known={_known.Count} boxes={_boxes.Count}:{trace}");
    }

    /// <summary>Opt-in trace of the memory check (PRIVACYGUARD_DEBUG_STATS=1): counts and scores only.</summary>
    private static readonly bool TraceRecall = Environment.GetEnvironmentVariable("PRIVACYGUARD_DEBUG_STATS") == "1";

    private static bool IsOnItsText(GrayImage cur, RedactionBox b) =>
        b.Template is not { IsTextured: true } t || TemplateMatcher.Score(cur, t, b.HalfX, b.HalfY) >= AcceptScore;

    /// <summary>Two templates of the same item: same size and nearly the same pixels.</summary>
    private static bool SameItem(Template a, Template b)
    {
        if (Math.Abs(a.Width - b.Width) > 1 || Math.Abs(a.Height - b.Height) > 1) return false;
        var w = Math.Min(a.Width, b.Width);
        var h = Math.Min(a.Height, b.Height);
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                double pa = a.Pixels[y * a.Width + x], pb = b.Pixels[y * b.Width + x];
                sa += pa; sb += pb; saa += pa * pa; sbb += pb * pb; sab += pa * pb;
            }
        var n = (double)(w * h);
        var cov = sab / n - sa / n * (sb / n);
        var va = saa / n - sa / n * (sa / n);
        var vb = sbb / n - sb / n * (sb / n);
        return va > 1 && vb > 1 && cov / Math.Sqrt(va * vb) >= 0.95;
    }

    /// <summary>Stops predicting motion once the window has gone quiet. Returns true if anything changed.</summary>
    public bool SettleIfIdle(long nowMs)
    {
        if (nowMs - _lastFrameMs < 80) return false;

        var changed = _windowVelocity != 0 || _velocityPerMs != 0;
        _windowVelocity = 0;
        _velocityPerMs = 0;
        foreach (var b in _boxes)
        {
            if (b.VelocityPerMs != 0) changed = true;
            b.VelocityPerMs = 0;
            b.LastShiftHalf = 0;
        }

        // Scrolling just stopped. A still window sends no new frames, so this is the last
        // chance to correct any box that drifted: check every one against the final frame.
        if (changed) VerifyBoxesAtRest(nowMs);

        // Lost boxes still expire while the window is static.
        for (var i = _boxes.Count - 1; i >= 0; i--)
        {
            if (_boxes[i].LostSinceMs is { } since && nowMs - since > LostTimeoutMs)
            {
                RemovedExpired++;
                AddPending(_boxes[i].Rect.Inflate(8, 16));
                _boxes.RemoveAt(i);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Re-finds every box on the latest frame: snaps it back onto its text if it drifted, marks
    /// it lost and asks OCR to look if the text cannot be found, and merges duplicates.
    /// </summary>
    public void VerifyBoxesAtRest(long nowMs)
    {
        if (!_hasFrame) return;

        foreach (var b in _boxes)
        {
            if (b.Template is not { IsTextured: true } t) continue;

            var m = TemplateMatcher.SearchLocal(_latest, t, b.HalfX, b.HalfY, 2, 2);
            if (m.Score < AcceptScore)
            {
                var wide = TemplateMatcher.SearchWide(_latest, t, b.HalfX, b.HalfY, 8, Math.Min(_latest.Height, 400));
                if (wide.Score >= WideAcceptScore && wide.Score > m.Score) m = wide;
            }

            if (m.Score >= AcceptScore)
            {
                b.HalfX = m.X;
                b.HalfY = m.Y;
                b.LostSinceMs = null;
                b.RescanRequested = false;
            }
            else
            {
                b.LostSinceMs ??= nowMs;
                if (!b.RescanRequested)
                {
                    AddPending(b.Rect.Inflate(8, 16));
                    b.RescanRequested = true;
                }
            }
        }

        // Two boxes that ended up on the same text become one.
        for (var i = _boxes.Count - 1; i > 0; i--)
        {
            for (var j = 0; j < i; j++)
            {
                if (_boxes[i].RuleId == _boxes[j].RuleId && _boxes[i].Rect.IoU(_boxes[j].Rect) > 0.5)
                {
                    _boxes.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // ---- pending work --------------------------------------------------------------

    private void AddPending(RectI rect)
    {
        var r = rect.Intersect(FullBounds);
        if (r.IsEmpty) return;
        _pending.Add(r);
        var merged = ChangeDetector.MergeOverlapping(_pending);
        _pending.Clear();
        _pending.AddRange(merged);
    }

    private void ShiftPending(int dy)
    {
        if (dy == 0) return;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var moved = _pending[i].Offset(0, dy).Intersect(FullBounds);
            if (moved.IsEmpty) _pending.RemoveAt(i);
            else _pending[i] = moved;
        }
        foreach (var areas in _inflight.Values)
            for (var i = 0; i < areas.Count; i++)
                areas[i] = areas[i].Offset(0, dy);
    }

    /// <summary>Reads the whole window again, for example because the rules changed: earlier reads no longer count.</summary>
    public void RequestFullScan()
    {
        _needsFullScan = true;
        _readSpans.Clear();
    }

    /// <summary>Pending areas that may be read now. Animated areas wait their turn.</summary>
    private List<RectI> EligiblePending(long nowMs)
    {
        if (_pending.Count == 0) return [];
        var hotAllowed = nowMs - _lastHotOcrMs >= HotOcrIntervalMs;
        return _pending.Where(r => hotAllowed || !IsHot(r)).ToList();
    }

    /// <summary>Drops boxes for rules that were deleted or switched off.</summary>
    public bool RemoveBoxesExcept(IReadOnlySet<Guid> liveRuleIds)
    {
        _offscreen.RemoveAll(r => !liveRuleIds.Contains(r.Box.RuleId));
        _known.RemoveAll(k => !liveRuleIds.Contains(k.RuleId));
        return _boxes.RemoveAll(b => !liveRuleIds.Contains(b.RuleId)) > 0;
    }

    /// <summary>Picks the next piece of this window to OCR, most urgent first.</summary>
    /// <param name="urgentOnly">Only new content or a new page; background re-reads wait.</param>
    public OcrRequest? TakeOcrRequest(long nowMs, bool allowPeriodic, bool urgentOnly = false)
    {
        if (!_hasFrame) return null;

        if (!_needsFullScan && _pending.Count == 0 && _detailY < 0 && allowPeriodic && nowMs - _lastFullScanMs > 60000)
            _needsFullScan = true;

        RectI crop;
        int scale;
        var isFull = false;
        var isNew = false;
        var reading = new List<RectI>();

        if (_needsFullScan)
        {
            crop = FullBounds;
            // Small windows are read enlarged at once; anything bigger is read at normal size
            // first (about twice as fast, so a new page is covered sooner), then re-read
            // enlarged in strips by the detail pass so small text is not missed.
            scale = crop.Area <= 300_000 ? 2 : 1;
            isFull = true;
            _needsFullScan = false;
            _pending.Clear();
            _lastFullScanMs = nowMs;

            // A big window is first read at normal size for speed, then re-read in enlarged
            // strips so small text is not missed.
            _detailY = scale == 1 ? 0 : -1;
        }
        else if (EligiblePending(nowMs) is { Count: > 0 } eligible)
        {
            isNew = true;
            // Newest content first: the edge it is scrolling in from. It will stay on screen the
            // longest, and small crops keep OCR fast enough to keep up with hard scrolling.
            var seed = _windowVelocity < 0 ? eligible.MaxBy(r => r.Bottom)
                     : _windowVelocity > 0 ? eligible.MinBy(r => r.Y)
                     : eligible.MaxBy(r => r.Area);
            var cluster = seed;
            bool grew;
            do
            {
                grew = false;
                foreach (var r in eligible)
                {
                    if (cluster.Contains(r) || r.Y > cluster.Bottom + 48 || r.Bottom < cluster.Y - 48) continue;
                    cluster = cluster.Union(r);
                    grew = true;
                }
            } while (grew);

            crop = new RectI(0, cluster.Y - 12, FullWidth, cluster.Height + 24).Intersect(FullBounds);
            if (crop.Height > MaxBandHeight)
            {
                crop = _windowVelocity < 0
                    ? RectI.FromLTRB(0, crop.Bottom - MaxBandHeight, FullWidth, crop.Bottom)
                    : crop with { Height = MaxBandHeight };
            }

            // Remember exactly which changed areas this job reads: strict mode keeps only those
            // covered, never the whole full-width strip.
            reading.AddRange(_pending.Select(r => r.Intersect(crop)).Where(r => !r.IsEmpty));

            // Whatever this crop does not cover stays queued; slivers are dropped. The crop must
            // be subtracted whole: shrinking it left a sliver at the window edge that was re-read forever.
            var remaining = RegionMath.Subtract(_pending, crop).Where(r => r.Height >= 6).ToList();
            _pending.Clear();
            _pending.AddRange(remaining);
            scale = crop.Area <= 900_000 ? 2 : 1;

            // The strip is full width and mostly static, so judge heat on the changed areas themselves.
            if (eligible.Any(r => crop.IntersectsWith(r) && IsHot(r))) _lastHotOcrMs = nowMs;
        }
        else if (_detailY >= 0)
        {
            if (urgentOnly) return null;
            crop = new RectI(0, _detailY, FullWidth, Math.Min(320, FullHeight - _detailY)).Intersect(FullBounds);
            scale = 2;
            _detailY += 290;
            if (_detailY >= FullHeight) _detailY = -1;
            if (crop.IsEmpty) return null;
        }
        else
        {
            return null;
        }

        var halfRegion = crop.Scale(0.5).Intersect(_latest.Bounds);
        var id = ++_nextRequestId;
        _inflight[id] = reading;

        return new OcrRequest
        {
            Tracker = this,
            Crop = crop,
            Scale = scale,
            MotionSumFull = _motionSumFull,
            HalfSnapshot = _latest.Crop(halfRegion),
            HalfRegion = halfRegion,
            IsFullScan = isFull,
            Id = id,
            PageGeneration = _pageGeneration,
            LayoutEpoch = _layoutEpoch,
            Urgent = isFull || isNew,
        };
    }

    /// <summary>Folds an OCR result back in, moving each detection to where its text is now.</summary>
    public void ApplyOcr(OcrRequest request, IReadOnlyList<Detection> detections, long nowMs)
    {
        _inflight.Remove(request.Id);

        // The page was switched while this was being read: its findings belong to the old page.
        if (request.PageGeneration != _pageGeneration) return;
        if (request.IsFullScan) _hasScanned = true;
        MarkRead(request.Crop.Y - request.MotionSumFull, request.Crop.Bottom - request.MotionSumFull);

        var deltaFull = (int)(_motionSumFull - request.MotionSumFull);
        var deltaHalf = ToHalf(deltaFull);
        var matched = new HashSet<RedactionBox>();

        foreach (var det in detections)
        {
            var padded = Pad(det.Rect).Intersect(FullBounds);
            if (padded.IsEmpty) continue;

            var halfRect = padded.Scale(0.5);
            var template = Template.Create(request.HalfSnapshot, halfRect.Offset(-request.HalfRegion.X, -request.HalfRegion.Y));

            var hx = halfRect.X;
            var hy = halfRect.Y + deltaHalf;
            var found = deltaHalf == 0;

            if (template is { IsTextured: true })
            {
                var m = TemplateMatcher.SearchLocal(_latest, template, hx, hy, 3, 4);
                if (m.Score < AcceptScore)
                {
                    var wide = TemplateMatcher.SearchWide(_latest, template, hx, hy, 8, Math.Min(_latest.Height, 400));
                    if (wide.Score >= WideAcceptScore && wide.Score > m.Score) m = wide;
                }
                if (m.Score >= AcceptScore)
                {
                    hx = m.X;
                    hy = m.Y;
                    found = true;
                }
            }

            var box = new RedactionBox
            {
                RuleId = det.RuleId,
                HalfX = hx,
                HalfY = hy,
                Width = padded.Width,
                Height = padded.Height,
                OffsetX = padded.X - halfRect.X * 2,
                OffsetY = padded.Y - halfRect.Y * 2,
                Template = template,
                LostSinceMs = found ? null : nowMs,
                VelocityPerMs = _velocityPerMs,
                // Read before the latest resize: where it lands now still needs confirming.
                NeedsOcrConfirm = request.LayoutEpoch != _layoutEpoch,
            };

            var existing = _boxes.FirstOrDefault(b => !matched.Contains(b) && b.RuleId == det.RuleId && b.Rect.IntersectsWith(box.Rect))
                        ?? _boxes.FirstOrDefault(b => !matched.Contains(b) && b.Rect.IoU(box.Rect) > 0.3);

            if (existing is null)
            {
                _boxes.Add(box);
                matched.Add(box);
                Learn(box, nowMs);
                continue;
            }

            matched.Add(existing);
            if (request.LayoutEpoch == _layoutEpoch)
            {
                existing.NeedsOcrConfirm = false;
                existing.OcrMisses = 0;
            }
            if (found || existing.LostSinceMs is not null)
            {
                existing.HalfX = box.HalfX;
                existing.HalfY = box.HalfY;
                existing.Width = box.Width;
                existing.Height = box.Height;
                existing.OffsetX = box.OffsetX;
                existing.OffsetY = box.OffsetY;
                existing.Template = template ?? existing.Template;
                existing.LostSinceMs = found ? null : existing.LostSinceMs ?? nowMs;
                existing.RescanRequested = false;
            }
            Learn(existing, nowMs);
        }

        // OCR looked where a lost box used to be and found nothing: the text is gone.
        // A box awaiting confirmation after a resize gets a second, targeted read first, so
        // one flaky OCR pass never uncovers text that is really there.
        var sameLayout = request.LayoutEpoch == _layoutEpoch;
        for (var i = _boxes.Count - 1; i >= 0; i--)
        {
            var b = _boxes[i];
            var unconfirmed = b.NeedsOcrConfirm && sameLayout;
            if (matched.Contains(b) || (b.LostSinceMs is null && !unconfirmed)) continue;
            var thenRect = b.Rect.Offset(0, -deltaFull);
            if (request.Crop.Intersect(thenRect).Area < thenRect.Area * 0.6) continue;

            if (unconfirmed && ++b.OcrMisses < 2)
            {
                AddPending(b.Rect.Inflate(8, 16));
                continue;
            }
            _boxes.RemoveAt(i);
            RemovedByOcr++;
        }
    }

    /// <summary>OCR boxes hug the glyphs; widen them so accents, descenders and anti-aliasing stay hidden.</summary>
    public static RectI Pad(RectI rect)
    {
        var padY = Math.Max(3, (int)(rect.Height * 0.3));
        var padX = Math.Max(4, (int)(rect.Height * 0.4));
        return rect.Inflate(padX, padY);
    }

    // ---- output -------------------------------------------------------------------

    /// <param name="strict">Also cover new content that has not been read yet.</param>
    public WindowSnapshot Snapshot(bool strict)
    {
        var boxes = new List<MovingRect>(_boxes.Count);
        foreach (var b in _boxes)
            boxes.Add(new MovingRect(b.Rect, b.VelocityPerMs));

        var pending = new List<MovingRect>();
        if (strict && _hasFrame)
        {
            if (!_hasScanned && CurtainUntilFirstScan)
            {
                pending.Add(new MovingRect(FullBounds, 0));
            }
            else if (_hasScanned)
            {
                // Tiny changes such as a blinking caret are not worth a curtain, and animated
                // areas (video, games) are never curtained, or they would stay black.
                const long minArea = 32 * 32 * 3;
                foreach (var r in _pending.Concat(_inflight.Values.SelectMany(a => a)))
                    if (r.Area >= minArea && !IsHot(r) && HasInk(r)) pending.Add(new MovingRect(r.Intersect(FullBounds), _velocityPerMs));
            }
        }

        return new WindowSnapshot(Hwnd, boxes, pending, _frameTimeMs, _frameIntervalMs);
    }

    /// <summary>
    /// Time from the overlay being updated to Windows showing it on screen, in milliseconds.
    /// Measured on a 60 Hz display by recording the screen while scrolling: 30 ms gave the
    /// fewest exposed frames (12 and 45 ms were both worse).
    /// Boxes are placed for the moment they become visible.
    /// </summary>
    public const double PresentLagMs = 30;

    /// <summary>
    /// Where a moving box should be drawn so it sits on its text at <paramref name="targetTimeMs"/>
    /// (now plus <see cref="PresentLagMs"/>). The box keeps its exact size: no margins, no trail.
    /// If the next frame is overdue the scroll has most likely stopped, so the box stays where
    /// the text was last seen instead of flying ahead.
    /// </summary>
    public static RectI PredictAt(MovingRect box, double frameTimeMs, double frameIntervalMs, double targetTimeMs)
    {
        if (Math.Abs(box.VelocityPerMs) < 0.01 || double.IsNaN(frameTimeMs)) return box.Rect;

        var elapsed = targetTimeMs - frameTimeMs;
        if (elapsed <= 0) return box.Rect;

        var horizon = frameIntervalMs * 1.5 + PresentLagMs;
        if (elapsed > horizon + frameIntervalMs) return box.Rect;

        var dy = box.VelocityPerMs * Math.Min(elapsed, horizon);
        return box.Rect.Offset(0, (int)Math.Round(dy));
    }
}
