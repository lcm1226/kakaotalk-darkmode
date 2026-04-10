import { STORAGE_KEY } from "./constants.js";
import { comboFromCodes } from "./keys.js";

function defaultState() {
  return {
    rules: [
      {
        id: "sample-gmail-rule",
        pattern: "https://mail.google.com/*",
        mappings: [
          {
            id: "sample-delete-to-hash",
            from: comboFromCodes(["Delete"]),
            to: comboFromCodes(["ShiftLeft", "Digit3"])
          }
        ]
      }
    ]
  };
}

export function normalizePattern(pattern) {
  return String(pattern || "").trim();
}

export function createEmptyRule(pattern = "") {
  return {
    id: crypto.randomUUID(),
    pattern: normalizePattern(pattern),
    mappings: []
  };
}

export function createMapping(fromCombo, toCombo) {
  return {
    id: crypto.randomUUID(),
    from: fromCombo,
    to: toCombo
  };
}

export async function loadRules() {
  const stored = await chrome.storage.local.get(STORAGE_KEY);
  const state = stored[STORAGE_KEY];

  if (!state || !Array.isArray(state.rules)) {
    return defaultState();
  }

  return {
    rules: state.rules
      .filter((rule) => rule && typeof rule.pattern === "string" && Array.isArray(rule.mappings))
      .map((rule) => ({
        id: rule.id || crypto.randomUUID(),
        pattern: normalizePattern(rule.pattern),
        mappings: rule.mappings
          .filter((mapping) => mapping && mapping.from && mapping.to)
          .map((mapping) => ({
            id: mapping.id || crypto.randomUUID(),
            from: mapping.from,
            to: mapping.to
          }))
      }))
  };
}

export async function saveRules(nextState) {
  await chrome.storage.local.set({
    [STORAGE_KEY]: {
      rules: nextState.rules || []
    }
  });
}

export function patternToRegex(pattern) {
  const normalized = normalizePattern(pattern);

  if (!normalized) {
    return null;
  }

  const escaped = normalized
    .replace(/[.+^${}()|[\]\\]/g, "\\$&")
    .replace(/\*/g, ".*");

  return new RegExp(`^${escaped}$`);
}

export function ruleMatchesUrl(rule, url) {
  const regex = patternToRegex(rule.pattern);
  if (!regex) {
    return false;
  }

  return regex.test(url);
}
