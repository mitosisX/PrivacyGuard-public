namespace PrivacyGuard.Engine.Geometry;

/// <summary>An integer rectangle in pixels. Right and Bottom are exclusive.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static RectI FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public RectI Intersect(RectI other)
    {
        var l = Math.Max(X, other.X);
        var t = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? FromLTRB(l, t, r, b) : default;
    }

    public bool IntersectsWith(RectI other) => !Intersect(other).IsEmpty;

    public bool Contains(RectI other) =>
        !other.IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public RectI Union(RectI other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLTRB(Math.Min(X, other.X), Math.Min(Y, other.Y),
                        Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public RectI Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public RectI Inflate(int dx, int dy) => FromLTRB(X - dx, Y - dy, Right + dx, Bottom + dy);

    /// <summary>Scales outwards so the result always covers the original area.</summary>
    public RectI Scale(double factor) => FromLTRB(
        (int)Math.Floor(X * factor), (int)Math.Floor(Y * factor),
        (int)Math.Ceiling(Right * factor), (int)Math.Ceiling(Bottom * factor));

    /// <summary>Intersection over union, 0 to 1.</summary>
    public double IoU(RectI other)
    {
        var inter = Intersect(other).Area;
        if (inter == 0) return 0;
        return inter / (double)(Area + other.Area - inter);
    }

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}

public static class RegionMath
{
    /// <summary>Removes <paramref name="cut"/> from every rectangle, splitting where needed.</summary>
    public static List<RectI> Subtract(IEnumerable<RectI> rects, RectI cut)
    {
        var result = new List<RectI>();
        foreach (var r in rects)
        {
            var i = r.Intersect(cut);
            if (i.IsEmpty)
            {
                result.Add(r);
                continue;
            }

            // Top and bottom bands span the full width; left and right fill the middle.
            if (i.Y > r.Y) result.Add(RectI.FromLTRB(r.X, r.Y, r.Right, i.Y));
            if (i.Bottom < r.Bottom) result.Add(RectI.FromLTRB(r.X, i.Bottom, r.Right, r.Bottom));
            if (i.X > r.X) result.Add(RectI.FromLTRB(r.X, i.Y, i.X, i.Bottom));
            if (i.Right < r.Right) result.Add(RectI.FromLTRB(i.Right, i.Y, r.Right, i.Bottom));
        }
        return result;
    }

    /// <summary>The parts of <paramref name="rect"/> that fall inside any of <paramref name="visible"/>.</summary>
    public static IEnumerable<RectI> ClipTo(RectI rect, IReadOnlyList<RectI> visible)
    {
        foreach (var v in visible)
        {
            var i = rect.Intersect(v);
            if (!i.IsEmpty) yield return i;
        }
    }

    public static long TotalArea(IEnumerable<RectI> rects) => rects.Sum(r => r.Area);

    /// <summary>Fraction of <paramref name="target"/> covered by the union of <paramref name="cover"/>, 0 to 1.</summary>
    public static double Coverage(RectI target, IEnumerable<RectI> cover)
    {
        if (target.IsEmpty) return 1;
        var remaining = new List<RectI> { target };
        foreach (var c in cover)
        {
            remaining = Subtract(remaining, c);
            if (remaining.Count == 0) break;
        }
        return 1 - TotalArea(remaining) / (double)target.Area;
    }
}
