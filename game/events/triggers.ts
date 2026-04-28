import { questAnchorPositions, distance2 } from "./anchors.js";
import type { EventEngineState, EventTriggerAtom, EventTriggerExpr } from "./types.js";
import { evalCondition, type ScriptEvalEnv } from "../scripting/conditions.js";
import type { GameContext } from "../simulation/context.js";
import type { ScriptRuntimeState } from "../scripting/runtime-state.js";

export interface EventTriggerEnv extends ScriptEvalEnv {
  readonly ctx: GameContext;
  readonly state: ScriptRuntimeState;
  readonly engine: EventEngineState;
}

function matchesQuestOutcome(
  state: ScriptRuntimeState,
  questId: import("../lore/types.js").LoreId,
  outcome: import("./types.js").EventQuestOutcome,
  minTime?: number,
): boolean {
  if (minTime !== undefined) {
    return state.questOutcomeLog.some((e) => {
      if (e.questId !== questId || e.gameTime < minTime) return false;
      if (outcome === "any") return true;
      return e.outcome === outcome;
    });
  }

  const success = state.completedQuests.has(questId);
  const failure = state.failedQuests.has(questId);
  if (outcome === "success") return success;
  if (outcome === "failure") return failure;
  return success || failure;
}

export function evalEventTriggerAtom(env: EventTriggerEnv, atom: EventTriggerAtom): boolean {
  switch (atom.op) {
    case "condition":
      return evalCondition({ ctx: env.ctx, state: env.state }, atom.condition);
    case "in_region":
      return env.ctx.world.regionId === atom.regionId;
    case "near_anchor": {
      const pos = env.ctx.playerPosition;
      if (!pos) return false;
      const anchor = questAnchorPositions.get(atom.anchorId);
      if (!anchor) return false;
      return distance2(pos, anchor) <= atom.maxDistance * atom.maxDistance;
    }
    case "game_time_gte":
      return env.engine.gameTime >= atom.time;
    case "after_event_delay": {
      const t0 = env.engine.lastFiredAt.get(atom.eventId);
      if (t0 === undefined) return false;
      return env.engine.gameTime >= t0 + atom.delay;
    }
    case "quest_outcome":
      return matchesQuestOutcome(
        env.state,
        atom.questId,
        atom.outcome,
        atom.outcomeAfterGameTime,
      );
    default: {
      const _exhaust: never = atom;
      return _exhaust;
    }
  }
}

export function evalEventTriggers(env: EventTriggerEnv, expr: EventTriggerExpr): boolean {
  switch (expr.op) {
    case "all":
      return expr.items.every((x) => evalEventTriggers(env, x));
    case "any":
      return expr.items.some((x) => evalEventTriggers(env, x));
    case "not":
      return !evalEventTriggers(env, expr.item);
    default:
      return evalEventTriggerAtom(env, expr);
  }
}

/** Sugar for data authors. */
export const T = {
  all: (...items: EventTriggerExpr[]): EventTriggerExpr => ({ op: "all", items }),
  any: (...items: EventTriggerExpr[]): EventTriggerExpr => ({ op: "any", items }),
  not: (item: EventTriggerExpr): EventTriggerExpr => ({ op: "not", item }),
  cond: (condition: import("../scripting/conditions.js").Condition): EventTriggerExpr => ({
    op: "condition",
    condition,
  }),
  inRegion: (regionId: import("../lore/types.js").LoreId): EventTriggerExpr => ({
    op: "in_region",
    regionId,
  }),
  nearAnchor: (anchorId: import("../lore/types.js").LoreId, maxDistance: number): EventTriggerExpr => ({
    op: "near_anchor",
    anchorId,
    maxDistance,
  }),
  gameTimeGte: (time: number): EventTriggerExpr => ({ op: "game_time_gte", time }),
  afterEvent: (eventId: import("../lore/types.js").LoreId, delay: number): EventTriggerExpr => ({
    op: "after_event_delay",
    eventId,
    delay,
  }),
  questOutcome: (
    questId: import("../lore/types.js").LoreId,
    outcome: import("./types.js").EventQuestOutcome,
    outcomeAfterGameTime?: number,
  ): EventTriggerExpr => ({
    op: "quest_outcome",
    questId,
    outcome,
    ...(outcomeAfterGameTime !== undefined ? { outcomeAfterGameTime } : {}),
  }),
} as const;
