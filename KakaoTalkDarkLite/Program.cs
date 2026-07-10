using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace KakaoTalkDarkLite;

internal static class Program
{
    private const string InstanceMutexName = @"Local\KakaoTalkDarkLite.Singleton";

    [STAThread]
    private static void Main()
    {
        using var instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        try
        {
            using var app = new LiteApplication();
            Application.Run(app);
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }
}

internal sealed class LiteApplication : ApplicationContext
{
    private const int SafetyRefreshIntervalMs = 5000;

    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NativeOverlay _overlay = new();
    private readonly StrengthSliderForm _sliderForm;
    private readonly WindowEventMonitor _windowEventMonitor;
    private byte _alpha = 115;
    private nint _lastTargetHandle;
    private bool _enabled = true;
    private bool _showSlider = true;
    private ToolStripMenuItem? _enabledMenuItem;
    private ToolStripMenuItem? _showSliderMenuItem;
    private int _lastSliderTargetX;
    private int _lastSliderTargetY;
    private int _lastSliderTargetWidth;
    private int _lastSliderTargetHeight;
    private bool _hasSliderTargetBounds;

    public LiteApplication()
    {
        _sliderForm = new StrengthSliderForm(_alpha, SetAlpha);
        _windowEventMonitor = new WindowEventMonitor(RefreshOverlay);
        _trayIcon = new NotifyIcon
        {
            Text = "KakaoTalk Dark Lite",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _timer = new System.Windows.Forms.Timer
        {
            Interval = SafetyRefreshIntervalMs
        };
        _timer.Tick += (_, _) => RefreshOverlay();
        _timer.Start();
        RefreshOverlay();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _windowEventMonitor.Dispose();
            _overlay.Dispose();
            _sliderForm.Close();
            _sliderForm.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _enabledMenuItem = new ToolStripMenuItem("Enabled")
        {
            Checked = _enabled,
            CheckOnClick = true
        };
        _enabledMenuItem.CheckedChanged += (_, _) =>
        {
            _enabled = _enabledMenuItem.Checked;
            RefreshOverlay();
        };
        menu.Items.Add(_enabledMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Light (35%)", null, (_, _) => SetAlpha(90));
        menu.Items.Add("Medium (45%)", null, (_, _) => SetAlpha(115));
        menu.Items.Add("Dark (57%)", null, (_, _) => SetAlpha(145));
        menu.Items.Add("Strong (65%)", null, (_, _) => SetAlpha(165));
        menu.Items.Add("Max (100%)", null, (_, _) => SetAlpha(255));
        menu.Items.Add(new ToolStripSeparator());
        _showSliderMenuItem = new ToolStripMenuItem("Show strength slider")
        {
            Checked = _showSlider,
            CheckOnClick = true
        };
        _showSliderMenuItem.CheckedChanged += (_, _) =>
        {
            _showSlider = _showSliderMenuItem.Checked;
            RefreshOverlay();
        };
        menu.Items.Add(_showSliderMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void SetAlpha(byte alpha)
    {
        _alpha = alpha;
        _sliderForm.SetAlpha(alpha);
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        if (!_enabled)
        {
            _windowEventMonitor.Detach();
            _overlay.Hide();
            _sliderForm.Hide();
            _hasSliderTargetBounds = false;
            return;
        }

        var target = _lastTargetHandle != nint.Zero &&
            KakaoTalkWindowFinder.TryGetWindowSnapshot(_lastTargetHandle, out var cachedTarget)
                ? cachedTarget
                : KakaoTalkWindowFinder.FindMainWindow();
        if (target is null)
        {
            _lastTargetHandle = nint.Zero;
            _windowEventMonitor.Detach();
            _overlay.Hide();
            _sliderForm.Hide();
            _hasSliderTargetBounds = false;
            return;
        }

        if (_lastTargetHandle != target.Value.Handle)
        {
            _lastTargetHandle = target.Value.Handle;
        }

        _ = NativeMethods.GetWindowThreadProcessId(target.Value.Handle, out var targetProcessId);
        _windowEventMonitor.Attach(targetProcessId, target.Value.Handle);

        _overlay.Show(
            target.Value.Handle,
            target.Value.X,
            target.Value.Y,
            target.Value.Width,
            target.Value.Height,
            _alpha);

        if (_showSlider)
        {
            if (!_hasSliderTargetBounds ||
                _lastSliderTargetX != target.Value.X ||
                _lastSliderTargetY != target.Value.Y ||
                _lastSliderTargetWidth != target.Value.Width ||
                _lastSliderTargetHeight != target.Value.Height)
            {
                _sliderForm.PositionNear(target.Value);
                _lastSliderTargetX = target.Value.X;
                _lastSliderTargetY = target.Value.Y;
                _lastSliderTargetWidth = target.Value.Width;
                _lastSliderTargetHeight = target.Value.Height;
                _hasSliderTargetBounds = true;
            }

            if (!_sliderForm.Visible)
            {
                _sliderForm.Show();
            }
        }
        else
        {
            _sliderForm.Hide();
            _hasSliderTargetBounds = false;
        }
    }
}

internal sealed class StrengthSliderForm : IDisposable
{
    private const string WindowClassName = "KakaoTalkDarkLite.StrengthSlider";
    private const int Width = 320;
    private const int Height = 38;
    private const int SliderMinimum = 0;
    private const int SliderMaximum = 255;
    private const int EdgePadding = 8;
    private const int TrackLeft = 86;
    private const int TrackRight = 252;
    private const int TrackY = 19;
    private const int KnobRadius = 6;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmMouseMove = 0x0200;
    private const int MkLeftButton = 0x0001;
    private const int SwHide = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint InstanceHandle = Marshal.GetHINSTANCE(typeof(StrengthSliderForm).Module);
    private static readonly NativeMethods.WndProcDelegate WindowProc = WndProc;
    private static readonly Dictionary<nint, StrengthSliderForm> Windows = new();
    private static bool s_isClassRegistered;

    private readonly Action<byte> _alphaChanged;
    private nint _hwnd;
    private byte _alpha;
    private bool _isDragging;
    private bool _isVisible;
    private bool _disposed;
    private int _lastX;
    private int _lastY;

    public StrengthSliderForm(byte alpha, Action<byte> alphaChanged)
    {
        _alphaChanged = alphaChanged;
        SetAlpha(alpha);
    }

    public bool Visible => _isVisible;

    public void Show()
    {
        EnsureWindow();
        _ = NativeMethods.SetWindowPos(_hwnd, nint.Zero, _lastX, _lastY, Width, Height, SwpNoActivate | SwpShowWindow);
        _isVisible = true;
    }

    public void Close()
    {
        Hide();
    }

    public void Hide()
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        _ = NativeMethods.ShowWindow(_hwnd, SwHide);
        _isVisible = false;
    }

    public void SetAlpha(byte alpha)
    {
        var value = (byte)Math.Clamp((int)alpha, SliderMinimum, SliderMaximum);
        if (_alpha == value)
        {
            return;
        }

        _alpha = value;
        if (_hwnd != nint.Zero)
        {
            _ = NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }
    }

    public void PositionNear(WindowSnapshot target)
    {
        var targetBounds = new Rectangle(target.X, target.Y, Math.Max(1, target.Width), Math.Max(1, target.Height));
        var workingArea = Screen.FromRectangle(targetBounds).WorkingArea;
        var left = ClampToRange(target.X + 12, workingArea.Left + EdgePadding, workingArea.Right - Width - EdgePadding);

        var top = target.Y - Height - EdgePadding;
        if (top < workingArea.Top + EdgePadding)
        {
            top = target.Y + EdgePadding;
        }

        top = ClampToRange(top, workingArea.Top + EdgePadding, workingArea.Bottom - Height - EdgePadding);
        var nextLocation = new Point(left, top);

        if (_lastX == nextLocation.X && _lastY == nextLocation.Y)
        {
            return;
        }

        _lastX = nextLocation.X;
        _lastY = nextLocation.Y;
        if (_hwnd != nint.Zero && _isVisible)
        {
            _ = NativeMethods.SetWindowPos(_hwnd, nint.Zero, _lastX, _lastY, Width, Height, SwpNoActivate | SwpShowWindow);
        }
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
            Windows.Remove(_hwnd);
            _ = NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    private void EnsureWindow()
    {
        if (_hwnd != nint.Zero && NativeMethods.IsWindow(_hwnd))
        {
            return;
        }

        RegisterClass();
        _hwnd = NativeMethods.CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            WindowClassName,
            "KakaoTalk Dark Lite Strength",
            WsPopup,
            _lastX,
            _lastY,
            Width,
            Height,
            nint.Zero,
            nint.Zero,
            InstanceHandle,
            nint.Zero);

        if (_hwnd == nint.Zero)
        {
            throw new InvalidOperationException("Failed to create slider window.");
        }

        Windows[_hwnd] = this;
    }

    private static void RegisterClass()
    {
        if (s_isClassRegistered)
        {
            return;
        }

        var windowClass = new NativeMethods.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
            InstanceHandle = InstanceHandle,
            ClassName = WindowClassName
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException("Failed to register slider window class.");
        }

        s_isClassRegistered = true;
    }

    private void Paint(nint hwnd)
    {
        var hdc = NativeMethods.BeginPaint(hwnd, out var paint);
        try
        {
            using var graphics = Graphics.FromHdc(hdc);
            graphics.Clear(Color.FromArgb(26, 28, 31));

        TextRenderer.DrawText(
            graphics,
            "Darkness",
            SystemFonts.MessageBoxFont,
            new Rectangle(12, 9, 70, 20),
            Color.FromArgb(238, 241, 245),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var percentText = $"{Math.Round(_alpha / 255.0 * 100)}%";
        TextRenderer.DrawText(
            graphics,
            percentText,
            SystemFonts.MessageBoxFont,
            new Rectangle(264, 9, 44, 20),
            Color.FromArgb(238, 241, 245),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var trackWidth = TrackRight - TrackLeft;
        var fillWidth = (int)Math.Round(trackWidth * (_alpha / 255.0));
        using var trackBrush = new SolidBrush(Color.FromArgb(69, 75, 84));
        using var fillBrush = new SolidBrush(Color.FromArgb(84, 176, 255));
        using var knobBrush = new SolidBrush(Color.FromArgb(238, 241, 245));
        using var knobBorderPen = new Pen(Color.FromArgb(18, 20, 24));
        graphics.FillRectangle(trackBrush, TrackLeft, TrackY - 2, trackWidth, 4);
        graphics.FillRectangle(fillBrush, TrackLeft, TrackY - 2, fillWidth, 4);

        var knobX = TrackLeft + fillWidth;
        var knobBounds = new Rectangle(knobX - KnobRadius, TrackY - KnobRadius, KnobRadius * 2, KnobRadius * 2);
            graphics.FillEllipse(knobBrush, knobBounds);
            graphics.DrawEllipse(knobBorderPen, knobBounds);
        }
        finally
        {
            _ = NativeMethods.EndPaint(hwnd, ref paint);
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmEraseBackground:
                return 1;
            case WmPaint:
                Paint(hwnd);
                return nint.Zero;
            case WmLeftButtonDown:
                _isDragging = true;
                _ = NativeMethods.SetCapture(hwnd);
                SetAlphaFromPoint(GetX(lParam));
                return nint.Zero;
            case WmMouseMove:
                if (_isDragging || (((int)wParam & MkLeftButton) == MkLeftButton))
                {
                    SetAlphaFromPoint(GetX(lParam));
                }

                return nint.Zero;
            case WmLeftButtonUp:
                _isDragging = false;
                _ = NativeMethods.ReleaseCapture();
                return nint.Zero;
            default:
                return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void SetAlphaFromPoint(int x)
    {
        var clampedX = ClampToRange(x, TrackLeft, TrackRight);
        var alpha = (byte)Math.Round((clampedX - TrackLeft) / (double)(TrackRight - TrackLeft) * SliderMaximum);
        if (_alpha == alpha)
        {
            return;
        }

        _alpha = alpha;
        Invalidate();
        _alphaChanged(alpha);
    }

    private void Invalidate()
    {
        if (_hwnd != nint.Zero)
        {
            _ = NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }
    }

    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        return Windows.TryGetValue(hwnd, out var window)
            ? window.HandleMessage(hwnd, message, wParam, lParam)
            : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static int GetX(nint lParam)
    {
        return (short)((long)lParam & 0xFFFF);
    }

    private static int ClampToRange(int value, int minimum, int maximum)
    {
        return maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);
    }
}

internal readonly record struct WindowSnapshot(nint Handle, int X, int Y, int Width, int Height, string Title, string ClassName)
{
    public long Area => (long)Width * Height;
}

internal static class KakaoTalkWindowFinder
{
    private const int MinimumWidth = 480;
    private const int MinimumHeight = 480;

    public static bool TryGetWindowSnapshot(nint hwnd, out WindowSnapshot snapshot)
    {
        snapshot = default;
        if (hwnd == nint.Zero ||
            !NativeMethods.IsWindow(hwnd) ||
            !NativeMethods.IsWindowVisible(hwnd) ||
            NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < MinimumWidth || height < MinimumHeight)
        {
            return false;
        }

        var title = NativeMethods.GetWindowText(hwnd);
        if (!IsMainTitle(title))
        {
            return false;
        }

        snapshot = new WindowSnapshot(hwnd, rect.Left, rect.Top, width, height, title, NativeMethods.GetClassName(hwnd));
        return true;
    }

    public static WindowSnapshot? FindMainWindow()
    {
        var processes = Process.GetProcessesByName("KakaoTalk");
        HashSet<uint> processIds;
        try
        {
            processIds = processes
                .Select(process => (uint)process.Id)
                .ToHashSet();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        if (processIds.Count == 0)
        {
            return null;
        }

        var candidates = new List<WindowSnapshot>();
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (!processIds.Contains(processId))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < MinimumWidth || height < MinimumHeight)
            {
                return true;
            }

            var title = NativeMethods.GetWindowText(hwnd);
            var className = NativeMethods.GetClassName(hwnd);
            candidates.Add(new WindowSnapshot(hwnd, rect.Left, rect.Top, width, height, title, className));
            return true;
        }, nint.Zero);

        return candidates
            .Where(candidate => IsMainTitle(candidate.Title))
            .OrderByDescending(candidate => candidate.Area)
            .FirstOrDefault();
    }

    private static bool IsMainTitle(string title)
    {
        return string.Equals(title, "카카오톡", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(title, "KakaoTalk", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class NativeOverlay : IDisposable
{
    private const string WindowClassName = "KakaoTalkDarkLite.Overlay";
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
    private static readonly nint InstanceHandle = Marshal.GetHINSTANCE(typeof(NativeOverlay).Module);
    private static readonly NativeMethods.WndProcDelegate WindowProc = WndProc;
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
        if (_ownerHwnd != ownerHwnd)
        {
            _ = NativeMethods.SetWindowLongPtr(_hwnd, GwlHwndParent, ownerHwnd);
            _ownerHwnd = ownerHwnd;
        }

        if (_lastAlpha != alpha)
        {
            _ = NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, LwaAlpha);
            _lastAlpha = alpha;
        }

        if (!_isVisible || _lastX != x || _lastY != y || _lastWidth != width || _lastHeight != height)
        {
            _ = NativeMethods.SetWindowPos(_hwnd, nint.Zero, x, y, width, height, SwpNoActivate | SwpNoZOrder | SwpShowWindow);
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

        _ = NativeMethods.ShowWindow(_hwnd, SwHide);
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
            _ = NativeMethods.DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
    }

    private void EnsureWindow()
    {
        if (_hwnd != nint.Zero && NativeMethods.IsWindow(_hwnd))
        {
            return;
        }

        RegisterClass();
        _hwnd = NativeMethods.CreateWindowEx(
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
            throw new InvalidOperationException("Failed to create overlay window.");
        }
    }

    private static void RegisterClass()
    {
        if (s_isClassRegistered)
        {
            return;
        }

        s_backgroundBrush = NativeMethods.CreateSolidBrush(ToColorRef(2, 5, 10));
        var windowClass = new NativeMethods.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProc),
            InstanceHandle = InstanceHandle,
            BackgroundBrush = s_backgroundBrush,
            ClassName = WindowClassName
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw new InvalidOperationException("Failed to register overlay window class.");
        }

        s_isClassRegistered = true;
    }

    private static uint ToColorRef(byte red, byte green, byte blue)
    {
        return (uint)(red | (green << 8) | (blue << 16));
    }

    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        return message == WmNcHitTest
            ? HtTransparent
            : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }
}

internal static class NativeMethods
{
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    public delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);
    public delegate void WinEventProcDelegate(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint DeviceContext;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Erase;

        public Rect PaintRectangle;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restore;

        [MarshalAs(UnmanagedType.Bool)]
        public bool IncrementalUpdate;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClassEx
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

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventProcDelegate eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(
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
    public static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    public static extern nint BeginPaint(nint hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(nint hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);

    [DllImport("user32.dll")]
    public static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("gdi32.dll")]
    public static extern nint CreateSolidBrush(uint color);

    public static string GetWindowText(nint hwnd)
    {
        var builder = new StringBuilder(256);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    public static string GetClassName(nint hwnd)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }
}
