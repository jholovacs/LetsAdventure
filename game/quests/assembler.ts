import { evalCondition, type ScriptEvalEnv } from "../scripting/conditions.js";
import type { QuestBlueprint, CompiledQuest, QuestFragmentPatch } from "./types.js";

export function compileQuest(
  blueprint: QuestBlueprint,
  env: ScriptEvalEnv,
): CompiledQuest {
  const prerequisites = blueprint.prerequisites ?? { op: "true" as const };
  const available = evalCondition(env, prerequisites);

  const journalNotes: string[] = [];
  const objectives = [...blueprint.baseObjectives];

  for (const fragment of blueprint.fragments) {
    const patch = fragment({ ctx: env.ctx, state: env.state });
    if (!patch) continue;
    if (patch.objectives?.length) objectives.push(...patch.objectives);
    if (patch.journalNotes?.length) journalNotes.push(...patch.journalNotes);
  }

  return {
    questId: blueprint.questId,
    title: blueprint.title,
    summary: blueprint.summary,
    objectives,
    journalNotes,
    available,
  };
}

/** Run fragments only (e.g. to preview dynamic objectives without gating). */
export function collectFragmentPatches(
  blueprint: Pick<QuestBlueprint, "fragments">,
  env: ScriptEvalEnv,
): QuestFragmentPatch[] {
  const out: QuestFragmentPatch[] = [];
  for (const fragment of blueprint.fragments) {
    const patch = fragment({ ctx: env.ctx, state: env.state });
    if (patch) out.push(patch);
  }
  return out;
}
