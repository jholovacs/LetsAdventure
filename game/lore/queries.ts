import { LORE_BIBLE } from "./bible.js";
import type { CultivationRealm, LoreId, Region, Sect } from "./types.js";

function indexById<T extends { id: LoreId }>(items: readonly T[]): Map<LoreId, T> {
  const m = new Map<LoreId, T>();
  for (const item of items) m.set(item.id, item);
  return m;
}

const realms = indexById(LORE_BIBLE.cultivationRealms);
const regions = indexById(LORE_BIBLE.regions);
const sects = indexById(LORE_BIBLE.sects);

/** O(1) lookup for UI, AI, and simulation. */
export function getCultivationRealm(id: LoreId): CultivationRealm | undefined {
  return realms.get(id);
}

export const cultivationRealmById: ReadonlyMap<LoreId, CultivationRealm> = realms;
export const regionById: ReadonlyMap<LoreId, Region> = regions;
export const sectById: ReadonlyMap<LoreId, Sect> = sects;
