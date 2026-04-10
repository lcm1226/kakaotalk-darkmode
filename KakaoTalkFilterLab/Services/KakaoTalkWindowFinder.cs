using System.Text.RegularExpressions;
using KakaoTalkFilterLab.Models;
using KakaoTalkFilterLab.Native;

namespace KakaoTalkFilterLab.Services;

internal sealed class KakaoTalkWindowFinder
{
    private static readonly Regex MainTitlePattern = new(
        @"^(?:\uCE74\uCE74\uC624\uD1A1|KakaoTalk)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MinimumWidth = 500;
    private const int MinimumHeight = 500;

    public WindowInfo? FindMainWindow()
    {
        var candidates = GetCandidates();

        return candidates
            .Where(IsStrongMainWindowCandidate)
            .OrderByDescending(window => window.Area)
            .FirstOrDefault()
            ?? candidates
                .Where(window => window.Width >= MinimumWidth && window.Height >= MinimumHeight)
                .OrderByDescending(window => window.Area)
                .FirstOrDefault();
    }

    public IReadOnlyList<WindowInfo> GetCandidates()
    {
        return Win32
            .EnumerateVisibleWindowsForProcess("KakaoTalk")
            .Where(window => window.Width > 200 && window.Height > 200)
            .OrderByDescending(window => window.Area)
            .ToList();
    }

    private static bool IsStrongMainWindowCandidate(WindowInfo window)
    {
        if (window.Width < MinimumWidth || window.Height < MinimumHeight)
        {
            return false;
        }

        return MainTitlePattern.IsMatch(window.Title);
    }
}
