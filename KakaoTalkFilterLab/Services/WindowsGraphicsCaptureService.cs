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

    private readonly object _sync = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _device;
    private nint _currentHandle;
    private SizeInt32 _currentSize;
    private BitmapSource? _latestFrame;
    private bool _framePending;
    private string _status = "Idle";
    private int? _lastCreateItemHResult;

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

        if (_session is not null && _currentHandle == hwnd && _currentSize.Equals(item.Size))
        {
            SetStatus(_latestFrame is null ? "WGC waiting frame" : "WGC active");
            return true;
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
        _session.StartCapture();
        SetStatus("WGC starting");
        return true;
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
        if (_framePending)
        {
            return;
        }

        _framePending = true;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

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
            var bitmapSource = await ConvertToBitmapSourceAsync(convertedBitmap);

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
            _framePending = false;
        }
    }

    private static async Task<BitmapSource> ConvertToBitmapSourceAsync(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);

        var bytes = new byte[(int)buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);

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
