using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using KakaoTalkFilterLab.Native;

namespace KakaoTalkFilterLab;

public partial class FrameOverlayWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const double SidebarVisibleWidth = 78;
    private const double TitleButtonsVisibleWidth = 138;
    private const double TitleButtonsVisibleHeight = 38;
    private bool _isPrivacyModeEnabled;

    public FrameOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdatePrivacyMaskLayout();
    }

    public void ApplyCapturedFrame(BitmapSource frame)
    {
        if (ReferenceEquals(CaptureImage.Source, frame) && CaptureImage.Visibility == Visibility.Visible)
        {
            return;
        }

        CaptureImage.Source = frame;
        CaptureImage.Visibility = Visibility.Visible;
    }

    public void SetPrivacyMode(bool isEnabled)
    {
        var desiredVisibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_isPrivacyModeEnabled == isEnabled && PrivacyMaskLayer.Visibility == desiredVisibility)
        {
            return;
        }

        _isPrivacyModeEnabled = isEnabled;
        PrivacyMaskLayer.Visibility = desiredVisibility;
        UpdatePrivacyMaskLayout();
    }

    public void ClearFrame()
    {
        CaptureImage.Source = null;
        CaptureImage.Visibility = Visibility.Collapsed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.EnableClickThrough(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            handled = true;
            return HtTransparent;
        }

        return nint.Zero;
    }

    private void UpdatePrivacyMaskLayout()
    {
        if (!_isPrivacyModeEnabled)
        {
            return;
        }

        var width = Math.Max(0, ActualWidth);
        var height = Math.Max(0, ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var sidebarWidth = Math.Min(SidebarVisibleWidth, width);
        var titleButtonsWidth = Math.Min(TitleButtonsVisibleWidth, Math.Max(0, width - sidebarWidth));
        var titleButtonsHeight = Math.Min(TitleButtonsVisibleHeight, height);

        var topCoverWidth = Math.Max(0, width - sidebarWidth - titleButtonsWidth);
        PrivacyTopCover.Width = topCoverWidth;
        PrivacyTopCover.Height = titleButtonsHeight;
        Canvas.SetLeft(PrivacyTopCover, sidebarWidth);
        Canvas.SetTop(PrivacyTopCover, 0);

        var bodyCoverHeight = Math.Max(0, height - titleButtonsHeight);
        PrivacyBodyCover.Width = Math.Max(0, width - sidebarWidth);
        PrivacyBodyCover.Height = bodyCoverHeight;
        Canvas.SetLeft(PrivacyBodyCover, sidebarWidth);
        Canvas.SetTop(PrivacyBodyCover, titleButtonsHeight);
    }
}