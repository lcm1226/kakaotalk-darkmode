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
        var processes = Process.GetProcessesByName("KakaoTalk");
        HashSet<uint> processIds;
        try
        {
            processIds = processes.Select(process => (uint)process.Id).ToHashSet();
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

        var candidates = new List<TargetWindow>();
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
            if (!IsMainWindowTitle(title.ToString()))
            {
                return true;
            }

            candidates.Add(new TargetWindow(
                hwnd,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height));
            return true;
        }, nint.Zero);

        return candidates
            .OrderByDescending(candidate => candidate.Width * candidate.Height)
            .Select(candidate => (TargetWindow?)candidate)
            .FirstOrDefault();
    }

    public static bool TryGetWindowSnapshot(nint hwnd, out TargetWindow target)
    {
        target = default;
        if (hwnd == nint.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) ||
            !NativeMethods.TryGetVisibleBounds(hwnd, out var bounds) ||
            bounds.Width < MinimumWidth || bounds.Height < MinimumHeight ||
            !HasMainWindowTitle(hwnd))
        {
            return false;
        }

        target = new TargetWindow(hwnd, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        return true;
    }

    private static bool HasMainWindowTitle(nint hwnd)
    {
        var title = new StringBuilder(256);
        _ = NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        return IsMainWindowTitle(title.ToString());
    }

    private static bool IsMainWindowTitle(string title)
    {
        var trimmedTitle = title.Trim();
        return string.Equals(trimmedTitle, "KakaoTalk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmedTitle, "\uCE74\uCE74\uC624\uD1A1", StringComparison.OrdinalIgnoreCase);
    }
}
