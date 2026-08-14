using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KakaoTalkGpuInvertSpike;

internal sealed class OverlayWindow : IDisposable
{
    private const string WindowClassName = "KakaoTalkGpuInvertSpike.Overlay";
    private const int PrivacyHotKeyId = 0x4B47;
    private const uint VirtualKeyH = 0x48;
    private static readonly NativeMethods.WndProc WindowProc = WndProc;
    private static readonly object ClassSync = new();
    private static readonly object WindowSync = new();
    private static readonly Dictionary<nint, OverlayWindow> Windows = [];
    private static bool s_classRegistered;

    private nint _hwnd;
    private nint _owner;
    private bool _visible;
    private bool _privacyHotKeyRegistered;

    public event Action? PrivacyHotKeyPressed;

    public nint Handle
    {
        get
        {
            EnsureCreated();
            return _hwnd;
        }
    }

    public bool IsVisible => _hwnd != nint.Zero &&
        NativeMethods.IsWindow(_hwnd) &&
        NativeMethods.IsWindowVisible(_hwnd);

    public void Position(TargetWindow target, bool show)
    {
        EnsureCreated();
        if (_owner != target.Handle)
        {
            _ = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GwlpHwndParent, target.Handle);
            _owner = target.Handle;
        }

        var flags = NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder;
        if (show)
        {
            flags |= NativeMethods.SwpShowWindow;
        }

        _ = NativeMethods.SetWindowPos(
            _hwnd,
            nint.Zero,
            target.X,
            target.Y,
            target.Width,
            target.Height,
            flags);
        if (show)
        {
            ShowWithoutActivation();
            if (!IsInputPassThrough())
            {
                Hide();
                throw new InvalidOperationException(
                    "The GPU overlay failed its input pass-through check.");
            }
        }
        else
        {
            Hide();
        }
    }

    public void Hide()
    {
        if (_hwnd != nint.Zero &&
            NativeMethods.IsWindow(_hwnd) &&
            (_visible || NativeMethods.IsWindowVisible(_hwnd)))
        {
            _ = NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
            _visible = false;
        }
    }

    public void Dispose()
    {
        if (_hwnd != nint.Zero)
        {
            if (_privacyHotKeyRegistered)
            {
                _ = NativeMethods.UnregisterHotKey(_hwnd, PrivacyHotKeyId);
                _privacyHotKeyRegistered = false;
            }

            lock (WindowSync)
            {
                Windows.Remove(_hwnd);
            }

            if (NativeMethods.IsWindow(_hwnd))
            {
                _ = NativeMethods.DestroyWindow(_hwnd);
            }

            _hwnd = nint.Zero;
            _owner = nint.Zero;
            _visible = false;
        }
    }

    private void EnsureCreated()
    {
        if (_hwnd != nint.Zero && NativeMethods.IsWindow(_hwnd))
        {
            return;
        }

        if (_hwnd != nint.Zero)
        {
            lock (WindowSync)
            {
                Windows.Remove(_hwnd);
            }

            _hwnd = nint.Zero;
            _owner = nint.Zero;
            _visible = false;
            _privacyHotKeyRegistered = false;
        }

        EnsureClassRegistered();
        var instance = NativeMethods.GetModuleHandle(null);
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExLayered |
                NativeMethods.WsExTransparent |
                NativeMethods.WsExToolWindow |
                NativeMethods.WsExNoActivate,
            WindowClassName,
            "KakaoTalk GPU Invert Spike",
            NativeMethods.WsPopup,
            0,
            0,
            1,
            1,
            nint.Zero,
            nint.Zero,
            instance,
            nint.Zero);
        if (_hwnd == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the GPU overlay window.");
        }

        if (!NativeMethods.SetLayeredWindowAttributes(
                _hwnd,
                0,
                byte.MaxValue,
                NativeMethods.LwaAlpha))
        {
            var error = Marshal.GetLastWin32Error();
            _ = NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
            throw new Win32Exception(error, "Could not initialize the layered GPU overlay window.");
        }

        if (!string.Equals(
            Environment.GetEnvironmentVariable("KAKAOTALK_GPU_ALLOW_SCREEN_CAPTURE"),
            "1",
            StringComparison.Ordinal))
        {
            _ = NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WdaExcludeFromCapture);
        }
        lock (WindowSync)
        {
            Windows[_hwnd] = this;
        }

        _privacyHotKeyRegistered = NativeMethods.RegisterHotKey(
            _hwnd,
            PrivacyHotKeyId,
            NativeMethods.ModControl | NativeMethods.ModNoRepeat,
            VirtualKeyH);
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassSync)
        {
            if (s_classRegistered)
            {
                return;
            }

            var windowClass = new NativeMethods.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
                Instance = NativeMethods.GetModuleHandle(null),
                WindowProc = WindowProc,
                ClassName = WindowClassName
            };
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the GPU overlay window class.");
            }

            s_classRegistered = true;
        }
    }

    private void ShowWithoutActivation()
    {
        _ = NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);
        if (!NativeMethods.IsWindowVisible(_hwnd))
        {
            _ = NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);
        }

        _visible = NativeMethods.IsWindowVisible(_hwnd);
    }

    private bool IsInputPassThrough()
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var bounds) ||
            bounds.Width <= 0 ||
            bounds.Height <= 0)
        {
            return false;
        }

        var hitPoint = new NativeMethods.Point
        {
            X = bounds.Left + bounds.Width / 2,
            Y = bounds.Top + bounds.Height / 2
        };
        return NativeMethods.WindowFromPoint(hitPoint) != _hwnd;
    }

    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WmHotKey && wParam.ToInt32() == PrivacyHotKeyId)
        {
            OverlayWindow? window;
            lock (WindowSync)
            {
                Windows.TryGetValue(hwnd, out window);
            }

            window?.PrivacyHotKeyPressed?.Invoke();
            return nint.Zero;
        }

        return message switch
        {
            NativeMethods.WmNcHitTest => NativeMethods.HtTransparent,
            NativeMethods.WmEraseBackground => 1,
            _ => NativeMethods.DefWindowProc(hwnd, message, wParam, lParam)
        };
    }
}
