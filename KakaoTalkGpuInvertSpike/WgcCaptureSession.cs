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

// IInspectable slots precede the session3 properties. WinRT boolean is one byte.
[ComImport]
[Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureSession3
{
    void GetIids(out uint count, out nint ids);
    void GetRuntimeClassName(out nint name);
    void GetTrustLevel(out int level);
    [PreserveSig] int GetIsBorderRequired(out byte value);
    [PreserveSig] int SetIsBorderRequired(byte value);
}

internal sealed class WgcCaptureSession : IDisposable
{
    private const int MaximumRenderFramesPerSecond = 30;
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid D3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private readonly object _sync = new();
    private readonly object _captureGate = new();
    private readonly GpuRenderer _renderer;
    private readonly Action _firstFramePresented;
    private readonly Action _captureFaulted;
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
    private long _lastRenderedTimestamp;

    public WgcCaptureSession(
        GpuRenderer renderer,
        Action firstFramePresented,
        Action captureFaulted)
    {
        _renderer = renderer;
        _firstFramePresented = firstFramePresented;
        _captureFaulted = captureFaulted;
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
        StopCapture();
        CaptureResources cleanup = default;

        try
        {
            lock (_captureGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!GraphicsCaptureSession.IsSupported())
                {
                    throw new NotSupportedException("Windows.Graphics.Capture is not supported.");
                }

                var item = CreateItemForWindow(targetHandle) ??
                    throw new InvalidOperationException("Could not create a WGC item for KakaoTalk.");
                try
                {
                    _item = item;
                    _captureSize = item.Size;
                    _lastRenderedTimestamp = 0;
                    _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                        _winRtDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        _captureSize);
                    _framePool.FrameArrived += OnFrameArrived;
                    _session = _framePool.CreateCaptureSession(item);
                    _session.IsCursorCaptureEnabled = false;
                    TryDisableCaptureBorder(_session);
                    _renderingEnabled = true;
                    lock (_sync)
                    {
                        _isFaulted = false;
                        _hasPresentedFrame = false;
                        _status = "Waiting for GPU frame";
                    }

                    _session.StartCapture();
                }
                catch
                {
                    cleanup = DetachCaptureCore();
                    throw;
                }
            }
        }
        catch
        {
            DisposeCaptureResources(cleanup);
            throw;
        }

        DisposeCaptureResources(cleanup);
    }

    public void SetRenderingEnabled(bool enabled)
    {
        _renderingEnabled = enabled;
    }

    public void Dispose()
    {
        CaptureResources resources;
        lock (_captureGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderingEnabled = false;
            resources = DetachCaptureCore();
        }

        DisposeCaptureResources(resources);
        try
        {
            _winRtDevice.Dispose();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("WinRT D3D device disposal error", exception);
        }
    }

    private void StopCapture()
    {
        CaptureResources resources;
        lock (_captureGate)
        {
            resources = DetachCaptureCore();
        }

        DisposeCaptureResources(resources);
    }

    private CaptureResources DetachCaptureCore()
    {
        var framePool = _framePool;
        var session = _session;
        _item = null;
        _session = null;
        _framePool = null;

        try
        {
            if (framePool is not null)
            {
                framePool.FrameArrived -= OnFrameArrived;
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Frame callback detach error", exception);
        }

        lock (_sync)
        {
            _hasPresentedFrame = false;
            _status = "Idle";
        }

        return new CaptureResources(session, framePool);
    }

    private static void DisposeCaptureResources(CaptureResources resources)
    {
        try
        {
            resources.Session?.Dispose();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Capture session disposal error", exception);
        }

        try
        {
            resources.FramePool?.Dispose();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Frame pool disposal error", exception);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var notifyFault = false;
        lock (_captureGate)
        {
            if (_disposed || sender != _framePool)
            {
                return;
            }

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

                if (!_renderingEnabled || !ShouldRenderFrame())
                {
                    return;
                }

                using var texture = GetTexture(frame.Surface);
                _renderer.Render(texture, _captureSize.Width, _captureSize.Height);
                RecordPresentedFrame();
            }
            catch (Exception exception)
            {
                _renderingEnabled = false;
                lock (_sync)
                {
                    notifyFault = !_isFaulted;
                    _isFaulted = true;
                    _status = $"Frame error: {exception.GetType().Name}: {exception.Message}";
                }
            }
        }

        if (notifyFault)
        {
            try
            {
                _captureFaulted();
            }
            catch (Exception exception)
            {
                AppDiagnostics.WriteException("Capture fault notification error", exception);
            }
        }
    }

    private bool ShouldRenderFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var minimumInterval = Stopwatch.Frequency / MaximumRenderFramesPerSecond;
        if (_lastRenderedTimestamp != 0 && now - _lastRenderedTimestamp < minimumInterval)
        {
            return false;
        }

        _lastRenderedTimestamp = now;
        return true;
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
            if (!Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                AppDiagnostics.WriteStatus("Capture border control is unavailable on this Windows version");
                return;
            }
            // The 19041 projection has no IsBorderRequired property, even on newer Windows.
            var borderSession = session.As<IGraphicsCaptureSession3>();
            Marshal.ThrowExceptionForHR(borderSession.SetIsBorderRequired(0));
            Marshal.ThrowExceptionForHR(borderSession.GetIsBorderRequired(out var required));
            AppDiagnostics.WriteStatus($"Capture border requested off | Required={required != 0}");
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteStatus($"Capture border control unavailable: {exception.Message}");
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

    private readonly record struct CaptureResources(
        GraphicsCaptureSession? Session,
        Direct3D11CaptureFramePool? FramePool);
}
