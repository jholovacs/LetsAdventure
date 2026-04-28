import type { LoreId } from "../lore/types.js";
import { allEventDefinitions } from "./content.js";
import type { EventDefinition } from "./types.js";

const byId = new Map<LoreId, EventDefinition>(allEventDefinitions.map((e) => [e.id, e]));

export function getEventDefinition(eventId: LoreId): EventDefinition | undefined {
  return byId.get(eventId);
}

export function eventDefinitionMap(): ReadonlyMap<LoreId, EventDefinition> {
  return byId;
}
