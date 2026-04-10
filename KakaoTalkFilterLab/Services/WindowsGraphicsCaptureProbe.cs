using Windows.Graphics.Capture;

namespace KakaoTalkFilterLab.Services;

internal static class WindowsGraphicsCaptureProbe
{
    public static bool IsApiReachable()
    {
        return GraphicsCaptureSession.IsSupported();
    }
}
