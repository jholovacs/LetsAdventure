import { L } from "../lore/ids.js";
import { C } from "./conditions.js";
import { Fx } from "./effects.js";
import type { NpcScript } from "./npc.js";

export const npcGateElderChen: NpcScript = {
  npcId: L.npc.gateElderChen,
  rules: [
    {
      id: "chen_quest_turn_in",
      priority: 20,
      when: C.all(C.flagTrue(`${L.quest.jadeCourier}.active`), C.flagTrue("courier.delivered")),
      effects: [Fx.completeQuest(L.quest.jadeCourier), Fx.setFlag("rep.jade_threshold", 2)],
      dialog: [
        { speaker: "npc", text: "The seal is unbroken. Good—my ledger stays clean." },
        { speaker: "narrator", text: "He marks your tally with a brush that smells of iron shavings." },
      ],
    },
    {
      id: "chen_offer_courier",
      priority: 10,
      when: C.all(
        C.inRegion(L.region.jadeThreshold),
        C.not(C.flagTrue(`${L.quest.jadeCourier}.active`)),
        C.not(C.questDone(L.quest.jadeCourier)),
      ),
      effects: [
        Fx.setFlag(`${L.quest.jadeCourier}.active`, true),
        Fx.setFlag("courier.delivered", false),
      ],
      dialog: [
        {
          speaker: "npc",
          text: "You look steady-handed. Carry spirit-rice to Mist Lake—no sampling, no heroics.",
        },
      ],
    },
    {
      id: "chen_weather_warn",
      priority: 5,
      when: C.any(C.weather("ash_storm"), C.worldTag("drought_season")),
      effects: [],
      dialog: [
        {
          speaker: "npc",
          text: "Road’s wrong today. If the sky turns copper, don’t argue with it—go around.",
        },
      ],
    },
  ],
  fallbackDialog: [{ speaker: "npc", text: "Gates stay shut at odd hours. State your business." }],
};

export const npcLotusSteward: NpcScript = {
  npcId: L.npc.lotusSteward,
  rules: [
    {
      id: "steward_sect_discount",
      priority: 10,
      when: C.all(C.sectIs(L.sect.crimsonLotus), C.flagTrue(`${L.quest.jadeCourier}.active`)),
      effects: [Fx.setFlag("trade.alchemy_discount", 0.15)],
      dialog: [
        {
          speaker: "npc",
          text: "House kin don’t pay full tithe on cinnabar. Don’t make me regret the favor.",
        },
      ],
    },
    {
      id: "steward_trait_note",
      priority: 5,
      when: C.trait("alchemy_kin"),
      effects: [],
      dialog: [
        {
          speaker: "npc",
          text: "Your hands remember heat. Try not to ‘remember’ it on my inventory.",
        },
      ],
    },
  ],
  fallbackDialog: [{ speaker: "npc", text: "State your formula needs—no samples without collateral." }],
};

export const allNpcScripts: NpcScript[] = [npcGateElderChen, npcLotusSteward];
