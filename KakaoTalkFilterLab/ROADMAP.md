# KakaoTalk Filter Lab Roadmap

## Goal
- `Smart` mode should preserve KakaoTalk profile-image regions while keeping text close to `Invert` clarity.
- Scope is the full profile-image family, not specific brands or logos.

## In Scope
- Chat list default avatars
- Chat list photo/logo avatars
- Friends tab main profile avatar
- Friends tab update-row thumbnails
- Friends tab small list avatars
- Multi-avatar chat thumbnails when they occupy the profile slot

## Out of Scope
- Sidebar icons
- Header/search/more icons
- Section headings, buttons, badges, and non-profile UI artwork
- Brand-specific one-off exceptions

## Current Diagnosis
- The old pipeline over-relied on color/texture connected components.
- This missed low-saturation logo/profile images and created false positives when color-only rules were widened.
- KakaoTalk PC installation resources confirm the official profile shape is a squircle path:
  - `skin/default/image/profileShapeSquircleSVGs/Combined/profileShpeSquircleOne.svg`
  - Resource IDs include `img_profile44_01`, `img_profile40_01`, and `img_profile36_01`.
- Main layout XML files are not usable as plain coordinate specs in the installed folder, so runtime bitmap/layout detection is still required.

## Working Plan
1. Layout-first avatar slot detection
- Gate preservation to likely profile columns/rows before color rules.
- Keep sidebar/header/UI icons excluded.

2. Slot-aware profile handling
- Left chat/friends list avatars
- Friends update-row thumbnails
- Main profile row avatar
- Multi-avatar chat profile blocks

3. Official shape masking
- Use Kakao's official squircle path for profile-slot preservation.
- Use actual component pixels only as evidence for finding the slot, not as the final visible shape when the slot is known.

4. Fringe cleanup
- Remove only the outer light fringe created by preserving light-mode anti-aliased edges.
- Do not damage inner logo/photo detail.

5. Validation set
- Chat tab: default avatar, photo avatar, logo avatar, multi-avatar group
- Friends tab: main avatar, update-row thumbnail, birthday/favorites small avatars

## Current Next Step
- Verify the official squircle path on fresh live chat/friends frames.
- If photos/logos are still inconsistent, move from component-preserve to slot-fill preserve for detected profile slots.
- Then tune only the outer 1px fringe.

## Done Recently
- Repo/file structure cleanup
- Captures unified under `KakaoTalkFilterLab/captures`
- Live auto-export/capture path recovered
- Invalid WGC frame-size fallback added before using WGC frames
- App UI follows system light/dark mode resources
- Close button minimizes the Filter Lab app to the system tray
- Official KakaoTalk profile squircle path was extracted from the installed PC client resources and applied to Smart masking

## Resume Notes
- If the project folder moves, continue from this file plus `CODEX_CONTINUITY.md`.
- All important paths in the continuity file are workspace-relative on purpose.
