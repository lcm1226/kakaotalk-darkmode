import {
  CODE_LABELS,
  MAX_COMBO_KEYS,
  MODIFIER_CODES,
  MODIFIER_SORT_ORDER
} from "./constants.js";

function normalizeCode(code) {
  if (code === " ") {
    return "Space";
  }

  return code;
}

export function eventToCombo(event) {
  const codes = new Set();

  if (event.ctrlKey) {
    codes.add("ControlLeft");
  }
  if (event.altKey) {
    codes.add("AltLeft");
  }
  if (event.shiftKey) {
    codes.add("ShiftLeft");
  }
  if (event.metaKey) {
    codes.add("MetaLeft");
  }

  const primaryCode = normalizeCode(event.code);
  if (primaryCode && !MODIFIER_CODES.has(primaryCode)) {
    codes.add(primaryCode);
  }

  return comboFromCodes([...codes]);
}

export function comboFromCodes(codes) {
  const unique = [...new Set((codes || []).map(normalizeCode).filter(Boolean))];
  const modifiers = unique
    .filter((code) => MODIFIER_CODES.has(code))
    .sort((left, right) => (MODIFIER_SORT_ORDER[left] || 99) - (MODIFIER_SORT_ORDER[right] || 99));
  const primary = unique.filter((code) => !MODIFIER_CODES.has(code)).sort();
  const ordered = [...modifiers, ...primary].slice(0, MAX_COMBO_KEYS);

  return {
    codes: ordered,
    signature: ordered.join("+"),
    display: ordered.map((code) => labelForCode(code)).join(" + ")
  };
}

export function comboFromSignature(signature) {
  return comboFromCodes(String(signature || "").split("+"));
}

export function describeCombo(combo) {
  return combo?.display || "";
}

export function isEditableTarget(target) {
  if (!target || !(target instanceof HTMLElement)) {
    return false;
  }

  if (target.isContentEditable) {
    return true;
  }

  const tagName = target.tagName?.toLowerCase();
  return tagName === "input" || tagName === "textarea" || tagName === "select";
}

export function comboToKeyboardEventInit(combo, type) {
  const codes = combo?.codes || [];
  const primary = codes.find((code) => !MODIFIER_CODES.has(code)) || "";

  return {
    bubbles: true,
    cancelable: true,
    composed: true,
    key: keyFromCode(primary, codes),
    code: primary,
    shiftKey: codes.includes("ShiftLeft") || codes.includes("ShiftRight"),
    ctrlKey: codes.includes("ControlLeft") || codes.includes("ControlRight"),
    altKey: codes.includes("AltLeft") || codes.includes("AltRight"),
    metaKey: codes.includes("MetaLeft") || codes.includes("MetaRight"),
    repeat: false,
    type
  };
}

function keyFromCode(primaryCode, codes) {
  const shifted = codes.includes("ShiftLeft") || codes.includes("ShiftRight");

  if (!primaryCode) {
    return "";
  }

  if (primaryCode.startsWith("Key")) {
    const letter = primaryCode.replace("Key", "");
    return shifted ? letter : letter.toLowerCase();
  }

  if (primaryCode.startsWith("Digit")) {
    const digit = primaryCode.replace("Digit", "");
    if (!shifted) {
      return digit;
    }

    const shiftedDigits = {
      "1": "!",
      "2": "@",
      "3": "#",
      "4": "$",
      "5": "%",
      "6": "^",
      "7": "&",
      "8": "*",
      "9": "(",
      "0": ")"
    };

    return shiftedDigits[digit] || digit;
  }

  if (primaryCode === "Space") {
    return " ";
  }

  const fallback = {
    Backspace: "Backspace",
    Tab: "Tab",
    Enter: "Enter",
    Escape: "Escape",
    Delete: "Delete",
    Insert: "Insert",
    Home: "Home",
    End: "End",
    PageUp: "PageUp",
    PageDown: "PageDown",
    ArrowUp: "ArrowUp",
    ArrowDown: "ArrowDown",
    ArrowLeft: "ArrowLeft",
    ArrowRight: "ArrowRight"
  };

  return fallback[primaryCode] || primaryCode;
}

function labelForCode(code) {
  if (CODE_LABELS[code]) {
    return CODE_LABELS[code];
  }

  if (code.startsWith("Key")) {
    return code.replace("Key", "");
  }

  if (code.startsWith("Digit")) {
    return code.replace("Digit", "");
  }

  return code;
}
