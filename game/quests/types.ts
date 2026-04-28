import type { LoreId } from "../lore/types.js";
import type { Condition } from "../scripting/conditions.js";
import type { GameContext } from "../simulation/context.js";
import type { ScriptRuntimeState } from "../scripting/runtime-state.js";

export type QuestObjectiveKind =
  | "go_region"
  | "talk_npc"
  | "collect_flag"
  | "defeat_group"
  | "custom";

export interface QuestObjective {
  readonly id: string;
  readonly kind: QuestObjectiveKind;
  readonly description: string;
  readonly targetId?: LoreId;
  readonly flagKey?: string;
  readonly count?: number;
}

export interface QuestFragmentInput {
  readonly ctx: GameContext;
  readonly state: ScriptRuntimeState;
}

/**
 * Return non-null to contribute objectives (or metadata) when this fragment applies.
 * Fragments are pure: no side effects; keep branching logic here for fast iteration.
 */
export type QuestFragment = (input: QuestFragmentInput) => QuestFragmentPatch | null;

export interface QuestFragmentPatch {
  readonly objectives?: readonly QuestObjective[];
  readonly journalNotes?: readonly string[];
}

export interface QuestBlueprint {
  readonly questId: LoreId;
  readonly title: string;
  readonly summary: string;
  readonly baseObjectives: readonly QuestObjective[];
  readonly fragments: readonly QuestFragment[];
  /** If false, quest should not be offered / shown as available. */
  readonly prerequisites?: Condition;
}

export interface CompiledQuest {
  readonly questId: LoreId;
  readonly title: string;
  readonly summary: string;
  readonly objectives: readonly QuestObjective[];
  readonly journalNotes: readonly string[];
  readonly available: boolean;
}
