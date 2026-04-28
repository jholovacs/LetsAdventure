import type { LoreId } from "../lore/types.js";
import {
  appendQuestOutcome,
  cloneScriptState,
  type ScriptRuntimeState,
} from "./runtime-state.js";

export type ScriptEffect =
  | { readonly type: "set_flag"; readonly key: string; readonly value: boolean | number | string }
  | { readonly type: "clear_flag"; readonly key: string }
  | { readonly type: "add_flag_number"; readonly key: string; readonly delta: number }
  | { readonly type: "complete_quest"; readonly questId: LoreId }
  | { readonly type: "fail_quest"; readonly questId: LoreId };

export interface ApplyScriptEffectsOptions {
  /** In-game clock when quest outcomes should be logged for the event engine. */
  readonly gameTime?: number;
}

export function applyScriptEffects(
  state: ScriptRuntimeState,
  effects: readonly ScriptEffect[],
  opts?: ApplyScriptEffectsOptions,
): ScriptRuntimeState {
  let next = cloneScriptState(state);
  const flags = { ...next.flags };
  let completed = new Set(next.completedQuests);
  let failed = new Set(next.failedQuests);
  let log = next.questOutcomeLog;
  const gt = opts?.gameTime;

  for (const e of effects) {
    switch (e.type) {
      case "set_flag":
        flags[e.key] = e.value;
        break;
      case "clear_flag":
        delete flags[e.key];
        break;
      case "add_flag_number": {
        const cur = flags[e.key];
        const base = typeof cur === "number" ? cur : 0;
        flags[e.key] = base + e.delta;
        break;
      }
      case "complete_quest":
        completed.add(e.questId);
        failed.delete(e.questId);
        flags[`${e.questId}.complete`] = true;
        flags[`${e.questId}.failed`] = false;
        flags[`${e.questId}.active`] = false;
        if (gt !== undefined) {
          log = appendQuestOutcome(log, { questId: e.questId, outcome: "success", gameTime: gt });
        }
        break;
      case "fail_quest":
        failed.add(e.questId);
        completed.delete(e.questId);
        flags[`${e.questId}.failed`] = true;
        flags[`${e.questId}.complete`] = false;
        flags[`${e.questId}.active`] = false;
        if (gt !== undefined) {
          log = appendQuestOutcome(log, { questId: e.questId, outcome: "failure", gameTime: gt });
        }
        break;
      default: {
        const _exhaust: never = e;
        return _exhaust;
      }
    }
  }

  next = { flags, completedQuests: completed, failedQuests: failed, questOutcomeLog: log };
  return next;
}

export const Fx = {
  setFlag: (key: string, value: boolean | number | string): ScriptEffect => ({
    type: "set_flag",
    key,
    value,
  }),
  clearFlag: (key: string): ScriptEffect => ({ type: "clear_flag", key }),
  addFlagNumber: (key: string, delta: number): ScriptEffect => ({
    type: "add_flag_number",
    key,
    delta,
  }),
  completeQuest: (questId: LoreId): ScriptEffect => ({ type: "complete_quest", questId }),
  failQuest: (questId: LoreId): ScriptEffect => ({ type: "fail_quest", questId }),
} as const;
