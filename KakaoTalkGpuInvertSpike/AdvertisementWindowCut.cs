using System.Runtime.InteropServices;
using System.Text;

namespace KakaoTalkGpuInvertSpike;

// Clip the owned ad popup and child surfaces that can paint beyond the main HWND's region.
internal sealed class AdvertisementWindowCut : IDisposable
{
    private readonly Dictionary<nint, WindowRegionCut> _regions = [];
    public int? AdvertisementTop { get; private set; }

    public void Apply(TargetWindow main, int pixels)
    {
        AdvertisementTop = null;
        if (pixels <= 0) Dispose();
        NativeMethods.GetWindowThreadProcessId(main.Handle, out var mainProcess);
        var found = new List<TargetWindow>();
        NativeMethods.EnumWindows((window, _) =>
        {
            if (GetWindow(window, 4) != main.Handle || !NativeMethods.IsWindowVisible(window)) return true;
            NativeMethods.GetWindowThreadProcessId(window, out var process);
            if (process != mainProcess) return true;
            var title = new StringBuilder(256);
            NativeMethods.GetWindowText(window, title, title.Capacity);
            if (title.Length != 0 || !NativeMethods.GetWindowRect(window, out var bounds)) return true;
            // Exclude detached conversations, dialogs and popups outside the bottom content band.
            if (bounds.Left < main.X || bounds.Right > main.X + main.Width ||
                bounds.Top < main.Y + main.Height / 2 || bounds.Bottom > main.Y + main.Height ||
                bounds.Width <= 0 || bounds.Height <= 0) return true;
            var hasWebView = false;
            EnumChildWindows(window, (child, _) =>
            {
                var className = new StringBuilder(128);
                GetClassName(child, className, className.Capacity);
                hasWebView = className.ToString() == "Chrome_RenderWidgetHostHWND";
                return !hasWebView;
            }, 0);
            if (hasWebView)
                found.Add(new TargetWindow(window, bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            return true;
        }, 0);

        AdvertisementTop = found.Count == 0 ? null : found.Min(target => target.Y);
        if (pixels <= 0) return;
        var boundary = main.Y + WindowRegionCut.VisibleHeight(main.Height, pixels);
        foreach (var parent in found.Select(target => target.Handle).Prepend(main.Handle).ToArray())
        {
            EnumChildWindows(parent, (child, _) =>
            {
                if (NativeMethods.IsWindowVisible(child) && NativeMethods.GetWindowRect(child, out var bounds) &&
                    bounds.Width > 0 && bounds.Height > 0 && bounds.Bottom > boundary &&
                    bounds.Left >= main.X && bounds.Right <= main.X + main.Width &&
                    bounds.Top >= main.Y && bounds.Bottom <= main.Y + main.Height)
                    found.Add(new TargetWindow(child, bounds.Left, bounds.Top, bounds.Width, bounds.Height));
                return true;
            }, 0);
        }
        found = found.DistinctBy(target => target.Handle).ToList();
        foreach (var target in found)
        {
            if (!_regions.TryGetValue(target.Handle, out var region))
                _regions.Add(target.Handle, region = new WindowRegionCut());
            region.Apply(target, Math.Clamp(target.Y + target.Height - boundary, 0, target.Height), allowEmpty: true);
        }
        var current = found.Select(target => target.Handle).ToHashSet();
        foreach (var window in _regions.Keys.Where(window => !current.Contains(window)).ToArray())
        {
            _regions[window].Dispose();
            _regions.Remove(window);
        }
    }

    public void Dispose()
    {
        foreach (var region in _regions.Values) region.Dispose();
        _regions.Clear();
        AdvertisementTop = null;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, NativeMethods.EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder text, int length);
}
