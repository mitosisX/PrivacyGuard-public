using PrivacyGuard.Engine.Geometry;

namespace PrivacyGuard.Engine.Imaging;

/// <summary>
/// Finds the parts of a frame that show new content, ignoring content that merely scrolled.
/// Only these parts need OCR, which is what keeps the engine fast.
/// </summary>
public static class ChangeDetector
{
    public const int BlockSize = 16;

    /// <param name="shift">Vertical scroll since the previous frame, from <see cref="Motion"/>.</param>
    /// <param name="threshold">Mean absolute difference per pixel that counts as a change.</param>
    public static int GridColumns(int width) => (width + BlockSize - 1) / BlockSize;
    public static int GridRows(int height) => (height + BlockSize - 1) / BlockSize;

    /// <param name="grid">Optional: receives the dirty flag of every block, row by row.</param>
    public static List<RectI> FindNewContent(GrayImage prev, GrayImage cur, int shift, int threshold = 8, bool[]? grid = null)
    {
        var result = new List<RectI>();
        if (prev.Width != cur.Width || prev.Height != cur.Height)
        {
            result.Add(cur.Bounds);
            if (grid is not null) Array.Fill(grid, true);
            return result;
        }

        var cols = GridColumns(cur.Width);
        var rows = GridRows(cur.Height);
        var dirty = new bool[rows, cols];

        for (var by = 0; by < rows; by++)
        {
            for (var bx = 0; bx < cols; bx++)
            {
                var block = new RectI(bx * BlockSize, by * BlockSize, BlockSize, BlockSize).Intersect(cur.Bounds);
                if (BlockDiff(prev, cur, block, 0) <= threshold) continue;

                // Content that only scrolled is already known. Allow one pixel of slack for
                // rounding in the half-resolution image.
                if (shift != 0 &&
                    (BlockDiff(prev, cur, block, shift) <= threshold ||
                     BlockDiff(prev, cur, block, shift - 1) <= threshold ||
                     BlockDiff(prev, cur, block, shift + 1) <= threshold))
                {
                    continue;
                }

                dirty[by, bx] = true;
            }
        }

        if (grid is not null)
            for (var by = 0; by < rows; by++)
                for (var bx = 0; bx < cols; bx++)
                    grid[by * cols + bx] = dirty[by, bx];

        // Runs of dirty blocks along each row, then merge runs that touch into rectangles.
        for (var by = 0; by < rows; by++)
        {
            var bx = 0;
            while (bx < cols)
            {
                if (!dirty[by, bx]) { bx++; continue; }
                var start = bx;
                while (bx < cols && dirty[by, bx]) bx++;
                var run = RectI.FromLTRB(start * BlockSize, by * BlockSize, bx * BlockSize, (by + 1) * BlockSize);
                result.Add(run.Intersect(cur.Bounds));
            }
        }

        return MergeOverlapping(result);
    }

    /// <summary>Mean absolute difference between cur(block) and prev(block shifted up by <paramref name="shift"/>).</summary>
    public static int BlockDiff(GrayImage prev, GrayImage cur, RectI block, int shift)
    {
        var w = cur.Width;
        var a = cur.Pixels;
        var b = prev.Pixels;
        long sum = 0;
        var n = 0;

        for (var y = block.Y; y < block.Bottom; y += 2)
        {
            var py = y - shift;
            if (py < 0 || py >= prev.Height) return 255; // revealed area: definitely new
            var row = y * w;
            var prow = py * w;
            for (var x = block.X; x < block.Right; x += 2)
            {
                sum += Math.Abs(a[row + x] - b[prow + x]);
                n++;
            }
        }
        return n == 0 ? 0 : (int)(sum / n);
    }

    /// <summary>
    /// Merges touching rectangles, but only when the merged box is not mostly empty. A tall
    /// narrow strip (a moving scrollbar) touching a wide one (a line of new text) stays two
    /// rectangles; merging them made one huge box, which showed as a full-window curtain.
    /// </summary>
    public static List<RectI> MergeOverlapping(List<RectI> rects)
    {
        var list = new List<RectI>(rects);
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < list.Count && !changed; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (!list[i].Inflate(1, 1).IntersectsWith(list[j])) continue;

                    var union = list[i].Union(list[j]);
                    var covered = list[i].Area + list[j].Area - list[i].Intersect(list[j]).Area;
                    if (list[i].Contains(list[j]) || list[j].Contains(list[i]) || union.Area <= covered * 1.3)
                    {
                        list[i] = list[i].Union(list[j]);
                        list.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);
        return list;
    }
}
