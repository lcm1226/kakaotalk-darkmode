using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KakaoTalkFilterLab.Models;
using KakaoTalkFilterLab.Native;

namespace KakaoTalkFilterLab;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void ApplyDim(byte alpha)
    {
        CaptureImage.Source = null;
        CaptureImage.Visibility = Visibility.Collapsed;
        DimFill.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        DimFill.Visibility = Visibility.Visible;
    }

    public void ApplyCapturedFrame(BitmapSource frame)
    {
        CaptureImage.Source = frame;
        CaptureImage.Visibility = Visibility.Visible;
        DimFill.Visibility = Visibility.Collapsed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.EnableClickThrough(hwnd);
    }
}
