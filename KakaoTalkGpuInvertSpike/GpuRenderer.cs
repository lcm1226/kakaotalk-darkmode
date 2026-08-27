using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static Vortice.Direct3D11.D3D11;

namespace KakaoTalkGpuInvertSpike;

internal sealed class GpuRenderer : IDisposable
{
    private const float SidebarVisibleWidth = 78;
    private const float TitleButtonsVisibleWidth = 138;
    private const float TitleButtonsVisibleHeight = 38;
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private readonly object _sync = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _settingsBuffer;
    private readonly ID3D11RenderTargetView[] _renderTargets = new ID3D11RenderTargetView[1];
    private readonly ID3D11ShaderResourceView[] _shaderResources = new ID3D11ShaderResourceView[1];
    private readonly ID3D11SamplerState[] _samplers = new ID3D11SamplerState[1];
    private readonly ID3D11Buffer[] _settingsBuffers = new ID3D11Buffer[1];
    private readonly Viewport[] _viewports = new Viewport[1];
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11Texture2D? _captureTexture;
    private ID3D11ShaderResourceView? _captureView;
    private int _captureWidth;
    private int _captureHeight;
    private int _outputWidth;
    private int _outputHeight;
    private float _invertStrength;
    private bool _privacyModeEnabled;
    private float _dpiScale;
    private bool _settingsDirty = true;
    private readonly string? _diagnosticCapturePath = Environment.GetEnvironmentVariable("KAKAOTALK_GPU_CAPTURE_PATH");
    private bool _diagnosticCaptureAttempted;
    private bool _disposed;

    public GpuRenderer(
        nint overlayHandle,
        int width,
        int height,
        int invertStrength,
        bool privacyModeEnabled,
        float dpiScale)
    {
        var (device, context) = CreateDevice();
        IDXGISwapChain1? swapChain = null;
        ID3D11VertexShader? vertexShader = null;
        ID3D11PixelShader? pixelShader = null;
        ID3D11SamplerState? sampler = null;
        ID3D11Buffer? settingsBuffer = null;
        try
        {
            swapChain = CreateSwapChain(device, overlayHandle, width, height);
            (vertexShader, pixelShader) = CreateShaders(device);
            sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            });
            settingsBuffer = device.CreateBuffer(
                (uint)Marshal.SizeOf<ShaderSettings>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                0);
        }
        catch
        {
            DisposeResource(settingsBuffer, "startup settings buffer");
            DisposeResource(sampler, "startup sampler");
            DisposeResource(pixelShader, "startup pixel shader");
            DisposeResource(vertexShader, "startup vertex shader");
            DisposeResource(swapChain, "startup swap chain");
            DisposeResource(context, "startup device context");
            DisposeResource(device, "startup D3D device");
            throw;
        }

        _device = device;
        _context = context;
        _swapChain = swapChain;
        _vertexShader = vertexShader;
        _pixelShader = pixelShader;
        _sampler = sampler;
        _settingsBuffer = settingsBuffer;
        _settingsBuffers[0] = _settingsBuffer;
        _invertStrength = Math.Clamp(invertStrength, 0, 100) / 100f;
        _privacyModeEnabled = privacyModeEnabled;
        _dpiScale = Math.Clamp(dpiScale, 0.5f, 4f);
        ResizeOutput(width, height);
    }

    public IDirect3DDevice CreateWinRtDevice()
    {
        var dxgiDevice = nint.Zero;
        var inspectableDevice = nint.Zero;
        var iid = DxgiDeviceIid;
        try
        {
            var queryResult = Marshal.QueryInterface(_device.NativePointer, ref iid, out dxgiDevice);
            Marshal.ThrowExceptionForHR(queryResult);
            var wrapResult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectableDevice);
            Marshal.ThrowExceptionForHR(wrapResult);
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectableDevice);
        }
        finally
        {
            if (inspectableDevice != nint.Zero)
            {
                _ = Marshal.Release(inspectableDevice);
            }

            if (dxgiDevice != nint.Zero)
            {
                _ = Marshal.Release(dxgiDevice);
            }
        }
    }

    public void ResizeOutput(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_renderTarget is not null && _outputWidth == width && _outputHeight == height)
            {
                return;
            }

            ReleaseRenderTarget();
            if (_outputWidth != 0 && _outputHeight != 0)
            {
                _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
            }

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _renderTarget = _device.CreateRenderTargetView(backBuffer);
            _outputWidth = width;
            _outputHeight = height;
            _settingsDirty = true;
            if (_captureTexture is not null)
            {
                DrawCapturedFrame();
            }
        }
    }

    public void UpdateSettings(int invertStrength, bool privacyModeEnabled, float dpiScale)
    {
        var normalizedStrength = Math.Clamp(invertStrength, 0, 100) / 100f;
        var normalizedDpiScale = Math.Clamp(dpiScale, 0.5f, 4f);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Math.Abs(_invertStrength - normalizedStrength) < 0.0001f &&
                _privacyModeEnabled == privacyModeEnabled &&
                Math.Abs(_dpiScale - normalizedDpiScale) < 0.0001f)
            {
                return;
            }

            _invertStrength = normalizedStrength;
            _privacyModeEnabled = privacyModeEnabled;
            _dpiScale = normalizedDpiScale;
            _settingsDirty = true;
            if (_captureTexture is not null && _renderTarget is not null)
            {
                DrawCapturedFrame();
            }
        }
    }

    public unsafe void Render(ID3D11Texture2D sourceTexture, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            EnsureCaptureTexture(width, height);
            _context.CopyResource(_captureTexture!, sourceTexture);
            DrawCapturedFrame();
        }
    }

    public unsafe void RenderDiagnosticPattern()
    {
        uint* pixels = stackalloc uint[4]
        {
            0xFF202020,
            0xFF3366CC,
            0xFF66CC33,
            0xFFE0E0E0
        };
        var initialData = new SubresourceData(pixels, 8, 16);
        var description = new Texture2DDescription
        {
            Width = 2,
            Height = 2,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        using var texture = _device.CreateTexture2D(description, initialData);
        Render(texture, 2, 2);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                ReleaseRenderTarget();
                _context.ClearState();
                _context.Flush();
            }
            catch (Exception exception)
            {
                AppDiagnostics.WriteException("GPU command cleanup error", exception);
            }

            DisposeResource(_renderTarget, "render target");
            _renderTarget = null;
            DisposeResource(_captureView, "capture view");
            DisposeResource(_captureTexture, "capture texture");
            DisposeResource(_settingsBuffer, "settings buffer");
            DisposeResource(_sampler, "sampler");
            DisposeResource(_pixelShader, "pixel shader");
            DisposeResource(_vertexShader, "vertex shader");
            DisposeResource(_swapChain, "swap chain");
            DisposeResource(_context, "device context");
            DisposeResource(_device, "D3D device");
        }
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context) CreateDevice()
    {
        ID3D11Device device;
        try
        {
            device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        catch (SharpGenException)
        {
            device = D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        }

        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice1>();
            dxgiDevice.SetMaximumFrameLatency(1).CheckError();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteStatus(
                $"DXGI frame-latency limit unavailable: {exception.GetType().Name}: {exception.Message}");
        }

        return (device, device.ImmediateContext);
    }

    private static IDXGISwapChain1 CreateSwapChain(
        ID3D11Device device,
        nint overlayHandle,
        int width,
        int height)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter).CheckError();
        using (adapter)
        {
            adapter.GetParent<IDXGIFactory2>(out var factory).CheckError();
            if (factory is null)
            {
                throw new InvalidOperationException("Could not get the DXGI factory.");
            }
            using (factory)
            {
                var description = new SwapChainDescription1
                {
                    Width = (uint)Math.Max(1, width),
                    Height = (uint)Math.Max(1, height),
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = SwapChainFlags.None
                };
                var swapChain = factory.CreateSwapChainForHwnd(
                    device,
                    overlayHandle,
                    description);
                try
                {
                    factory.MakeWindowAssociation(
                        overlayHandle,
                        WindowAssociationFlags.IgnoreAltEnter).CheckError();
                    return swapChain;
                }
                catch
                {
                    DisposeResource(swapChain, "partially initialized swap chain");
                    throw;
                }
            }
        }
    }

    private static unsafe (ID3D11VertexShader Vertex, ID3D11PixelShader Pixel) CreateShaders(
        ID3D11Device device)
    {
        const string shaderSource = """
            Texture2D CapturedTexture : register(t0);
            SamplerState CapturedSampler : register(s0);

            cbuffer FilterSettings : register(b0)
            {
                float InvertStrength;
                float PrivacyModeEnabled;
                float SidebarWidth;
                float TitleButtonsWidth;
                float TitleButtonsHeight;
                float OutputWidth;
                float OutputHeight;
                float BorderThickness;
            };

            struct VertexOutput
            {
                float4 Position : SV_Position;
                float2 TextureCoordinate : TEXCOORD0;
            };

            VertexOutput VertexMain(uint vertexId : SV_VertexID)
            {
                float2 positions[3] =
                {
                    float2(-1.0, -1.0),
                    float2(-1.0,  3.0),
                    float2( 3.0, -1.0)
                };
                float2 textureCoordinates[3] =
                {
                    float2(0.0, 1.0),
                    float2(0.0, -1.0),
                    float2(2.0, 1.0)
                };

                VertexOutput output;
                output.Position = float4(positions[vertexId], 0.0, 1.0);
                output.TextureCoordinate = textureCoordinates[vertexId];
                return output;
            }

            float4 PixelMain(VertexOutput input) : SV_Target
            {
                float4 source = CapturedTexture.Sample(CapturedSampler, input.TextureCoordinate);
                float3 color = lerp(source.rgb, 1.0 - source.rgb, saturate(InvertStrength));
                if (PrivacyModeEnabled > 0.5)
                {
                    bool coversBody = input.Position.x >= SidebarWidth &&
                        input.Position.y >= TitleButtonsHeight;
                    bool coversTitle = input.Position.x >= SidebarWidth &&
                        input.Position.x < OutputWidth - TitleButtonsWidth &&
                        input.Position.y < TitleButtonsHeight;
                    if (coversBody || coversTitle)
                    {
                        color = 0.0;
                    }
                }

                float cornerBlockSize = BorderThickness * 6.0;
                bool isBorder = input.Position.x < BorderThickness ||
                    input.Position.x > OutputWidth - BorderThickness ||
                    input.Position.y < BorderThickness ||
                    input.Position.y > OutputHeight - BorderThickness;
                bool isCornerBlock =
                    (input.Position.x < cornerBlockSize ||
                        input.Position.x > OutputWidth - cornerBlockSize) &&
                    (input.Position.y < cornerBlockSize ||
                        input.Position.y > OutputHeight - cornerBlockSize);
                if (isBorder || isCornerBlock)
                {
                    color = float3(0.025, 0.028, 0.032);
                }

                return float4(color, 1.0);
            }
            """;

        var vertexBytes = ShaderCompiler.Compile(shaderSource, "VertexMain", "vs_5_0");
        var pixelBytes = ShaderCompiler.Compile(shaderSource, "PixelMain", "ps_5_0");
        fixed (byte* vertexPointer = vertexBytes)
        fixed (byte* pixelPointer = pixelBytes)
        {
            var vertexShader = device.CreateVertexShader(vertexPointer, (nuint)vertexBytes.Length, null);
            try
            {
                var pixelShader = device.CreatePixelShader(pixelPointer, (nuint)pixelBytes.Length, null);
                return (vertexShader, pixelShader);
            }
            catch
            {
                DisposeResource(vertexShader, "partially initialized vertex shader");
                throw;
            }
        }
    }

    private void EnsureCaptureTexture(int width, int height)
    {
        if (_captureTexture is not null && _captureWidth == width && _captureHeight == height)
        {
            return;
        }

        _captureView?.Dispose();
        _captureTexture?.Dispose();
        _captureTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
        _captureView = _device.CreateShaderResourceView(_captureTexture);
        _captureWidth = width;
        _captureHeight = height;
    }

    private unsafe void DrawCapturedFrame()
    {
        if (_captureView is null || _renderTarget is null)
        {
            return;
        }

        UpdateSettingsBuffer();
        _renderTargets[0] = _renderTarget;
        _context.OMSetRenderTargets(1, _renderTargets, null);
        _viewports[0] = new Viewport(0, 0, _outputWidth, _outputHeight, 0, 1);
        _context.RSSetViewports(1, _viewports);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader, null, 0);
        _context.PSSetShader(_pixelShader, null, 0);

        _shaderResources[0] = _captureView;
        _samplers[0] = _sampler;
        _context.PSSetShaderResources(0, 1, _shaderResources);
        _context.PSSetSamplers(0, 1, _samplers);
        _context.PSSetConstantBuffers(0, 1, _settingsBuffers);
        _context.Draw(3, 0);
        TryCaptureDiagnosticFrame();

        _shaderResources[0] = null!;
        _context.PSSetShaderResources(0, 1, _shaderResources);
        _swapChain.Present(0, PresentFlags.None).CheckError();
    }

    private unsafe void UpdateSettingsBuffer()
    {
        if (!_settingsDirty)
        {
            return;
        }

        var mapped = _context.Map(
            _settingsBuffer,
            MapMode.WriteDiscard,
            Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(ShaderSettings*)mapped.DataPointer = new ShaderSettings
            {
                InvertStrength = _invertStrength,
                PrivacyModeEnabled = _privacyModeEnabled ? 1 : 0,
                SidebarWidth = SidebarVisibleWidth * _dpiScale,
                TitleButtonsWidth = TitleButtonsVisibleWidth * _dpiScale,
                TitleButtonsHeight = TitleButtonsVisibleHeight * _dpiScale,
                OutputWidth = _outputWidth,
                OutputHeight = _outputHeight,
                BorderThickness = Math.Clamp(1.5f * _dpiScale, 2, 4)
            };
        }
        finally
        {
            _context.Unmap(_settingsBuffer, 0);
        }

        _settingsDirty = false;
    }

    private unsafe void TryCaptureDiagnosticFrame()
    {
        if (_diagnosticCaptureAttempted || string.IsNullOrWhiteSpace(_diagnosticCapturePath))
        {
            return;
        }

        _diagnosticCaptureAttempted = true;
        try
        {
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var description = backBuffer.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            using var staging = _device.CreateTexture2D(description);
            _context.CopyResource(staging, backBuffer);
            var mapped = _context.Map(staging, 0);
            try
            {
                using var bitmap = new Bitmap(_outputWidth, _outputHeight, PixelFormat.Format32bppArgb);
                var bounds = new Rectangle(0, 0, _outputWidth, _outputHeight);
                var bitmapData = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var bytesPerRow = checked(_outputWidth * 4);
                    for (var row = 0; row < _outputHeight; row++)
                    {
                        var source = (byte*)mapped.DataPointer + (row * mapped.RowPitch);
                        var destination = (byte*)bitmapData.Scan0 + (row * bitmapData.Stride);
                        Buffer.MemoryCopy(source, destination, bitmapData.Stride, bytesPerRow);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticCapturePath)!);
                bitmap.Save(_diagnosticCapturePath, ImageFormat.Png);
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        catch (Exception exception)
        {
            try
            {
                File.WriteAllText(_diagnosticCapturePath + ".error.txt", exception.ToString());
            }
            catch
            {
                // Diagnostics must never affect the rendering path.
            }
        }
    }

    private void ReleaseRenderTarget()
    {
        _renderTargets[0] = null!;
        _context.OMSetRenderTargets(1, _renderTargets, null);
        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void DisposeResource(IDisposable? resource, string name)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException($"GPU {name} disposal error", exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderSettings
    {
        public float InvertStrength;
        public float PrivacyModeEnabled;
        public float SidebarWidth;
        public float TitleButtonsWidth;
        public float TitleButtonsHeight;
        public float OutputWidth;
        public float OutputHeight;
        public float BorderThickness;
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
