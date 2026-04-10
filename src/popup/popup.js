import { MAX_COMBO_KEYS } from "../shared/constants.js";
import { getUnsupportedTargetReason } from "../shared/browser-shortcuts.js";
import { comboFromCodes, describeCombo } from "../shared/keys.js";
import { createEmptyRule, createMapping, loadRules, ruleMatchesUrl, saveRules } from "../shared/storage.js";

const currentUrlElement = document.querySelector("#current-url");
const patternInput = document.querySelector("#pattern");
const fromDisplay = document.querySelector("#from-display");
const toDisplay = document.querySelector("#to-display");
const captureFromButton = document.querySelector("#capture-from");
const captureToButton = document.querySelector("#capture-to");
const form = document.querySelector("#mapping-form");
const saveButton = document.querySelector("#save-button");
const openOptionsButton = document.querySelector("#open-options");
const activeRulesElement = document.querySelector("#active-rules");
const formStatus = document.querySelector("#form-status");

let activeTabUrl = "";
let pendingSourceCombo = null;
let pendingTargetCombo = null;
let captureMode = null;
let capturedCodes = new Set();
let editingContext = null;

async function init() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  activeTabUrl = tab?.url || "";
  currentUrlElement.textContent = activeTabUrl || "This tab does not expose a usable URL.";
  patternInput.value = suggestPatternFromUrl(activeTabUrl);
  resetEditor();
  setStatus("Page-level shortcuts work best. Browser shortcuts like Ctrl + L cannot be replayed from a page.", "empty");
  await renderActiveRules();
}

function suggestPatternFromUrl(url) {
  try {
    const parsed = new URL(url);
    return `${parsed.origin}/*`;
  } catch {
    return "";
  }
}

function startCapture(mode) {
  captureMode = mode;
  capturedCodes = new Set();

  captureFromButton.classList.toggle("capturing", mode === "from");
  captureToButton.classList.toggle("capturing", mode === "to");

  if (mode === "from") {
    fromDisplay.value = "Press a key combo...";
  } else {
    toDisplay.value = "Press a key combo...";
  }
}

function stopCapture() {
  captureMode = null;
  capturedCodes = new Set();
  captureFromButton.classList.remove("capturing");
  captureToButton.classList.remove("capturing");
}

function setStatus(message, tone = "empty") {
  formStatus.textContent = message;
  formStatus.className = `status ${tone}`;
}

function resetEditor() {
  pendingSourceCombo = null;
  pendingTargetCombo = null;
  editingContext = null;
  fromDisplay.value = "";
  toDisplay.value = "";
  saveButton.textContent = "Save mapping";
}

function handleCapture(event) {
  if (!captureMode) {
    return;
  }

  event.preventDefault();
  event.stopPropagation();

  if (event.type === "keydown") {
    if (event.code) {
      capturedCodes.add(event.code);
    }

    const combo = comboFromCodes([...capturedCodes]);
    const display = describeCombo(combo);

    if (captureMode === "from") {
      pendingSourceCombo = combo;
      fromDisplay.value = display || `Up to ${MAX_COMBO_KEYS} keys`;
    } else {
      pendingTargetCombo = combo;
      toDisplay.value = display || `Up to ${MAX_COMBO_KEYS} keys`;
      const unsupportedReason = getUnsupportedTargetReason(combo);
      if (unsupportedReason) {
        setStatus(unsupportedReason, "warning");
      } else {
        setStatus("Target shortcut looks page-safe.", "success");
      }
    }
    return;
  }

  if (event.type === "keyup") {
    if (captureMode === "from" && pendingSourceCombo?.codes?.length) {
      fromDisplay.value = describeCombo(pendingSourceCombo);
    }

    if (captureMode === "to" && pendingTargetCombo?.codes?.length) {
      toDisplay.value = describeCombo(pendingTargetCombo);
    }

    stopCapture();
  }
}

async function renderActiveRules() {
  if (!activeTabUrl) {
    activeRulesElement.textContent = "Open a normal web page to see matching rules.";
    return;
  }

  const state = await loadRules();
  const matchingRules = state.rules.filter((rule) => ruleMatchesUrl(rule, activeTabUrl));

  if (!matchingRules.length) {
    activeRulesElement.textContent = "No matching rules yet.";
    return;
  }

  const list = document.createElement("div");
  list.className = "rule-list";

  matchingRules.forEach((rule) => {
    rule.mappings.forEach((mapping) => {
      const item = document.createElement("div");
      item.className = "mapping-item";

      const line = document.createElement("p");
      line.className = "mapping-line";
      line.innerHTML = `<strong>${mapping.from.display}</strong> -> ${mapping.to.display}`;

      const actions = document.createElement("div");
      actions.className = "mapping-actions";

      const editButton = document.createElement("button");
      editButton.type = "button";
      editButton.className = "secondary";
      editButton.textContent = "Edit";
      editButton.addEventListener("click", () => startEditing(rule, mapping));

      const deleteButton = document.createElement("button");
      deleteButton.type = "button";
      deleteButton.className = "secondary";
      deleteButton.textContent = "Delete";
      deleteButton.addEventListener("click", async () => {
        await deleteMapping(rule.id, mapping.id);
      });

      actions.append(editButton, deleteButton);
      item.append(line, actions);
      list.append(item);
    });
  });

  activeRulesElement.innerHTML = "";
  activeRulesElement.append(list);
}

function startEditing(rule, mapping) {
  editingContext = {
    ruleId: rule.id,
    mappingId: mapping.id
  };
  patternInput.value = rule.pattern;
  pendingSourceCombo = mapping.from;
  pendingTargetCombo = mapping.to;
  fromDisplay.value = describeCombo(mapping.from);
  toDisplay.value = describeCombo(mapping.to);
  saveButton.textContent = "Update mapping";
  const unsupportedReason = getUnsupportedTargetReason(mapping.to);
  setStatus(unsupportedReason || "Editing current page mapping.", unsupportedReason ? "warning" : "success");
}

async function deleteMapping(ruleId, mappingId) {
  const state = await loadRules();
  const rule = state.rules.find((entry) => entry.id === ruleId);
  if (!rule) {
    return;
  }

  rule.mappings = rule.mappings.filter((mapping) => mapping.id !== mappingId);
  state.rules = state.rules.filter((entry) => entry.mappings.length > 0);
  await saveRules(state);
  if (editingContext?.mappingId === mappingId) {
    resetEditor();
  }
  await renderActiveRules();
  setStatus("Mapping deleted.", "success");
}

async function saveMapping(event) {
  event.preventDefault();

  const pattern = patternInput.value.trim();
  if (!pattern || !pendingSourceCombo?.codes?.length || !pendingTargetCombo?.codes?.length) {
    setStatus("Capture both source and target keys before saving.", "warning");
    return;
  }

  const unsupportedReason = getUnsupportedTargetReason(pendingTargetCombo);
  if (unsupportedReason) {
    setStatus(unsupportedReason, "warning");
    return;
  }

  const state = await loadRules();
  let rule = editingContext
    ? state.rules.find((entry) => entry.id === editingContext.ruleId)
    : state.rules.find((entry) => entry.pattern === pattern);

  if (editingContext && !rule) {
    editingContext = null;
  }

  if (!rule) {
    rule = createEmptyRule(pattern);
    state.rules.push(rule);
  }

  rule.pattern = pattern;

  if (editingContext) {
    const mapping = rule.mappings.find((entry) => entry.id === editingContext.mappingId);
    if (mapping) {
      mapping.from = pendingSourceCombo;
      mapping.to = pendingTargetCombo;
    }
  } else {
    const exists = rule.mappings.some(
      (mapping) => mapping.from.signature === pendingSourceCombo.signature && mapping.to.signature === pendingTargetCombo.signature
    );

    if (!exists) {
      rule.mappings.push(createMapping(pendingSourceCombo, pendingTargetCombo));
    }
  }

  await saveRules(state);
  await renderActiveRules();
  const wasEditing = Boolean(editingContext);
  resetEditor();
  setStatus(wasEditing ? "Mapping updated." : "Mapping saved.", "success");
}

captureFromButton.addEventListener("click", () => startCapture("from"));
captureToButton.addEventListener("click", () => startCapture("to"));
window.addEventListener("keydown", handleCapture, true);
window.addEventListener("keyup", handleCapture, true);
form.addEventListener("submit", saveMapping);
openOptionsButton.addEventListener("click", () => chrome.runtime.openOptionsPage());

init();
