using PrivacyGuard.Core.Models;
using PrivacyGuard.Engine.Geometry;
using PrivacyGuard.Engine.Imaging;
using PrivacyGuard.Engine.Ocr;
using PrivacyGuard.Engine.Tracking;

namespace PrivacyGuard.Tests;

/// <summary>Synthetic "web page": white background with rows of dark, word-like blobs.</summary>
internal static class FakePage
{
    public static GrayImage Create(int width, int height, int seed = 42)
    {
        var img = new GrayImage(width, height);
        Array.Fill(img.Pixels, (byte)245);
        var rng = new Random(seed);
        for (var lineY = 6; lineY < height - 10; lineY += 14)
        {
            var x = 4 + rng.Next(10);
            while (x < width - 20)
            {
                var wordW = 6 + rng.Next(30);
                for (var y = lineY; y < lineY + 7; y++)
                    for (var xx = x; xx < Math.Min(width, x + wordW); xx++)
                        img[xx, y] = (byte)(rng.Next(3) == 0 ? 245 : 20 + rng.Next(60));
                x += wordW + 4 + rng.Next(6);
            }
        }
        return img;
    }

    /// <summary>The part of the page visible when scrolled down by <paramref name="scroll"/> pixels.</summary>
    public static GrayImage Viewport(GrayImage page, int scroll, int height) =>
        page.Crop(new RectI(0, scroll, page.Width, height));
}

public class GeometryTests
{
    [Fact]
    public void Subtract_splits_around_a_hole()
    {
        var parts = RegionMath.Subtract([new RectI(0, 0, 100, 100)], new RectI(40, 40, 20, 20));
        Assert.Equal(4, parts.Count);
        Assert.Equal(100 * 100 - 20 * 20, RegionMath.TotalArea(parts));
        Assert.DoesNotContain(parts, p => p.IntersectsWith(new RectI(40, 40, 20, 20)));
    }

    [Fact]
    public void Coverage_reports_fraction_hidden()
    {
        var target = new RectI(0, 0, 100, 10);
        Assert.Equal(1.0, RegionMath.Coverage(target, [new RectI(-5, -5, 200, 50)]), 3);
        Assert.Equal(0.5, RegionMath.Coverage(target, [new RectI(0, 0, 50, 10)]), 3);
        Assert.Equal(0.0, RegionMath.Coverage(target, []), 3);
    }

    [Fact]
    public void Scale_always_covers_the_original()
    {
        var r = new RectI(3, 5, 7, 9);
        var half = r.Scale(0.5);
        Assert.True(half.Scale(2).Contains(r));
    }
}

public class MotionTests
{
    private static int EstimateShift(GrayImage a, GrayImage b)
    {
        var pa = new int[a.Height];
        var pb = new int[b.Height];
        Motion.RowProfile(a, pa);
        Motion.RowProfile(b, pb);
        return Motion.EstimateVerticalShift(a, b, pa, pb, a.Height / 2);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(41)]
    [InlineData(-25)]
    [InlineData(-71)]
    [InlineData(3)]
    public void Odd_pixel_scroll_is_measured_exactly(int scrollFull)
    {
        // Build a full-resolution page, scroll it by an odd number of pixels, then halve both
        // frames exactly as the GPU does. In the half-size images this is a half-pixel shift.
        var page = FakePage.Create(800, 4000, seed: 5);
        GrayImage Half(int scroll)
        {
            var full = FakePage.Viewport(page, scroll, 700);
            var half = new GrayImage(400, 350);
            for (var y = 0; y < 350; y++)
                for (var x = 0; x < 400; x++)
                    half[x, y] = (byte)((full[2 * x, 2 * y] + full[2 * x + 1, 2 * y] + full[2 * x, 2 * y + 1] + full[2 * x + 1, 2 * y + 1] + 2) / 4);
            return half;
        }

        Assert.Equal(-scrollFull, EstimateShift(Half(1500), Half(1500 + scrollFull)));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(5)]
    [InlineData(120)]
    [InlineData(-45)]
    public void Detects_scroll_distance_and_direction(int scrollBy)
    {
        var page = FakePage.Create(400, 2000);
        var before = FakePage.Viewport(page, 600, 400);
        var after = FakePage.Viewport(page, 600 + scrollBy, 400);

        // Scrolling down by s moves content up by s, so the shift is -s.
        Assert.Equal(-scrollBy * 2, EstimateShift(before, after));
    }

    [Theory]
    [InlineData(35)]
    [InlineData(70)]
    [InlineData(-35)]
    [InlineData(-70)]
    [InlineData(17)]
    public void Repetitive_page_is_not_off_by_one_line(int scrollBy)
    {
        // Rows of near-identical "lorem ipsum" lines 12 px apart; only a short line number differs.
        var page = new GrayImage(400, 3000);
        Array.Fill(page.Pixels, (byte)245);
        var rng = new Random(9);
        // Each pixel row of a line differs, like real glyphs; every line is identical to the next.
        var glyphRows = Enumerable.Range(0, 7).Select(_ => Enumerable.Range(0, 400).Select(_ => (byte)(rng.Next(3) == 0 ? 40 : 245)).ToArray()).ToArray();
        for (var top = 0; top < 3000 - 12; top += 12)
        {
            for (var y = top; y < top + 7; y++)
            {
                Buffer.BlockCopy(glyphRows[y - top], 0, page.Pixels, y * 400, 400);
                var number = top / 12;
                for (var x = 2; x < 20; x++) page[x, y] = (byte)(((number >> (x % 8)) & 1) == 1 ? 30 : 245);
            }
        }

        var before = FakePage.Viewport(page, 1000, 330);
        var after = FakePage.Viewport(page, 1000 + scrollBy, 330);
        Assert.Equal(-scrollBy * 2, EstimateShift(before, after));
    }

    [Fact]
    public void Static_content_has_no_shift()
    {
        var page = FakePage.Create(400, 1000);
        var frame = FakePage.Viewport(page, 100, 400);
        Assert.Equal(0, EstimateShift(frame, frame));
    }

    [Fact]
    public void Small_repaint_on_a_page_of_identical_lines_is_not_a_scroll()
    {
        // Every line looks the same, so the row fingerprint matches at a one-line shift.
        var page = new GrayImage(400, 400);
        Array.Fill(page.Pixels, (byte)245);
        var rng = new Random(1);
        var line = Enumerable.Range(0, 400).Select(_ => (byte)(rng.Next(2) == 0 ? 30 : 245)).ToArray();
        for (var y = 0; y < 400; y++)
            if (y % 12 < 6) Buffer.BlockCopy(line, 0, page.Pixels, y * 400, 400);

        var after = new GrayImage(400, 400);
        after.CopyFrom(page);
        for (var y = 0; y < 14; y++)             // title bar repaint
            for (var x = 0; x < 400; x++)
                after[x, y] = 90;

        Assert.Equal(0, EstimateShift(page, after));
    }

    [Fact]
    public void Blank_screen_has_no_shift()
    {
        var blank = new GrayImage(300, 300);
        Assert.Equal(0, EstimateShift(blank, blank));
    }
}

public class ChangeDetectorTests
{
    [Fact]
    public void Scrolling_marks_only_the_newly_revealed_strip()
    {
        var page = FakePage.Create(400, 2000);
        var before = FakePage.Viewport(page, 600, 400);
        var after = FakePage.Viewport(page, 640, 400);

        var dirty = ChangeDetector.FindNewContent(before, after, shift: -40);

        Assert.NotEmpty(dirty);
        // Everything dirty is at the bottom, where 40 new rows scrolled in.
        Assert.All(dirty, r => Assert.True(r.Y >= 400 - 40 - ChangeDetector.BlockSize, $"unexpected dirty area {r}"));
    }

    [Fact]
    public void Identical_frames_have_no_changes()
    {
        var page = FakePage.Create(400, 1000);
        var frame = FakePage.Viewport(page, 100, 400);
        Assert.Empty(ChangeDetector.FindNewContent(frame, frame, 0));
    }

    [Fact]
    public void A_new_word_in_place_is_found()
    {
        var page = FakePage.Create(400, 1000);
        var before = FakePage.Viewport(page, 0, 400);
        var after = FakePage.Viewport(page, 0, 400);
        for (var y = 200; y < 210; y++)
            for (var x = 100; x < 180; x++)
                after[x, y] = 255;

        var dirty = ChangeDetector.FindNewContent(before, after, 0);
        Assert.Contains(dirty, r => r.IntersectsWith(new RectI(100, 200, 80, 10)));
    }
}

public class UpscaleTests
{
    [Fact]
    public void Flat_image_stays_flat_and_size_doubles()
    {
        var src = Enumerable.Repeat((byte)120, 6 * 4).ToArray();
        var dst = new byte[12 * 8];
        Upscale.Bilinear(src, 6, 4, 2, dst);
        Assert.All(dst, v => Assert.Equal(120, v));
    }

    [Fact]
    public void Edges_are_smoothed_not_blocky()
    {
        // A hard black-to-white edge gains an in-between grey when enlarged smoothly.
        byte[] src = [0, 0, 255, 255];
        var dst = new byte[8 * 2];
        Upscale.Bilinear(src, 4, 1, 2, dst);
        Assert.Contains(dst, v => v is > 20 and < 235);
        Assert.Equal(0, dst[0]);
        Assert.Equal(255, dst[7]);
    }
}

public class TemplateMatcherTests
{
    [Fact]
    public void Finds_a_patch_after_it_moves()
    {
        var page = FakePage.Create(400, 1000);
        var template = Template.Create(page, new RectI(50, 300, 120, 10))!;
        var m = TemplateMatcher.SearchWide(page, template, 50, 250, 8, 100);
        Assert.Equal(50, m.X);
        Assert.Equal(300, m.Y);
        Assert.True(m.Score > 0.95, $"score {m.Score}");
    }

    [Fact]
    public void Brightness_change_does_not_break_matching()
    {
        var page = FakePage.Create(400, 600);
        var template = Template.Create(page, new RectI(60, 200, 100, 10))!;
        var brighter = new GrayImage(page.Width, page.Height);
        for (var i = 0; i < page.Pixels.Length; i++) brighter.Pixels[i] = (byte)Math.Min(255, page.Pixels[i] * 0.8 + 40);

        Assert.True(TemplateMatcher.Score(brighter, template, 60, 200) > 0.95);
    }
}

public class DetectionMapperTests
{
    private static readonly Rule Crypto = new() { Kind = RuleKind.BuiltIn, BuiltIn = BuiltInPattern.CryptoAddress };

    [Fact]
    public void Maps_a_matched_word_back_to_frame_pixels()
    {
        var lines = new List<OcrLineBox>
        {
            new([
                new OcrWordBox("Wallet:", 20, 10, 100, 20),
                new OcrWordBox("0x71C7656EC7ab88b098defB751B7401B5f6d8976F", 130, 10, 600, 20),
            ]),
        };

        // OCR ran on a crop enlarged 2x whose top-left is at (0, 300) in the window.
        var d = Assert.Single(DetectionMapper.Map(lines, [Crypto], scale: 2, offsetX: 0, offsetY: 300));
        Assert.Equal(RectI.FromLTRB(65, 305, 365, 315), d.Rect);
        Assert.Equal(Crypto.Id, d.RuleId);
    }

    [Fact]
    public void Part_of_a_word_is_sliced_proportionally()
    {
        var rule = new Rule { Kind = RuleKind.Regex, Pattern = @"\d{4}" };
        var lines = new List<OcrLineBox> { new([new OcrWordBox("ACC1234", 0, 0, 70, 10)]) };
        var d = Assert.Single(DetectionMapper.Map(lines, [rule], 1, 0, 0));
        Assert.Equal(30, d.Rect.X);
        Assert.Equal(70, d.Rect.Right);
    }

    [Fact]
    public void A_name_wrapped_over_two_lines_gives_a_box_per_line()
    {
        var rule = new Rule { Kind = RuleKind.Text, Pattern = "Orion 4G WIFI", Fuzzy = true };
        var lines = new List<OcrLineBox>
        {
            new([new OcrWordBox("Wi-Fi:", 0, 0, 40, 10), new OcrWordBox("Orion", 50, 0, 40, 10), new OcrWordBox("4G", 95, 0, 15, 10)]),
            new([new OcrWordBox("WIFI", 0, 14, 30, 10), new OcrWordBox("connected", 40, 14, 60, 10)]),
        };
        var detections = DetectionMapper.Map(lines, [rule], 1, 0, 0);
        Assert.Equal(2, detections.Count);
        Assert.Contains(detections, d => d.Rect.Y == 0 && d.Rect.X == 50);
        Assert.Contains(detections, d => d.Rect.Y == 14 && d.Rect.Right == 30);
    }

    [Fact]
    public void Address_split_by_ocr_into_touching_pieces_is_still_found()
    {
        // What Windows OCR produced at 3x: "ox71 | C7656EC7ab88b098defB75187401 | B5f6d8976F".
        var lines = new List<OcrLineBox>
        {
            new([
                new OcrWordBox("Note:", 0, 0, 60, 30),
                new OcrWordBox("ox71", 80, 0, 60, 30),
                new OcrWordBox("C7656EC7ab88b098defB75187401", 142, 0, 400, 30),
                new OcrWordBox("B5f6d8976F", 544, 0, 150, 30),
            ]),
        };
        var d = Assert.Single(DetectionMapper.Map(lines, [Crypto], 1, 0, 0));
        Assert.Equal(80, d.Rect.X);
        Assert.Equal(694, d.Rect.Right);
    }

    [Fact]
    public void Normal_word_spacing_is_not_glued_together()
    {
        var rule = new Rule { Kind = RuleKind.Text, Pattern = "Orion 4G WIFI" };
        var lines = new List<OcrLineBox>
        {
            new([new OcrWordBox("Orion", 0, 0, 50, 12), new OcrWordBox("4G", 56, 0, 16, 12), new OcrWordBox("WIFI", 78, 0, 30, 12)]),
        };
        Assert.Single(DetectionMapper.Map(lines, [rule], 1, 0, 0));
    }

    [Fact]
    public void App_rules_never_match_screen_text()
    {
        var rule = new Rule { Kind = RuleKind.AppWindow, Pattern = "MetaMask" };
        var lines = new List<OcrLineBox> { new([new OcrWordBox("MetaMask", 0, 0, 70, 10)]) };
        Assert.Empty(DetectionMapper.Map(lines, [rule], 1, 0, 0));
    }
}

public class WindowTrackerTests
{
    private const int W = 400, H = 300;           // half-resolution viewport
    private static readonly Guid RuleId = Guid.NewGuid();

    private static (WindowTracker Tracker, GrayImage Page) StartWithBoxAt(int pageScroll, RectI fullRect)
    {
        var page = FakePage.Create(W, 3000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, pageScroll, H), W * 2, H * 2, 0);

        var request = tracker.TakeOcrRequest(0, allowPeriodic: false)!;
        Assert.True(request.IsFullScan);
        tracker.ApplyOcr(request, [new Detection(fullRect, RuleId)], 0);
        return (tracker, page);
    }

    [Fact]
    public void First_frame_asks_for_a_full_scan()
    {
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(FakePage.Create(W, 1000), 0, H), W * 2, H * 2, 0);
        var request = tracker.TakeOcrRequest(0, false);
        Assert.NotNull(request);
        Assert.True(request!.IsFullScan);
        Assert.Equal(new RectI(0, 0, W * 2, H * 2), request.Crop);
    }

    [Fact]
    public void Box_follows_text_while_scrolling_down_hard()
    {
        // Text found at full-res y=400 (half-res 200).
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        var startY = tracker.Boxes.Single().Rect.Y;

        // Scroll 24 half-res px (48 full-res) per frame, five frames.
        for (var i = 1; i <= 5; i++)
            tracker.OnFrame(FakePage.Viewport(page, 1000 + i * 24, H), W * 2, H * 2, i * 16);

        var box = tracker.Boxes.Single();
        Assert.Null(box.LostSinceMs);
        Assert.InRange(box.Rect.Y, startY - 5 * 48 - 2, startY - 5 * 48 + 2);
    }

    [Fact]
    public void Box_follows_text_when_scrolling_back_up()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 300, 200, 14));
        var startY = tracker.Boxes.Single().Rect.Y;

        for (var i = 1; i <= 4; i++)
            tracker.OnFrame(FakePage.Viewport(page, 1000 - i * 15, H), W * 2, H * 2, i * 16);

        Assert.InRange(tracker.Boxes.Single().Rect.Y, startY + 4 * 30 - 2, startY + 4 * 30 + 2);
    }

    [Fact]
    public void Moving_box_is_predicted_ahead_at_its_exact_size_and_settles_when_still()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        // Frames 16 ms apart; the page scrolls 60 px (full resolution) in that time.
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 100, frameTimeMs: 100);
        tracker.OnFrame(FakePage.Viewport(page, 1030, H), W * 2, H * 2, 116, frameTimeMs: 116);

        var snap = tracker.Snapshot(strict: false);
        var box = snap.Boxes.Single();
        Assert.Equal(-60.0 / 16, box.VelocityPerMs, 3);

        // Drawn 10 ms after the frame, plus the display lag: the text has moved further up.
        var shown = WindowTracker.PredictAt(box, snap.FrameTimeMs, snap.FrameIntervalMs, 126 + WindowTracker.PresentLagMs);
        var expectedOffset = (int)Math.Round(-60.0 / 16 * (10 + WindowTracker.PresentLagMs));
        Assert.Equal(box.Rect.Offset(0, expectedOffset), shown);
        Assert.Equal(box.Rect.Height, shown.Height);   // never grows

        Assert.True(tracker.SettleIfIdle(500));
        Assert.Equal(0, tracker.Snapshot(false).Boxes.Single().VelocityPerMs);
    }

    [Fact]
    public void Boxes_never_grow_even_when_scrolling_very_fast()
    {
        var box = new MovingRect(new RectI(100, 400, 240, 20), VelocityPerMs: -10);   // 10,000 px/s
        foreach (var t in new[] { 5.0, 16, 30, 45 })
            Assert.Equal(20, WindowTracker.PredictAt(box, 0, 16.7, t).Height);
    }

    [Fact]
    public void Overdue_frame_means_scroll_stopped_so_box_stays_with_the_text()
    {
        var box = new MovingRect(new RectI(100, 400, 240, 20), VelocityPerMs: -3);
        // Long after the last frame with no new one: do not keep flying ahead.
        Assert.Equal(box.Rect, WindowTracker.PredictAt(box, frameTimeMs: 0, frameIntervalMs: 16.7, targetTimeMs: 200));
    }

    [Fact]
    public void Box_is_removed_when_its_text_scrolls_out_of_view()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 40, 200, 14));
        for (var i = 1; i <= 6; i++)
            tracker.OnFrame(FakePage.Viewport(page, 1000 + i * 20, H), W * 2, H * 2, i * 16);
        Assert.Empty(tracker.Boxes);
    }

    [Fact]
    public void Item_that_scrolls_out_and_back_is_covered_again_without_ocr()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 200, 240, 14));
        long t = 0;

        // Scroll down until the item leaves the top of the view...
        var scroll = 1000;
        for (var i = 0; i < 8; i++) tracker.OnFrame(FakePage.Viewport(page, scroll += 20, H), W * 2, H * 2, t += 16);
        Assert.Empty(tracker.Boxes);

        // ...and back up. No OCR results are applied in between.
        for (var i = 0; i < 8; i++) tracker.OnFrame(FakePage.Viewport(page, scroll -= 20, H), W * 2, H * 2, t += 16);

        var box = Assert.Single(tracker.Boxes);
        Assert.Equal(RuleId, box.RuleId);
        Assert.InRange(box.Rect.Y, 200 - 30, 200 + 4);   // back where the text is (padding included)
        Assert.True(tracker.RestoredFromMemory >= 1);
    }

    [Fact]
    public void Item_half_past_the_edge_is_still_covered()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 30, 240, 14));
        // Scroll so the item straddles the top edge, then stop.
        tracker.OnFrame(FakePage.Viewport(page, 1012, H), W * 2, H * 2, 16);
        tracker.OnFrame(FakePage.Viewport(page, 1012, H), W * 2, H * 2, 3000);
        tracker.SettleIfIdle(3000);

        var box = Assert.Single(tracker.Boxes);
        Assert.Null(box.LostSinceMs);
    }

    [Fact]
    public void Scrolling_asks_ocr_for_just_the_new_strip()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        while (tracker.TakeOcrRequest(0, false) is { } detail) tracker.ApplyOcr(detail, [], 0); // drain detail passes

        tracker.OnFrame(FakePage.Viewport(page, 1040, H), W * 2, H * 2, 16);
        var request = tracker.TakeOcrRequest(16, false);

        Assert.NotNull(request);
        Assert.False(request!.IsFullScan);
        Assert.True(request.Crop.Height < H * 2 / 2, $"band should be small, was {request.Crop}");
        Assert.True(request.Crop.Bottom >= H * 2 - 4, "band should be at the bottom edge");
    }

    [Fact]
    public void Pending_work_at_the_window_edge_drains_completely()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        while (tracker.TakeOcrRequest(0, false) is { } detail) tracker.ApplyOcr(detail, [], 0);

        // New content right at the bottom edge, then at the top edge.
        var frame = FakePage.Viewport(page, 1000, H);
        for (var x = 10; x < 300; x++) { frame[x, H - 2] = 0; frame[x, 1] = 0; }
        tracker.OnFrame(frame, W * 2, H * 2, 16);

        var requests = 0;
        while (tracker.TakeOcrRequest(20, false) is { } r)
        {
            tracker.ApplyOcr(r, [], 20);
            Assert.True(++requests < 10, "OCR work never drained: a sliver keeps coming back");
        }
        Assert.False(tracker.HasPendingWork);
    }

    [Fact]
    public void Text_that_disappears_is_unhidden_after_ocr_confirms()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        var frame = FakePage.Viewport(page, 1000, H);

        // The text is erased (e.g. the user cleared a field).
        for (var y = 195; y < 212; y++)
            for (var x = 45; x < 175; x++)
                frame[x, y] = 245;
        tracker.OnFrame(frame, W * 2, H * 2, 16);
        Assert.NotNull(tracker.Boxes.Single().LostSinceMs);

        var request = tracker.TakeOcrRequest(20, false)!;
        tracker.ApplyOcr(request, [], 40);
        Assert.Empty(tracker.Boxes);
    }

    [Fact]
    public void Strict_mode_covers_a_newly_opened_window_until_the_first_scan()
    {
        var tracker = new WindowTracker(new IntPtr(1)) { CurtainUntilFirstScan = true };
        tracker.OnFrame(FakePage.Viewport(FakePage.Create(W, 1000), 0, H), W * 2, H * 2, 0);
        Assert.Equal(new RectI(0, 0, W * 2, H * 2), tracker.Snapshot(strict: true).Pending.Single().Rect);
        Assert.Empty(tracker.Snapshot(strict: false).Pending);
    }

    [Fact]
    public void Turning_protection_on_never_blacks_out_windows_that_were_already_open()
    {
        var tracker = new WindowTracker(new IntPtr(1)) { CurtainUntilFirstScan = false };
        tracker.OnFrame(FakePage.Viewport(FakePage.Create(W, 1000), 0, H), W * 2, H * 2, 0);
        Assert.Empty(tracker.Snapshot(strict: true).Pending);
    }

    [Fact]
    public void Strict_curtain_covers_only_the_changed_area_not_a_full_width_strip()
    {
        var page = FakePage.Create(W, 1000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 0, H), W * 2, H * 2, 0);
        tracker.ApplyOcr(tracker.TakeOcrRequest(0, false)!, [], 0);
        while (tracker.TakeOcrRequest(0, false) is { } detail) tracker.ApplyOcr(detail, [], 0);

        var frame = FakePage.Viewport(page, 0, H);
        for (var y = 150; y < 162; y++)
            for (var x = 40; x < 140; x++)
                frame[x, y] = (byte)(x % 7 == 0 ? 20 : 240);
        tracker.OnFrame(frame, W * 2, H * 2, 16);
        tracker.TakeOcrRequest(20, false);   // now being read: still curtained, but only where it changed

        var curtains = tracker.Snapshot(strict: true).Pending;
        Assert.NotEmpty(curtains);
        Assert.All(curtains, c => Assert.True(c.Rect.Width < W * 2 / 2, $"curtain {c.Rect} spans the window"));
    }

    [Fact]
    public void Video_like_area_heats_up_gets_no_curtain_and_is_read_rarely()
    {
        var page = FakePage.Create(W, 1000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 0, H), W * 2, H * 2, 0);
        tracker.ApplyOcr(tracker.TakeOcrRequest(0, false)!, [], 0);
        while (tracker.TakeOcrRequest(0, false) is { } detail) tracker.ApplyOcr(detail, [], 0);

        // A "video" in the middle repaints with new noise every frame; the rest is static.
        var rng = new Random(3);
        var video = new RectI(100, 100, 160, 90);
        var ocrCount = 0;
        for (var i = 1; i <= 60; i++)
        {
            var frame = FakePage.Viewport(page, 0, H);
            for (var y = video.Y; y < video.Bottom; y++)
                for (var x = video.X; x < video.Right; x++)
                    frame[x, y] = (byte)rng.Next(256);
            var now = i * 16L;
            tracker.OnFrame(frame, W * 2, H * 2, now);
            if (tracker.TakeOcrRequest(now, false) is { } r)
            {
                ocrCount++;
                tracker.ApplyOcr(r, [], now);
            }
        }

        var full = video.Scale(2);
        Assert.True(tracker.IsHot(full), "repainting area should be hot");
        Assert.Empty(tracker.Snapshot(strict: true).Pending);
        // 60 frames in about a second: only the first few reads plus about two throttled ones.
        Assert.InRange(ocrCount, 1, 8);
    }

    [Fact]
    public void Text_appearing_once_is_curtained_in_strict_mode()
    {
        var page = FakePage.Create(W, 1000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 0, H), W * 2, H * 2, 0);
        tracker.ApplyOcr(tracker.TakeOcrRequest(0, false)!, [], 0);
        while (tracker.TakeOcrRequest(0, false) is { } detail) tracker.ApplyOcr(detail, [], 0);

        var frame = FakePage.Viewport(page, 0, H);
        for (var y = 150; y < 162; y++)
            for (var x = 40; x < 300; x++)
                frame[x, y] = (byte)(x % 7 == 0 ? 20 : 240);
        tracker.OnFrame(frame, W * 2, H * 2, 16);

        Assert.Contains(tracker.Snapshot(strict: true).Pending, r => r.Rect.IntersectsWith(new RectI(80, 300, 520, 24)));
    }

    private static GrayImage Halve(GrayImage full)
    {
        var half = new GrayImage(full.Width / 2, full.Height / 2);
        for (var y = 0; y < half.Height; y++)
            for (var x = 0; x < half.Width; x++)
                half[x, y] = (byte)((full[2 * x, 2 * y] + full[2 * x + 1, 2 * y] + full[2 * x, 2 * y + 1] + full[2 * x + 1, 2 * y + 1] + 2) / 4);
        return half;
    }

    [Fact]
    public void Full_resolution_strips_make_odd_pixel_scrolling_exact_over_many_frames()
    {
        var page = FakePage.Create(W * 2, 6000, seed: 11);   // full-resolution page
        var tracker = new WindowTracker(new IntPtr(1));
        var scroll = 1000;
        GrayImage Full() => FakePage.Viewport(page, scroll, H * 2);

        tracker.OnFrame(Halve(Full()), W * 2, H * 2, 0, 0, strip: Full());
        foreach (var step in new[] { 25, 25, 41, 7, 33, 25, -19, -25, 61 })
        {
            scroll += step;
            tracker.OnFrame(Halve(Full()), W * 2, H * 2, 0, 0, strip: Full());
        }

        // Content moved up by the total scrolled distance, exactly.
        Assert.Equal(-(scroll - 1000), tracker.MotionSumFull);
    }

    [Fact]
    public void Box_that_drifted_is_snapped_back_when_scrolling_stops()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        var correct = tracker.Boxes.Single().Rect;

        // Simulate drift: push the box two lines off its text.
        tracker.Boxes.Single().HalfY += 24;
        tracker.VerifyBoxesAtRest(10);

        Assert.Equal(correct, tracker.Boxes.Single().Rect);
        Assert.Null(tracker.Boxes.Single().LostSinceMs);
    }

    [Fact]
    public void Blank_area_changing_gets_no_curtain_in_strict_mode()
    {
        var page = FakePage.Create(W, 1000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 0, H), W * 2, H * 2, 0);
        tracker.ApplyOcr(tracker.TakeOcrRequest(0, false)!, [], 0);
        while (tracker.TakeOcrRequest(0, false) is { } detail) tracker.ApplyOcr(detail, [], 0);

        // A big area turns plain grey (a panel opening): it changed, but there is nothing to read.
        var frame = FakePage.Viewport(page, 0, H);
        for (var y = 96; y < 208; y++)   // aligned to the 16 px change grid
            for (var x = 48; x < 352; x++)
                frame[x, y] = 200;
        tracker.OnFrame(frame, W * 2, H * 2, 16);

        Assert.Empty(tracker.Snapshot(strict: true).Pending);
    }

    [Fact]
    public void Scrolling_back_over_already_read_content_needs_no_ocr_and_no_curtain()
    {
        var page = FakePage.Create(W * 2, 6000, seed: 21);
        var tracker = new WindowTracker(new IntPtr(1));
        var scroll = 1000;
        GrayImage Full() => FakePage.Viewport(page, scroll, H * 2);
        void Frame() => tracker.OnFrame(Halve(Full()), W * 2, H * 2, 0, 0, strip: Full());
        void DrainOcr() { while (tracker.TakeOcrRequest(0, false) is { } r) tracker.ApplyOcr(r, [], 0); }

        Frame();
        DrainOcr();

        // Scroll down 300 px in steps, reading the new strips as they come in...
        for (var i = 0; i < 10; i++) { scroll += 30; Frame(); DrainOcr(); }
        // ...then scroll back up over the same content.
        for (var i = 0; i < 10; i++)
        {
            scroll -= 30;
            Frame();
            Assert.False(tracker.HasPendingWork, $"step {i}: already-read content was queued for OCR again");
            Assert.Empty(tracker.Snapshot(strict: true).Pending);
        }
    }

    [Fact]
    public void Switching_pages_clears_old_boxes_at_once_and_reads_the_new_page()
    {
        var (tracker, _) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        Assert.Single(tracker.Boxes);

        // A completely different page appears in the same window, without scrolling.
        var otherPage = FakePage.Create(W, 3000, seed: 99);
        tracker.OnFrame(FakePage.Viewport(otherPage, 500, H), W * 2, H * 2, 50);

        Assert.Empty(tracker.Boxes);
        Assert.Equal(1, tracker.PageSwitches);
        var request = tracker.TakeOcrRequest(60, false);
        Assert.True(request!.IsFullScan);
    }

    [Fact]
    public void Ocr_result_for_the_previous_page_is_ignored()
    {
        var page = FakePage.Create(W, 3000);
        var tracker = new WindowTracker(new IntPtr(1));
        tracker.OnFrame(FakePage.Viewport(page, 1000, H), W * 2, H * 2, 0);
        var oldRequest = tracker.TakeOcrRequest(0, false)!;

        // The page switches while the old page is still being read...
        tracker.OnFrame(FakePage.Viewport(FakePage.Create(W, 3000, seed: 99), 500, H), W * 2, H * 2, 30);
        // ...and then the old result arrives, with a wallet "found" on the old page.
        tracker.ApplyOcr(oldRequest, [new Detection(new RectI(100, 400, 240, 14), Guid.NewGuid())], 60);

        Assert.Empty(tracker.Boxes);
    }

    [Fact]
    public void A_large_playing_video_is_not_mistaken_for_a_page_switch()
    {
        var (tracker, page) = StartWithBoxAt(1000, new RectI(100, 40, 240, 14));
        var rng = new Random(5);
        var video = new RectI(0, 60, W, H - 60);     // about 80% of the window, below the boxed text

        for (var i = 1; i <= 30; i++)
        {
            var frame = FakePage.Viewport(page, 1000, H);
            for (var y = video.Y; y < video.Bottom; y++)
                for (var x = video.X; x < video.Right; x++)
                    frame[x, y] = (byte)rng.Next(256);
            tracker.OnFrame(frame, W * 2, H * 2, i * 33L);
        }

        // The first frames of the video look like a page switch (and clear the box once); once it
        // is recognised as animated, it must stop wiping boxes, so OCR can put the box back.
        Assert.True(tracker.PageSwitches <= 3, $"video caused {tracker.PageSwitches} page switches");
    }

    [Fact]
    public void Rule_removal_drops_its_boxes()
    {
        var (tracker, _) = StartWithBoxAt(1000, new RectI(100, 400, 240, 14));
        Assert.True(tracker.RemoveBoxesExcept(new HashSet<Guid>()));
        Assert.Empty(tracker.Boxes);
    }
}
