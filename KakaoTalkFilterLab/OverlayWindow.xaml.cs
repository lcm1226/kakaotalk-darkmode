using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KakaoTalkFilterLab.Models;
using KakaoTalkFilterLab.Native;

namespace KakaoTalkFilterLab;

public partial class OverlayWindow : Window
{
    private const double SidebarVisibleWidth = 78;
    private const double TitleButtonsVisibleWidth = 138;
    private const double TitleButtonsVisibleHeight = 38;
    private bool _isPrivacyModeEnabled;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdatePrivacyMaskLayout();
    }

    public void ApplyDim(byte alpha)
    {
        CaptureImage.Source = null;
        CaptureImage.Visibility = Visibility.Collapsed;
        DimFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 0, 0, 0));
        DimFill.Visibility = Visibility.Visible;
    }

    public void ApplyCapturedFrame(BitmapSource frame)
    {
        CaptureImage.Source = frame;
        CaptureImage.Visibility = Visibility.Visible;
        DimFill.Visibility = Visibility.Collapsed;
    }

    public void SetPrivacyMode(bool isEnabled)
    {
        _isPrivacyModeEnabled = isEnabled;
        PrivacyMaskLayer.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdatePrivacyMaskLayout();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.EnableClickThrough(hwnd);
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
