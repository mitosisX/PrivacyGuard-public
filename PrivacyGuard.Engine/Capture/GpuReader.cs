using System.Runtime.InteropServices;
using PrivacyGuard.Engine.Geometry;
using PrivacyGuard.Engine.Imaging;
using PrivacyGuard.Engine.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using RectI = PrivacyGuard.Engine.Geometry.RectI;

namespace PrivacyGuard.Engine.Capture;

/// <summary>
/// Owns the Direct3D device and turns captured GPU frames into small grayscale images.
/// Frames are halved on the GPU (mipmap generation) before the CPU reads them, so only a
/// quarter of the pixels cross to system memory. Only used from the engine's worker thread.
/// </summary>
internal sealed unsafe class GpuReader : IDisposable
{
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private ID3D11Texture2D? _mip;
    private ID3D11ShaderResourceView? _mipView;
    private int _mipWidth, _mipHeight;

    private ID3D11Texture2D? _halfStaging;
    private int _halfWidth, _halfHeight;

    private ID3D11Texture2D? _cropStaging;
    private int _cropWidth, _cropHeight;

    public IDirect3DDevice WinRtDevice { get; }

    public GpuReader()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            levels, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;

        using var dxgi = _device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try
        {
            WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Gets the Direct3D texture behind a captured frame's surface.</summary>
    public static ID3D11Texture2D TextureFrom(IDirect3DSurface surface)
    {
        var unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iidAccess = IidDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iidAccess, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface is the first method after IUnknown.
                var vtable = *(void***)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
                var iidTexture = IidTexture2D;
                IntPtr texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &iidTexture, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Downscales the top-left width x height of <paramref name="source"/> by 2 into <paramref name="destination"/>.</summary>
    public void ReadHalfGray(ID3D11Texture2D source, int width, int height, GrayImage destination)
    {
        EnsureMip(width, height);
        _context.CopySubresourceRegion(_mip!, 0, 0, 0, 0, source, 0, new Box(0, 0, 0, width, height, 1));
        _context.GenerateMips(_mipView!);

        var hw = Math.Max(1, width / 2);
        var hh = Math.Max(1, height / 2);
        EnsureHalfStaging(hw, hh);
        _context.CopySubresourceRegion(_halfStaging!, 0, 0, 0, 0, _mip!, 1, new Box(0, 0, 0, hw, hh, 1));

        destination.Resize(hw, hh);
        _context.Map(_halfStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            ToGray((byte*)mapped.DataPointer, (int)mapped.RowPitch, hw, hh, destination.Pixels);
        }
        finally
        {
            _context.Unmap(_halfStaging!, 0);
        }
    }

    public const int StripWidth = 128;

    /// <summary>Strip centres in tenths of the width. The edges often hold what makes each line unique (line numbers, times, avatars).</summary>
    private static readonly int[] StripCenters = [1, 5, 9];

    private ID3D11Texture2D? _stripStaging;
    private int _stripStagingWidth, _stripStagingHeight;

    /// <summary>How many strips a frame of this width gets, and how wide each is.</summary>
    public static (int Count, int Width) StripLayout(int frameWidth)
    {
        var count = frameWidth >= 3 * StripWidth ? 3 : 1;
        return (count, count == 3 ? StripWidth : Math.Min(frameWidth, 3 * StripWidth));
    }

    /// <summary>Left edge of strip <paramref name="index"/> in the frame.</summary>
    public static int StripLeft(int index, int frameWidth)
    {
        var (count, stripWidth) = StripLayout(frameWidth);
        return count == 1
            ? Math.Max(0, (frameWidth - stripWidth) / 2)
            : Math.Clamp(frameWidth * StripCenters[index] / 10 - stripWidth / 2, 0, frameWidth - stripWidth);
    }

    /// <summary>
    /// Reads three narrow full-resolution vertical strips (at 1/10, 1/2 and 9/10 of the width)
    /// side by side into <paramref name="destination"/>. Scroll is measured on these: at full
    /// resolution a scroll by any whole number of pixels lines up exactly, which half-size frames
    /// cannot do for odd distances. About a fifth of the pixels of the full frame.
    /// </summary>
    public void ReadStripsGray(ID3D11Texture2D source, int width, int height, GrayImage destination)
    {
        var (count, stripWidth) = StripLayout(width);
        var totalWidth = count * stripWidth;

        if (_stripStaging is null || _stripStagingWidth < totalWidth || _stripStagingHeight < height)
        {
            _stripStaging?.Dispose();
            _stripStagingWidth = Math.Max(totalWidth, _stripStagingWidth);
            _stripStagingHeight = Math.Max(height, _stripStagingHeight);
            _stripStaging = CreateStaging(_stripStagingWidth, _stripStagingHeight);
        }

        for (var i = 0; i < count; i++)
        {
            var x0 = StripLeft(i, width);
            _context.CopySubresourceRegion(_stripStaging, 0, (uint)(i * stripWidth), 0, 0, source, 0,
                new Box(x0, 0, 0, x0 + stripWidth, height, 1));
        }

        destination.Resize(totalWidth, height);
        _context.Map(_stripStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            ToGray((byte*)mapped.DataPointer, (int)mapped.RowPitch, totalWidth, height, destination.Pixels);
        }
        finally
        {
            _context.Unmap(_stripStaging, 0);
        }
    }

    /// <summary>
    /// Reads a full-resolution region as grayscale, enlarged by <paramref name="scale"/> for OCR.
    /// The buffer is rented from a shared pool (crops are megabytes; allocating one per OCR made
    /// memory spike): the caller must return it with <see cref="System.Buffers.ArrayPool{T}.Return"/>.
    /// </summary>
    public byte[] ReadCropGray(ID3D11Texture2D source, RectI crop, int scale, out int outWidth, out int outHeight)
    {
        EnsureCropStaging(crop.Width, crop.Height);
        _context.CopySubresourceRegion(_cropStaging!, 0, 0, 0, 0, source, 0,
            new Box(crop.X, crop.Y, 0, crop.Right, crop.Bottom, 1));

        var luma = System.Buffers.ArrayPool<byte>.Shared.Rent(crop.Width * crop.Height);
        _context.Map(_cropStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            ToGray((byte*)mapped.DataPointer, (int)mapped.RowPitch, crop.Width, crop.Height, luma);
        }
        finally
        {
            _context.Unmap(_cropStaging!, 0);
        }

        outWidth = crop.Width * scale;
        outHeight = crop.Height * scale;
        if (scale == 1) return luma;

        var enlarged = System.Buffers.ArrayPool<byte>.Shared.Rent(outWidth * outHeight);
        Upscale.Bilinear(luma, crop.Width, crop.Height, scale, enlarged);
        System.Buffers.ArrayPool<byte>.Shared.Return(luma);
        return enlarged;
    }

    /// <summary>BGRA to luma.</summary>
    private static void ToGray(byte* src, int rowPitch, int width, int height, byte[] dest)
    {
        fixed (byte* d = dest)
        {
            for (var y = 0; y < height; y++)
            {
                var s = src + y * rowPitch;
                var row = d + y * width;
                for (var x = 0; x < width; x++, s += 4)
                    row[x] = (byte)((s[0] * 29 + s[1] * 150 + s[2] * 77) >> 8);
            }
        }
    }

    private void EnsureMip(int width, int height)
    {
        if (_mip is not null && _mipWidth >= width && _mipHeight >= height) return;
        _mipView?.Dispose();
        _mip?.Dispose();
        _mipWidth = Math.Max(width, _mipWidth);
        _mipHeight = Math.Max(height, _mipHeight);
        _mip = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)_mipWidth, (uint)_mipHeight, 1, 2,
            BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None,
            1, 0, ResourceOptionFlags.GenerateMips));
        _mipView = _device.CreateShaderResourceView(_mip);
    }

    private void EnsureHalfStaging(int width, int height)
    {
        if (_halfStaging is not null && _halfWidth >= width && _halfHeight >= height) return;
        _halfStaging?.Dispose();
        _halfWidth = Math.Max(width, _halfWidth);
        _halfHeight = Math.Max(height, _halfHeight);
        _halfStaging = CreateStaging(_halfWidth, _halfHeight);
    }

    private void EnsureCropStaging(int width, int height)
    {
        if (_cropStaging is not null && _cropWidth >= width && _cropHeight >= height) return;
        _cropStaging?.Dispose();
        _cropWidth = Math.Max(width, _cropWidth);
        _cropHeight = Math.Max(height, _cropHeight);
        _cropStaging = CreateStaging(_cropWidth, _cropHeight);
    }

    private ID3D11Texture2D CreateStaging(int width, int height) =>
        _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));

    /// <summary>Frees the large scratch textures, for example after big windows closed.</summary>
    public void Trim()
    {
        _mipView?.Dispose(); _mipView = null;
        _mip?.Dispose(); _mip = null; _mipWidth = _mipHeight = 0;
        _halfStaging?.Dispose(); _halfStaging = null; _halfWidth = _halfHeight = 0;
        _cropStaging?.Dispose(); _cropStaging = null; _cropWidth = _cropHeight = 0;
        _stripStaging?.Dispose(); _stripStaging = null; _stripStagingWidth = _stripStagingHeight = 0;
    }

    public void Dispose()
    {
        Trim();
        _context.ClearState();
        _context.Dispose();
        _device.Dispose();
    }
}
