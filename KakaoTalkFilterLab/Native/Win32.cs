using System.Runtime.InteropServices;
using System.Text;
using KakaoTalkFilterLab.Models;

namespace KakaoTalkFilterLab.Native;

internal static class Win32
{
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolwindow = 0x80;
    private const int WsExNoactivate = 0x08000000;
    public const int WmHotkey = 0x0312;
    public const uint ModControl = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    public static void SetImmersiveDarkMode(nint hwnd, bool enabled)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        var value = enabled ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
    }
    public static IReadOnlyList<WindowInfo> EnumerateVisibleWindowsForProcess(string processName)
    {
        var results = new List<WindowInfo>();
        var processIds = System.Diagnostics.Process
            .GetProcessesByName(processName)
            .Select(process => (uint)process.Id)
            .ToHashSet();

        if (processIds.Count == 0)
        {
            return results;
        }

        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            if (IsIconic(hwnd))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out var processId);
            if (!processIds.Contains(processId))
            {
                return true;
            }

            if (!TryGetWindowBounds(hwnd, out var rect))
            {
                return true;
            }

            var titleBuilder = new StringBuilder(256);
            var classBuilder = new StringBuilder(256);
            _ = GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
            _ = GetClassName(hwnd, classBuilder, classBuilder.Capacity);

            results.Add(new WindowInfo(
                hwnd,
                titleBuilder.ToString().Trim(),
                classBuilder.ToString(),
                rect.Left,
                rect.Top,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top));

            return true;
        }, nint.Zero);

        return results;
    }

    public static void EnableClickThrough(nint hwnd)
    {
        var current = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        var updated = current | WsExTransparent | WsExToolwindow | WsExNoactivate;
        _ = SetWindowLongPtr(hwnd, GwlExstyle, (nint)updated);
    }

    public static bool MoveOverlayToBounds(nint overlayHwnd, int x, int y, int width, int height, bool showWindow)
    {
        var flags = SwpNoActivate | SwpNoZorder;
        if (showWindow)
        {
            flags |= SwpShowWindow;
        }

        return SetWindowPos(
            overlayHwnd,
            nint.Zero,
            x,
            y,
            width,
            height,
            flags);
    }

    public static bool TryRegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey)
    {
        return RegisterHotKey(hwnd, id, modifiers, virtualKey);
    }

    public static void TryUnregisterHotKey(nint hwnd, int id)
    {
        _ = UnregisterHotKey(hwnd, id);
    }

    public static nint TryCaptureWindowBitmap(nint hwnd, int width, int height)
    {
        var screenDc = GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            return nint.Zero;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == nint.Zero)
        {
            _ = ReleaseDC(nint.Zero, screenDc);
            return nint.Zero;
        }

        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (bitmap == nint.Zero)
        {
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(nint.Zero, screenDc);
            return nint.Zero;
        }

        var oldObject = SelectObject(memoryDc, bitmap);
        var rendered = PrintWindow(hwnd, memoryDc, 0x00000002);
        _ = SelectObject(memoryDc, oldObject);
        _ = DeleteDC(memoryDc);
        _ = ReleaseDC(nint.Zero, screenDc);

        if (!rendered)
        {
            _ = DeleteObject(bitmap);
            return nint.Zero;
        }

        return bitmap;
    }

    public static void DeleteGdiObject(nint handle)
    {
        if (handle != nint.Zero)
        {
            _ = DeleteObject(handle);
        }
    }

    private static bool TryGetWindowBounds(nint hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0 &&
            rect.Right > rect.Left &&
            rect.Bottom > rect.Top)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }
}
