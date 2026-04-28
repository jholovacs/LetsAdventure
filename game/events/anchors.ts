import type { LoreId } from "../lore/types.js";
import type { Vec3 } from "../simulation/context.js";

/** Static world positions for proximity triggers; gameplay host can mirror from loaded maps. */
export const questAnchorPositions: ReadonlyMap<LoreId, Vec3> = new Map([
  ["anchor.jade_gate" as LoreId, { x: 120, y: 40, z: 0 }],
  ["anchor.mist_ferry" as LoreId, { x: 300, y: 210, z: 0 }],
  // NPCs can share anchor ids — register the same LoreId on the NPC entity.
  ["npc.gate_elder_chen" as LoreId, { x: 122, y: 41, z: 0 }],
]);

export function distance2(a: Vec3, b: Vec3): number {
  const dz = (a.z ?? 0) - (b.z ?? 0);
  const dx = a.x - b.x;
  const dy = a.y - b.y;
  return dx * dx + dy * dy + dz * dz;
}
