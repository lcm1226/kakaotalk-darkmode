using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Interop;
using KakaoTalkFilterLab.Models;
using KakaoTalkFilterLab.Native;
using KakaoTalkFilterLab.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace KakaoTalkFilterLab;

public partial class MainWindow : Window
{
    private const double DimOpacityMinimum = 0;
    private const double DimOpacityMaximum = 200;
    private const double FilterStrengthMinimum = 0;
    private const double FilterStrengthMaximum = 100;
    private const double BrightnessMinimum = -100;
    private const double BrightnessMaximum = 100;
    private const double ContrastMinimum = 50;
    private const double ContrastMaximum = 170;
    private const double GammaMinimum = 60;
    private const double GammaMaximum = 180;
    private const double DefaultDimOpacity = 115;
    private const double DefaultFilterStrength = 100;
    private const double DefaultBrightness = 0;
    private const double DefaultContrast = 100;
    private const double DefaultGamma = 100;
    private const double DefaultSmartStrength = 100;
    private const double DefaultSmartBrightness = 50;
    private const double DefaultSmartContrast = 120;
    private const double DefaultSmartGamma = 90;
    private const int DefaultModeIndex = 0;
    private const int PrivacyModeHotKeyId = 0x4B48;
    private static readonly TimeSpan MinimumInvertProcessInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan MinimumSmartProcessInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan WindowDiscoveryInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;
    private readonly KakaoTalkWindowFinder _windowFinder = new();
    private static readonly bool AutoExportFramesEnabled = false;

    private readonly WindowCaptureService _captureService = new();
    private readonly WindowsGraphicsCaptureService _windowsGraphicsCaptureService = new();
    private readonly OverlayWindow _overlayWindow = new();
    private readonly string _settingsPath = GetSettingsPath();
    private bool _isUiReady;
    private WindowInfo? _lastOverlayWindowInfo;
    private nint _overlayOwnerHandle;
    private DateTime _lastAutoExportUtc = DateTime.MinValue;
    private bool _isUpdatingParameterUi;
    private bool _isExitRequested;
    private Forms.NotifyIcon? _trayIcon;
    private double _dimOpacityValue = DefaultDimOpacity;
    private bool _isPrivacyModeEnabled;
    private bool _isFrameProcessing;
    private int _filterPipelineVersion;
    private DateTime _lastCandidateGridUpdateUtc = DateTime.MinValue;
    private DateTime _lastFilterProcessFinishedUtc = DateTime.MinValue;
    private BitmapSource? _lastProcessedFrame;
    private WindowInfo? _cachedMainWindow;
    private DateTime _lastWindowDiscoveryUtc = DateTime.MinValue;
    private readonly FilterTuning _invertTuning = new();
    private readonly FilterTuning _smartTuning = new()
    {
        Strength = DefaultSmartStrength,
        Brightness = DefaultSmartBrightness,
        Contrast = DefaultSmartContrast,
        Gamma = DefaultSmartGamma
    };

    public MainWindow()
    {
        InitializeComponent();
        LoadPersistedState();

        OverlayEnabledCheckBox.Checked += OverlayEnabledChanged;
        OverlayEnabledCheckBox.Unchecked += OverlayEnabledChanged;
        PrivacyModeCheckBox.Checked += PrivacyModeChanged;
        PrivacyModeCheckBox.Unchecked += PrivacyModeChanged;
        OpacitySlider.ValueChanged += OpacitySliderChanged;
        StrengthSlider.ValueChanged += FilterSliderChanged;
        BrightnessSlider.ValueChanged += FilterSliderChanged;
        ContrastSlider.ValueChanged += FilterSliderChanged;
        GammaSlider.ValueChanged += FilterSliderChanged;
        ModeComboBox.SelectionChanged += ModeChanged;
        ExportFramesButton.Click += ExportFramesClicked;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _timer.Tick += OnTick;

        Loaded += (_, _) =>
        {
            _isUiReady = true;
            PrivacyModeCheckBox.IsChecked = _isPrivacyModeEnabled;
            ConfigureParameterSlider();
            Refresh();
            _timer.Start();
        };

        Closed += (_, _) =>
        {
            SavePersistedState();
            _timer.Stop();
            UnregisterPrivacyModeHotKey();
            _trayIcon?.Dispose();
            _windowsGraphicsCaptureService.Dispose();
            _overlayWindow.Close();
        };

        Closing += MainWindowClosing;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SourceInitialized += OnSourceInitialized;
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "KakaoTalk Filter Lab",
            Icon = Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void MainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _trayIcon?.ShowBalloonTip(1200, "KakaoTalk Filter Lab", "Still running in the system tray.", Forms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        Close();
    }
    private void OnTick(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            ApplySystemTheme();
        }
    }

    private void ApplySystemTheme()
    {
        var isDark = IsSystemAppThemeDark();
        Resources["AppWindowBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#171A1F" : "#EEF1F4"));
        Resources["AppTextBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#F2F4F7" : "#111827"));
        Resources["AppSecondaryTextBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#AAB2C0" : "#667085"));
        Resources["AppGridBackgroundBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#1F232A" : "#F6F8FA"));
        Resources["AppGridBorderBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#343A46" : "#D4D9E1"));
        Resources["AppGridRowBrush"] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isDark ? "#1F232A" : "#F6F8FA"));

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
        {
            Win32.SetImmersiveDarkMode(hwnd, isDark);
        }
    }

    private static bool IsSystemAppThemeDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        if (!_isUiReady)
        {
            return;
        }

        var mainWindow = GetMainWindowSnapshot();

        if (mainWindow is null)
        {
            SetStatus("No main window matched", null);
            SetTextIfChanged(CaptureValue, "-");
            HideOverlay();
            return;
        }

        SetStatus("Main window detected", mainWindow);
        UpdateOverlay(mainWindow);
    }


    private WindowInfo? GetMainWindowSnapshot()
    {
        var nowUtc = DateTime.UtcNow;
        if (_cachedMainWindow is not null &&
            nowUtc - _lastWindowDiscoveryUtc < WindowDiscoveryInterval &&
            Win32.TryGetWindowInfo(_cachedMainWindow.Handle, out var cachedWindow) &&
            cachedWindow is not null &&
            cachedWindow.Width > 200 &&
            cachedWindow.Height > 200)
        {
            _cachedMainWindow = cachedWindow;
            return cachedWindow;
        }

        var candidates = _windowFinder.GetCandidates();
        _lastWindowDiscoveryUtc = nowUtc;
        UpdateCandidateWindows(candidates);

        _cachedMainWindow = _windowFinder.FindMainWindow(candidates);
        return _cachedMainWindow;
    }
    private void UpdateOverlay(WindowInfo targetWindow)
    {
        if (OverlayEnabledCheckBox.IsChecked != true)
        {
            HideOverlay();
            return;
        }

        var helper = new System.Windows.Interop.WindowInteropHelper(_overlayWindow);
        var overlayHwnd = helper.EnsureHandle();

        if (_overlayOwnerHandle != targetWindow.Handle)
        {
            helper.Owner = targetWindow.Handle;
            _overlayOwnerHandle = targetWindow.Handle;
        }

        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
        }

        _overlayWindow.SetPrivacyMode(_isPrivacyModeEnabled);

        var shouldShowWindow = _lastOverlayWindowInfo is null;
        if (_lastOverlayWindowInfo is null || _lastOverlayWindowInfo != targetWindow)
        {
            _ = Win32.MoveOverlayToBounds(
                overlayHwnd,
                targetWindow.X,
                targetWindow.Y,
                targetWindow.Width,
                targetWindow.Height,
                shouldShowWindow);

            _lastOverlayWindowInfo = targetWindow;
        }

        ApplyCurrentMode(targetWindow);
    }

    private void ApplyCurrentMode(WindowInfo targetWindow)
    {
        var mode = GetSelectedMode();

        if (mode == OverlayMode.Dim)
        {
            InvalidateFilterPipeline();
            _windowsGraphicsCaptureService.Stop();
            SetTextIfChanged(CaptureValue, "Dim overlay");
            _overlayWindow.ApplyDim(GetCurrentDimOpacity());
            return;
        }

        var capturedFrame = CaptureCurrentFrame(targetWindow, out var captureStatus);
        SetTextIfChanged(CaptureValue, captureStatus);
        if (capturedFrame is null)
        {
            InvalidateFilterPipeline();
            SetTextIfChanged(StatusValue, "Capture failed, using dim fallback");
            _overlayWindow.ApplyDim(GetCurrentDimOpacity());
            return;
        }

        TryAutoExportFrames(targetWindow, capturedFrame, captureStatus);

        QueueCapturedFrameProcessing(capturedFrame, mode);
    }

    private void QueueCapturedFrameProcessing(BitmapSource capturedFrame, OverlayMode mode)
    {
        var nowUtc = DateTime.UtcNow;
        if (_isFrameProcessing || nowUtc - _lastFilterProcessFinishedUtc < GetMinimumFilterProcessInterval(mode))
        {
            return;
        }

        if (ReferenceEquals(capturedFrame, _lastProcessedFrame))
        {
            return;
        }

        if (capturedFrame.CanFreeze && !capturedFrame.IsFrozen)
        {
            capturedFrame.Freeze();
        }

        var tuning = GetCurrentFilterTuning();
        var strength = GetStrengthValue(tuning);
        var brightness = GetBrightnessValue(tuning);
        var contrast = GetContrastValue(tuning);
        var gamma = GetGammaValue(tuning);
        var pipelineVersion = _filterPipelineVersion;

        _isFrameProcessing = true;
        _lastProcessedFrame = capturedFrame;
        _ = ProcessCapturedFrameAsync(capturedFrame, mode, strength, brightness, contrast, gamma, pipelineVersion);
    }

    private async Task ProcessCapturedFrameAsync(
        BitmapSource capturedFrame,
        OverlayMode mode,
        double strength,
        double brightness,
        double contrast,
        double gamma,
        int pipelineVersion)
    {
        try
        {
            var outputFrame = await Task.Run(() => ApplyModeToFrame(capturedFrame, mode, strength, brightness, contrast, gamma));
            if (pipelineVersion == _filterPipelineVersion &&
                GetSelectedMode() == mode &&
                OverlayEnabledCheckBox.IsChecked == true &&
                _overlayWindow.IsVisible)
            {
                _overlayWindow.ApplyCapturedFrame(outputFrame);
            }
        }
        catch (Exception ex)
        {
            if (pipelineVersion == _filterPipelineVersion)
            {
                SetTextIfChanged(CaptureValue, "Filter failed: " + ex.GetType().Name);
                _overlayWindow.ApplyDim(GetCurrentDimOpacity());
            }
        }
        finally
        {
            _lastFilterProcessFinishedUtc = DateTime.UtcNow;
            _isFrameProcessing = false;
        }
    }

    private static TimeSpan GetMinimumFilterProcessInterval(OverlayMode mode)
    {
        return mode == OverlayMode.SmartInvert ? MinimumSmartProcessInterval : MinimumInvertProcessInterval;
    }

    private void InvalidateFilterPipeline()
    {
        _filterPipelineVersion++;
        _lastFilterProcessFinishedUtc = DateTime.MinValue;
        _lastProcessedFrame = null;
    }

    private void HideOverlay()
    {
        InvalidateFilterPipeline();

        if (_overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
        }

        _lastOverlayWindowInfo = null;
        _overlayOwnerHandle = 0;
        _cachedMainWindow = null;
    }

    private BitmapSource? CaptureCurrentFrame(WindowInfo targetWindow, out string captureStatus)
    {
        BitmapSource? capturedFrame = null;
        captureStatus = "PrintWindow";

        try
        {
            if (_windowsGraphicsCaptureService.StartOrUpdate(targetWindow.Handle))
            {
                capturedFrame = _windowsGraphicsCaptureService.LatestFrame;
                captureStatus = _windowsGraphicsCaptureService.Status;
                if (capturedFrame is not null && !IsValidCapturedFrame(capturedFrame, targetWindow))
                {
                    capturedFrame = null;
                    captureStatus += " invalid-size";
                }
            }
            else
            {
                captureStatus = _windowsGraphicsCaptureService.Status;
            }
        }
        catch (Exception ex)
        {
            _windowsGraphicsCaptureService.Stop();
            captureStatus = "WGC crash fallback: " + ex.GetType().Name;
        }

        if (capturedFrame is null)
        {
            capturedFrame = _captureService.Capture(targetWindow);
            if (capturedFrame is not null)
            {
                captureStatus = string.IsNullOrWhiteSpace(captureStatus)
                    ? "PrintWindow"
                    : captureStatus + " -> PrintWindow";
            }
        }

        return capturedFrame;
    }

    private static bool IsValidCapturedFrame(BitmapSource frame, WindowInfo targetWindow)
    {
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || targetWindow.Width <= 0 || targetWindow.Height <= 0)
        {
            return false;
        }

        if (frame.PixelWidth < targetWindow.Width * 0.75 || frame.PixelHeight < targetWindow.Height * 0.75)
        {
            return false;
        }

        var frameAspect = frame.PixelWidth / (double)frame.PixelHeight;
        var targetAspect = targetWindow.Width / (double)targetWindow.Height;
        return Math.Abs(frameAspect - targetAspect) <= 0.18;
    }

    private BitmapSource ApplyModeToFrame(
        BitmapSource capturedFrame,
        OverlayMode mode,
        double strength,
        double brightness,
        double contrast,
        double gamma)
    {
        return mode switch
        {
            OverlayMode.Invert => _captureService.Invert(
                capturedFrame,
                strength,
                brightness,
                contrast,
                gamma),
            OverlayMode.SmartInvert => _captureService.SmartInvert(
                capturedFrame,
                strength,
                brightness,
                contrast,
                gamma),
            _ => capturedFrame
        };
    }

    private void ExportFramesClicked(object sender, RoutedEventArgs e)
    {
        var mainWindow = _windowFinder.FindMainWindow();
        if (mainWindow is null)
        {
            ExportValue.Text = "No main window";
            return;
        }

        var capturedFrame = CaptureCurrentFrame(mainWindow, out var captureStatus);
        SetTextIfChanged(CaptureValue, captureStatus);
        if (capturedFrame is null)
        {
            ExportValue.Text = "Export failed: no frame";
            return;
        }

        try
        {
            ExportFrameSet(mainWindow, capturedFrame, captureStatus);
        }
        catch (Exception ex)
        {
            ExportValue.Text = "Export failed: " + ex.GetType().Name;
        }
    }

    private void TryAutoExportFrames(WindowInfo targetWindow, BitmapSource capturedFrame, string captureStatus)
    {
        if (!AutoExportFramesEnabled)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastAutoExportUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        try
        {
            ExportFrameSet(targetWindow, capturedFrame, captureStatus);
            _lastAutoExportUtc = nowUtc;
        }
        catch (Exception ex)
        {
            ExportValue.Text = "Auto export failed: " + ex.GetType().Name;
        }
    }

    private void ExportFrameSet(WindowInfo targetWindow, BitmapSource capturedFrame, string captureStatus)
    {
        var exportDirectory = GetExportDirectory();
        var sourcePath = Path.Combine(exportDirectory, "source.png");
        var invertPath = Path.Combine(exportDirectory, "invert.png");
        var smartPath = Path.Combine(exportDirectory, "smart.png");

        _captureService.SavePng(capturedFrame, sourcePath);
        _captureService.SavePng(
            _captureService.Invert(
                capturedFrame,
                GetStrengthValue(_invertTuning),
                GetBrightnessValue(_invertTuning),
                GetContrastValue(_invertTuning),
                GetGammaValue(_invertTuning)),
            invertPath);
        _captureService.SavePng(
            _captureService.SmartInvert(
                capturedFrame,
                GetStrengthValue(_smartTuning),
                GetBrightnessValue(_smartTuning),
                GetContrastValue(_smartTuning),
                GetGammaValue(_smartTuning)),
            smartPath);

        var reportPath = Path.Combine(exportDirectory, "report.txt");
        File.WriteAllText(
            reportPath,
            $"CapturedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\nCapture={captureStatus}\r\nHandle=0x{targetWindow.Handle:X}\r\nBounds={targetWindow.X},{targetWindow.Y},{targetWindow.Width},{targetWindow.Height}\r\nFrame={capturedFrame.PixelWidth},{capturedFrame.PixelHeight}\r\nDimOpacity={Math.Round(_dimOpacityValue)}\r\nInvertStrength={Math.Round(_invertTuning.Strength)}\r\nInvertBrightness={Math.Round(_invertTuning.Brightness)}\r\nInvertContrast={Math.Round(_invertTuning.Contrast)}\r\nInvertGamma={Math.Round(_invertTuning.Gamma)}\r\nSmartStrength={Math.Round(_smartTuning.Strength)}\r\nSmartBrightness={Math.Round(_smartTuning.Brightness)}\r\nSmartContrast={Math.Round(_smartTuning.Contrast)}\r\nSmartGamma={Math.Round(_smartTuning.Gamma)}\r\n");

        ExportValue.Text = exportDirectory;
    }

    private void UpdateCandidateWindows(IReadOnlyList<WindowInfo> candidates)
    {
        var nowUtc = DateTime.UtcNow;
        if (CandidatesGrid.ItemsSource is not null && nowUtc - _lastCandidateGridUpdateUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastCandidateGridUpdateUtc = nowUtc;
        CandidatesGrid.ItemsSource = candidates
            .Select(window => new
            {
                Handle = $"0x{window.Handle:X}",
                window.Title,
                window.ClassName,
                window.Width,
                window.Height
            })
            .ToList();
    }

    private static string GetExportDirectory()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "captures"));
    }

    private OverlayMode GetSelectedMode()
    {
        return ModeComboBox.SelectedIndex switch
        {
            1 => OverlayMode.Invert,
            2 => OverlayMode.SmartInvert,
            _ => OverlayMode.Dim
        };
    }

    private byte GetCurrentDimOpacity()
    {
        return (byte)Math.Clamp((int)Math.Round(_dimOpacityValue), 0, 255);
    }

    private void ConfigureParameterSlider()
    {
        _isUpdatingParameterUi = true;

        switch (GetSelectedMode())
        {
            case OverlayMode.Dim:
                ParameterLabel.Text = "Opacity";
                ParameterLabel.Visibility = Visibility.Visible;
                OpacitySlider.Minimum = DimOpacityMinimum;
                OpacitySlider.Maximum = DimOpacityMaximum;
                OpacitySlider.Value = _dimOpacityValue;
                OpacitySlider.Visibility = Visibility.Visible;
                ParameterValue.Visibility = Visibility.Visible;
                FilterSettingsPanel.Visibility = Visibility.Collapsed;
                break;
            case OverlayMode.Invert:
                ParameterLabel.Visibility = Visibility.Collapsed;
                OpacitySlider.Visibility = Visibility.Collapsed;
                ParameterValue.Visibility = Visibility.Collapsed;
                FilterSettingsPanel.Visibility = Visibility.Visible;
                LoadFilterControls(_invertTuning);
                break;
            case OverlayMode.SmartInvert:
                ParameterLabel.Visibility = Visibility.Collapsed;
                OpacitySlider.Visibility = Visibility.Collapsed;
                ParameterValue.Visibility = Visibility.Collapsed;
                FilterSettingsPanel.Visibility = Visibility.Visible;
                LoadFilterControls(_smartTuning);
                break;
        }

        _isUpdatingParameterUi = false;
        UpdateParameterText();
    }

    private void UpdateParameterText()
    {
        switch (GetSelectedMode())
        {
            case OverlayMode.Dim:
                ParameterValue.Text = $"{Math.Round(GetCurrentDimOpacity() / 255.0 * 100)}%";
                break;
            case OverlayMode.Invert:
                UpdateFilterValueText(_invertTuning);
                break;
            case OverlayMode.SmartInvert:
                UpdateFilterValueText(_smartTuning);
                break;
        }
    }

    private void StoreCurrentParameterValue()
    {
        switch (GetSelectedMode())
        {
            case OverlayMode.Dim:
                _dimOpacityValue = OpacitySlider.Value;
                break;
            case OverlayMode.Invert:
                StoreFilterControls(_invertTuning);
                break;
            case OverlayMode.SmartInvert:
                StoreFilterControls(_smartTuning);
                break;
        }
    }

    private void SetStatus(string status, WindowInfo? window)
    {
        SetTextIfChanged(StatusValue, status);

        if (window is null)
        {
            SetTextIfChanged(HandleValue, "-");
            SetTextIfChanged(TitleValue, "-");
            SetTextIfChanged(ClassValue, "-");
            SetTextIfChanged(BoundsValue, "-");
            return;
        }

        SetTextIfChanged(HandleValue, $"0x{window.Handle:X}");
        SetTextIfChanged(TitleValue, window.Title);
        SetTextIfChanged(ClassValue, window.ClassName);
        SetTextIfChanged(BoundsValue, $"X={window.X}, Y={window.Y}, W={window.Width}, H={window.Height}");
    }

    private static void SetTextIfChanged(TextBlock textBlock, string value)
    {
        if (textBlock.Text != value)
        {
            textBlock.Text = value;
        }
    }

    private void OverlayEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady)
        {
            return;
        }

        Refresh();
    }

    private void PrivacyModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady)
        {
            return;
        }

        _isPrivacyModeEnabled = PrivacyModeCheckBox.IsChecked == true;
        _overlayWindow.SetPrivacyMode(_isPrivacyModeEnabled);
        Refresh();
    }

    private void OpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiReady || _isUpdatingParameterUi)
        {
            return;
        }

        StoreCurrentParameterValue();
        UpdateParameterText();
        InvalidateFilterPipeline();
        Refresh();
    }

    private void FilterSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiReady || _isUpdatingParameterUi)
        {
            return;
        }

        StoreCurrentParameterValue();
        UpdateParameterText();
        InvalidateFilterPipeline();
        Refresh();
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady)
        {
            return;
        }

        ConfigureParameterSlider();
        InvalidateFilterPipeline();
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        RegisterPrivacyModeHotKey();
        ApplySystemTheme();

        var source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);
    }

    private void LoadPersistedState()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var json = File.ReadAllText(_settingsPath);
            var state = JsonSerializer.Deserialize<PersistedUiState>(json);
            if (state is null)
            {
                return;
            }

            _dimOpacityValue = Math.Clamp(state.DimOpacity, DimOpacityMinimum, DimOpacityMaximum);
            _isPrivacyModeEnabled = state.IsPrivacyModeEnabled;
            ApplyPersistedTuning(_invertTuning, state.InvertStrength, state.InvertBrightness, state.InvertContrast, state.InvertGamma);
            ApplyPersistedTuning(_smartTuning, state.SmartStrength, state.SmartBrightness, state.SmartContrast, state.SmartGamma);
            ModeComboBox.SelectedIndex = Math.Clamp(state.ModeIndex, 0, 2);
        }
        catch
        {
            ModeComboBox.SelectedIndex = DefaultModeIndex;
        }
    }

    private void SavePersistedState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new PersistedUiState
            {
                ModeIndex = ModeComboBox.SelectedIndex,
                DimOpacity = _dimOpacityValue,
                IsPrivacyModeEnabled = _isPrivacyModeEnabled,
                InvertStrength = _invertTuning.Strength,
                InvertBrightness = _invertTuning.Brightness,
                InvertContrast = _invertTuning.Contrast,
                InvertGamma = _invertTuning.Gamma,
                SmartStrength = _smartTuning.Strength,
                SmartBrightness = _smartTuning.Brightness,
                SmartContrast = _smartTuning.Contrast,
                SmartGamma = _smartTuning.Gamma
            };

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
        }
    }

    private FilterTuning GetCurrentFilterTuning()
    {
        return GetSelectedMode() == OverlayMode.SmartInvert ? _smartTuning : _invertTuning;
    }

    private void LoadFilterControls(FilterTuning tuning)
    {
        StrengthSlider.Minimum = FilterStrengthMinimum;
        StrengthSlider.Maximum = FilterStrengthMaximum;
        StrengthSlider.Value = tuning.Strength;

        BrightnessSlider.Minimum = BrightnessMinimum;
        BrightnessSlider.Maximum = BrightnessMaximum;
        BrightnessSlider.Value = tuning.Brightness;

        ContrastSlider.Minimum = ContrastMinimum;
        ContrastSlider.Maximum = ContrastMaximum;
        ContrastSlider.Value = tuning.Contrast;

        GammaSlider.Minimum = GammaMinimum;
        GammaSlider.Maximum = GammaMaximum;
        GammaSlider.Value = tuning.Gamma;
    }

    private void StoreFilterControls(FilterTuning tuning)
    {
        tuning.Strength = StrengthSlider.Value;
        tuning.Brightness = BrightnessSlider.Value;
        tuning.Contrast = ContrastSlider.Value;
        tuning.Gamma = GammaSlider.Value;
    }

    private void UpdateFilterValueText(FilterTuning tuning)
    {
        StrengthValue.Text = $"{Math.Round(tuning.Strength)}%";
        BrightnessValue.Text = FormatSignedValue(tuning.Brightness);
        ContrastValue.Text = $"{Math.Round(tuning.Contrast)}%";
        GammaValue.Text = $"{Math.Round(tuning.Gamma)}%";
    }

    private static string FormatSignedValue(double value)
    {
        var rounded = Math.Round(value);
        return rounded > 0 ? $"+{rounded}" : rounded.ToString("0");
    }

    private static double GetStrengthValue(FilterTuning tuning)
    {
        return Math.Clamp(tuning.Strength / 100.0, 0, 1);
    }

    private static double GetBrightnessValue(FilterTuning tuning)
    {
        return Math.Clamp(tuning.Brightness / 100.0, -1, 1);
    }

    private static double GetContrastValue(FilterTuning tuning)
    {
        return Math.Clamp(tuning.Contrast / 100.0, 0.5, 1.7);
    }

    private static double GetGammaValue(FilterTuning tuning)
    {
        return Math.Clamp(tuning.Gamma / 100.0, 0.6, 1.8);
    }

    private static void ApplyPersistedTuning(FilterTuning tuning, double strength, double brightness, double contrast, double gamma)
    {
        tuning.Strength = Math.Clamp(strength, FilterStrengthMinimum, FilterStrengthMaximum);
        tuning.Brightness = Math.Clamp(brightness, BrightnessMinimum, BrightnessMaximum);
        tuning.Contrast = Math.Clamp(contrast, ContrastMinimum, ContrastMaximum);
        tuning.Gamma = Math.Clamp(gamma, GammaMinimum, GammaMaximum);
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KakaoTalkFilterLab",
            "ui-state.json");
    }

    private sealed class FilterTuning
    {
        public double Strength { get; set; } = DefaultFilterStrength;
        public double Brightness { get; set; } = DefaultBrightness;
        public double Contrast { get; set; } = DefaultContrast;
        public double Gamma { get; set; } = DefaultGamma;
    }

    private sealed class PersistedUiState
    {
        public int ModeIndex { get; set; } = DefaultModeIndex;
        public double DimOpacity { get; set; } = DefaultDimOpacity;
        public bool IsPrivacyModeEnabled { get; set; }
        public double InvertStrength { get; set; } = DefaultFilterStrength;
        public double InvertBrightness { get; set; } = DefaultBrightness;
        public double InvertContrast { get; set; } = DefaultContrast;
        public double InvertGamma { get; set; } = DefaultGamma;
        public double SmartStrength { get; set; } = DefaultSmartStrength;
        public double SmartBrightness { get; set; } = DefaultSmartBrightness;
        public double SmartContrast { get; set; } = DefaultSmartContrast;
        public double SmartGamma { get; set; } = DefaultSmartGamma;
    }

    private void TogglePrivacyMode()
    {
        _isPrivacyModeEnabled = !_isPrivacyModeEnabled;

        if (_isUiReady)
        {
            PrivacyModeCheckBox.IsChecked = _isPrivacyModeEnabled;
        }
        else
        {
            _overlayWindow.SetPrivacyMode(_isPrivacyModeEnabled);
        }

        Refresh();
    }

    private void RegisterPrivacyModeHotKey()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _ = Win32.TryRegisterHotKey(hwnd, PrivacyModeHotKeyId, Win32.ModControl, (uint)KeyInterop.VirtualKeyFromKey(Key.H));
    }

    private void UnregisterPrivacyModeHotKey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            Win32.TryUnregisterHotKey(hwnd, PrivacyModeHotKeyId);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WmHotkey && wParam.ToInt32() == PrivacyModeHotKeyId)
        {
            TogglePrivacyMode();
            handled = true;
        }

        return nint.Zero;
    }
}
