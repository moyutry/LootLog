using System.Runtime.InteropServices;
using FortniteHelper.Infrastructure;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FortniteHelper.Services;

/// <summary>32-bit BGRA pixels, tightly packed (stride = width * 4).</summary>
public sealed class CapturedFrame(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>True when the image is (almost) entirely black - e.g. DRM video or exclusive fullscreen.</summary>
    public bool IsBlank
    {
        get
        {
            int step = Math.Max(4, (Pixels.Length / 4 / 2000) * 4);
            for (int i = 0; i + 2 < Pixels.Length; i += step)
                if (Pixels[i] > 6 || Pixels[i + 1] > 6 || Pixels[i + 2] > 6) return false;
            return true;
        }
    }
}

/// <summary>
/// Captures a small screen region. DXGI Desktop Duplication works with borderless/fullscreen games;
/// GDI is the fallback. Only the requested region is ever read back.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    readonly object _gate = new();
    ID3D11Device? _device;
    ID3D11DeviceContext? _context;
    IDXGIOutputDuplication? _dup;
    RECT _outputRect;
    IntPtr _outputMonitor;
    ID3D11Texture2D? _staging;
    int _stagingW, _stagingH;
    bool _dxgiBroken;
    DateTime _retryDxgiAt;

    public string LastMethod { get; private set; } = "-";

    /// <param name="reuse">A pixel buffer to fill instead of allocating a new one (used when it has the right size).</param>
    public CapturedFrame? Capture(RECT region, CaptureMethod method, byte[]? reuse = null)
    {
        lock (_gate)
        {
            if (region.Width <= 0 || region.Height <= 0) return null;
            CapturedFrame? blankDxgi = null;
            if (method != CaptureMethod.Gdi && (!_dxgiBroken || DateTime.UtcNow > _retryDxgiAt))
            {
                try
                {
                    var f = CaptureDxgi(region, reuse);
                    if (f is not null && (!f.IsBlank || method == CaptureMethod.Dxgi)) { LastMethod = "DXGI"; return f; }
                    blankDxgi = f;
                }
                catch (Exception ex)
                {
                    Log.Warn("DXGI capture failed, using GDI: " + ex.Message);
                    ResetDxgi();
                    _dxgiBroken = true;
                    _retryDxgiAt = DateTime.UtcNow.AddSeconds(30);
                }
                if (method == CaptureMethod.Dxgi) return null;
            }
            var g = CaptureGdi(region, reuse);
            LastMethod = "GDI";
            if ((g is null || g.IsBlank) && blankDxgi is not null) { LastMethod = "DXGI"; return blankDxgi; }
            return g;
        }
    }

    // ---------------- DXGI ----------------
    CapturedFrame? CaptureDxgi(RECT region, byte[]? reuse)
    {
        var monitor = Native.MonitorFromPoint(new POINT { X = region.Left + region.Width / 2, Y = region.Top + region.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST);
        if (_dup is null || monitor != _outputMonitor) CreateDuplication(monitor);
        if (_dup is null || _device is null || _context is null) return null;

        // Clip to the output
        int left = Math.Max(region.Left, _outputRect.Left) - _outputRect.Left;
        int top = Math.Max(region.Top, _outputRect.Top) - _outputRect.Top;
        int right = Math.Min(region.Right, _outputRect.Right) - _outputRect.Left;
        int bottom = Math.Min(region.Bottom, _outputRect.Bottom) - _outputRect.Top;
        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0) return null;

        IDXGIResource? res = null;
        bool acquired = false;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var hr = _dup.AcquireNextFrame(attempt == 0 ? 60u : 120u, out _, out res);
                if (hr.Success) { acquired = true; break; }
                if (hr.Code == unchecked((int)0x887A0027)) continue; // WAIT_TIMEOUT: nothing changed yet
                if (hr.Code == unchecked((int)0x887A0026)) // ACCESS_LOST (mode change, UAC, ...)
                {
                    ResetDxgi();
                    CreateDuplication(monitor);
                    if (_dup is null) return null;
                    continue;
                }
                hr.CheckError();
            }
            if (!acquired || res is null) return null;

            using var tex = res.QueryInterface<ID3D11Texture2D>();
            if (_staging is null || _stagingW != w || _stagingH != h)
            {
                _staging?.Dispose();
                _staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
                _stagingW = w;
                _stagingH = h;
            }

            _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, tex, 0, new Box(left, top, 0, right, bottom, 1));
            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = reuse is { } rb && rb.Length == w * h * 4 ? rb : new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * w * 4, w * 4);
                return new CapturedFrame(pixels, w, h);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }
        finally
        {
            res?.Dispose();
            if (acquired) _dup?.ReleaseFrame();
        }
    }

    void CreateDuplication(IntPtr monitor)
    {
        ResetDxgi();
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using (factory)
        {
            for (uint a = 0; factory!.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            if (desc.Monitor != monitor) continue;

                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                                out _device, out _context).CheckError();
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            _dup = output1.DuplicateOutput(_device);
                            var dc = desc.DesktopCoordinates;
                            _outputRect = new RECT(dc.Left, dc.Top, dc.Right, dc.Bottom);
                            _outputMonitor = monitor;
                            return;
                        }
                    }
                }
            }
        }
        throw new InvalidOperationException("No DXGI output found for the game monitor.");
    }

    void ResetDxgi()
    {
        _staging?.Dispose(); _staging = null;
        _dup?.Dispose(); _dup = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _outputMonitor = IntPtr.Zero;
    }

    // ---------------- GDI ----------------
    static CapturedFrame? CaptureGdi(RECT r, byte[]? reuse)
    {
        int w = r.Width, h = r.Height;
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = Native.CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = Native.SelectObject(mem, dib);
        try
        {
            // No CAPTUREBLT: layered windows (our own overlays) are not included.
            if (!Native.BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, Native.SRCCOPY)) return null;
            var pixels = reuse is { } rb && rb.Length == w * h * 4 ? rb : new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new CapturedFrame(pixels, w, h);
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        lock (_gate) ResetDxgi();
    }
}
