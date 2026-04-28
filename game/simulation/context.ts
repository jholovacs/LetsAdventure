import type { LoreId } from "../lore/types.js";

export type TimeBand = "dawn" | "day" | "dusk" | "night";

export interface Vec3 {
  readonly x: number;
  readonly y: number;
  readonly z?: number;
}

/** Mutable-over-time view of the player for scripting and quests. */
export interface PlayerSnapshot {
  readonly realmId: LoreId;
  readonly stageIndex: number;
  readonly sectId?: LoreId;
  /** Free-form tags: "spirit_root_fire", "clan_exile", etc. */
  readonly traits: readonly string[];
}

/** Environment relevant to availability, dialog, and objectives. */
export interface WorldSnapshot {
  readonly regionId: LoreId;
  readonly timeBand: TimeBand;
  readonly weather: string;
  /** Engine tags: "market_open", "indoors", "sect_grounds", … */
  readonly tags: readonly string[];
}

export interface GameContext {
  readonly player: PlayerSnapshot;
  readonly world: WorldSnapshot;
  /** When set, `near_anchor` event triggers can resolve distance to quest anchors. */
  readonly playerPosition?: Vec3;
}

export function worldHasTag(world: WorldSnapshot, tag: string): boolean {
  return world.tags.includes(tag);
}

export function playerHasTrait(player: PlayerSnapshot, trait: string): boolean {
  return player.traits.includes(trait);
}
