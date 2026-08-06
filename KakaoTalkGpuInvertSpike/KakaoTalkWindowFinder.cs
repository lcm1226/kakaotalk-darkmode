using System.Diagnostics;
using System.Text;

namespace KakaoTalkGpuInvertSpike;

internal readonly record struct TargetWindow(nint Handle, int X, int Y, int Width, int Height);

internal static class KakaoTalkWindowFinder
{
    private const int MinimumWidth = 500;
    private const int MinimumHeight = 500;

    public static TargetWindow? FindMainWindow()
    {
        var processIds = Process.GetProcessesByName("KakaoTalk")
            .Select(process => (uint)process.Id)
            .ToHashSet();
        if (processIds.Count == 0)
        {
            return null;
        }

        var candidates = new List<(TargetWindow Window, bool Strong)>();
        _ = NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (!processIds.Contains(processId) || !NativeMethods.TryGetVisibleBounds(hwnd, out var bounds))
            {
                return true;
            }

            if (bounds.Width < MinimumWidth || bounds.Height < MinimumHeight)
            {
                return true;
            }

            var title = new StringBuilder(256);
            _ = NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            var titleText = title.ToString().Trim();
            var strong = string.Equals(titleText, "KakaoTalk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(titleText, "\uCE74\uCE74\uC624\uD1A1", StringComparison.OrdinalIgnoreCase);
            candidates.Add((new TargetWindow(
                hwnd,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height), strong));
            return true;
        }, nint.Zero);

        return candidates
            .OrderByDescending(candidate => candidate.Strong)
            .ThenByDescending(candidate => candidate.Window.Width * candidate.Window.Height)
            .Select(candidate => (TargetWindow?)candidate.Window)
            .FirstOrDefault();
    }

    public static bool TryGetWindowSnapshot(nint hwnd, out TargetWindow target)
    {
        target = default;
        if (hwnd == nint.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) ||
            !NativeMethods.TryGetVisibleBounds(hwnd, out var bounds) ||
            bounds.Width < MinimumWidth || bounds.Height < MinimumHeight)
        {
            return false;
        }

        target = new TargetWindow(hwnd, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        return true;
    }
}
