using System.Windows.Forms;

namespace KakaoTalkGpuInvertSpike;

internal static class Program
{
    private const string InstanceMutexName = @"Local\KakaoTalkGpuInvertSpike.Singleton";

    [STAThread]
    private static void Main()
    {
        using var instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        AppDiagnostics.Initialize();
        _ = NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
        ApplicationConfiguration.Initialize();
        try
        {
            if (string.Equals(
                Environment.GetEnvironmentVariable("KAKAOTALK_GPU_OVERLAY_SMOKE_TEST"),
                "1",
                StringComparison.Ordinal))
            {
                using var smokeTest = new OverlaySmokeTestApplication();
                Application.Run(smokeTest);
                return;
            }

            using var application = new GpuInvertApplication();
            Application.Run(application);
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Fatal main-loop exception", exception);
        }
        finally
        {
            AppDiagnostics.WriteLifecycle("Process stopped");
            instanceMutex.ReleaseMutex();
        }
    }
}

internal sealed class OverlaySmokeTestApplication : ApplicationContext
{
    private readonly OverlayWindow _overlay = new();
    private readonly GpuRenderer _renderer;
    private readonly System.Windows.Forms.Timer _exitTimer;
    private bool _hasRendered;

    public OverlaySmokeTestApplication()
    {
        _overlay.Position(new TargetWindow(nint.Zero, 0, 0, 320, 240), true);
        _renderer = new GpuRenderer(_overlay.Handle, 320, 240, 100, false, 1);
        _renderer.UpdateSettings(75, true, 1);
        SaveStrengthControlDiagnostic();
        _exitTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _exitTimer.Tick += (_, _) =>
        {
            if (!_hasRendered)
            {
                _hasRendered = true;
                try
                {
                    _renderer.RenderDiagnosticPattern();
                }
                catch (Exception exception)
                {
                    WriteSmokeTestError(exception);
                    ExitThread();
                    return;
                }

                _exitTimer.Interval = 1000;
                return;
            }

            ExitThread();
        };
        _exitTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exitTimer.Stop();
            _exitTimer.Dispose();
            _renderer.Dispose();
            _overlay.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void WriteSmokeTestError(Exception exception)
    {
        var capturePath = Environment.GetEnvironmentVariable("KAKAOTALK_GPU_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
            File.WriteAllText(capturePath + ".smoke-error.txt", exception.ToString());
        }
        catch
        {
            // Smoke-test diagnostics must not mask the original failure.
        }
    }

    private static void SaveStrengthControlDiagnostic()
    {
        var capturePath = Environment.GetEnvironmentVariable("KAKAOTALK_GPU_CONTROL_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        using var slider = new StrengthSliderForm(75, true, _ => { }, _ => { }, () => { });
        slider.SaveDiagnosticImage(capturePath);
    }
}

internal sealed class GpuInvertApplication : ApplicationContext
{
    private const int SafetyRefreshIntervalMs = 5000;
    private const int StartupRefreshIntervalMs = 100;
    private const int WaitingRefreshIntervalMs = 1000;
    private const int WindowEventDebounceIntervalMs = 50;
    private const int PipelineRetryBaseIntervalMs = 2000;
    private const int PipelineRetryMaximumIntervalMs = 30000;
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _windowEventTimer;
    private readonly OverlayWindow _overlay = new();
    private readonly WindowEventMonitor _windowEventMonitor;
    private readonly StrengthSliderForm _strengthSlider;
    private readonly Control _uiDispatcher = new();
    private ToolStripMenuItem? _enabledMenuItem;
    private ToolStripMenuItem? _privacyModeMenuItem;
    private ToolStripMenuItem? _strengthMenuItem;
    private ToolStripMenuItem? _showStrengthControlMenuItem;
    private ToolStripMenuItem? _statusMenuItem;
    private GpuRenderer? _renderer;
    private WgcCaptureSession? _capture;
    private nint _targetHandle;
    private bool _enabled = true;
    private bool _privacyModeEnabled;
    private bool _showStrengthControl;
    private int _invertStrength = 100;
    private int _windowRefreshQueued;
    private int _pipelineFailureCount;
    private nint _failedTargetHandle;
    private DateTimeOffset _nextPipelineRetryAt;
    private string? _pipelineFailureStatus;
    private string? _lastLoggedState;
    private string? _lastDisplayedStatus;
    private DateTimeOffset _lastLogAt;

    public GpuInvertApplication()
    {
        var settings = GpuInvertSettings.Load();
        _enabled = settings.Enabled;
        _privacyModeEnabled = settings.PrivacyModeEnabled;
        _invertStrength = Math.Clamp(settings.InvertStrength, 0, 100);
        _showStrengthControl = settings.ShowStrengthControl;
        _uiDispatcher.CreateControl();
        _windowEventTimer = new System.Windows.Forms.Timer
        {
            Interval = WindowEventDebounceIntervalMs
        };
        _windowEventTimer.Tick += (_, _) =>
        {
            _windowEventTimer.Stop();
            Refresh();
        };
        _strengthSlider = new StrengthSliderForm(
            _invertStrength,
            _enabled,
            SetInvertStrength,
            SetEnabled,
            () => SetStrengthControlVisible(false));
        _overlay.PrivacyHotKeyPressed += TogglePrivacyMode;
        _ = _overlay.Handle;
        _windowEventMonitor = new WindowEventMonitor(QueueWindowRefresh);
        _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ??
            (Icon)SystemIcons.Application.Clone();
        _trayIcon = new NotifyIcon
        {
            Text = "KakaoTalk GPU Invert Spike",
            Icon = _applicationIcon,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _timer = new System.Windows.Forms.Timer { Interval = StartupRefreshIntervalMs };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _windowEventTimer.Stop();
            _windowEventTimer.Dispose();
            _windowEventMonitor.Dispose();
            GpuInvertSettings.Save(new GpuInvertSettings
            {
                Enabled = _enabled,
                PrivacyModeEnabled = _privacyModeEnabled,
                InvertStrength = _invertStrength,
                ShowStrengthControl = _showStrengthControl
            });
            _strengthSlider.Dispose();
            DisposePipeline();
            _uiDispatcher.Dispose();
            _overlay.PrivacyHotKeyPressed -= TogglePrivacyMode;
            _overlay.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _applicationIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _enabledMenuItem = new ToolStripMenuItem("Enabled")
        {
            Checked = _enabled,
            CheckOnClick = true
        };
        _enabledMenuItem.CheckedChanged += (_, _) => SetEnabled(_enabledMenuItem.Checked);
        menu.Items.Add(_enabledMenuItem);
        _privacyModeMenuItem = new ToolStripMenuItem("Privacy mode (Ctrl+H)")
        {
            Checked = _privacyModeEnabled,
            CheckOnClick = true
        };
        _privacyModeMenuItem.CheckedChanged += (_, _) =>
        {
            _privacyModeEnabled = _privacyModeMenuItem.Checked;
            ApplyRenderSettings();
        };
        menu.Items.Add(_privacyModeMenuItem);
        _strengthMenuItem = new ToolStripMenuItem($"Invert strength: {_invertStrength}%");
        _strengthMenuItem.Click += (_, _) => SetStrengthControlVisible(true);
        menu.Items.Add(_strengthMenuItem);
        _showStrengthControlMenuItem = new ToolStripMenuItem("Show strength control")
        {
            Checked = _showStrengthControl,
            CheckOnClick = true
        };
        _showStrengthControlMenuItem.CheckedChanged += (_, _) =>
            SetStrengthControlVisible(_showStrengthControlMenuItem.Checked);
        menu.Items.Add(_showStrengthControlMenuItem);
        _statusMenuItem = new ToolStripMenuItem("Starting GPU pipeline") { Enabled = false };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void Refresh()
    {
        if (!_enabled)
        {
            _windowEventMonitor.Detach();
            if (_targetHandle == nint.Zero ||
                !KakaoTalkWindowFinder.TryGetWindowSnapshot(_targetHandle, out _))
            {
                DisposePipeline();
            }
            else
            {
                _capture?.SetRenderingEnabled(false);
            }

            _overlay.Hide();
            if (!_showStrengthControl)
            {
                _strengthSlider.Hide();
            }
            SetStatus("Disabled");
            return;
        }

        var target = _targetHandle != nint.Zero &&
            KakaoTalkWindowFinder.TryGetWindowSnapshot(_targetHandle, out var cachedTarget)
                ? cachedTarget
                : KakaoTalkWindowFinder.FindMainWindow();
        if (target is null)
        {
            if (_timer.Interval != WaitingRefreshIntervalMs)
            {
                _timer.Interval = WaitingRefreshIntervalMs;
            }

            _windowEventMonitor.Detach();
            DisposePipeline();
            _overlay.Hide();
            _strengthSlider.Hide();
            SetStatus("Waiting for KakaoTalk");
            return;
        }

        if (_failedTargetHandle != nint.Zero && _failedTargetHandle != target.Value.Handle)
        {
            ResetPipelineRetry();
        }

        NativeMethods.GetWindowThreadProcessId(target.Value.Handle, out var targetProcessId);
        _windowEventMonitor.Attach(targetProcessId, target.Value.Handle);

        if (_targetHandle != target.Value.Handle || _capture is null || _renderer is null)
        {
            var now = DateTimeOffset.UtcNow;
            if (_failedTargetHandle == target.Value.Handle && now < _nextPipelineRetryAt)
            {
                _overlay.Hide();
                if (_showStrengthControl)
                {
                    _strengthSlider.ShowNear(target.Value);
                }
                else
                {
                    _strengthSlider.Hide();
                }

                _timer.Interval = WaitingRefreshIntervalMs;
                var retrySeconds = Math.Max(
                    1,
                    (int)Math.Ceiling((_nextPipelineRetryAt - now).TotalSeconds));
                SetStatus(
                    $"GPU pipeline retry pending | {retrySeconds}s | {_pipelineFailureStatus}");
                return;
            }

            StartPipeline(target.Value);
        }

        if (_capture is null || _renderer is null)
        {
            return;
        }

        if (_capture.IsFaulted)
        {
            SchedulePipelineRetry(target.Value, _capture.Status);
            return;
        }

        try
        {
            _capture.SetRenderingEnabled(true);
            _renderer.ResizeOutput(target.Value.Width, target.Value.Height);
            _renderer.UpdateSettings(
                _invertStrength,
                _privacyModeEnabled,
                GetDpiScale(target.Value.Handle));
            _overlay.Position(target.Value, _capture.HasPresentedFrame);
            if (_showStrengthControl)
            {
                _strengthSlider.ShowNear(target.Value);
            }
            else
            {
                _strengthSlider.Hide();
            }

            if (_capture.HasPresentedFrame &&
                _overlay.IsVisible &&
                _timer.Interval != SafetyRefreshIntervalMs)
            {
                _timer.Interval = SafetyRefreshIntervalMs;
            }
            SetStatus(_capture.HasPresentedFrame
                ? $"{_capture.Status} | {_capture.FramesPerSecond:F1} FPS | {_capture.TotalFrames} frames"
                : _capture.Status);
        }
        catch (Exception exception)
        {
            SchedulePipelineRetry(
                target.Value,
                $"Pipeline error: {exception.GetType().Name}");
        }
    }

    private void StartPipeline(TargetWindow target)
    {
        DisposePipeline();
        _timer.Interval = StartupRefreshIntervalMs;
        _overlay.Position(target, false);
        try
        {
            _renderer = new GpuRenderer(
                _overlay.Handle,
                target.Width,
                target.Height,
                _invertStrength,
                _privacyModeEnabled,
                GetDpiScale(target.Handle));
            _targetHandle = target.Handle;
            _capture = new WgcCaptureSession(
                _renderer,
                () => QueueFirstFrameDisplay(target.Handle));
            _capture.Start(target.Handle);
            ResetPipelineRetry();
            SetStatus("Waiting for first GPU frame");
        }
        catch (Exception exception)
        {
            SchedulePipelineRetry(
                target,
                $"Start error: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void SchedulePipelineRetry(TargetWindow target, string status)
    {
        SetStatus(status);
        DisposePipeline();
        _overlay.Hide();
        _pipelineFailureCount = _failedTargetHandle == target.Handle
            ? _pipelineFailureCount + 1
            : 1;
        _failedTargetHandle = target.Handle;
        _pipelineFailureStatus = status;
        var retryMultiplier = 1 << Math.Min(_pipelineFailureCount - 1, 4);
        var retryMilliseconds = Math.Min(
            PipelineRetryMaximumIntervalMs,
            PipelineRetryBaseIntervalMs * retryMultiplier);
        _nextPipelineRetryAt = DateTimeOffset.UtcNow.AddMilliseconds(retryMilliseconds);
        _timer.Interval = WaitingRefreshIntervalMs;

        if (_showStrengthControl)
        {
            _strengthSlider.ShowNear(target);
        }
        else
        {
            _strengthSlider.Hide();
        }
    }

    private void ResetPipelineRetry()
    {
        _pipelineFailureCount = 0;
        _failedTargetHandle = nint.Zero;
        _nextPipelineRetryAt = default;
        _pipelineFailureStatus = null;
    }

    private void DisposePipeline()
    {
        _capture?.Dispose();
        _capture = null;
        _renderer?.Dispose();
        _renderer = null;
        _targetHandle = nint.Zero;
    }

    private void QueueFirstFrameDisplay(nint expectedTargetHandle)
    {
        if (_uiDispatcher.IsDisposed || !_uiDispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                if (_targetHandle != expectedTargetHandle ||
                    !KakaoTalkWindowFinder.TryGetWindowSnapshot(expectedTargetHandle, out var target))
                {
                    return;
                }

                _overlay.Position(target, true);
                if (_timer.Interval != SafetyRefreshIntervalMs)
                {
                    _timer.Interval = SafetyRefreshIntervalMs;
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // Shutdown can race with the free-threaded WGC callback.
        }
    }

    private void TogglePrivacyMode()
    {
        if (_privacyModeMenuItem is not null)
        {
            _privacyModeMenuItem.Checked = !_privacyModeMenuItem.Checked;
            return;
        }

        _privacyModeEnabled = !_privacyModeEnabled;
        ApplyRenderSettings();
    }

    private void QueueWindowRefresh()
    {
        if (Interlocked.Exchange(ref _windowRefreshQueued, 1) != 0)
        {
            return;
        }

        if (_uiDispatcher.IsDisposed || !_uiDispatcher.IsHandleCreated)
        {
            Interlocked.Exchange(ref _windowRefreshQueued, 0);
            return;
        }

        try
        {
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _windowRefreshQueued, 0);
                if (!_windowEventTimer.Enabled)
                {
                    _windowEventTimer.Start();
                }
            }));
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _windowRefreshQueued, 0);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _windowRefreshQueued, 0);
        }
    }

    private void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            _strengthSlider.SetEnabled(enabled);
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            ResetPipelineRetry();
        }

        if (_enabledMenuItem is not null && _enabledMenuItem.Checked != enabled)
        {
            _enabledMenuItem.Checked = enabled;
        }

        _strengthSlider.SetEnabled(enabled);
        Refresh();
    }

    private void SetStrengthControlVisible(bool visible)
    {
        if (_showStrengthControl == visible)
        {
            if (visible)
            {
                _strengthSlider.ShowControl();
            }

            return;
        }

        _showStrengthControl = visible;
        if (_showStrengthControlMenuItem is not null &&
            _showStrengthControlMenuItem.Checked != visible)
        {
            _showStrengthControlMenuItem.Checked = visible;
        }

        if (!visible)
        {
            _strengthSlider.Hide();
            return;
        }

        Refresh();
        _strengthSlider.ShowControl();
    }

    private void SetInvertStrength(int strength)
    {
        _invertStrength = Math.Clamp(strength, 0, 100);
        if (_strengthMenuItem is not null)
        {
            _strengthMenuItem.Text = $"Invert strength: {_invertStrength}%";
        }

        ApplyRenderSettings();
    }

    private void ApplyRenderSettings()
    {
        _renderer?.UpdateSettings(
            _invertStrength,
            _privacyModeEnabled,
            GetDpiScale(_targetHandle));
    }

    private static float GetDpiScale(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return 1;
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1 : dpi / 96f;
    }

    private void SetStatus(string status)
    {
        if (_statusMenuItem is not null && !string.Equals(status, _lastDisplayedStatus, StringComparison.Ordinal))
        {
            _statusMenuItem.Text = status;
            _lastDisplayedStatus = status;
        }

        var separatorIndex = status.IndexOf('|');
        var state = (separatorIndex >= 0 ? status[..separatorIndex] : status).Trim();
        var now = DateTimeOffset.Now;
        if (string.Equals(state, _lastLoggedState, StringComparison.Ordinal) &&
            now - _lastLogAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastLoggedState = state;
        _lastLogAt = now;
        AppDiagnostics.WriteStatus(status);
    }
}
