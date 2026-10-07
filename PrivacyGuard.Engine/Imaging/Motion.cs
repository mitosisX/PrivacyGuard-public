namespace PrivacyGuard.Engine.Imaging;

/// <summary>Estimates how far a window's content scrolled between two frames.</summary>
public static class Motion
{
    /// <summary>
    /// How many fingerprint candidates go on to the pixel check. Pages with evenly spaced lines
    /// produce many near-equal fingerprint matches one line apart, so the list must be long.
    /// </summary>
    private const int Candidates = 40;


    /// <summary>
    /// Per-row texture: the sum of horizontal brightness changes along each row. Text rows
    /// score high and blank rows score zero, so the profile is a cheap fingerprint of the layout.
    /// </summary>
    public static void RowProfile(GrayImage image, int[] profile)
    {
        var w = image.Width;
        var px = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * w;
            var sum = 0;
            for (var x = 0; x < w - 1; x++)
                sum += Math.Abs(px[row + x + 1] - px[row + x]);
            profile[y] = sum;
        }
    }

    /// <summary>
    /// Finds how far the content moved between two half-resolution frames, in FULL-resolution
    /// pixels (positive means content moved down). Odd results are half-pixel shifts in the
    /// half-resolution images; they are matched by interpolating between rows. Measured on a
    /// real window, ignoring half pixels made odd-pixel scrolls lock onto the wrong line.
    /// Returns 0 unless a shift is clearly proven.
    /// </summary>
    /// <param name="maxShiftHalf">Largest shift to consider, in pixels of these images.</param>
    /// <param name="expectedShift">The previous frame's shift in full pixels. Scrolling is smooth, so near ties go to it.</param>
    /// <param name="scale">Full-resolution pixels per image pixel: 2 for half-size frames, 1 for full-resolution strips (exact, preferred).</param>
    public static int EstimateVerticalShift(GrayImage prev, GrayImage cur, int[] prevProfile, int[] curProfile, int maxShiftHalf, int expectedShift = 0, int scale = 2)
    {
        var height = cur.Height;
        if (height < 8 || prev.Height != height || prev.Width != cur.Width) return 0;
        maxShiftHalf = Math.Min(maxShiftHalf, height * 2 / 3);

        // 1. Which rows changed? Judged per segment, not per whole row: on a page of look-alike
        //    lines, scrolling exactly one line only changes the short line numbers.
        var changedRows = new List<int>(height);
        for (var y = 0; y < height; y++)
            if (RowChanged(prev, cur, y)) changedRows.Add(y);
        if (changedRows.Count < Math.Max(4, height / 20)) return 0;

        // 2. Candidate shifts: fingerprint matches, the previous speed, and half-pixel neighbours.
        var minOverlap = Math.Max(4, height / 3);
        var scored = new List<(int Shift, double Error)>();
        for (var d = -maxShiftHalf; d <= maxShiftHalf; d++)
        {
            if (d == 0 || height - Math.Abs(d) < minOverlap) continue;
            scored.Add((d, ProfileError(prevProfile, curProfile, height, d)));
        }

        // Order matters for speed: the previous speed first (usually right), then fingerprint rank.
        var ordered = new List<int>();
        var seen = new HashSet<int> { 0 };
        void Add(int c)
        {
            if (Math.Abs(c) <= maxShiftHalf * scale && seen.Add(c)) ordered.Add(c);
        }
        for (var k = 0; k <= 4; k++)
        {
            Add(expectedShift + k);
            Add(expectedShift - k);
        }
        // Distinct local minima only: on a repetitive page the fingerprint has dozens of
        // near-equal dips, and plain "best 40" fills up with neighbours of the same dip.
        var minima = new List<(int Shift, double Error)>();
        for (var i = 0; i < scored.Count; i++)
        {
            var e = scored[i].Error;
            var left = i > 0 && scored[i - 1].Shift == scored[i].Shift - 1 ? scored[i - 1].Error : double.MaxValue;
            var right = i < scored.Count - 1 && scored[i + 1].Shift == scored[i].Shift + 1 ? scored[i + 1].Error : double.MaxValue;
            if (e <= left && e <= right) minima.Add(scored[i]);
        }
        foreach (var (d, _) in minima.OrderBy(s => s.Error).Take(Candidates))
            for (var k = -2; k <= 2; k++) Add(d * scale + k);

        // 3. Real pixels decide, over every column of the changed rows: on evenly spaced lines
        //    the only difference between "one line" and "two lines" can be a few pixels of a line
        //    number, which column sampling would skip. Each candidate stops as soon as it is
        //    already worse than the best so far, so the full check stays cheap.
        var zeroError = PixelError(prev, cur, changedRows, 0, rowStep: 2, abortAbove: double.MaxValue, scale);
        var results = new List<(int Shift, double Error)>();
        var bestError = double.MaxValue;
        foreach (var d in ordered)
        {
            var e = PixelError(prev, cur, changedRows, d, rowStep: 2, abortAbove: bestError * 1.02 + 0.02, scale);
            if (e == double.MaxValue) continue;
            results.Add((d, e));
            if (e < bestError) bestError = e;
        }
        if (results.Count == 0) return 0;

        // Only a genuine tie (truly identical content) falls back to the previous speed.
        var best = results.Where(c => c.Error <= bestError * 1.01 + 0.01)
                          .OrderBy(c => Math.Abs(c.Shift - expectedShift))
                          .First();

        return best.Error < zeroError * 0.5 ? best.Shift : 0;
    }

    /// <summary>True if any 32-pixel segment of the row changed noticeably.</summary>
    private static bool RowChanged(GrayImage prev, GrayImage cur, int y)
    {
        var w = cur.Width;
        var a = cur.Pixels;
        var b = prev.Pixels;
        var row = y * w;
        for (var start = 0; start < w; start += 32)
        {
            var end = Math.Min(w, start + 32);
            var sum = 0;
            for (var x = start; x < end; x += 2) sum += Math.Abs(a[row + x] - b[row + x]);
            if (sum / Math.Max(1, (end - start) / 2) > 6) return true;
        }
        return false;
    }

    /// <summary>
    /// Mean absolute difference between cur and prev moved by <paramref name="shiftFull"/> full
    /// pixels. An odd shift is half a pixel in these images: prev is sampled between two rows.
    /// Returns MaxValue when too few rows overlap to judge, or as soon as the error is certain
    /// to exceed <paramref name="abortAbove"/>.
    /// </summary>
    internal static double PixelError(GrayImage prev, GrayImage cur, List<int> rows, int shiftFull, int rowStep, double abortAbove, int scale = 2)
    {
        var d0 = scale == 1 ? shiftFull : (int)Math.Floor(shiftFull / 2.0);
        var half = scale == 2 && (shiftFull & 1) != 0;   // works for negatives too
        var d1 = half ? d0 + 1 : d0;

        var w = cur.Width;
        var a = cur.Pixels;
        var b = prev.Pixels;

        // The mean can never be below sum / maxSamples, so once that exceeds the bound, stop.
        var maxSamples = (double)((rows.Count + rowStep - 1) / rowStep) * w;
        var abortSum = abortAbove >= double.MaxValue / 2 ? long.MaxValue : (long)Math.Ceiling(abortAbove * maxSamples);

        long sum = 0;
        var n = 0;
        var used = 0;
        for (var i = 0; i < rows.Count; i += rowStep)
        {
            var y = rows[i];
            var p0 = y - d0;
            var p1 = y - d1;
            if (p0 < 0 || p0 >= prev.Height || p1 < 0 || p1 >= prev.Height) continue;
            used++;
            var ra = y * w;
            var rb0 = p0 * w;
            var rb1 = p1 * w;
            for (var x = 0; x < w; x++)
            {
                var expected = half ? (b[rb0 + x] + b[rb1 + x] + 1) >> 1 : b[rb0 + x];
                sum += Math.Abs(a[ra + x] - expected);
            }
            n += w;
            if (sum > abortSum) return double.MaxValue;
        }

        // A shift that compares only a handful of rows proves nothing.
        if (n == 0 || used * rowStep < Math.Max(4, rows.Count / 3)) return double.MaxValue;
        return sum / (double)n;
    }

    private static double ProfileError(int[] prev, int[] cur, int height, int d)
    {
        var start = Math.Max(0, d);
        var end = Math.Min(height, height + d);
        long sum = 0;
        long energy = 0;
        for (var y = start; y < end; y++)
        {
            var a = cur[y];
            var b = prev[y - d];
            sum += Math.Abs(a - b);
            energy += a + b;
        }

        var n = end - start;
        // A shift that only lines up blank space with blank space proves nothing.
        if (n <= 0 || energy == 0) return double.MaxValue;
        return sum / (double)n;
    }
}
