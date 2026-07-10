using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace KakaoTalkFilterLab.Services;

internal enum WgcFrameTransform
{
    None,
    Invert
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow(IntPtr window, in Guid iid);

    IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
}

internal sealed class WindowsGraphicsCaptureService : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private const uint D3D11SdkVersion = 7;
    private TimeSpan _minimumFrameCopyInterval = TimeSpan.FromMilliseconds(1000);

    private readonly object _sync = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _device;
    private nint _currentHandle;
    private SizeInt32 _currentSize;
    private BitmapSource? _latestFrame;
    private bool _framePending;
    private DateTime _lastFrameCopyUtc = DateTime.MinValue;
    private string _status = "Idle";
    private WgcFrameTransform _frameTransform = WgcFrameTransform.None;
    private double _invertStrength = 1.0;
    private int? _lastCreateItemHResult;

    public TimeSpan MinimumFrameCopyInterval
    {
        get
        {
            lock (_sync)
            {
                return _minimumFrameCopyInterval;
            }
        }
        set
        {
            lock (_sync)
            {
                _minimumFrameCopyInterval = value < TimeSpan.FromMilliseconds(250)
                    ? TimeSpan.FromMilliseconds(250)
                    : value;
            }
        }
    }
    public void ConfigureFrameTransform(WgcFrameTransform transform, double invertStrength = 1.0)
    {
        lock (_sync)
        {
            invertStrength = Math.Clamp(invertStrength, 0.0, 1.0);
            if (_frameTransform == transform && Math.Abs(_invertStrength - invertStrength) < 0.0001)
            {
                return;
            }

            _frameTransform = transform;
            _invertStrength = invertStrength;
            _latestFrame = null;
            _lastFrameCopyUtc = DateTime.MinValue;
        }
    }
    public bool IsSupported => GraphicsCaptureSession.IsSupported();

    public string Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public BitmapSource? LatestFrame
    {
        get
        {
            lock (_sync)
            {
                return _latestFrame;
            }
        }
    }

    public bool StartOrUpdate(nint hwnd)
    {
        if (!IsSupported || hwnd == 0)
        {
            SetStatus("WGC unsupported");
            return false;
        }

        if (_session is not null && _currentHandle == hwnd)
        {
            SetStatus(_latestFrame is null ? "WGC waiting frame" : "WGC active");
            return true;
        }

        GraphicsCaptureItem? item;
        try
        {
            item = CreateItemForWindow(hwnd);
        }
        catch (Exception ex)
        {
            SetStatus("WGC item error: " + ex.GetType().Name);
            return false;
        }

        if (item is null)
        {
            SetStatus(_lastCreateItemHResult is int hr
                ? $"WGC item unavailable (0x{hr:X8})"
                : "WGC item unavailable");
            return false;
        }

        Stop();

        _device ??= CreateDirect3DDevice();
        if (_device is null)
        {
            SetStatus("WGC D3D device failed");
            return false;
        }

        _item = item;
        _currentHandle = hwnd;
        _currentSize = item.Size;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);

        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        TryDisableCaptureBorder(_session);
        _session.StartCapture();
        SetStatus("WGC starting");
        return true;
    }

    private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        try
        {
            var property = session.GetType().GetProperty("IsBorderRequired");
            property?.SetValue(session, false);
        }
        catch
        {
            // Older Windows builds can ignore this; the app still works with the OS border.
        }
    }
    public void Stop()
    {
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _session = null;
        _framePool = null;
        _item = null;
        _currentHandle = 0;
        _currentSize = default;
        _framePending = false;

        lock (_sync)
        {
            _latestFrame = null;
            _status = "Idle";
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private GraphicsCaptureItem? CreateItemForWindow(nint hwnd)
    {
        _lastCreateItemHResult = null;
        try
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var itemPtr = interop.CreateForWindow(hwnd, GraphicsCaptureItemIid);
            _lastCreateItemHResult = 0;
            if (itemPtr == nint.Zero)
            {
                return null;
            }

            try
            {
                return GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        catch (COMException ex)
        {
            _lastCreateItemHResult = ex.HResult;
            return null;
        }
        finally
        {
        }
    }

    private static IDirect3DDevice? CreateDirect3DDevice()
    {
        if (TryCreateDirect3DDevice(D3DDriverTypeHardware, out var device))
        {
            return device;
        }

        return TryCreateDirect3DDevice(D3DDriverTypeWarp, out device) ? device : null;
    }

    private static bool TryCreateDirect3DDevice(int driverType, out IDirect3DDevice? device)
    {
        device = null;

        var hr = D3D11CreateDevice(
            nint.Zero,
            driverType,
            nint.Zero,
            D3D11CreateDeviceBgraSupport,
            nint.Zero,
            0,
            D3D11SdkVersion,
            out var d3dDevice,
            out _,
            out var immediateContext);

        if (hr < 0 || d3dDevice == nint.Zero)
        {
            if (immediateContext != nint.Zero)
            {
                Marshal.Release(immediateContext);
            }

            if (d3dDevice != nint.Zero)
            {
                Marshal.Release(d3dDevice);
            }

            return false;
        }

        try
        {
            var dxgiGuid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.QueryInterface(d3dDevice, ref dxgiGuid, out var dxgiDevice);
            if (dxgiDevice == nint.Zero)
            {
                return false;
            }

            try
            {
                var wrapHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectableDevice);
                if (wrapHr < 0 || inspectableDevice == nint.Zero)
                {
                    return false;
                }

                try
                {
                    device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectableDevice);
                    return true;
                }
                finally
                {
                    Marshal.Release(inspectableDevice);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            if (immediateContext != nint.Zero)
            {
                Marshal.Release(immediateContext);
            }

            Marshal.Release(d3dDevice);
        }
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var ownsPending = false;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            TimeSpan minimumFrameCopyInterval;
            WgcFrameTransform frameTransform;
            double invertStrength;
            lock (_sync)
            {
                minimumFrameCopyInterval = _minimumFrameCopyInterval;
                frameTransform = _frameTransform;
                invertStrength = _invertStrength;
            }

            if (_framePending || nowUtc - _lastFrameCopyUtc < minimumFrameCopyInterval)
            {
                return;
            }

            _framePending = true;
            ownsPending = true;
            _lastFrameCopyUtc = nowUtc;

            if (frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0)
            {
                return;
            }

            if (!_currentSize.Equals(frame.ContentSize))
            {
                _currentSize = frame.ContentSize;
                _framePool?.Recreate(
                    _device!,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    frame.ContentSize);
            }

            using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            using var convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var bitmapSource = await ConvertToBitmapSourceAsync(convertedBitmap, frameTransform, invertStrength);

            lock (_sync)
            {
                _latestFrame = bitmapSource;
                _status = "WGC frame ready";
            }
        }
        catch (Exception ex)
        {
            SetStatus("WGC frame error: " + ex.GetType().Name);
        }
        finally
        {
            if (ownsPending)
            {
                _framePending = false;
            }
        }
    }

    private static async Task<BitmapSource> ConvertToBitmapSourceAsync(SoftwareBitmap bitmap, WgcFrameTransform transform, double invertStrength)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);

        var bytes = new byte[(int)buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);

        ApplyFrameTransform(bytes, transform, invertStrength);
        await Task.Yield();

        var source = BitmapSource.Create(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            bytes,
            bitmap.PixelWidth * 4);

        source.Freeze();
        return source;
    }

    private static void ApplyFrameTransform(byte[] bytes, WgcFrameTransform transform, double invertStrength)
    {
        if (transform != WgcFrameTransform.Invert)
        {
            return;
        }

        invertStrength = Math.Clamp(invertStrength, 0.0, 1.0);
        if (invertStrength >= 0.999)
        {
            for (var index = 0; index < bytes.Length; index += 4)
            {
                bytes[index] = (byte)(255 - bytes[index]);
                bytes[index + 1] = (byte)(255 - bytes[index + 1]);
                bytes[index + 2] = (byte)(255 - bytes[index + 2]);
            }

            return;
        }

        var inverseStrength = 1.0 - invertStrength;
        for (var index = 0; index < bytes.Length; index += 4)
        {
            bytes[index] = (byte)Math.Clamp((int)Math.Round((bytes[index] * inverseStrength) + ((255 - bytes[index]) * invertStrength)), 0, 255);
            bytes[index + 1] = (byte)Math.Clamp((int)Math.Round((bytes[index + 1] * inverseStrength) + ((255 - bytes[index + 1]) * invertStrength)), 0, 255);
            bytes[index + 2] = (byte)Math.Clamp((int)Math.Round((bytes[index + 2] * inverseStrength) + ((255 - bytes[index + 2]) * invertStrength)), 0, 255);
        }
    }
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private void SetStatus(string status)
    {
        lock (_sync)
        {
            _status = status;
        }
    }
}
