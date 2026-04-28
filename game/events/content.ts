import { L } from "../lore/ids.js";
import { C } from "../scripting/conditions.js";
import { Fx } from "../scripting/effects.js";
import type { EventDefinition } from "./types.js";
import { T } from "./triggers.js";

/**
 * Fires once when the courier quest records a success (see `questOutcomeLog` + completed set).
 * `lastFiredAt` for this id enables relative-time triggers elsewhere.
 */
export const eventQuestJadeCompletePulse: EventDefinition = {
  id: L.event.questJadeCompletePulse,
  triggers: T.questOutcome(L.quest.jadeCourier, "success"),
  actions: [Fx.setFlag("quest.jade_courier.pulse_ack", true)],
  once: true,
};

/**
 * Example: 120 in-game clock units after the completion pulse, offer a follow-up if the player
 * is near the jade gate anchor and strong enough.
 */
export const eventCourierFollowup: EventDefinition = {
  id: L.event.courierFollowup,
  triggers: T.all(
    T.afterEvent(L.event.questJadeCompletePulse, 120),
    T.nearAnchor(L.anchor.jadeGate, 25),
    T.cond(C.realmGte(L.realm.foundation)),
  ),
  actions: [
    Fx.setFlag(`${L.quest.jadeCourier}.followup_offered`, true),
    Fx.setFlag("rep.jade_threshold", 3),
  ],
  once: true,
};

/** Fires once when in-game clock crosses a campaign beat (predetermined timestamp). */
export const eventScarletDeadline: EventDefinition = {
  id: L.event.scarletDeadline,
  triggers: T.all(
    T.gameTimeGte(10_080),
    T.inRegion(L.region.scarletWastes),
    T.cond(C.not(C.questDone(L.quest.scarletPassage))),
  ),
  actions: [Fx.setFlag("world.scarlet_deadline", true)],
  once: true,
};

/** Proximity + player-status gate: marks ambient activity near Elder Chen (anchor shared with NPC id). */
export const eventJadeAmbient: EventDefinition = {
  id: L.event.jadeAmbient,
  triggers: T.all(
    T.nearAnchor(L.npc.gateElderChen, 18),
    T.cond(C.flagTrue(`${L.quest.jadeCourier}.active`)),
  ),
  actions: [Fx.setFlag("ambient.jade_gate_ping", true)],
  once: true,
};

/** Example failure hook: if scarlet passage fails after the deadline flag exists, stamp world state. */
export const eventScarletFailureRipple: EventDefinition = {
  id: L.event.scarletFailureRipple,
  triggers: T.all(T.cond(C.flagTrue("world.scarlet_deadline")), T.questOutcome(L.quest.scarletPassage, "failure")),
  actions: [Fx.setFlag("world.scarlet_shame", true)],
  once: true,
};

export const allEventDefinitions: EventDefinition[] = [
  eventQuestJadeCompletePulse,
  eventCourierFollowup,
  eventScarletDeadline,
  eventJadeAmbient,
  eventScarletFailureRipple,
];
