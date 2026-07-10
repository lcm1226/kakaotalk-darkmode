using System.Runtime.InteropServices;

namespace KakaoTalkFilterLab;

internal sealed class NativeOverlayWindow : IDisposable
{
    private const string WindowClassName = "KakaoTalkFilterLab.NativeOverlay";
    private const int CsHRedraw = 0x0002;
    private const int CsVRedraw = 0x0001;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int GwlHwndParent = -8;
    private const int HtTransparent = -1;
    private const int WmNcHitTest = 0x0084;
    private const int SwHide = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const uint LwaAlpha = 0x00000002;
    private static readonly nint InstanceHandle = Marshal.GetHINSTANCE(typeof(NativeOverlayWindow).Module);
    private static readonly WndProcDelegate WindowProc = WndProc;
    private static bool s_isClassRegistered;
    private static nint s_backgroundBrush;

    private nint _hwnd;
    private nint _ownerHwnd;
    private byte? _lastAlpha;
    private int _lastX;
    private int _lastY;
    private int _lastWidth;
    private int _lastHeight;
    private bool _isVisible;
    private bool _disposed;

    public void Show(nint ownerHwnd, int x, int y, int width, int height, byte alpha)
    {
        if (_disposed || ownerHwnd == nint.Zero || width <= 0 || height <= 0)
        {
            Hide();
            return;
        }

        EnsureWindow();
        SetOwner(ownerHwnd);

        if (_lastAlpha != alpha)
        {
            _ = SetLayeredWindowAttributes(_hwnd, 0, alpha, LwaAlpha);
            _lastAlpha = alpha;
        }

        if (!_isVisible ||
            _lastX != x ||
            _lastY != y ||
            _lastWidth != width ||
            _lastHeight != height)
        {
            _ = SetWindowPos(_hwnd, nint.Zero, x, y, width, height, SwpNoActivate | SwpNoZOrder | SwpShowWindow);
            _lastX = x;
            _lastY = y;
            _lastWidth = width;
            _lastHeight = height;
        }

        _isVisible = true;
    }

    public void Hide()
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        _ = ShowWindow(_hwnd, SwHide);
        _isVisible = false;
        _lastAlpha = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hwnd != nint.Zero)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    private void EnsureWindow()
    {
        if (_hwnd != nint.Zero && IsWindow(_hwnd))
        {
            return;
        }

        RegisterWindowClass();

        _hwnd = CreateWindowEx(
            WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate,
            WindowClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            1,
            1,
            nint.Zero,
            nint.Zero,
            InstanceHandle,
            nint.Zero);

        if (_hwnd == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create native overlay window.");
        }
    }

    private void SetOwner(nint ownerHwnd)
    {
        if (_ownerHwnd == ownerHwnd)
        {
            return;
        }

        _ = SetWindowLongPtr(_hwnd, GwlHwndParent, ownerHwnd);
        _ownerHwnd = ownerHwnd;
    }

    private static void RegisterWindowClass()
    {
        if (s_isClassRegistered)
        {
            return;
        }

        s_backgroundBrush = CreateSolidBrush(ToColorRef(2, 5, 10));
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = CsHRedraw | CsVRedraw,
            WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
            InstanceHandle = InstanceHandle,
            BackgroundBrush = s_backgroundBrush,
            ClassName = WindowClassName
        };

        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            throw new InvalidOperationException("Failed to register native overlay window class.");
        }

        s_isClassRegistered = true;
    }

    private static uint ToColorRef(byte red, byte green, byte blue)
    {
        return (uint)(red | (green << 8) | (blue << 16));
    }

    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmNcHitTest)
        {
            return HtTransparent;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint InstanceHandle;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public nint IconSmall;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);
}
