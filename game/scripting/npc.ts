import type { LoreId } from "../lore/types.js";
import type { GameContext } from "../simulation/context.js";
import type { Condition } from "./conditions.js";
import { evalCondition, type ScriptEvalEnv } from "./conditions.js";
import type { ScriptEffect } from "./effects.js";
import type { ScriptRuntimeState } from "./runtime-state.js";

export type DialogSpeaker = "npc" | "narrator" | "player";

export interface DialogLine {
  readonly speaker: DialogSpeaker;
  readonly text: string;
}

export interface NpcRule {
  readonly id: string;
  /** Higher runs first; default 0. */
  readonly priority?: number;
  readonly when: Condition;
  readonly effects: readonly ScriptEffect[];
  readonly dialog?: readonly DialogLine[];
}

export interface NpcScript {
  readonly npcId: LoreId;
  readonly rules: readonly NpcRule[];
  readonly fallbackDialog?: readonly DialogLine[];
}

export interface NpcInteractionResult {
  readonly matchedRuleId: string | null;
  readonly effects: readonly ScriptEffect[];
  readonly dialog: readonly DialogLine[];
}

export function runNpcInteraction(
  script: NpcScript,
  ctx: GameContext,
  state: ScriptRuntimeState,
): NpcInteractionResult {
  const env: ScriptEvalEnv = { ctx, state };
  const sorted = [...script.rules].sort((a, b) => (b.priority ?? 0) - (a.priority ?? 0));

  for (const rule of sorted) {
    if (!evalCondition(env, rule.when)) continue;
    return {
      matchedRuleId: rule.id,
      effects: rule.effects,
      dialog: rule.dialog ? [...rule.dialog] : [],
    };
  }

  return {
    matchedRuleId: null,
    effects: [],
    dialog: script.fallbackDialog ? [...script.fallbackDialog] : [],
  };
}
