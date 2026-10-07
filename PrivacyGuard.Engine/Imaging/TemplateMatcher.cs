using PrivacyGuard.Engine.Geometry;

namespace PrivacyGuard.Engine.Imaging;

/// <summary>A small grayscale patch used to follow a redacted item from frame to frame.</summary>
public sealed class Template
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public double Mean { get; }
    public double Std { get; }

    private Template(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;

        double sum = 0, sumSq = 0;
        foreach (var p in pixels)
        {
            sum += p;
            sumSq += p * p;
        }
        var n = pixels.Length;
        Mean = sum / n;
        Std = Math.Sqrt(Math.Max(0, sumSq / n - Mean * Mean));
    }

    /// <summary>True when the patch has enough detail to be matched reliably.</summary>
    public bool IsTextured => Std >= 6;

    /// <summary>Cuts a template out of an image. Returns null if the region is too small.</summary>
    public static Template? Create(GrayImage image, RectI region)
    {
        var r = region.Intersect(image.Bounds);
        if (r.Width < 3 || r.Height < 2) return null;

        var pixels = new byte[r.Width * r.Height];
        for (var y = 0; y < r.Height; y++)
            Buffer.BlockCopy(image.Pixels, (r.Y + y) * image.Width + r.X, pixels, y * r.Width, r.Width);

        return new Template(r.Width, r.Height, pixels);
    }
}

public readonly record struct MatchResult(int X, int Y, double Score)
{
    public static readonly MatchResult None = new(0, 0, -1);
}

/// <summary>Normalised cross-correlation search. Robust to brightness changes such as hover highlights.</summary>
public static class TemplateMatcher
{
    /// <summary>Correlation between the template and the image at (x, y). -1 when out of bounds.</summary>
    public static double Score(GrayImage image, Template t, int x, int y, int step = 1)
    {
        if (x < 0 || y < 0 || x + t.Width > image.Width || y + t.Height > image.Height) return -1;
        if (t.Std < 1e-6) return -1;

        var img = image.Pixels;
        var w = image.Width;
        var tp = t.Pixels;
        double sumI = 0, sumII = 0, sumIT = 0;
        var n = 0;

        for (var ty = 0; ty < t.Height; ty += step)
        {
            var row = (y + ty) * w + x;
            var trow = ty * t.Width;
            for (var tx = 0; tx < t.Width; tx += step)
            {
                double i = img[row + tx];
                double tv = tp[trow + tx];
                sumI += i;
                sumII += i * i;
                sumIT += i * tv;
                n++;
            }
        }

        var meanI = sumI / n;
        var varI = sumII / n - meanI * meanI;
        if (varI < 1) return 0;

        // Uses the template's full-resolution mean, which is accurate enough when subsampling.
        var cov = sumIT / n - meanI * t.Mean;
        return cov / (Math.Sqrt(varI) * t.Std);
    }

    /// <summary>Exhaustive search in a small window around (cx, cy).</summary>
    public static MatchResult SearchLocal(GrayImage image, Template t, int cx, int cy, int radiusX, int radiusY)
    {
        var best = MatchResult.None;
        for (var y = cy - radiusY; y <= cy + radiusY; y++)
        {
            for (var x = cx - radiusX; x <= cx + radiusX; x++)
            {
                var s = Score(image, t, x, y);
                if (s > best.Score) best = new MatchResult(x, y, s);
            }
        }
        return best;
    }

    /// <summary>
    /// Coarse-to-fine search over a large vertical range, for when an item jumped further
    /// than the scroll estimate predicted.
    /// </summary>
    public static MatchResult SearchWide(GrayImage image, Template t, int cx, int cy, int radiusX, int radiusY)
    {
        var coarse = MatchResult.None;
        for (var y = cy - radiusY; y <= cy + radiusY; y += 2)
        {
            for (var x = cx - radiusX; x <= cx + radiusX; x += 2)
            {
                var s = Score(image, t, x, y, step: 2);
                if (s > coarse.Score) coarse = new MatchResult(x, y, s);
            }
        }

        if (coarse.Score < 0.4) return coarse;
        return SearchLocal(image, t, coarse.X, coarse.Y, 2, 2);
    }
}
