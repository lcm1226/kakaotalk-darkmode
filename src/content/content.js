const STORAGE_KEY = "remapKeyEverywhereRules";
const MODIFIER_CODES = new Set([
  "ShiftLeft",
  "ShiftRight",
  "ControlLeft",
  "ControlRight",
  "AltLeft",
  "AltRight",
  "MetaLeft",
  "MetaRight"
]);
const MODIFIER_ORDER = [
  "ControlLeft",
  "ControlRight",
  "AltLeft",
  "AltRight",
  "ShiftLeft",
  "ShiftRight",
  "MetaLeft",
  "MetaRight"
];

let activeMappings = [];
let suppressDepth = 0;

function normalizePattern(pattern) {
  return String(pattern || "").trim();
}

function patternToRegex(pattern) {
  const normalized = normalizePattern(pattern);

  if (!normalized) {
    return null;
  }

  const escaped = normalized
    .replace(/[.+^${}()|[\]\\]/g, "\\$&")
    .replace(/\*/g, ".*");

  return new RegExp(`^${escaped}$`);
}

function ruleMatchesUrl(rule, url) {
  const regex = patternToRegex(rule.pattern);
  if (!regex) {
    return false;
  }

  return regex.test(url);
}

async function loadRules() {
  const stored = await chrome.storage.local.get(STORAGE_KEY);
  const state = stored[STORAGE_KEY];

  if (!state || !Array.isArray(state.rules)) {
    return {
      rules: [
        {
          id: "sample-gmail-rule",
          pattern: "https://mail.google.com/*",
          mappings: [
            {
              id: "sample-delete-to-hash",
              from: {
                codes: ["Delete"],
                signature: "Delete",
                display: "Delete"
              },
              to: {
                codes: ["ShiftLeft", "Digit3"],
                signature: "ShiftLeft+Digit3",
                display: "Shift + 3"
              }
            }
          ]
        }
      ]
    };
  }

  return {
    rules: state.rules.filter((rule) => rule && typeof rule.pattern === "string" && Array.isArray(rule.mappings))
  };
}

function eventToCombo(event) {
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

  if (event.code && !MODIFIER_CODES.has(event.code)) {
    codes.add(event.code === " " ? "Space" : event.code);
  }

  const ordered = [...codes];
  return {
    codes: ordered,
    signature: ordered.join("+")
  };
}

function comboToKeyboardEventInit(combo, type) {
  const codes = combo?.codes || [];
  const primary = codes.find((code) => !MODIFIER_CODES.has(code)) || "";
  const shifted = codes.includes("ShiftLeft") || codes.includes("ShiftRight");
  const key = keyFromCode(primary, shifted);

  return {
    bubbles: true,
    cancelable: true,
    composed: true,
    key,
    code: primary,
    shiftKey: shifted,
    ctrlKey: codes.includes("ControlLeft") || codes.includes("ControlRight"),
    altKey: codes.includes("AltLeft") || codes.includes("AltRight"),
    metaKey: codes.includes("MetaLeft") || codes.includes("MetaRight"),
    keyCode: keyCodeFromCode(primary, shifted),
    which: keyCodeFromCode(primary, shifted),
    charCode: type === "keypress" ? charCodeFromKey(key) : 0,
    repeat: false,
    type
  };
}

function keyFromCode(primaryCode, shifted) {
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

    return {
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
    }[digit] || digit;
  }

  if (primaryCode === "Space") {
    return " ";
  }

  return primaryCode;
}

function keyCodeFromCode(primaryCode, shifted) {
  if (!primaryCode) {
    return 0;
  }

  if (primaryCode.startsWith("Key")) {
    return primaryCode.replace("Key", "").charCodeAt(0);
  }

  if (primaryCode.startsWith("Digit")) {
    const key = keyFromCode(primaryCode, shifted);
    return key.charCodeAt(0);
  }

  return {
    Backspace: 8,
    Tab: 9,
    Enter: 13,
    Escape: 27,
    Space: 32,
    PageUp: 33,
    PageDown: 34,
    End: 35,
    Home: 36,
    ArrowLeft: 37,
    ArrowUp: 38,
    ArrowRight: 39,
    ArrowDown: 40,
    Insert: 45,
    Delete: 46
  }[primaryCode] || 0;
}

function charCodeFromKey(key) {
  return key && key.length === 1 ? key.charCodeAt(0) : 0;
}

function isEditableTarget(target) {
  if (!target || !(target instanceof HTMLElement)) {
    return false;
  }

  if (target.isContentEditable) {
    return true;
  }

  const tagName = target.tagName?.toLowerCase();
  return tagName === "input" || tagName === "textarea" || tagName === "select";
}

async function refreshMappings() {
  const state = await loadRules();
  const matchingRules = state.rules.filter((rule) => ruleMatchesUrl(rule, window.location.href));
  activeMappings = matchingRules.flatMap((rule) => rule.mappings);
}

function findMappingForEvent(event) {
  const combo = eventToCombo(event);
  return activeMappings.find((mapping) => mapping.from?.signature === combo.signature) || null;
}

function decorateKeyboardEvent(event, init) {
  for (const [property, value] of Object.entries({
    keyCode: init.keyCode,
    which: init.which,
    charCode: init.charCode
  })) {
    try {
      Object.defineProperty(event, property, {
        configurable: true,
        get() {
          return value;
        }
      });
    } catch {
      // Ignore if the browser refuses to redefine the property.
    }
  }

  return event;
}

function emitKeyboardEvent(target, type, combo, overrideCode = "") {
  const normalizedCombo = overrideCode
    ? {
        ...combo,
        codes: [...combo.codes.filter((code) => MODIFIER_CODES.has(code)), overrideCode]
      }
    : combo;
  const init = comboToKeyboardEventInit(normalizedCombo, type);
  const keyboardEvent = decorateKeyboardEvent(new KeyboardEvent(type, init), init);
  target.dispatchEvent(keyboardEvent);
}

function dispatchRemappedSequence(target, combo) {
  suppressDepth += 1;

  try {
    const dispatchTargets = new Set([
      target,
      document,
      document.body,
      document.activeElement instanceof HTMLElement ? document.activeElement : null
    ]);
    const modifiers = MODIFIER_ORDER.filter((code) => combo.codes.includes(code));
    const primary = combo.codes.find((code) => !MODIFIER_CODES.has(code));
    const shouldFireKeypress = Boolean(primary && keyFromCode(primary, combo.codes.includes("ShiftLeft") || combo.codes.includes("ShiftRight")).length === 1);

    for (const dispatchTarget of dispatchTargets) {
      if (!(dispatchTarget instanceof EventTarget)) {
        continue;
      }

      for (const modifier of modifiers) {
        emitKeyboardEvent(dispatchTarget, "keydown", combo, modifier);
      }

      if (primary) {
        emitKeyboardEvent(dispatchTarget, "keydown", combo, primary);
        if (shouldFireKeypress) {
          emitKeyboardEvent(dispatchTarget, "keypress", combo, primary);
        }
        emitKeyboardEvent(dispatchTarget, "keyup", combo, primary);
      }

      for (const modifier of [...modifiers].reverse()) {
        emitKeyboardEvent(dispatchTarget, "keyup", combo, modifier);
      }
    }
  } finally {
    queueMicrotask(() => {
      suppressDepth = Math.max(0, suppressDepth - 1);
    });
  }
}

function handleKeydown(event) {
  if (suppressDepth > 0 || event.defaultPrevented || event.repeat) {
    return;
  }

  const mapping = findMappingForEvent(event);
  if (!mapping) {
    return;
  }

  const target = event.target instanceof EventTarget ? event.target : document;
  const dispatchTarget = target instanceof HTMLElement || target instanceof Document ? target : document;

  event.preventDefault();
  event.stopImmediatePropagation();

  if (isEditableTarget(event.target)) {
    dispatchRemappedSequence(dispatchTarget, mapping.to);
    return;
  }

  dispatchRemappedSequence(document, mapping.to);
}

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName === "local" && changes[STORAGE_KEY]) {
    refreshMappings();
  }
});

window.addEventListener("keydown", handleKeydown, true);
window.addEventListener("focus", () => {
  refreshMappings();
}, true);

refreshMappings();
