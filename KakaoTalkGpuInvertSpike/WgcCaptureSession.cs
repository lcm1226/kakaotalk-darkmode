using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace KakaoTalkGpuInvertSpike;

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    nint CreateForWindow(nint window, in Guid iid);
    nint CreateForMonitor(nint monitor, in Guid iid);
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface(in Guid iid);
}

internal sealed class WgcCaptureSession : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid D3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private readonly object _sync = new();
    private readonly GpuRenderer _renderer;
    private readonly Action _firstFramePresented;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private SizeInt32 _captureSize;
    private long _framesInWindow;
    private long _totalFrames;
    private double _framesPerSecond;
    private string _status = "Idle";
    private bool _hasPresentedFrame;
    private bool _isFaulted;
    private volatile bool _renderingEnabled = true;
    private bool _disposed;

    public WgcCaptureSession(GpuRenderer renderer, Action firstFramePresented)
    {
        _renderer = renderer;
        _firstFramePresented = firstFramePresented;
        _winRtDevice = renderer.CreateWinRtDevice();
    }

    public bool HasPresentedFrame
    {
        get { lock (_sync) { return _hasPresentedFrame; } }
    }

    public string Status
    {
        get { lock (_sync) { return _status; } }
    }

    public double FramesPerSecond
    {
        get { lock (_sync) { return _framesPerSecond; } }
    }

    public long TotalFrames
    {
        get { lock (_sync) { return _totalFrames; } }
    }

    public bool IsFaulted
    {
        get { lock (_sync) { return _isFaulted; } }
    }

    public void Start(nint targetHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported.");
        }

        var item = CreateItemForWindow(targetHandle) ??
            throw new InvalidOperationException("Could not create a WGC item for KakaoTalk.");
        StopCapture();
        _item = item;
        _captureSize = item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _captureSize);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        TryDisableCaptureBorder(_session);
        _session.StartCapture();
        lock (_sync)
        {
            _isFaulted = false;
        }

        SetStatus("Waiting for GPU frame");
    }

    public void SetRenderingEnabled(bool enabled)
    {
        _renderingEnabled = enabled;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCapture();
        _winRtDevice.Dispose();
    }

    private void StopCapture()
    {
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _item = null;
        _session = null;
        _framePool = null;
        lock (_sync)
        {
            _hasPresentedFrame = false;
            _status = "Idle";
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0)
            {
                return;
            }

            if (!_captureSize.Equals(frame.ContentSize))
            {
                _captureSize = frame.ContentSize;
                sender.Recreate(
                    _winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    _captureSize);
                SetStatus("GPU frame pool resized");
                return;
            }

            if (!_renderingEnabled)
            {
                return;
            }

            using var texture = GetTexture(frame.Surface);
            _renderer.Render(texture, _captureSize.Width, _captureSize.Height);
            RecordPresentedFrame();
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _isFaulted = true;
                _status = $"Frame error: {exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    private static GraphicsCaptureItem? CreateItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForWindow(hwnd, GraphicsCaptureItemIid);
        if (itemPointer == nint.Zero)
        {
            return null;
        }

        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            _ = Marshal.Release(itemPointer);
        }
    }

    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var texturePointer = access.GetInterface(D3D11Texture2DIid);
        if (texturePointer == nint.Zero)
        {
            throw new InvalidOperationException("The WGC surface did not expose ID3D11Texture2D.");
        }

        return new ID3D11Texture2D(texturePointer);
    }

    private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        try
        {
            session.GetType().GetProperty("IsBorderRequired")?.SetValue(session, false);
        }
        catch
        {
            // Older Windows versions may keep the system capture border.
        }
    }

    private void RecordPresentedFrame()
    {
        var isFirstFrame = false;
        lock (_sync)
        {
            isFirstFrame = !_hasPresentedFrame;
            _hasPresentedFrame = true;
            _isFaulted = false;
            _totalFrames++;
            _framesInWindow++;
            var elapsed = _frameClock.Elapsed.TotalSeconds;
            if (elapsed >= 1)
            {
                _framesPerSecond = _framesInWindow / elapsed;
                _framesInWindow = 0;
                _frameClock.Restart();
            }

            _status = "GPU invert active";
        }

        if (isFirstFrame)
        {
            _firstFramePresented();
        }
    }

    private void SetStatus(string status)
    {
        lock (_sync)
        {
            _status = status;
        }
    }
}
