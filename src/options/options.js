import { MAX_COMBO_KEYS } from "../shared/constants.js";
import { getUnsupportedTargetReason } from "../shared/browser-shortcuts.js";
import { comboFromCodes, describeCombo } from "../shared/keys.js";
import { createEmptyRule, createMapping, loadRules, saveRules } from "../shared/storage.js";

const rulesElement = document.querySelector("#rules");
const addRuleButton = document.querySelector("#add-rule");
const ruleTemplate = document.querySelector("#rule-template");
const mappingTemplate = document.querySelector("#mapping-template");

let state = { rules: [] };
let captureSession = null;

async function init() {
  state = await loadRules();
  render();
}

function render() {
  rulesElement.innerHTML = "";

  state.rules.forEach((rule) => {
    const fragment = ruleTemplate.content.cloneNode(true);
    const card = fragment.querySelector(".rule-card");
    const patternInput = fragment.querySelector(".pattern-input");
    const mappingList = fragment.querySelector(".mapping-list");
    const ruleStatus = fragment.querySelector(".rule-status");
    const addMappingButton = fragment.querySelector(".add-mapping");
    const saveRuleButton = fragment.querySelector(".save-rule");
    const deleteRuleButton = fragment.querySelector(".delete-rule");

    patternInput.value = rule.pattern;
    card.dataset.ruleId = rule.id;

    rule.mappings.forEach((mapping) => {
      mappingList.appendChild(createMappingNode(rule.id, mapping));
    });

    addMappingButton.addEventListener("click", () => {
      const emptyMapping = createMapping(comboFromCodes([]), comboFromCodes([]));
      rule.mappings.push(emptyMapping);
      render();
    });

    saveRuleButton.addEventListener("click", async () => {
      rule.pattern = patternInput.value.trim();
      const unsupportedMapping = rule.mappings.find((mapping) => getUnsupportedTargetReason(mapping.to));
      if (unsupportedMapping) {
        ruleStatus.textContent = getUnsupportedTargetReason(unsupportedMapping.to);
        ruleStatus.className = "rule-status warning";
        return;
      }
      rule.mappings = rule.mappings.filter((mapping) => mapping.from.codes.length && mapping.to.codes.length);
      await persist();
      ruleStatus.textContent = "Rule saved.";
      ruleStatus.className = "rule-status success";
    });

    deleteRuleButton.addEventListener("click", async () => {
      state.rules = state.rules.filter((entry) => entry.id !== rule.id);
      await persist();
    });

    rulesElement.appendChild(fragment);
  });

  if (!state.rules.length) {
    rulesElement.innerHTML = "<p>No URL rules yet. Start by adding one.</p>";
  }
}

function createMappingNode(ruleId, mapping) {
  const fragment = mappingTemplate.content.cloneNode(true);
  const row = fragment.querySelector(".mapping-row");
  const sourceButton = fragment.querySelector(".source-button");
  const targetButton = fragment.querySelector(".target-button");
  const sourceDisplay = fragment.querySelector(".source-display");
  const targetDisplay = fragment.querySelector(".target-display");
  const deleteButton = fragment.querySelector(".delete-mapping");

  row.dataset.ruleId = ruleId;
  row.dataset.mappingId = mapping.id;
  sourceDisplay.value = describeCombo(mapping.from);
  targetDisplay.value = describeCombo(mapping.to);

  sourceButton.addEventListener("click", () => startCapture(sourceButton, sourceDisplay, ruleId, mapping.id, "from"));
  targetButton.addEventListener("click", () => startCapture(targetButton, targetDisplay, ruleId, mapping.id, "to"));

  deleteButton.addEventListener("click", async () => {
    const rule = state.rules.find((entry) => entry.id === ruleId);
    if (!rule) {
      return;
    }

    rule.mappings = rule.mappings.filter((entry) => entry.id !== mapping.id);
    await persist();
  });

  return fragment;
}

function startCapture(button, display, ruleId, mappingId, side) {
  if (captureSession?.button) {
    captureSession.button.classList.remove("capturing");
  }

  button.classList.add("capturing");
  display.value = `Press up to ${MAX_COMBO_KEYS} keys...`;
  captureSession = {
    button,
    display,
    ruleId,
    mappingId,
    side,
    capturedCodes: new Set()
  };
}

function stopCapture() {
  if (captureSession?.button) {
    captureSession.button.classList.remove("capturing");
  }
  captureSession = null;
}

function updateMappingFromCapture() {
  if (!captureSession) {
    return;
  }

  const combo = comboFromCodes([...captureSession.capturedCodes]);
  const rule = state.rules.find((entry) => entry.id === captureSession.ruleId);
  const mapping = rule?.mappings.find((entry) => entry.id === captureSession.mappingId);

  if (!mapping) {
    stopCapture();
    return;
  }

  mapping[captureSession.side] = combo;
  captureSession.display.value = describeCombo(combo);

  if (captureSession.side === "to") {
    const unsupportedReason = getUnsupportedTargetReason(combo);
    captureSession.display.title = unsupportedReason;
  }
}

window.addEventListener("keydown", (event) => {
  if (!captureSession) {
    return;
  }

  event.preventDefault();
  event.stopPropagation();

  if (event.code) {
    captureSession.capturedCodes.add(event.code);
  }

  updateMappingFromCapture();
}, true);

window.addEventListener("keyup", async (event) => {
  if (!captureSession) {
    return;
  }

  event.preventDefault();
  event.stopPropagation();

  updateMappingFromCapture();
  stopCapture();
  await persist(false);
}, true);

async function persist(shouldRender = true) {
  await saveRules(state);
  if (shouldRender) {
    render();
  }
}

addRuleButton.addEventListener("click", async () => {
  state.rules.push(createEmptyRule(""));
  await persist();
});

init();
