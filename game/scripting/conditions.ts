import { cultivationRealmById } from "../lore/queries.js";
import type { LoreId } from "../lore/types.js";
import type { GameContext } from "../simulation/context.js";
import { playerHasTrait, worldHasTag } from "../simulation/context.js";
import type { ScriptRuntimeState } from "./runtime-state.js";

export interface ScriptEvalEnv {
  readonly ctx: GameContext;
  readonly state: ScriptRuntimeState;
}

/** Declarative predicates; compose without custom code in data-driven scripts. */
export type Condition =
  | { readonly op: "true" }
  | { readonly op: "false" }
  | { readonly op: "all"; readonly items: readonly Condition[] }
  | { readonly op: "any"; readonly items: readonly Condition[] }
  | { readonly op: "not"; readonly item: Condition }
  | { readonly op: "flag_eq"; readonly key: string; readonly value: boolean | number | string }
  | { readonly op: "flag_true"; readonly key: string }
  | { readonly op: "flag_gte"; readonly key: string; readonly value: number }
  | { readonly op: "quest_completed"; readonly questId: LoreId }
  | { readonly op: "quest_failed"; readonly questId: LoreId }
  | { readonly op: "realm_order_gte"; readonly realmId: LoreId }
  | { readonly op: "in_region"; readonly regionId: LoreId }
  | { readonly op: "sect_is"; readonly sectId: LoreId }
  | { readonly op: "player_trait"; readonly trait: string }
  | { readonly op: "world_tag"; readonly tag: string }
  | { readonly op: "weather_is"; readonly weather: string }
  | { readonly op: "time_any"; readonly bands: readonly GameContext["world"]["timeBand"][] };

function realmOrder(id: LoreId): number {
  return cultivationRealmById.get(id)?.order ?? -1;
}

export function evalCondition(env: ScriptEvalEnv, cond: Condition): boolean {
  switch (cond.op) {
    case "true":
      return true;
    case "false":
      return false;
    case "all":
      return cond.items.every((c) => evalCondition(env, c));
    case "any":
      return cond.items.some((c) => evalCondition(env, c));
    case "not":
      return !evalCondition(env, cond.item);
    case "flag_eq": {
      const v = env.state.flags[cond.key];
      return v === cond.value;
    }
    case "flag_true": {
      const v = env.state.flags[cond.key];
      return v === true || v === "true";
    }
    case "flag_gte": {
      const v = env.state.flags[cond.key];
      return typeof v === "number" && v >= cond.value;
    }
    case "quest_completed":
      return env.state.completedQuests.has(cond.questId);
    case "quest_failed":
      return env.state.failedQuests.has(cond.questId);
    case "realm_order_gte":
      return realmOrder(env.ctx.player.realmId) >= realmOrder(cond.realmId);
    case "in_region":
      return env.ctx.world.regionId === cond.regionId;
    case "sect_is":
      return env.ctx.player.sectId === cond.sectId;
    case "player_trait":
      return playerHasTrait(env.ctx.player, cond.trait);
    case "world_tag":
      return worldHasTag(env.ctx.world, cond.tag);
    case "weather_is":
      return env.ctx.world.weather === cond.weather;
    case "time_any":
      return cond.bands.includes(env.ctx.world.timeBand);
    default: {
      const _exhaust: never = cond;
      return _exhaust;
    }
  }
}

/** Sugar for common bundles. */
export const C = {
  true: { op: "true" } as const satisfies Condition,
  false: { op: "false" } as const satisfies Condition,
  all: (...items: Condition[]): Condition => ({ op: "all", items }),
  any: (...items: Condition[]): Condition => ({ op: "any", items }),
  not: (item: Condition): Condition => ({ op: "not", item }),
  flagEq: (key: string, value: boolean | number | string): Condition => ({ op: "flag_eq", key, value }),
  flagTrue: (key: string): Condition => ({ op: "flag_true", key }),
  flagGte: (key: string, value: number): Condition => ({ op: "flag_gte", key, value }),
  questDone: (questId: LoreId): Condition => ({ op: "quest_completed", questId }),
  questFailed: (questId: LoreId): Condition => ({ op: "quest_failed", questId }),
  realmGte: (realmId: LoreId): Condition => ({ op: "realm_order_gte", realmId }),
  inRegion: (regionId: LoreId): Condition => ({ op: "in_region", regionId }),
  sectIs: (sectId: LoreId): Condition => ({ op: "sect_is", sectId }),
  trait: (trait: string): Condition => ({ op: "player_trait", trait }),
  worldTag: (tag: string): Condition => ({ op: "world_tag", tag }),
  weather: (weather: string): Condition => ({ op: "weather_is", weather }),
  timeAny: (...bands: GameContext["world"]["timeBand"][]): Condition => ({ op: "time_any", bands }),
} as const;
