using PrivacyGuard.Engine.Geometry;

namespace PrivacyGuard.Engine.Imaging;

/// <summary>An 8-bit grayscale image. The buffer is reused across resizes to avoid garbage.</summary>
public sealed class GrayImage
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; }

    public GrayImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[Math.Max(1, width * height)];
    }

    public GrayImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length < width * height) throw new ArgumentException("Buffer too small.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public RectI Bounds => new(0, 0, Width, Height);

    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    public void Resize(int width, int height)
    {
        var needed = width * height;
        if (needed > Pixels.Length) Pixels = new byte[needed];
        Width = width;
        Height = height;
    }

    public void CopyFrom(GrayImage source)
    {
        Resize(source.Width, source.Height);
        Buffer.BlockCopy(source.Pixels, 0, Pixels, 0, source.Width * source.Height);
    }

    /// <summary>Copies a region into a new image. Parts outside this image are left black.</summary>
    public GrayImage Crop(RectI region)
    {
        var result = new GrayImage(Math.Max(1, region.Width), Math.Max(1, region.Height));
        var clipped = region.Intersect(Bounds);
        for (var y = clipped.Y; y < clipped.Bottom; y++)
        {
            Buffer.BlockCopy(Pixels, y * Width + clipped.X,
                result.Pixels, (y - region.Y) * result.Width + (clipped.X - region.X), clipped.Width);
        }
        return result;
    }
}
