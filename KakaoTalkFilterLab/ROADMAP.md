# KakaoTalk Filter Lab Roadmap

## Goal
- `Smart` mode should preserve KakaoTalk profile images as close to the source as possible.
- Scope is the full profile-image family, not specific brands or logos.

## In Scope
- Chat list default avatars
- Chat list photo/logo avatars
- Friends tab main profile avatar
- Friends tab update row thumbnails
- Friends tab small list avatars

## Out of Scope
- Sidebar icons
- Header/search/more icons
- Non-profile thumbnails and generic UI artwork
- Brand-specific one-off exceptions

## Current Diagnosis
- The old pipeline over-relied on color/texture connected components.
- That caused repeated false positives and too much tuning on the wrong stage.
- The next stable basis is:
  1. detect likely avatar slots from layout
  2. intersect with actual image/component pixels
  3. preserve the inside almost 그대로
  4. only trim outer fringe pixels

## Working Plan
1. Layout-first avatar slot detection
- Add a first-pass gate that only allows likely profile regions.
- Separate left-column list avatars from horizontal thumbnail rows.

2. Type split after slot detection
- `small-default`
- `small-photo-or-logo`
- `main-default`
- `main-photo-or-logo`

3. Preserve-mask rebuild
- Build masks from `slot shape ∩ actual component`.
- Avoid brand-specific branches.

4. Fringe-only cleanup
- Keep inner content intact.
- Only handle outer anti-aliased edge pixels.

5. Validation set
- Chat list default avatar
- Chat list logo/photo avatar
- Friends main avatar
- Friends update-row thumbnail

## Current Next Step
- Verified: profile-photo interiors are now preserved much more consistently in live `friends` frames and in saved `chat/friends` validation frames.
- Next:
  1. verify/update-row thumbnail row on a fresh live frame
  2. reduce outer white fringe without harming inner logo/photo detail
  3. re-check chat tab and friends tab after fringe cleanup

## Done Recently
- Repo/file structure cleanup
- Captures unified under `KakaoTalkFilterLab/captures`
- Old browser-extension leftovers removed from project root
- Live auto-export/capture path recovered
- `report.txt` now includes captured frame size
- Invalid WGC frame-size fallback added before using WGC frames

## Resume Notes
- If the project folder moves, continue from this file plus `CODEX_CONTINUITY.md`.
- All important paths in the continuity file are workspace-relative on purpose.
