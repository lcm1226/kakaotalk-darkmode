# Codex Continuity

## Current Status
- WPF project: `KakaoTalkFilterLab`
- AHK prototype is intentionally untouched.
- Main focus remains `Smart` mode profile-avatar preservation.

## Important Paths
- Build output to run: `KakaoTalkFilterLab/bin/Debug/net8.0-windows10.0.19041.0/KakaoTalkFilterLab.exe`
- Live captures: `KakaoTalkFilterLab/captures`
- KakaoTalk PC install checked: `C:/Program Files (x86)/Kakao/KakaoTalk`

## What Was Verified
- Live capture/export works with `WGC active`.
- `report.txt` records target bounds and actual frame size.
- The Filter Lab app now follows Windows app light/dark mode and closes to tray instead of exiting.
- KakaoTalk PC resources expose the official profile squircle path at:
  - `skin/default/image/profileShapeSquircleSVGs/Combined/profileShpeSquircleOne.svg`
- `Resource.xml` maps default profile resources to squircle thumbnail IDs:
  - `img_profile44_01`
  - `img_profile40_01`
  - `img_profile36_01`
- Main layout XML files in the install folder are not readable as plain coordinate specs, so exact slot bounds still need runtime bitmap/layout inference.

## Key Code Areas
- Capture validation and export:
  - `KakaoTalkFilterLab/MainWindow.xaml.cs`
- Window bounds and window enumeration:
  - `KakaoTalkFilterLab/Native/Win32.cs`
- Smart avatar preservation logic:
  - `KakaoTalkFilterLab/Services/WindowCaptureService.cs`

## Current Smart Strategy
- Detect likely profile slots first.
- Use color/texture components only as evidence that a profile image exists in the slot.
- Preserve original pixels inside a Kakao official squircle shape for profile slots.
- Keep non-profile UI icons excluded.

## Remaining Work
1. Verify official squircle masking on fresh live chat and friends frames.
2. If profile photos/logos still invert inconsistently, switch detected profile slots to slot-fill preservation instead of component-proximity preservation.
3. Reduce the outer 1px light fringe without damaging inner photo/logo detail.
4. Re-check chat tab and friends tab before committing future Smart tuning changes.

## Build / Run
- Build:
  - `dotnet build .\KakaoTalkFilterLab\KakaoTalkFilterLab.csproj`
- Run:
  - `KakaoTalkFilterLab\bin\Debug\net8.0-windows10.0.19041.0\KakaoTalkFilterLab.exe`

## How To Resume After Folder Move
- Open the moved workspace root.
- Read:
  1. `KakaoTalkFilterLab/ROADMAP.md`
  2. `KakaoTalkFilterLab/CODEX_CONTINUITY.md`
- Then continue from the `Remaining Work` section.
- Do not rely on old absolute paths from previous threads; use these repo-relative paths instead.
