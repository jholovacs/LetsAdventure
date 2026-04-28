import { L } from "../lore/ids.js";
import { C } from "../scripting/conditions.js";
import { objectivesIf, when } from "./fragments.js";
import type { QuestBlueprint, QuestObjective } from "./types.js";

const baseCourierObjectives: QuestObjective[] = [
  {
    id: "deliver_spirit_rice",
    kind: "go_region",
    description: "Deliver sealed spirit-rice to the Mist Lake broker.",
    targetId: L.region.mistLake,
  },
];

/**
 * Example: core objectives stay stable; fragments add steps from player/world context.
 */
export const questJadeCourier: QuestBlueprint = {
  questId: L.quest.jadeCourier,
  title: "Jade Threshold Courier",
  summary: "A mundane commission that becomes complicated if the road knows your name.",
  baseObjectives: baseCourierObjectives,
  prerequisites: C.all(
    C.inRegion(L.region.jadeThreshold),
    C.not(C.questDone(L.quest.jadeCourier)),
  ),
  fragments: [
    objectivesIf(C.realmGte(L.realm.foundation), [
      {
        id: "optional_escort",
        kind: "custom",
        description:
          "Bandits respect Foundation cultivators—prove your stage at the roadside shrine.",
      },
    ]),
    when(C.any(C.weather("ash_storm"), C.worldTag("drought_season")), () => ({
      objectives: [
        {
          id: "detour_oasis",
          kind: "go_region",
          description: "The ash storm closed the ferry; detour through Iron Root Range.",
          targetId: L.region.ironRootRange,
        },
      ],
      journalNotes: ["The steward muttered about 'season debts'—the wastes are hungry this year."],
    })),
    when(C.sectIs(L.sect.azurePeak), () => ({
      objectives: [
        {
          id: "sword_oath_stamp",
          kind: "collect_flag",
          description: "Affix the sword-court seal so Azure Peak accepts liability for the cargo.",
          flagKey: "courier.azure_seal",
        },
      ],
    })),
    when(C.trait("spirit_root_fire"), () => ({
      journalNotes: ["The rice sacks steamed slightly when you touched them—wood qi might have been safer."],
    })),
  ],
};

const baseScarletObjectives: QuestObjective[] = [
  {
    id: "reach_scarlet_gate",
    kind: "go_region",
    description: "Reach the outer markers of the Scarlet Wastes.",
    targetId: L.region.scarletWastes,
  },
];

export const questScarletPassage: QuestBlueprint = {
  questId: L.quest.scarletPassage,
  title: "Passage of Cinders",
  summary: "The wastes test more than cultivation; they test what you will trade for distance.",
  baseObjectives: baseScarletObjectives,
  prerequisites: C.all(
    C.realmGte(L.realm.qiVein),
    C.not(C.questDone(L.quest.scarletPassage)),
    C.any(C.inRegion(L.region.ironRootRange), C.inRegion(L.region.scarletWastes)),
  ),
  fragments: [
    objectivesIf(C.timeAny("night", "dusk"), [
      {
        id: "night_beacon",
        kind: "custom",
        description: "Light the pilgrim beacons; cinder-winds misread strangers after dark.",
      },
    ]),
    when(C.inRegion(L.region.netherFen), () => ({
      objectives: [
        {
          id: "fen_guide",
          kind: "talk_npc",
          description: "Buy safe passage lore from a Bone River guide—if they name a price.",
          targetId: L.sect.boneRiver,
        },
      ],
    })),
  ],
};

export const allQuestBlueprints: QuestBlueprint[] = [questJadeCourier, questScarletPassage];
