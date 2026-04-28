import type { LoreId } from "../lore/types.js";
import { applyScriptEffects } from "../scripting/effects.js";
import type { ScriptRuntimeState } from "../scripting/runtime-state.js";
import type { GameContext } from "../simulation/context.js";
import { evalEventTriggers, type EventTriggerEnv } from "./triggers.js";
import type {
  EventCycleResult,
  EventDefinition,
  EventEngineState,
  PendingSchedule,
  ScheduleFireMode,
} from "./types.js";

export function emptyEventEngineState(gameTime = 0): EventEngineState {
  return {
    gameTime,
    firedOneShot: new Set(),
    lastFiredAt: new Map(),
    pendingSchedules: [],
    nextScheduleSeq: 0,
  };
}

export function advanceEventClock(engine: EventEngineState, delta: number): EventEngineState {
  return { ...engine, gameTime: engine.gameTime + delta };
}

function buildEnv(ctx: GameContext, state: ScriptRuntimeState, engine: EventEngineState): EventTriggerEnv {
  return { ctx, state, engine };
}

function fireEventInternal(
  def: EventDefinition,
  scriptState: ScriptRuntimeState,
  engine: EventEngineState,
  pendingBase: readonly PendingSchedule[] | PendingSchedule[],
): { scriptState: ScriptRuntimeState; engine: EventEngineState } {
  const gameTime = engine.gameTime;
  const script = applyScriptEffects(scriptState, def.actions, { gameTime });

  const firedOneShot =
    def.once !== false ? new Set(engine.firedOneShot).add(def.id) : new Set(engine.firedOneShot);

  const lastFiredAt = new Map(engine.lastFiredAt).set(def.id, gameTime);

  let nextSeq = engine.nextScheduleSeq;
  const extra: PendingSchedule[] = [];
  for (const s of def.scheduleAfterFire ?? []) {
    extra.push({
      scheduleId: `${s.eventId}#${nextSeq}`,
      eventId: s.eventId,
      fireAt: gameTime + s.delay,
      mode: s.mode ?? "reevaluate_triggers",
    });
    nextSeq += 1;
  }

  return {
    scriptState: script,
    engine: {
      ...engine,
      firedOneShot,
      lastFiredAt,
      pendingSchedules: [...pendingBase, ...extra],
      nextScheduleSeq: nextSeq,
    },
  };
}

function tryFireFromSchedule(
  def: EventDefinition,
  mode: ScheduleFireMode,
  script: ScriptRuntimeState,
  engine: EventEngineState,
  pending: readonly PendingSchedule[],
  ctx: GameContext,
): { script: ScriptRuntimeState; engine: EventEngineState; pending: PendingSchedule[]; ok: boolean } {
  const pendingMutable = [...pending];
  if (def.once !== false && engine.firedOneShot.has(def.id)) {
    return { script, engine, pending: pendingMutable, ok: false };
  }

  const partialEngine: EventEngineState = { ...engine, pendingSchedules: pendingMutable };
  if (mode === "reevaluate_triggers" && !evalEventTriggers(buildEnv(ctx, script, partialEngine), def.triggers)) {
    return { script, engine, pending: pendingMutable, ok: false };
  }

  const r = fireEventInternal(def, script, partialEngine, pendingMutable);
  return {
    script: r.scriptState,
    engine: r.engine,
    pending: [...r.engine.pendingSchedules],
    ok: true,
  };
}

function tryFireFromPoll(
  def: EventDefinition,
  script: ScriptRuntimeState,
  engine: EventEngineState,
  pending: readonly PendingSchedule[],
  ctx: GameContext,
): { script: ScriptRuntimeState; engine: EventEngineState; pending: PendingSchedule[]; ok: boolean } {
  const pendingMutable = [...pending];
  if (def.once !== false && engine.firedOneShot.has(def.id)) {
    return { script, engine, pending: pendingMutable, ok: false };
  }

  const partialEngine: EventEngineState = { ...engine, pendingSchedules: pendingMutable };
  if (!evalEventTriggers(buildEnv(ctx, script, partialEngine), def.triggers)) {
    return { script, engine, pending: pendingMutable, ok: false };
  }

  const r = fireEventInternal(def, script, partialEngine, pendingMutable);
  return {
    script: r.scriptState,
    engine: r.engine,
    pending: [...r.engine.pendingSchedules],
    ok: true,
  };
}

const MAX_EVENT_CHAIN = 64;

/**
 * Single simulation step: run due timers, then evaluate polling triggers, repeating until quiescent
 * (handles zero-delay schedule chains within one host tick).
 */
export function processEventCycle(input: {
  readonly definitions: ReadonlyMap<LoreId, EventDefinition>;
  readonly ctx: GameContext;
  readonly scriptState: ScriptRuntimeState;
  readonly engineState: EventEngineState;
}): EventCycleResult {
  let script = input.scriptState;
  let engine: EventEngineState = { ...input.engineState };
  let pending = [...engine.pendingSchedules];
  const firedEventIds: LoreId[] = [];

  let guard = 0;
  let progressed = true;

  while (progressed && guard++ < MAX_EVENT_CHAIN) {
    progressed = false;
    const gameTime = engine.gameTime;

    const due = pending
      .filter((s) => s.fireAt <= gameTime)
      .sort((a, b) => a.fireAt - b.fireAt || a.scheduleId.localeCompare(b.scheduleId));
    pending = pending.filter((s) => s.fireAt > gameTime);

    for (const s of due) {
      const def = input.definitions.get(s.eventId);
      if (!def) continue;

      const r = tryFireFromSchedule(def, s.mode, script, engine, pending, input.ctx);
      script = r.script;
      engine = { ...r.engine, gameTime };
      pending = r.pending;
      if (r.ok) {
        firedEventIds.push(def.id);
        progressed = true;
      }
    }

    for (const def of input.definitions.values()) {
      const r = tryFireFromPoll(def, script, engine, pending, input.ctx);
      script = r.script;
      engine = { ...r.engine, gameTime };
      pending = r.pending;
      if (r.ok) {
        firedEventIds.push(def.id);
        progressed = true;
      }
    }
  }

  return {
    scriptState: script,
    engineState: {
      ...engine,
      pendingSchedules: pending,
    },
    firedEventIds,
  };
}
