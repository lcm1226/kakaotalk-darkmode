# KakaoDark Portable 0.1.1

This release fixes screenshot Privacy masking, mixed-DPI bottom clipping, and detection of narrow KakaoTalk main windows. It replaces 0.1.0 for normal use.

## Download and Upgrade

Download `KakaoDark-0.1.1-portable-win-x64.zip`, extract it, and run `KakaoDark.exe`. The Windows x64 executable is self-contained; no separate .NET installation is required.

Before upgrading, use **Exit** in the old KakaoDark tray menu. Closing the compact control only hides it. The application has a singleton guard, so launching a new EXE while the old one is running merely asks the old process to refresh.

Keep the EXE at a stable local path if you use a Startup shortcut. Settings are stored in `%LOCALAPPDATA%\KakaoTalkGpuInvertSpike\settings.json` and remain on that PC. No user settings or captured chat screenshots are included in the ZIP.

## Screenshot Privacy Fix

### Reproduction

The installed EXE and the 0.1.0 release EXE had identical SHA-256 hashes. With GPU Invert enabled and Privacy in StatusOnly mode, a direct desktop-region screenshot contained the original chat content.

The cause was the native fallback mask being hidden after the first GPU frame. Privacy was then drawn only inside the GPU surface, which is intentionally excluded from capture to avoid capturing the filter itself.

### Change

- Keep a separate, normal native Privacy mask visible while Privacy is active, even when GPU frames are available.
- Position it within the main window's owned-window stack rather than repeatedly raising it above unrelated foreground applications.
- Preserve the existing shader mask for the displayed GPU output.
- Preserve Off -> StatusOnly -> Full -> Off cycling, the visible time/unread column, Auto privacy, and Peek.
- GPU OFF uses the native white mask and now disposes the capture pipeline instead of only pausing frame rendering.

### Evidence and Limits

A fresh desktop-region screenshot after the change contained a black conversation mask and a visible right-hand status column. The same sampled conversation pixel changed from original content to opaque black.

This protects ordinary composited desktop/region screenshots in the checked environment. APIs that read only KakaoTalk's source HWND, such as PrintWindow or direct window WGC, can bypass an external mask. This release does not claim protection against every capture method or a camera photographing the screen.

## Mixed-DPI Bottom Cut Fix

### Reproduction

KakaoTalk used system DPI 120 while the overlay monitor used DPI 144. A region requested with physical coordinates expanded when Windows interpreted it in the target window's coordinates. One observed main window was 640x918 physical pixels while its applied region was 768x958, so the supposed cut did not remove the bottom and a white ad background remained.

### Change

- Read physical bounds in a per-monitor DPI context.
- Convert the requested boundary to the target HWND's coordinate space.
- Read, compare, apply, and restore HRGNs in that same target DPI context.
- Restore the caller's thread DPI context after every scoped operation, including exceptional paths.
- Continue clipping the main window, owned ad popup, and crossing child surfaces without substituting a white cover.

### Evidence

- A Cut value of 120 produced an actual 120-physical-pixel cut in the observed mixed-DPI configuration; hit testing below the edge reached the background application.
- The local ad band required Cut 145 to remove its entire frame. The resulting advertisement HWND had an empty region, not an opaque cover.
- Integer scaling can conservatively remove up to one additional physical pixel.
- The local Cut value was adjusted for this machine only. The shipped default remains 125, and existing per-PC values remain preserved.

## Narrow Main-Window Detection

The previous fixed 500x500 physical-pixel threshold could reject a standard main window at 100% scaling. Detection now uses a 240x300 logical-pixel minimum scaled by GetDpiForWindow. KakaoTalk process and exact main-title checks still exclude detached conversation windows.

Regression cases include a 350x600 window at 100% and its equivalent 525x900 window at 150%. A separate Windows 10 desktop hardware acceptance has not been performed; the application continues to target Windows 10 x64-compatible APIs.

## Verification

- 59 isolated checks passed, covering Privacy cycling, GPU mask pixels, native geometry, legacy settings, cut restoration, resize calculations, FancyZones compensation, narrow-window detection, and mixed-DPI coordinate conversion.
- Release build and self-contained win-x64 publication succeeded.
- Direct before/after desktop-region screenshots were inspected locally; private screenshots are not uploaded to GitHub.
- Native region and hit-test readings confirmed removal of the ad frame and input reaching the background.
- User-adjusted Invert strength and Privacy settings were preserved during replacement. Strength 0 means no visual inversion even if the GPU checkbox is enabled.
- A further automated Ctrl+H input check was not completed because of the bounded foreground interval. Remote-browser restoration later stopped under Computer Use policy; no further UI input was issued.

The executable is unsigned and Windows SmartScreen may warn on first launch. Exit normally through the tray to restore modified window regions; if force termination leaves KakaoTalk clipped, restart KakaoTalk.
