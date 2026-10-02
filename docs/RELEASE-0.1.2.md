# KakaoDark Portable 0.1.2

This release makes the GPU Invert overlay visible to desktop and remote-session
capture, including Chrome Remote Desktop. It replaces 0.1.1 for users who need to
view KakaoTalk through a remote connection.

## Download and Upgrade

Download `KakaoDark-0.1.2-portable-win-x64.zip`, extract it, and run
`KakaoDark.exe`. The Windows x64 executable is self-contained; no separate .NET
installation is required.

Before upgrading, use **Exit** in the old KakaoDark tray menu. Closing the compact
control only hides it. The application has a singleton guard, so launching a new
EXE while the old one is running merely asks the old process to refresh.

Settings remain in `%LOCALAPPDATA%\KakaoTalkGpuInvertSpike\settings.json` on that
PC. No user settings or captured chat screenshots are included in the ZIP.

## Remote Desktop Capture Compatibility

### Cause

The 0.1.1 GPU overlay window was assigned
`WDA_EXCLUDEFROMCAPTURE` (`SetWindowDisplayAffinity`). This intentionally hides
the overlay from desktop capture. In a remote desktop session, the source
KakaoTalk window was therefore visible but the separately rendered inverted
overlay was omitted, making that area appear black or unfiltered.

### Change

- Remove the default capture-exclusion flag from the GPU overlay.
- Keep the GPU overlay click-through, non-activating, and independently rendered.
- Keep the separate native Privacy mask visible to ordinary desktop/region
  screenshots when Privacy is enabled.
- Do not change the source-window capture, shader, inversion strength, or privacy
  behavior.

This change allows the composed desktop image, including the GPU overlay, to be
captured by remote desktop and screenshot tools. It also means tools that capture
the whole desktop can see the overlay, as expected. Capture APIs that read only
the KakaoTalk source HWND can still omit external overlays and masks.

## Verification

- The Release build succeeded with zero warnings and zero errors.
- All 59 existing headless `CutVerification` checks passed without bringing an
  application window to the foreground.
- The generated control and Privacy render diagnostics were visually inspected.
- A self-contained single-file win-x64 publish succeeded. The portable ZIP
  contains only `KakaoDark.exe` and `README.txt`.
- The overlay was not launched over the user's live desktop, and Chrome Remote
  Desktop behavior on the Windows 10 desktop still requires confirmation after
  installing this release. That hardware acceptance is not claimed here.

The executable is unsigned and Windows SmartScreen may warn on first launch.
