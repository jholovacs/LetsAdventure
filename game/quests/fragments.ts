import type { LoreId } from "../lore/types.js";
import { evalCondition, type Condition } from "../scripting/conditions.js";
import type { QuestFragment, QuestFragmentPatch, QuestObjective } from "./types.js";

function patchObjectives(...objectives: QuestObjective[]): QuestFragmentPatch {
  return { objectives };
}

/** Include extra objectives only when condition holds on the fragment input. */
export function when(
  condition: Condition,
  build: (input: Parameters<QuestFragment>[0]) => QuestFragmentPatch | null,
): QuestFragment {
  return (input) => {
    if (!evalCondition({ ctx: input.ctx, state: input.state }, condition)) return null;
    return build(input);
  };
}

/** Objectives gated by a static condition; no extra input needed. */
export function objectivesIf(
  condition: Condition,
  objectives: readonly QuestObjective[],
): QuestFragment {
  return (input) => {
    if (!evalCondition({ ctx: input.ctx, state: input.state }, condition)) return null;
    return { objectives: [...objectives] };
  };
}

/** Helper: one objective. */
export function extraObjective(objective: QuestObjective): QuestFragmentPatch {
  return patchObjectives(objective);
}

export function mergePatches(patches: readonly QuestFragmentPatch[]): QuestFragmentPatch {
  const objectives: QuestObjective[] = [];
  const journalNotes: string[] = [];
  for (const p of patches) {
    if (p.objectives) objectives.push(...p.objectives);
    if (p.journalNotes) journalNotes.push(...p.journalNotes);
  }
  return {
    objectives: objectives.length ? objectives : undefined,
    journalNotes: journalNotes.length ? journalNotes : undefined,
  };
}
