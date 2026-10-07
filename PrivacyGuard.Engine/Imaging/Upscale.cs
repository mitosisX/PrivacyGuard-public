namespace PrivacyGuard.Engine.Imaging;

/// <summary>
/// Smooth (bilinear) enlargement for OCR. Measured on Windows OCR: blocky 2x enlargement made
/// it silently drop long tokens such as wallet addresses, while smooth 2x read them perfectly.
/// </summary>
public static class Upscale
{
    public static void Bilinear(byte[] source, int width, int height, int scale, byte[] destination)
    {
        var outWidth = width * scale;
        var outHeight = height * scale;
        if (destination.Length < outWidth * outHeight) throw new ArgumentException("Destination too small.", nameof(destination));

        // Fixed-point weights (0..256) for every output column, computed once.
        var x0 = new int[outWidth];
        var x1 = new int[outWidth];
        var ax = new int[outWidth];
        for (var x = 0; x < outWidth; x++) Weights(x, scale, width, out x0[x], out x1[x], out ax[x]);

        for (var y = 0; y < outHeight; y++)
        {
            Weights(y, scale, height, out var y0, out var y1, out var ay);
            var r0 = y0 * width;
            var r1 = y1 * width;
            var o = y * outWidth;
            for (var x = 0; x < outWidth; x++)
            {
                var top = source[r0 + x0[x]] * (256 - ax[x]) + source[r0 + x1[x]] * ax[x];
                var bottom = source[r1 + x0[x]] * (256 - ax[x]) + source[r1 + x1[x]] * ax[x];
                destination[o + x] = (byte)((top * (256 - ay) + bottom * ay + 32768) >> 16);
            }
        }
    }

    private static void Weights(int outIndex, int scale, int size, out int i0, out int i1, out int weight)
    {
        var f = (outIndex + 0.5) / scale - 0.5;
        if (f <= 0)
        {
            i0 = i1 = 0;
            weight = 0;
            return;
        }
        i0 = Math.Min((int)f, size - 1);
        i1 = Math.Min(i0 + 1, size - 1);
        weight = (int)((f - (int)f) * 256);
    }
}
