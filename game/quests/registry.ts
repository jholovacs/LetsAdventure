import type { LoreId } from "../lore/types.js";
import { allQuestBlueprints } from "./content.js";
import type { QuestBlueprint } from "./types.js";

const byQuestId = new Map<LoreId, QuestBlueprint>(
  allQuestBlueprints.map((q) => [q.questId, q]),
);

export function getQuestBlueprint(questId: LoreId): QuestBlueprint | undefined {
  return byQuestId.get(questId);
}
