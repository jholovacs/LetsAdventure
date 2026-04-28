import type { LoreId } from "./types.js";

/** Central place for lore ids so graphs and references stay consistent. */
export const L = {
  realm: {
    qiVein: "realm.qi_vein" as LoreId,
    foundation: "realm.foundation" as LoreId,
    coreFormation: "realm.core_formation" as LoreId,
    nascentSoul: "realm.nascent_soul" as LoreId,
    spiritSevering: "realm.spirit_severing" as LoreId,
    voidRefining: "realm.void_refining" as LoreId,
    daoIntegration: "realm.dao_integration" as LoreId,
    immortalAscension: "realm.immortal_ascension" as LoreId,
  },
  region: {
    jadeThreshold: "region.jade_threshold" as LoreId,
    scarletWastes: "region.scarlet_wastes" as LoreId,
    mistLake: "region.mist_lake" as LoreId,
    ironRootRange: "region.iron_root_range" as LoreId,
    silentArchive: "region.silent_archive" as LoreId,
    netherFen: "region.nether_fen" as LoreId,
  },
  sect: {
    azurePeak: "sect.azure_peak" as LoreId,
    crimsonLotus: "sect.crimson_lotus" as LoreId,
    ironPilgrim: "sect.iron_pilgrim" as LoreId,
    whiteTome: "sect.white_tome" as LoreId,
    boneRiver: "sect.bone_river" as LoreId,
  },
  npc: {
    gateElderChen: "npc.gate_elder_chen" as LoreId,
    lotusSteward: "npc.lotus_steward" as LoreId,
  },
  quest: {
    jadeCourier: "quest.jade_courier" as LoreId,
    scarletPassage: "quest.scarlet_passage" as LoreId,
  },
  anchor: {
    jadeGate: "anchor.jade_gate" as LoreId,
    mistFerry: "anchor.mist_ferry" as LoreId,
  },
  event: {
    courierFollowup: "event.courier_followup" as LoreId,
    scarletDeadline: "event.scarlet_deadline" as LoreId,
    jadeAmbient: "event.jade_ambient" as LoreId,
    questJadeCompletePulse: "event.quest_jade_complete_pulse" as LoreId,
    scarletFailureRipple: "event.scarlet_failure_ripple" as LoreId,
  },
} as const;
