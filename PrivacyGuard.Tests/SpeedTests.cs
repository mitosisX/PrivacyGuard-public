using PrivacyGuard.Engine.Geometry;
using PrivacyGuard.Engine.Imaging;
using PrivacyGuard.Engine.Ocr;
using PrivacyGuard.Engine.Tracking;

namespace PrivacyGuard.Tests;

/// <summary>The faster paths: secrets covered from memory before OCR, and the second OCR reader's limits.</summary>
public class SpeedTests
{
    private const int W = 400, H = 300;           // half-resolution viewport
    private static readonly Guid RuleId = Guid.NewGuid();
    private static readonly RectI Secret = new(100, 400, 240, 14);

    private static (WindowTracker Tracker, GrayImage Page) StartWithBox()
    {
        var page = FakePage.Create(W, 3000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 0);
        var request = tracker.TakeOcrRequest(0, allowPeriodic: false)!;
        tracker.ApplyOcr(request, [new Detection(Secret, RuleId)], 0);
        Assert.Single(tracker.Boxes);
        return (tracker, page);
    }

    private static void DrainOcr(WindowTracker tracker, long now, IReadOnlyList<Detection>? detections = null)
    {
        while (tracker.TakeOcrRequest(now, false) is { } r) tracker.ApplyOcr(r, detections ?? [], now);
    }

    [Fact]
    public void Switching_back_to_a_page_covers_its_secret_before_ocr_reads_it()
    {
        var (tracker, page) = StartWithBox();
        var where = tracker.Boxes[0].Rect;

        // Another tab, then back to the first one.
        tracker.OnFrame(FakePage.Viewport(FakePage.Create(W, 3000, seed: 99), 500, H), W * 2, H * 2, 50);
        Assert.Empty(tracker.Boxes);
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 100);

        // Covered on the very frame it came back, with no OCR in between.
        var box = Assert.Single(tracker.Boxes);
        Assert.Equal(where, box.Rect);
        Assert.True(box.NeedsOcrConfirm);
        Assert.Equal(1, tracker.RecalledFromMemory);

        // OCR then reads the page and confirms it.
        var scan = tracker.TakeOcrRequest(110, false)!;
        Assert.True(scan.IsFullScan);
        tracker.ApplyOcr(scan, [new Detection(Secret, RuleId)], 120);
        Assert.False(Assert.Single(tracker.Boxes).NeedsOcrConfirm);
    }

    [Fact]
    public void A_box_from_memory_that_ocr_cannot_confirm_is_removed_after_two_reads()
    {
        var (tracker, page) = StartWithBox();
        tracker.OnFrame(FakePage.Viewport(FakePage.Create(W, 3000, seed: 99), 500, H), W * 2, H * 2, 50);
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 100);
        Assert.Single(tracker.Boxes);

        // The first read does not find it: one miss is not enough to uncover it...
        var scan = tracker.TakeOcrRequest(110, false)!;
        tracker.ApplyOcr(scan, [], 120);
        Assert.Single(tracker.Boxes);

        // ...the targeted re-read does not find it either: now it goes.
        DrainOcr(tracker, 130);
        Assert.Empty(tracker.Boxes);
    }

    [Fact]
    public void A_secret_that_disappears_and_comes_back_in_place_is_covered_from_memory()
    {
        var (tracker, page) = StartWithBox();
        DrainOcr(tracker, 0);
        var full = FakePage.Viewport(page, 1000, H);

        // The secret is hidden (a panel collapses)...
        var blank = FakePage.Viewport(page, 1000, H);
        var half = Secret.Scale(0.5).Inflate(4, 4).Intersect(blank.Bounds);
        for (var y = half.Y; y < half.Bottom; y++)
            for (var x = half.X; x < half.Right; x++)
                blank[x, y] = 245;
        tracker.OnFrame(blank, W * 2, H * 2, 100);
        DrainOcr(tracker, 120);      // OCR looks there and finds nothing
        Assert.Empty(tracker.Boxes);

        // ...and shown again: covered on that frame, before OCR.
        tracker.OnFrame(full, W * 2, H * 2, 200);
        var box = Assert.Single(tracker.Boxes);
        Assert.True(box.NeedsOcrConfirm);
        Assert.Equal(1, tracker.RecalledFromMemory);
    }

    [Fact]
    public void Remembered_secrets_of_a_removed_rule_are_forgotten()
    {
        var (tracker, page) = StartWithBox();
        tracker.RemoveBoxesExcept(new HashSet<Guid>());
        tracker.OnFrame(FakePage.Viewport(FakePage.Create(W, 3000, seed: 99), 500, H), W * 2, H * 2, 50);
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 100);
        Assert.Empty(tracker.Boxes);
    }

    [Fact]
    public void The_second_reader_never_takes_background_rereads()
    {
        var page = FakePage.Create(W, 3000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 0);
        DrainOcr(tracker, 0);

        // A minute later the page is re-read in the background: normal size first, then
        // enlarged strips. None of that is urgent, so the second reader leaves it alone.
        var rescan = tracker.TakeOcrRequest(61_000, allowPeriodic: true)!;
        Assert.True(rescan.IsFullScan);
        tracker.ApplyOcr(rescan, [], 61_000);

        Assert.Null(tracker.TakeOcrRequest(61_010, false, urgentOnly: true));
        var detail = tracker.TakeOcrRequest(61_010, false);
        Assert.NotNull(detail);
        Assert.False(detail!.Urgent);
    }

    [Fact]
    public void Detail_strips_of_a_new_page_are_urgent_so_both_readers_share_them()
    {
        var page = FakePage.Create(W, 3000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 0);

        // The first read of a big window goes at normal size; small text waits for the
        // enlarged strips. Those strips are part of covering a new page, not a background re-read.
        var scan = tracker.TakeOcrRequest(0, false, urgentOnly: true)!;
        Assert.True(scan.IsFullScan);
        tracker.ApplyOcr(scan, [], 0);

        var strip = tracker.TakeOcrRequest(10, false, urgentOnly: true);
        Assert.NotNull(strip);
        Assert.False(strip!.IsFullScan);
        Assert.True(strip.Urgent);
    }

    [Fact]
    public void A_resize_defers_the_full_rescan_until_the_size_settles()
    {
        var (tracker, page) = StartWithBox();
        DrainOcr(tracker, 0);

        // The window grows: a frame of a new size. Nothing is read while it keeps changing...
        tracker.OnFrame(FakePage.Viewport(page, 1000, H + 20), W * 2, (H + 20) * 2, 100);
        Assert.Null(tracker.TakeOcrRequest(100, false));
        Assert.Null(tracker.TakeOcrRequest(100 + WindowTracker.ResizeSettleMs - 1, false));

        // ...the old box stays up as a safe guess meanwhile...
        Assert.True(Assert.Single(tracker.Boxes).NeedsOcrConfirm);

        // ...and once the size has settled, the whole window is read on the new layout.
        var scan = tracker.TakeOcrRequest(100 + WindowTracker.ResizeSettleMs, false);
        Assert.NotNull(scan);
        Assert.True(scan!.IsFullScan);
    }

    [Fact]
    public void Content_scrolling_into_view_counts_as_urgent()
    {
        var page = FakePage.Create(W, 3000);
        var tracker = new WindowTracker(new IntPtr(1));
        var scroll = 1000;
        tracker.OnFrame(FakePage.Viewport(page, scroll, H), W * 2, H * 2, 0);
        DrainOcr(tracker, 0);

        tracker.OnFrame(FakePage.Viewport(page, scroll + 30, H), W * 2, H * 2, 16);
        var request = tracker.TakeOcrRequest(20, false, urgentOnly: true);
        Assert.NotNull(request);
        Assert.True(request!.Urgent);
    }
}
