# Codex Continuity

## Current Status
- WPF project: `KakaoTalkFilterLab`
- AHK prototype is intentionally untouched.
- Main focus remains `Smart` mode profile-avatar preservation.

## What Was Verified
- Live capture/export is working again.
- `KakaoTalkFilterLab/captures/report.txt` now shows both:
  - target window bounds
  - actual captured frame size
- Recent live logs showed repeated successful capture with:
  - `Capture=WGC active`
  - `Bounds=594x813`
  - `Frame=594x813`
- On current live `friends` frame, profile-photo interiors appear preserved instead of being fully inverted.
- Saved validation frames that are currently useful:
  - `KakaoTalkFilterLab/captures/case-friends-1/source.png`
  - `KakaoTalkFilterLab/captures/case-friends-1/smart.png`
  - `KakaoTalkFilterLab/captures/case-chat-1/source.png`
  - `KakaoTalkFilterLab/captures/case-chat-1/smart.png`
  - `KakaoTalkFilterLab/captures/case-chat-2/source.png`
  - `KakaoTalkFilterLab/captures/case-chat-2/smart.png`

## Key Code Areas
- Capture validation and export:
  - `KakaoTalkFilterLab/MainWindow.xaml.cs`
- Window bounds and window enumeration:
  - `KakaoTalkFilterLab/Native/Win32.cs`
- Smart avatar preservation logic:
  - `KakaoTalkFilterLab/Services/WindowCaptureService.cs`

## Important Recent Changes
- Added invalid-size rejection for bad WGC frames before accepting them.
- Export report now records `Frame=width,height`.
- Smart avatar preservation currently restores original pixels for preserved avatar/photo regions rather than partial color blending.
- Avatar handling is layout-first:
  - left list avatars
  - thumbnail row avatars

## Remaining Work
1. Verify thumbnail-row photo avatars on a fresh live `friends` frame.
2. Reduce outer white fringe on avatars without damaging inner logo/photo detail.
3. Re-check both:
   - `friends` tab
   - `chat` tab
4. If still stable, keep the current preservation behavior and only tune fringe logic.

## Build / Run
- Build:
  - `dotnet build .\\KakaoTalkFilterLab\\KakaoTalkFilterLab.csproj`
- Run:
  - `KakaoTalkFilterLab\\bin\\Debug\\net8.0-windows10.0.19041.0\\KakaoTalkFilterLab.exe`

## How To Resume After Folder Move
- Open the moved workspace root.
- Read:
  1. `KakaoTalkFilterLab/ROADMAP.md`
  2. `KakaoTalkFilterLab/CODEX_CONTINUITY.md`
- Then continue from the `Remaining Work` section.
- Do not rely on old absolute paths from previous threads; use these repo-relative paths instead.
