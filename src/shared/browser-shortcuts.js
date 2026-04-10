import { comboFromCodes } from "./keys.js";

const UNSUPPORTED_TARGET_COMBOS = new Map([
  [comboFromCodes(["ControlLeft", "KeyL"]).signature, "Ctrl + L is a browser shortcut, so a page script cannot trigger the address bar."],
  [comboFromCodes(["ControlLeft", "ShiftLeft", "Tab"]).signature, "Ctrl + Shift + Tab is a browser shortcut, so a page script cannot switch browser tabs."],
  [comboFromCodes(["ControlLeft", "Tab"]).signature, "Ctrl + Tab is a browser shortcut, so a page script cannot switch browser tabs."],
  [comboFromCodes(["ControlLeft", "KeyT"]).signature, "Ctrl + T is a browser shortcut, so a page script cannot open a browser tab."],
  [comboFromCodes(["ControlLeft", "KeyW"]).signature, "Ctrl + W is a browser shortcut, so a page script cannot close a browser tab."],
  [comboFromCodes(["MetaLeft", "KeyL"]).signature, "Meta + L is a browser shortcut, so a page script cannot trigger the address bar."],
  [comboFromCodes(["MetaLeft", "ShiftLeft", "BracketLeft"]).signature, "Meta + Shift + [ is a browser shortcut on some Chromium builds and cannot be replayed reliably from a page."],
  [comboFromCodes(["MetaLeft", "ShiftLeft", "BracketRight"]).signature, "Meta + Shift + ] is a browser shortcut on some Chromium builds and cannot be replayed reliably from a page."]
]);

export function getUnsupportedTargetReason(combo) {
  return UNSUPPORTED_TARGET_COMBOS.get(combo?.signature || "") || "";
}
