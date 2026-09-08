using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KakaoTalkGpuInvertSpike;

// FancyZones uses the native rectangle. Keep the hidden tail outside that rectangle.
internal sealed class ZoneCutAlignment : IDisposable
{
    private const string BaseHeightProperty = "KakaoDark.ZoneBaseHeight";
    private const string AppliedHeightProperty = "KakaoDark.ZoneAppliedHeight";
    private nint _window;
    private uint _process;
    private uint _thread;

    public TargetWindow Apply(TargetWindow target, int cut, bool manualResize = false, bool moving = false)
    {
        var thread = NativeMethods.GetWindowThreadProcessId(target.Handle, out var process);
        if (_window != target.Handle || _thread != thread || _process != process)
        {
            Dispose();
            _window = target.Handle;
            _process = process;
            _thread = thread;
        }
        if (moving || IsZoomed(_window) || !NativeMethods.GetWindowRect(_window, out var bounds)) return target;
        if (!IsZoned(_window))
        {
            Forget();
            return target;
        }
        var applied = (int)GetProp(_window, AppliedHeightProperty);
        var baseline = (int)GetProp(_window, BaseHeightProperty);
        if (manualResize)
            baseline = Math.Max(1, bounds.Height - cut);
        else if (baseline <= 0 || applied != bounds.Height)
            baseline = bounds.Height;
        var desired = checked(baseline + Math.Clamp(cut, 0, 10000));
        if (bounds.Height != desired)
        {
            // Stamp before SetWindowPos: location events may arrive synchronously.
            Stamp(baseline, desired);
            if (!NativeMethods.SetWindowPos(_window, 0, bounds.Left, bounds.Top, bounds.Width, desired,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder))
            {
                Forget();
                throw new Win32Exception("Cannot align the visible bottom with its FancyZones placement.");
            }
            NativeMethods.GetWindowRect(_window, out bounds);
        }
        Stamp(baseline, bounds.Height);
        if (cut == 0) Forget();
        return NativeMethods.TryGetVisibleBounds(_window, out var visible)
            ? target with { X = visible.Left, Y = visible.Top, Width = visible.Width, Height = visible.Height }
            : target;
    }

    public void Dispose()
    {
        var thread = NativeMethods.GetWindowThreadProcessId(_window, out var process);
        if (_window != 0 && thread == _thread && process == _process)
        {
            var baseline = (int)GetProp(_window, BaseHeightProperty);
            var applied = (int)GetProp(_window, AppliedHeightProperty);
            if (IsZoned(_window) && baseline > 0)
            {
                if (NativeMethods.IsIconic(_window) || IsZoomed(_window))
                {
                    var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
                    if (GetWindowPlacement(_window, ref placement) && placement.Normal.Height == applied)
                    {
                        placement.Normal.Bottom = placement.Normal.Top + baseline;
                        SetWindowPlacement(_window, ref placement);
                    }
                }
                else if (NativeMethods.GetWindowRect(_window, out var bounds) && bounds.Height == applied)
                    NativeMethods.SetWindowPos(_window, 0, bounds.Left, bounds.Top, bounds.Width, baseline,
                        NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
            }
            Forget();
        }
        _window = 0;
    }

    private void Stamp(int baseline, int applied)
    {
        if (!SetProp(_window, BaseHeightProperty, baseline) || !SetProp(_window, AppliedHeightProperty, applied))
            throw new Win32Exception("Cannot track the compensated window height.");
    }

    private void Forget()
    {
        RemoveProp(_window, BaseHeightProperty);
        RemoveProp(_window, AppliedHeightProperty);
    }

    private static bool IsZoned(nint window) =>
        GetProp(window, "FancyZones_zones") != 0 || GetProp(window, "FancyZones_zones_max128") != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length, Flags, ShowCommand;
        public NativeMethods.Point Minimum, Maximum;
        public NativeMethods.Rect Normal;
    }
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(nint window, ref Placement placement);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string name);
}
