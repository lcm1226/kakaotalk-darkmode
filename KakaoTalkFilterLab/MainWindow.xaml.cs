using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using KakaoTalkFilterLab.Models;
using KakaoTalkFilterLab.Native;
using KakaoTalkFilterLab.Services;
using Microsoft.Win32;

namespace KakaoTalkFilterLab;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly KakaoTalkWindowFinder _windowFinder = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly WindowsGraphicsCaptureService _windowsGraphicsCaptureService = new();
    private readonly OverlayWindow _overlayWindow = new();
    private bool _isUiReady;
    private WindowInfo? _lastOverlayWindowInfo;
    private nint _overlayOwnerHandle;
    private DateTime _lastAutoExportUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        OverlayEnabledCheckBox.Checked += OverlayEnabledChanged;
        OverlayEnabledCheckBox.Unchecked += OverlayEnabledChanged;
        OpacitySlider.ValueChanged += OpacitySliderChanged;
        ModeComboBox.SelectionChanged += ModeChanged;
        ExportFramesButton.Click += ExportFramesClicked;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += OnTick;

        Loaded += (_, _) =>
        {
            _isUiReady = true;
            UpdateOpacityText();
            Refresh();
            _timer.Start();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _windowsGraphicsCaptureService.Dispose();
            _overlayWindow.Close();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Refresh();
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

        var candidates = _windowFinder.GetCandidates();
        var mainWindow = _windowFinder.FindMainWindow();

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

        if (mainWindow is null)
        {
            SetStatus("No main window matched", null);
            CaptureValue.Text = "-";
            HideOverlay();
            return;
        }

        SetStatus("Main window detected", mainWindow);
        UpdateOverlay(mainWindow);
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
        var opacity = GetCurrentOpacity();

        if (mode == OverlayMode.Dim)
        {
            _windowsGraphicsCaptureService.Stop();
            CaptureValue.Text = "Dim overlay";
            _overlayWindow.ApplyDim(opacity);
            return;
        }

        var capturedFrame = CaptureCurrentFrame(targetWindow, out var captureStatus);
        CaptureValue.Text = captureStatus;
        if (capturedFrame is null)
        {
            StatusValue.Text = "Capture failed, using dim fallback";
            _overlayWindow.ApplyDim(opacity);
            return;
        }

        TryAutoExportFrames(targetWindow, capturedFrame, captureStatus);

        var outputFrame = ApplyModeToFrame(capturedFrame, mode);

        _overlayWindow.ApplyCapturedFrame(outputFrame);
    }

    private void HideOverlay()
    {
        if (_overlayWindow.IsVisible)
        {
            _overlayWindow.Hide();
        }

        _lastOverlayWindowInfo = null;
        _overlayOwnerHandle = 0;
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

    private BitmapSource ApplyModeToFrame(BitmapSource capturedFrame, OverlayMode mode)
    {
        return mode switch
        {
            OverlayMode.Invert => _captureService.Invert(capturedFrame),
            OverlayMode.SmartInvert => _captureService.SmartInvert(capturedFrame),
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
        CaptureValue.Text = captureStatus;
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
        _captureService.SavePng(_captureService.Invert(capturedFrame), invertPath);
        _captureService.SavePng(_captureService.SmartInvert(capturedFrame), smartPath);

        var reportPath = Path.Combine(exportDirectory, "report.txt");
        File.WriteAllText(
            reportPath,
            $"CapturedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\nCapture={captureStatus}\r\nHandle=0x{targetWindow.Handle:X}\r\nBounds={targetWindow.X},{targetWindow.Y},{targetWindow.Width},{targetWindow.Height}\r\n");

        ExportValue.Text = exportDirectory;
    }

    private static string GetExportDirectory()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "captures"));
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

    private byte GetCurrentOpacity()
    {
        return (byte)Math.Clamp((int)OpacitySlider.Value, 0, 255);
    }

    private void UpdateOpacityText()
    {
        var percent = Math.Round(GetCurrentOpacity() / 255.0 * 100);
        OpacityValue.Text = $"{percent}%";
    }

    private void SetStatus(string status, WindowInfo? window)
    {
        StatusValue.Text = status;

        if (window is null)
        {
            HandleValue.Text = "-";
            TitleValue.Text = "-";
            ClassValue.Text = "-";
            BoundsValue.Text = "-";
            return;
        }

        HandleValue.Text = $"0x{window.Handle:X}";
        TitleValue.Text = window.Title;
        ClassValue.Text = window.ClassName;
        BoundsValue.Text = $"X={window.X}, Y={window.Y}, W={window.Width}, H={window.Height}";
    }

    private void OverlayEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiReady)
        {
            return;
        }

        Refresh();
    }

    private void OpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiReady)
        {
            return;
        }

        UpdateOpacityText();
        Refresh();
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUiReady)
        {
            return;
        }

        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }
}
