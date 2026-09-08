using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KakaoTalkGpuInvertSpike;

// Clip drawing and hit testing without changing the host window's layout.
internal sealed class WindowRegionCut : IDisposable
{
    private readonly bool _clipNonClientFrame;
    private bool _restoreNonClientFrame;
    private nint _window, _original, _applied;
    private uint _process, _thread;

    public WindowRegionCut(bool clipNonClientFrame = false)
    {
        _clipNonClientFrame = clipNonClientFrame;
    }

    public static int VisibleHeight(int height, int pixels) =>
        Math.Max(1, height - Math.Clamp(pixels, 0, Math.Max(0, height - 1)));

    public void Apply(TargetWindow target, int pixels, bool allowEmpty = false)
    {
        if (pixels <= 0) { Dispose(); return; }
        var thread = NativeMethods.GetWindowThreadProcessId(target.Handle, out var process);
        if (_window != target.Handle || _thread != thread || _process != process)
        {
            Dispose();
            _window = target.Handle;
            _thread = thread;
            _process = process;
            _original = ReadRegion(_window);
        }
        // KakaoTalk's DWM non-client surface otherwise fills the removed region with white.
        if (_clipNonClientFrame && DwmGetWindowAttribute(_window, 1, out var enabled, sizeof(int)) == 0 && enabled != 0)
        {
            var disabledPolicy = 1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_window, 2, ref disabledPolicy, sizeof(int)));
            _restoreNonClientFrame = true;
        }
        if (!NativeMethods.GetWindowRect(_window, out var bounds))
            throw new InvalidOperationException("Cannot read window bounds for bottom cut.");

        var current = ReadRegion(_window);
        try
        {
            // Preserve changes the host makes to its own region during resize/theme changes.
            if (_applied != 0 && !SameRegion(current, _applied))
            {
                Delete(_original);
                _original = Copy(current);
            }
            var visibleHeight = allowEmpty ? Math.Max(0, target.Height - pixels) : VisibleHeight(target.Height, pixels);
            var bottom = Math.Clamp(target.Y - bounds.Top + visibleHeight, allowEmpty ? 0 : 1, bounds.Height);
            var desired = CreateRectRgn(0, 0, bounds.Width, bottom);
            if (desired == 0) throw new Win32Exception();
            try
            {
                if (_original != 0 && CombineRgn(desired, desired, _original, 1) == 0)
                    throw new Win32Exception();
                if (SameRegion(current, desired)) return;
                var transfer = Copy(desired);
                if (SetWindowRgn(_window, transfer, true) == 0)
                {
                    Delete(transfer);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot apply bottom cut.");
                }
                Delete(_applied);
                _applied = desired;
                desired = 0;
            }
            finally { Delete(desired); }
        }
        finally { Delete(current); }
    }

    public void Dispose()
    {
        var thread = NativeMethods.GetWindowThreadProcessId(_window, out var process);
        if (_window != 0 && _applied != 0 && thread == _thread && process == _process)
        {
            var current = ReadRegion(_window);
            try
            {
                if (SameRegion(current, _applied))
                {
                    var transfer = Copy(_original);
                    if (SetWindowRgn(_window, transfer, true) == 0)
                    {
                        Delete(transfer);
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restore window region.");
                    }
                }
            }
            finally { Delete(current); }
        }
        if (_restoreNonClientFrame && _window != 0 && thread == _thread && process == _process)
        {
            var enabledPolicy = 2;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_window, 2, ref enabledPolicy, sizeof(int)));
        }
        _restoreNonClientFrame = false;
        Delete(_original);
        Delete(_applied);
        _original = _applied = _window = 0;
        _thread = _process = 0;
    }

    private static nint ReadRegion(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception();
        if (GetWindowRgn(window, region) != 0) return region;
        Delete(region);
        return 0;
    }
    private static bool SameRegion(nint first, nint second) =>
        first == 0 || second == 0 ? first == second : EqualRgn(first, second);
    private static nint Copy(nint source)
    {
        if (source == 0) return 0;
        var result = CreateRectRgn(0, 0, 0, 0);
        if (result == 0) throw new Win32Exception();
        if (CombineRgn(result, source, 0, 5) != 0) return result;
        Delete(result);
        throw new Win32Exception();
    }
    private static void Delete(nint region) { if (region != 0) _ = DeleteObject(region); }
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualRgn(nint first, nint second);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}
