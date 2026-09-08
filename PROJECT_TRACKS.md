# Project Tracks

This repository keeps stable and experimental rendering paths separate.

## Simple

`KakaoTalkDarkLite` is the stable, low-cost product track. It uses a native,
click-through tint overlay and does not capture or process KakaoTalk pixels.
Changes to this track should remain conservative.

## GPU Invert Spike

`KakaoTalkGpuInvertSpike` is an isolated performance experiment. Its target
pipeline is:

```text
Windows.Graphics.Capture D3D11 surface
    -> D3D11 pixel shader
    -> click-through DXGI swap-chain overlay
```

The spike must not copy captured frames through `SoftwareBitmap`, managed pixel
arrays, or WPF `BitmapSource`. It can replace the older CPU Invert path only
after visual correctness, input pass-through, frame pacing, CPU, GPU, and
memory usage are measured on the real KakaoTalk window.

The current tray controls include Enabled, Full privacy (`Ctrl+H`), Auto
privacy, Focus Reveal (`Ctrl+Shift+H`), and strength-control display (`Ctrl+B`).
The three hotkeys are registered only while a KakaoTalk process window or the
compact control has foreground focus, so they remain available to unrelated apps. A
left-button double-click on the tray icon also displays the compact control.
The 411x40 compact control exposes
synchronized GPU invert, Privacy, and Auto checkboxes, a Focus Reveal eye
button, a 0-100% slider, and a close button. Focus Reveal temporarily removes
Full privacy for eight seconds and then locks the content again. Auto privacy
masks content whenever KakaoTalk loses foreground focus. Disabling GPU invert
pauses GPU rendering and hides the capture overlay without disabling privacy.
Close requests hide the compact control instead of disposing it, and the
application recreates the control if an external window message disposes it.
The `Cut` numeric input after Auto clips physical pixels from the main window's
bottom edge (default 125, zero restores the original region). It saves each
committed value immediately as `BottomCutPixels`; valid typed values commit after
250 ms without requiring Enter or focus loss. A native region clips the main
window without changing its layout. Its DWM non-client rendering is disabled
while cut is active to prevent a white surface in the removed area, and restored
when cut is removed. The GPU window and output buffer use the visible height,
with texture coordinates preserving the original content scale. The privacy
fallback ends at the same boundary. KakaoTalk's bottom advertisement is
an owned top-level WebView popup, so its region is clipped separately to the
same screen boundary (including an empty region for a fully removed ad).
Detection requires direct main-window ownership, the same process, an empty
title, bounds contained in the lower half of the main window, and a Chromium
render child. Descendant surfaces crossing the bottom boundary are clipped too.
Detached conversation windows are not included. Normal exit and target changes
restore the original regions. Forced process termination cannot run restoration;
restart KakaoTalk if its window remains clipped after an abnormal exit.
`CutVerification` exercises native region boundaries, restoration, resizing,
settings serialization, and the input callback without focusing visible apps.
Its optional live test checks WGC and background hit testing, with a bounded
foreground announcement and restoration. Live validation must stop when OS
foreground ownership cannot be confirmed.
Capture border suppression uses the runtime session3 interface rather than
reflection on the 19041 projection, which does not expose IsBorderRequired.
Support and the returned border requirement are logged; Windows may still
require consent or display a border requested by another capture session.
The compact control sits flush against the main window with no vertical gap.
A four-DIP input strip just inside the visible bottom edge resizes the actual
main window while keeping Cut unchanged. It is owned by the main window and
does not repeatedly raise itself above unrelated applications.
When FancyZones marks the main window with `FancyZones_zones` or
`FancyZones_zones_max128`, native height receives one hidden-tail allowance so
the visible bottom matches the zone. Window properties track the base/applied
heights to avoid accumulated expansion across refreshes. Manual resizing updates
the baseline; zero Cut and normal shutdown restore the uncompensated zone height.
This is placement compensation, not a change to Windows' definition of HWND bounds.
The installed single-file build is named `KakaoDark.exe`; the singleton and
settings location remain unchanged. The old startup executable and obsolete
`kakao_crop` script are backed up outside their operating paths.
Settings are stored on graceful exit in
`%LOCALAPPDATA%\KakaoTalkGpuInvertSpike\settings.json`.

When a GPU frame is visible, privacy remains a branch in the existing shader.
The full title-bar strip and left sidebar stay visible while the conversation
content below the title bar is masked. When GPU invert is disabled or its
pipeline is recovering, one solid native mask window covers the same content
region without WGC or pixel processing. This fallback window is disabled,
transparent to hit testing, and verified with `WindowFromPoint` before it
remains visible.

Window discovery accepts only a visible KakaoTalk process window whose title is
exactly `KakaoTalk` or the Korean localized KakaoTalk title. It never falls back
to a large untitled or room-titled window, so detached chat windows remain
excluded.

KakaoTalk location events are marshalled to the WinForms UI thread and
coalesced into 100 ms refreshes. The coalescing flag remains set for the full
debounce period, so repeated Win32/AHK window-position updates cannot flood the
UI message queue. Overlay and strength-control position calls are skipped when
their bounds have not changed.

The overlay is a disabled native window in addition to using transparent and
non-activating extended styles. This removes it from mouse hit testing across
process boundaries while keeping the DXGI content visible. WGC rendering is
capped at 30 FPS, the DXGI device queue is limited to one frame, and the shader
uses point sampling and branch-only edge masking to reduce latency and GPU work.

Transient WGC startup or frame errors use bounded exponential retry instead of
disabling the effect or retrying continuously. WGC callbacks are detached and
drained before capture resources are released. Runtime status is written to
`spike.log`, and process lifecycle or unhandled exceptions are written to
`crash.log` in the same local settings directory. Both logs rotate at 1 MB.
An `active-session.txt` marker remains after an unclean shutdown so the next
launch can record the interrupted session in `crash.log`.
Launching the executable while an instance is already running signals that
instance to clear its retry backoff and refresh immediately instead of exiting
silently with no recovery action.

For one-shot visual diagnostics only, set `KAKAOTALK_GPU_CAPTURE_PATH` to a PNG
path before launch. The renderer then reads back and saves its first processed
backbuffer. Normal launches never create a staging texture or perform this
readback.

Set `KAKAOTALK_GPU_OVERLAY_SMOKE_TEST=1` to run a one-shot native overlay
smoke test without opening or capturing KakaoTalk. This verifies window
creation and click-through behavior while leaving the normal pipeline idle.
When combined with `KAKAOTALK_GPU_CAPTURE_PATH`, the smoke test renders a
synthetic GPU frame through the strength and privacy shader settings. Set
`KAKAOTALK_GPU_CONTROL_CAPTURE_PATH` to capture the rendered strength control.
Set `KAKAOTALK_GPU_AUTO_EXIT_MS` to 1000-120000 during lifecycle QA to exercise
the normal WGC and GPU disposal path after the requested number of milliseconds.

## Filter Lab

`KakaoTalkFilterLab` preserves the existing `Dim`, `Invert`, `Smart`, and
`Dark` prototypes and remains the reference implementation for behavior and
historical experiments. It is not the stable low-cost track.

## AHK Prototype

`kakaotalk-dark-overlay.ahk` is a completed prototype and is left unchanged
unless work on it is explicitly requested.
