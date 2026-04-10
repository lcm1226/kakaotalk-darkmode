#Requires AutoHotkey v2.0
#SingleInstance Force
DetectHiddenWindows True

; KakaoTalk main-window dark overlay prototype for Windows 11.
; Run this with AutoHotkey v2 while the KakaoTalk main window is open.

TargetExe := "KakaoTalk.exe"
MainWindowTitleKorean := Chr(0xCE74) Chr(0xCE74) Chr(0xC624) Chr(0xD1A1)
MinimumMainWindowWidth := 500
MinimumMainWindowHeight := 500
OverlayOpacity := 115 ; 0-255. Around 115 is about 45% opacity.
PollIntervalMs := 80

InsetLeft := 0
InsetTop := 0
InsetRight := 0
InsetBottom := 0

PanelW := 220
PanelH := 44
PanelOffsetX := 0
PanelOffsetY := 0

enabled := true
targetHwnd := 0
overlayGui := 0
overlayHwnd := 0
overlayOwnerHwnd := 0
panelGui := 0
panelHwnd := 0
panelOwnerHwnd := 0
opacityLabel := 0
opacitySlider := 0
enabledCheck := 0
overlayShown := false
panelShown := false
lastOverlayX := ""
lastOverlayY := ""
lastOverlayW := ""
lastOverlayH := ""
lastPanelX := ""
lastPanelY := ""

SetTimer(UpdateWindows, PollIntervalMs)
OnExit((*) => DestroyAll())

TraySetIcon("shell32.dll", 174)
A_TrayMenu.Delete()
A_TrayMenu.Add("Show controls", (*) => ShowPanel())
A_TrayMenu.Add("Exit", (*) => ExitApp())

UpdateWindows()

CreateOverlay(ownerHwnd) {
    global overlayGui, overlayHwnd, overlayOwnerHwnd, OverlayOpacity, overlayShown

    DestroyOverlay()

    ; E0x20 = click-through, E0x08000000 = no activate, E0x00080000 = layered.
    overlayGui := Gui("-Caption +ToolWindow +Owner" ownerHwnd " +E0x20 +E0x08000000 +E0x00080000")
    overlayGui.BackColor := "000000"
    overlayGui.MarginX := 0
    overlayGui.MarginY := 0
    overlayGui.Show("NA Hide x0 y0 w1 h1")
    overlayHwnd := overlayGui.Hwnd
    overlayOwnerHwnd := ownerHwnd
    overlayShown := false
    ApplyOverlayOpacity()
}

CreatePanel(ownerHwnd) {
    global panelGui, panelHwnd, panelOwnerHwnd, opacityLabel, opacitySlider, enabledCheck, panelShown
    global OverlayOpacity, PanelW, PanelH

    DestroyPanel()

    panelGui := Gui("-Caption +ToolWindow +Owner" ownerHwnd)
    panelGui.BackColor := "202020"
    panelGui.MarginX := 0
    panelGui.MarginY := 0
    panelGui.SetFont("s9 cFFFFFF", "Segoe UI")

    enabledCheck := panelGui.Add("Checkbox", "x5 y12 w48 h20 Checked", "On")
    enabledCheck.OnEvent("Click", OnEnabledChanged)

    opacitySlider := panelGui.Add("Slider", "x65 y10 w110 h24 Range0-255 ToolTip", OverlayOpacity)
    opacitySlider.OnEvent("Change", OnOpacityChanged)

    opacityLabel := panelGui.Add("Text", "x177 y12 w38 h20 Center", OpacityText())

    panelGui.Show("NA Hide x0 y0 w" PanelW " h" PanelH)
    panelHwnd := panelGui.Hwnd
    panelOwnerHwnd := ownerHwnd
    panelShown := false
}

OnEnabledChanged(*) {
    global enabled, enabledCheck

    enabled := enabledCheck.Value = 1
    UpdateWindows()
}

OnOpacityChanged(*) {
    global OverlayOpacity, opacitySlider, opacityLabel

    OverlayOpacity := opacitySlider.Value
    ApplyOverlayOpacity()
    opacityLabel.Text := OpacityText()
}

OpacityText() {
    global OverlayOpacity
    return Round(OverlayOpacity / 255 * 100) "%"
}

ApplyOverlayOpacity() {
    global OverlayOpacity, overlayHwnd

    if (!IsLiveWindow(overlayHwnd)) {
        return
    }

    try {
        WinSetTransparent(OverlayOpacity, "ahk_id " overlayHwnd)
    }
}

UpdateWindows(*) {
    global enabled, targetHwnd, overlayHwnd, panelHwnd
    global lastOverlayX, lastOverlayY, lastOverlayW, lastOverlayH, lastPanelX, lastPanelY
    global InsetLeft, InsetTop, InsetRight, InsetBottom
    global PanelW, PanelH, PanelOffsetX, PanelOffsetY

    hwnd := FindMainWindow()
    if (!hwnd || WinGetMinMax("ahk_id " hwnd) = -1) {
        HideOverlay()
        HidePanel()
        targetHwnd := 0
        return
    }

    targetHwnd := hwnd

    try {
        WinGetPos(&x, &y, &w, &h, "ahk_id " hwnd)
    } catch {
        HideOverlay()
        HidePanel()
        targetHwnd := 0
        return
    }

    overlayX := x + InsetLeft
    overlayY := y + InsetTop
    overlayW := w - InsetLeft - InsetRight
    overlayH := h - InsetTop - InsetBottom

    if (overlayW <= 0 || overlayH <= 0) {
        HideOverlay()
        HidePanel()
        return
    }

    EnsureOverlay(hwnd)
    EnsurePanel(hwnd)

    if (enabled) {
        if (overlayX != lastOverlayX || overlayY != lastOverlayY || overlayW != lastOverlayW || overlayH != lastOverlayH) {
            SafeWinMove(overlayHwnd, overlayX, overlayY, overlayW, overlayH)
            lastOverlayX := overlayX
            lastOverlayY := overlayY
            lastOverlayW := overlayW
            lastOverlayH := overlayH
        }
        ShowOverlay()
    } else {
        HideOverlay()
    }

    panelX := x + PanelOffsetX
    panelY := y + PanelOffsetY
    if (panelX != lastPanelX || panelY != lastPanelY) {
        SafeWinMove(panelHwnd, panelX, panelY, PanelW, PanelH)
        lastPanelX := panelX
        lastPanelY := panelY
    }
    ShowPanel()
}

FindMainWindow() {
    global TargetExe, MinimumMainWindowWidth, MinimumMainWindowHeight

    hwnds := WinGetList("ahk_exe " TargetExe)
    bestHwnd := 0
    bestArea := 0

    for hwnd in hwnds {
        if (!IsLiveWindow(hwnd) || !DllCall("IsWindowVisible", "ptr", hwnd, "int")) {
            continue
        }

        title := Trim(WinGetTitle("ahk_id " hwnd))
        if (!LooksLikeMainWindowTitle(title)) {
            continue
        }

        try {
            WinGetPos(, , &w, &h, "ahk_id " hwnd)
        } catch {
            continue
        }

        area := w * h
        if (w >= MinimumMainWindowWidth && h >= MinimumMainWindowHeight && area > bestArea) {
            bestHwnd := hwnd
            bestArea := area
        }
    }

    return bestHwnd
}

LooksLikeMainWindowTitle(title) {
    global MainWindowTitleKorean

    return title = MainWindowTitleKorean || title = "KakaoTalk"
}

EnsureOverlay(ownerHwnd) {
    global overlayHwnd, overlayOwnerHwnd, lastOverlayX, lastOverlayY, lastOverlayW, lastOverlayH

    if (!IsLiveWindow(overlayHwnd) || overlayOwnerHwnd != ownerHwnd) {
        CreateOverlay(ownerHwnd)
        lastOverlayX := ""
        lastOverlayY := ""
        lastOverlayW := ""
        lastOverlayH := ""
    }
}

EnsurePanel(ownerHwnd) {
    global panelHwnd, panelOwnerHwnd, lastPanelX, lastPanelY

    if (!IsLiveWindow(panelHwnd) || panelOwnerHwnd != ownerHwnd) {
        CreatePanel(ownerHwnd)
        lastPanelX := ""
        lastPanelY := ""
    }
}

SafeWinMove(hwnd, x, y, w, h) {
    if (!IsLiveWindow(hwnd)) {
        return
    }

    try {
        WinMove(x, y, w, h, "ahk_id " hwnd)
    }
}

IsLiveWindow(hwnd) {
    return hwnd && DllCall("IsWindow", "ptr", hwnd, "int")
}

ShowOverlay() {
    global overlayGui, overlayShown
    if (overlayGui && !overlayShown) {
        overlayGui.Show("NA")
        overlayShown := true
    }
}

HideOverlay() {
    global overlayGui, overlayShown
    if (overlayGui && overlayShown) {
        overlayGui.Hide()
        overlayShown := false
    }
}

ShowPanel() {
    global panelGui, panelShown
    if (panelGui && !panelShown) {
        panelGui.Show("NA")
        panelShown := true
    }
}

HidePanel() {
    global panelGui, panelShown
    if (panelGui && panelShown) {
        panelGui.Hide()
        panelShown := false
    }
}

DestroyOverlay() {
    global overlayGui, overlayHwnd, overlayOwnerHwnd, overlayShown
    if (overlayGui) {
        try overlayGui.Destroy()
    }
    overlayGui := 0
    overlayHwnd := 0
    overlayOwnerHwnd := 0
    overlayShown := false
}

DestroyPanel() {
    global panelGui, panelHwnd, panelOwnerHwnd, panelShown
    if (panelGui) {
        try panelGui.Destroy()
    }
    panelGui := 0
    panelHwnd := 0
    panelOwnerHwnd := 0
    panelShown := false
}

DestroyAll() {
    DestroyOverlay()
    DestroyPanel()
}
