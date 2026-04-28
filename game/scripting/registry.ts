import type { LoreId } from "../lore/types.js";
import { allNpcScripts } from "./content.js";
import type { NpcScript } from "./npc.js";

const byNpcId = new Map<LoreId, NpcScript>(allNpcScripts.map((s) => [s.npcId, s]));

export function getNpcScript(npcId: LoreId): NpcScript | undefined {
  return byNpcId.get(npcId);
}
