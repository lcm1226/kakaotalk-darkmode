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

The current tray controls include Enabled, Privacy mode (`Ctrl+H`), and a
compact invert-strength window with an enable checkbox, a 0-100% slider, and a
close button. Disabling the effect from this window pauses GPU rendering and
hides the overlay without rebuilding the WGC session, so re-enabling is
immediate. Settings are stored on graceful exit in
`%LOCALAPPDATA%\KakaoTalkGpuInvertSpike\settings.json`.

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

## Filter Lab

`KakaoTalkFilterLab` preserves the existing `Dim`, `Invert`, `Smart`, and
`Dark` prototypes and remains the reference implementation for behavior and
historical experiments. It is not the stable low-cost track.

## AHK Prototype

`kakaotalk-dark-overlay.ahk` is a completed prototype and is left unchanged
unless work on it is explicitly requested.
