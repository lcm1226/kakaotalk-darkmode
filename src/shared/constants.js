export const STORAGE_KEY = "remapKeyEverywhereRules";
export const MAX_COMBO_KEYS = 4;
export const MODIFIER_CODES = new Set([
  "ShiftLeft",
  "ShiftRight",
  "ControlLeft",
  "ControlRight",
  "AltLeft",
  "AltRight",
  "MetaLeft",
  "MetaRight"
]);

export const MODIFIER_SORT_ORDER = {
  ControlLeft: 1,
  ControlRight: 1,
  AltLeft: 2,
  AltRight: 2,
  ShiftLeft: 3,
  ShiftRight: 3,
  MetaLeft: 4,
  MetaRight: 4
};

export const CODE_LABELS = {
  Backspace: "Backspace",
  Tab: "Tab",
  Enter: "Enter",
  Escape: "Escape",
  Space: "Space",
  Delete: "Delete",
  Insert: "Insert",
  Home: "Home",
  End: "End",
  PageUp: "PageUp",
  PageDown: "PageDown",
  ArrowUp: "ArrowUp",
  ArrowDown: "ArrowDown",
  ArrowLeft: "ArrowLeft",
  ArrowRight: "ArrowRight",
  ShiftLeft: "Shift",
  ShiftRight: "Shift",
  ControlLeft: "Ctrl",
  ControlRight: "Ctrl",
  AltLeft: "Alt",
  AltRight: "Alt",
  MetaLeft: "Meta",
  MetaRight: "Meta"
};
