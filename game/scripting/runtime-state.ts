import type { LoreId } from "../lore/types.js";

/**
 * Persistent narrative state the script engine reads and updates.
 * The gameplay host owns persistence; this is the in-memory shape.
 */
export type QuestOutcomeKind = "success" | "failure";

export interface QuestOutcomeEntry {
  readonly questId: LoreId;
  readonly outcome: QuestOutcomeKind;
  readonly gameTime: number;
}

export interface ScriptRuntimeState {
  readonly flags: Record<string, boolean | number | string>;
  readonly completedQuests: ReadonlySet<LoreId>;
  readonly failedQuests: ReadonlySet<LoreId>;
  /** Append-only audit for event triggers (cap in applyScriptEffects). */
  readonly questOutcomeLog: readonly QuestOutcomeEntry[];
}

const QUEST_LOG_CAP = 512;

export function emptyScriptState(): ScriptRuntimeState {
  return {
    flags: {},
    completedQuests: new Set(),
    failedQuests: new Set(),
    questOutcomeLog: [],
  };
}

export function cloneScriptState(state: ScriptRuntimeState): ScriptRuntimeState {
  return {
    flags: { ...state.flags },
    completedQuests: new Set(state.completedQuests),
    failedQuests: new Set(state.failedQuests),
    questOutcomeLog: [...state.questOutcomeLog],
  };
}

export function appendQuestOutcome(
  log: readonly QuestOutcomeEntry[],
  entry: QuestOutcomeEntry,
): QuestOutcomeEntry[] {
  const next = [...log, entry];
  return next.length > QUEST_LOG_CAP ? next.slice(next.length - QUEST_LOG_CAP) : next;
}
