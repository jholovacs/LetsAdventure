import type { LoreId } from "../lore/types.js";
import type { Condition } from "../scripting/conditions.js";
import type { ScriptEffect } from "../scripting/effects.js";

export type EventQuestOutcome = "success" | "failure" | "any";

export type ScheduleFireMode = "reevaluate_triggers" | "fire_actions_only";

/** How timer-based instances behave when `fireAt` is reached. */
export interface PendingSchedule {
  readonly scheduleId: string;
  readonly eventId: LoreId;
  readonly fireAt: number;
  readonly mode: ScheduleFireMode;
}

export interface EventEngineState {
  /** Monotonic in-game clock in arbitrary units (seconds, minutes, or custom ticks). */
  readonly gameTime: number;
  /** One-shot events that have already run (when `once` is true). */
  readonly firedOneShot: ReadonlySet<LoreId>;
  /** When an event last fired; used by `after_event_delay` triggers and analytics. */
  readonly lastFiredAt: ReadonlyMap<LoreId, number>;
  readonly pendingSchedules: readonly PendingSchedule[];
  readonly nextScheduleSeq: number;
}

export type EventTriggerExpr =
  | { readonly op: "all"; readonly items: readonly EventTriggerExpr[] }
  | { readonly op: "any"; readonly items: readonly EventTriggerExpr[] }
  | { readonly op: "not"; readonly item: EventTriggerExpr }
  | EventTriggerAtom;

export type EventTriggerAtom =
  | { readonly op: "condition"; readonly condition: Condition }
  | { readonly op: "in_region"; readonly regionId: LoreId }
  | {
      readonly op: "near_anchor";
      readonly anchorId: LoreId;
      /** Euclidean distance in the same units as `playerPosition` / anchor coords. */
      readonly maxDistance: number;
    }
  | { readonly op: "game_time_gte"; readonly time: number }
  | { readonly op: "after_event_delay"; readonly eventId: LoreId; readonly delay: number }
  | {
      readonly op: "quest_outcome";
      readonly questId: LoreId;
      readonly outcome: EventQuestOutcome;
      /** If set, requires a matching entry in `questOutcomeLog` at or after this time. */
      readonly outcomeAfterGameTime?: number;
    };

export interface ScheduleAfterFire {
  readonly eventId: LoreId;
  readonly delay: number;
  readonly mode?: ScheduleFireMode;
}

export interface EventDefinition {
  readonly id: LoreId;
  readonly triggers: EventTriggerExpr;
  readonly actions: readonly ScriptEffect[];
  /** When this event fires, enqueue these targets at `gameTime + delay`. */
  readonly scheduleAfterFire?: readonly ScheduleAfterFire[];
  /**
   * If true (default), the event runs at most once per save across polling and schedules.
   * Set false only if you intend repeating ambient hooks (consider cooldowns later).
   */
  readonly once?: boolean;
}

export interface EventCycleResult {
  readonly scriptState: import("../scripting/runtime-state.js").ScriptRuntimeState;
  readonly engineState: EventEngineState;
  readonly firedEventIds: readonly LoreId[];
}
