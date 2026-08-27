using System.Windows.Forms;

namespace KakaoTalkGpuInvertSpike;

internal static class Program
{
    private const string InstanceMutexName = @"Local\KakaoTalkGpuInvertSpike.Singleton";
    private const string InstanceRefreshEventName = @"Local\KakaoTalkGpuInvertSpike.Refresh";

    [STAThread]
    private static void Main()
    {
        using var instanceRefreshEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            InstanceRefreshEventName);
        using var instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            instanceRefreshEvent.Set();
            return;
        }

        AppDiagnostics.Initialize();
        var shutdownReason = "graceful";
        try
        {
            _ = NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
            ApplicationConfiguration.Initialize();

            if (string.Equals(
                Environment.GetEnvironmentVariable("KAKAOTALK_GPU_OVERLAY_SMOKE_TEST"),
                "1",
                StringComparison.Ordinal))
            {
                using var smokeTest = new OverlaySmokeTestApplication();
                Application.Run(smokeTest);
                return;
            }

            using var application = new GpuInvertApplication(instanceRefreshEvent);
            Application.Run(application);
        }
        catch (Exception exception)
        {
            shutdownReason = "fatal main-loop exception";
            AppDiagnostics.WriteException("Fatal main-loop exception", exception);
        }
        finally
        {
            AppDiagnostics.CompleteSession(
                shutdownReason,
                string.Equals(shutdownReason, "graceful", StringComparison.Ordinal));
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException exception)
            {
                AppDiagnostics.WriteException("Singleton mutex release error", exception);
            }
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

        using var slider = new StrengthSliderForm(
            75,
            true,
            true,
            true,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            () => { },
            () => { });
        slider.SetPeekActive(true);
        slider.SaveDiagnosticImage(capturePath);
    }
}

internal sealed class GpuInvertApplication : ApplicationContext
{
    private const int SafetyRefreshIntervalMs = 5000;
    private const int StartupRefreshIntervalMs = 100;
    private const int WaitingRefreshIntervalMs = 1000;
    private const int WindowEventDebounceIntervalMs = 100;
    private const int PipelineRetryBaseIntervalMs = 2000;
    private const int PipelineRetryMaximumIntervalMs = 30000;
    private const int PeekDurationMs = 8000;
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _windowEventTimer;
    private readonly System.Windows.Forms.Timer _peekTimer;
    private readonly System.Windows.Forms.Timer? _automaticExitTimer;
    private readonly RegisteredWaitHandle _instanceRefreshRegistration;
    private readonly OverlayWindow _overlay = new();
    private readonly PrivacyMaskOverlay _privacyMask = new();
    private readonly WindowEventMonitor _windowEventMonitor;
    private readonly StrengthSliderForm _strengthSlider;
    private readonly Control _uiDispatcher = new();
    private ToolStripMenuItem? _enabledMenuItem;
    private ToolStripMenuItem? _privacyModeMenuItem;
    private ToolStripMenuItem? _autoPrivacyMenuItem;
    private ToolStripMenuItem? _peekMenuItem;
    private ToolStripMenuItem? _strengthMenuItem;
    private ToolStripMenuItem? _showStrengthControlMenuItem;
    private ToolStripMenuItem? _statusMenuItem;
    private GpuRenderer? _renderer;
    private WgcCaptureSession? _capture;
    private nint _targetHandle;
    private bool _enabled = true;
    private bool _privacyModeEnabled;
    private bool _autoPrivacyEnabled;
    private bool _peekActive;
    private nint _peekForegroundAnchor;
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
    private bool _refreshInProgress;
    private bool _shuttingDown;

    public GpuInvertApplication(EventWaitHandle instanceRefreshEvent)
    {
        var settings = GpuInvertSettings.Load();
        _enabled = settings.Enabled;
        _privacyModeEnabled = settings.PrivacyModeEnabled;
        _autoPrivacyEnabled = settings.AutoPrivacyEnabled;
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
            Interlocked.Exchange(ref _windowRefreshQueued, 0);
            Refresh();
        };
        _peekTimer = new System.Windows.Forms.Timer { Interval = PeekDurationMs };
        _peekTimer.Tick += (_, _) => EndPeek(true);
        _strengthSlider = new StrengthSliderForm(
            _invertStrength,
            _enabled,
            _privacyModeEnabled,
            _autoPrivacyEnabled,
            SetInvertStrength,
            SetEnabled,
            SetPrivacyMode,
            SetAutoPrivacy,
            TogglePeekFromControl,
            () => SetStrengthControlVisible(false));
        _overlay.PrivacyHotKeyPressed += TogglePrivacyMode;
        _overlay.PeekHotKeyPressed += TogglePeek;
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
        _automaticExitTimer = CreateAutomaticExitTimer();
        _instanceRefreshRegistration = ThreadPool.RegisterWaitForSingleObject(
            instanceRefreshEvent,
            (_, _) => QueueInstanceRefresh(),
            null,
            Timeout.Infinite,
            false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shuttingDown = true;
            _instanceRefreshRegistration.Unregister(null);
            _timer.Stop();
            _timer.Dispose();
            _windowEventTimer.Stop();
            _windowEventTimer.Dispose();
            _peekTimer.Stop();
            _peekTimer.Dispose();
            _automaticExitTimer?.Stop();
            _automaticExitTimer?.Dispose();
            _windowEventMonitor.Dispose();
            GpuInvertSettings.Save(new GpuInvertSettings
            {
                Enabled = _enabled,
                PrivacyModeEnabled = _privacyModeEnabled,
                AutoPrivacyEnabled = _autoPrivacyEnabled,
                InvertStrength = _invertStrength,
                ShowStrengthControl = _showStrengthControl
            });
            DisposePipeline();
            _privacyMask.Dispose();
            _strengthSlider.Dispose();
            _uiDispatcher.Dispose();
            _overlay.PrivacyHotKeyPressed -= TogglePrivacyMode;
            _overlay.PeekHotKeyPressed -= TogglePeek;
            _overlay.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _applicationIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private System.Windows.Forms.Timer? CreateAutomaticExitTimer()
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable("KAKAOTALK_GPU_AUTO_EXIT_MS"),
                out var interval))
        {
            return null;
        }

        var timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(interval, 1000, 120000)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ExitThread();
        };
        timer.Start();
        return timer;
    }

    private void QueueInstanceRefresh()
    {
        if (_shuttingDown || _uiDispatcher.IsDisposed || !_uiDispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                if (_shuttingDown)
                {
                    return;
                }

                AppDiagnostics.WriteStatus("Existing instance requested an immediate refresh");
                ResetPipelineRetry();
                Refresh();
            }));
        }
        catch (ObjectDisposedException)
        {
            // Shutdown can race with the named refresh event.
        }
        catch (InvalidOperationException)
        {
            // Shutdown can race with the named refresh event.
        }
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
        _privacyModeMenuItem = new ToolStripMenuItem("Full privacy (Ctrl+H)")
        {
            Checked = _privacyModeEnabled,
            CheckOnClick = true
        };
        _privacyModeMenuItem.CheckedChanged += (_, _) =>
            SetPrivacyMode(_privacyModeMenuItem.Checked);
        menu.Items.Add(_privacyModeMenuItem);
        _autoPrivacyMenuItem = new ToolStripMenuItem("Auto privacy when unfocused")
        {
            Checked = _autoPrivacyEnabled,
            CheckOnClick = true
        };
        _autoPrivacyMenuItem.CheckedChanged += (_, _) =>
            SetAutoPrivacy(_autoPrivacyMenuItem.Checked);
        menu.Items.Add(_autoPrivacyMenuItem);
        _peekMenuItem = new ToolStripMenuItem("Focus reveal for 8 seconds (Ctrl+Shift+H)");
        _peekMenuItem.Click += (_, _) => TogglePeek();
        menu.Items.Add(_peekMenuItem);
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
        if (_shuttingDown || _refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            RefreshCore();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Refresh recovery", exception);
            _windowEventMonitor.Detach();
            DisposePipeline();
            _overlay.Hide();
            _privacyMask.Hide();
            _strengthSlider.Hide();
            _timer.Interval = WaitingRefreshIntervalMs;
            SetStatus($"Recovering: {exception.GetType().Name}");
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void RefreshCore()
    {
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
            _privacyMask.Hide();
            _strengthSlider.Hide();
            EndPeek(false);
            SetStatus("Waiting for KakaoTalk");
            return;
        }

        if (_failedTargetHandle != nint.Zero && _failedTargetHandle != target.Value.Handle)
        {
            ResetPipelineRetry();
        }

        NativeMethods.GetWindowThreadProcessId(target.Value.Handle, out var targetProcessId);
        _windowEventMonitor.Attach(targetProcessId, target.Value.Handle);
        var targetFocused = IsTargetForeground(target.Value.Handle);
        if (_peekActive && !IsPeekContextValid(targetFocused))
        {
            EndPeek(false);
        }

        var effectivePrivacy = GetEffectivePrivacy(targetFocused);
        var dpiScale = GetDpiScale(target.Value.Handle);
        _strengthSlider.SetPeekActive(_peekActive);

        if (!_enabled)
        {
            _capture?.SetRenderingEnabled(false);
            _overlay.Hide();
            _privacyMask.Position(target.Value, dpiScale, effectivePrivacy);
            UpdateStrengthControl(target.Value);
            _timer.Interval = SafetyRefreshIntervalMs;
            SetStatus(effectivePrivacy ? "GPU disabled | Privacy active" : "GPU disabled");
            return;
        }

        if (_targetHandle != target.Value.Handle || _capture is null || _renderer is null)
        {
            var now = DateTimeOffset.UtcNow;
            if (_failedTargetHandle == target.Value.Handle && now < _nextPipelineRetryAt)
            {
                _overlay.Hide();
                _privacyMask.Position(target.Value, dpiScale, effectivePrivacy);
                UpdateStrengthControl(target.Value);

                _timer.Interval = WaitingRefreshIntervalMs;
                var retrySeconds = Math.Max(
                    1,
                    (int)Math.Ceiling((_nextPipelineRetryAt - now).TotalSeconds));
                SetStatus(
                    $"GPU pipeline retry pending | {retrySeconds}s | {_pipelineFailureStatus}");
                return;
            }

            StartPipeline(target.Value, effectivePrivacy);
        }

        if (_capture is null || _renderer is null)
        {
            _privacyMask.Position(target.Value, dpiScale, effectivePrivacy);
            return;
        }

        if (_capture.IsFaulted)
        {
            SchedulePipelineRetry(target.Value, _capture.Status);
            _privacyMask.Position(target.Value, dpiScale, effectivePrivacy);
            return;
        }

        try
        {
            _capture.SetRenderingEnabled(true);
            _renderer.ResizeOutput(target.Value.Width, target.Value.Height);
            _renderer.UpdateSettings(
                _invertStrength,
                effectivePrivacy,
                dpiScale);
            _overlay.Position(target.Value, _capture.HasPresentedFrame);
            if (_capture.HasPresentedFrame)
            {
                _privacyMask.Hide();
            }
            else
            {
                _privacyMask.Position(target.Value, dpiScale, effectivePrivacy);
            }

            UpdateStrengthControl(target.Value);

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
            _privacyMask.Position(target.Value, dpiScale, effectivePrivacy);
        }
    }

    private void StartPipeline(TargetWindow target, bool effectivePrivacy)
    {
        DisposePipeline();
        _timer.Interval = StartupRefreshIntervalMs;
        try
        {
            _overlay.Position(target, false);
            _renderer = new GpuRenderer(
                _overlay.Handle,
                target.Width,
                target.Height,
                _invertStrength,
                effectivePrivacy,
                GetDpiScale(target.Handle));
            _targetHandle = target.Handle;
            _capture = new WgcCaptureSession(
                _renderer,
                () => QueueFirstFrameDisplay(target.Handle),
                QueueWindowRefresh);
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
        _targetHandle = nint.Zero;
        var capture = _capture;
        _capture = null;
        try
        {
            capture?.Dispose();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Capture disposal error", exception);
        }

        var renderer = _renderer;
        _renderer = null;
        try
        {
            renderer?.Dispose();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Renderer disposal error", exception);
        }
    }

    private void QueueFirstFrameDisplay(nint expectedTargetHandle)
    {
        if (_shuttingDown || _uiDispatcher.IsDisposed || !_uiDispatcher.IsHandleCreated)
        {
            return;
        }

        try
        {
            _uiDispatcher.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (_shuttingDown ||
                        _targetHandle != expectedTargetHandle ||
                        !KakaoTalkWindowFinder.TryGetWindowSnapshot(expectedTargetHandle, out var target))
                    {
                        return;
                    }

                    var targetFocused = IsTargetForeground(target.Handle);
                    if (_peekActive && !IsPeekContextValid(targetFocused))
                    {
                        EndPeek(false);
                    }

                    _renderer?.UpdateSettings(
                        _invertStrength,
                        GetEffectivePrivacy(targetFocused),
                        GetDpiScale(target.Handle));
                    _overlay.Position(target, true);
                    _privacyMask.Hide();
                    if (_timer.Interval != SafetyRefreshIntervalMs)
                    {
                        _timer.Interval = SafetyRefreshIntervalMs;
                    }
                }
                catch (Exception exception)
                {
                    AppDiagnostics.WriteException("First-frame display recovery", exception);
                    QueueWindowRefresh();
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
        SetPrivacyMode(!_privacyModeEnabled);
    }

    private void TogglePeek()
    {
        TogglePeek(false);
    }

    private void TogglePeekFromControl()
    {
        TogglePeek(true);
    }

    private void TogglePeek(bool allowUnfocusedControlRequest)
    {
        if (_peekActive)
        {
            EndPeek(true);
            return;
        }

        var target = _targetHandle != nint.Zero &&
            KakaoTalkWindowFinder.TryGetWindowSnapshot(_targetHandle, out var cachedTarget)
                ? cachedTarget
                : KakaoTalkWindowFinder.FindMainWindow();
        if (target is null)
        {
            SetStatus("Focus reveal unavailable: KakaoTalk not found");
            return;
        }

        if (!allowUnfocusedControlRequest && !IsTargetForeground(target.Value.Handle))
        {
            SetStatus("Focus reveal blocked while KakaoTalk is unfocused");
            return;
        }

        if (!_privacyModeEnabled)
        {
            SetStatus("Focus reveal requires Full privacy");
            return;
        }

        _peekActive = true;
        _peekForegroundAnchor = NativeMethods.GetForegroundWindow();
        _peekTimer.Stop();
        _peekTimer.Start();
        UpdatePeekUi();
        AppDiagnostics.WriteStatus("Focus reveal started | 8 seconds");
        Refresh();
    }

    private void EndPeek(bool refresh)
    {
        if (!_peekActive)
        {
            return;
        }

        _peekActive = false;
        _peekForegroundAnchor = nint.Zero;
        _peekTimer.Stop();
        UpdatePeekUi();
        AppDiagnostics.WriteStatus("Focus reveal ended");
        if (refresh)
        {
            Refresh();
        }
    }

    private void UpdatePeekUi()
    {
        _strengthSlider.SetPeekActive(_peekActive);
        if (_peekMenuItem is not null)
        {
            _peekMenuItem.Text = _peekActive
                ? "End focus reveal now (Ctrl+Shift+H)"
                : "Focus reveal for 8 seconds (Ctrl+Shift+H)";
        }
    }

    private void QueueWindowRefresh()
    {
        if (_shuttingDown)
        {
            return;
        }

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
                if (_shuttingDown)
                {
                    Interlocked.Exchange(ref _windowRefreshQueued, 0);
                    return;
                }

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

    private void SetPrivacyMode(bool enabled)
    {
        var changed = _privacyModeEnabled != enabled;
        _privacyModeEnabled = enabled;
        if (!enabled)
        {
            EndPeek(false);
        }

        if (_privacyModeMenuItem is not null && _privacyModeMenuItem.Checked != enabled)
        {
            _privacyModeMenuItem.Checked = enabled;
        }

        _strengthSlider.SetPrivacyMode(enabled);
        if (changed)
        {
            Refresh();
        }
    }

    private void SetAutoPrivacy(bool enabled)
    {
        var changed = _autoPrivacyEnabled != enabled;
        _autoPrivacyEnabled = enabled;
        if (_autoPrivacyMenuItem is not null && _autoPrivacyMenuItem.Checked != enabled)
        {
            _autoPrivacyMenuItem.Checked = enabled;
        }

        _strengthSlider.SetAutoPrivacy(enabled);
        if (changed)
        {
            Refresh();
        }
    }

    private void ApplyRenderSettings()
    {
        try
        {
            _renderer?.UpdateSettings(
                _invertStrength,
                GetEffectivePrivacy(IsTargetForeground(_targetHandle)),
                GetDpiScale(_targetHandle));
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("Render setting update recovery", exception);
            QueueWindowRefresh();
        }
    }

    private void UpdateStrengthControl(TargetWindow target)
    {
        if (_showStrengthControl)
        {
            _strengthSlider.ShowNear(target);
        }
        else
        {
            _strengthSlider.Hide();
        }
    }

    private bool GetEffectivePrivacy(bool targetFocused)
    {
        return !_peekActive &&
            (_privacyModeEnabled || (_autoPrivacyEnabled && !targetFocused));
    }

    private bool IsPeekContextValid(bool targetFocused)
    {
        if (targetFocused)
        {
            return true;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        return foreground != nint.Zero && foreground == _peekForegroundAnchor;
    }

    private bool IsTargetForeground(nint targetHandle)
    {
        if (targetHandle == nint.Zero)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == targetHandle ||
            (_strengthSlider.IsHandleCreated && foreground == _strengthSlider.Handle) ||
            (foreground != nint.Zero &&
                NativeMethods.GetAncestor(foreground, NativeMethods.GaRootOwner) == targetHandle);
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
