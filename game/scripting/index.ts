export { C, evalCondition, type Condition, type ScriptEvalEnv } from "./conditions.js";
export {
  applyScriptEffects,
  Fx,
  type ApplyScriptEffectsOptions,
  type ScriptEffect,
} from "./effects.js";
export {
  runNpcInteraction,
  type DialogLine,
  type DialogSpeaker,
  type NpcInteractionResult,
  type NpcRule,
  type NpcScript,
} from "./npc.js";
export {
  appendQuestOutcome,
  cloneScriptState,
  emptyScriptState,
  type QuestOutcomeEntry,
  type QuestOutcomeKind,
  type ScriptRuntimeState,
} from "./runtime-state.js";
export { allNpcScripts, npcGateElderChen, npcLotusSteward } from "./content.js";
export { getNpcScript } from "./registry.js";
