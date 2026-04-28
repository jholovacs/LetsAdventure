import { L } from "./ids.js";
import type { LoreBible } from "./types.js";

/**
 * Canonical lore for the mortal plane. Numbers and names are original;
 * structure follows common Xianxia beats (sects, realms, tribulation, dao).
 */
export const LORE_BIBLE: LoreBible = {
  cosmology: {
    worldName: "Huāngmò Jiān",
    worldEpithet: "The Thin Place Between Heaven and Dust",
    summary:
      "The mortal cultivation world sits on a frayed seam of the Great Cycle. " +
      "Qi rises from dragon veins and settles in marrow; the heavens answer " +
      "ambition with tribulation lightning, not mercy. Sects claim mountains " +
      "and call it order; the wildlands call the same mountains home and call it freedom. " +
      "Every breakthrough is a small theft from fate—paid back, with interest, in thunder.",
    upperRealmsNote:
      "Sages say the Upper Realm is not a place but a verdict: those who integrate " +
      "the Dao deeply enough are drawn upward like iron to a lodestone. " +
      "What players see first is the mortal plane—enough glory and cruelty to fill a life.",
    daoConcepts: [
      {
        term: "Tiāndào",
        gloss:
          "Heaven’s pattern: not kindness, but consequence made legible. " +
          "Cultivators read it in omens, pill success rates, and the color of a tribulation cloud.",
      },
      {
        term: "Rěn",
        gloss:
          "Endurance-as-power: suffering stored and refined into intent. " +
          "Many orthodox sects preach it; demonic paths weaponize it.",
      },
      {
        term: "Yuán",
        gloss:
          "Root-nature: the spiritual ‘color’ you were born with. It gates speed, " +
          "compatible arts, and how violently the world tries to correct you.",
      },
      {
        term: "Karma of Borrowing",
        gloss:
          "Every shortcut—pills, formations, stolen fortunes—leaves a thread. " +
          "Pull enough threads, and heaven tugs back.",
      },
    ],
  },

  cultivationRealms: [
    {
      id: L.realm.qiVein,
      order: 1,
      name: "Qi Refinement",
      epithet: "Opening the Meridians",
      stages: ["Gathering Mist", "Channeling Current", "Tempering Breath", "Meridian Gate", "Small Circulation"],
      blurb:
        "Learn to feel qi without dying from it. Most ‘geniuses’ stall here; most ‘trash’ never leave.",
    },
    {
      id: L.realm.foundation,
      order: 2,
      name: "Foundation Establishment",
      epithet: "Setting the Root",
      stages: ["Earth Bed", "Stone Root", "Jade Stem", "Golden Core Seed", "Perfect Foundation"],
      blurb:
        "The body becomes a vessel that remembers. A flawed foundation echoes through every later realm.",
    },
    {
      id: L.realm.coreFormation,
      order: 3,
      name: "Core Formation",
      epithet: "The Golden Core",
      stages: ["False Core", "Solid Core", "Luminous Core", "Rotating Core", "Nine Revolutions"],
      blurb:
        "Condense a sun inside the dantian. Peer disciples are separated forever by core ‘color’ and density.",
    },
    {
      id: L.realm.nascentSoul,
      order: 4,
      name: "Nascent Soul",
      epithet: "The Second Life",
      stages: ["Embryo", "Awakening", "Out-of-Body", "Soul Armament", "Yin-Yang Balance"],
      blurb:
        "A second self that can flee death—once. Sect politics becomes assassination and soul traps.",
    },
    {
      id: L.realm.spiritSevering,
      order: 5,
      name: "Spirit Severing",
      epithet: "Cutting the Tethers",
      stages: ["First Cut", "Second Cut", "Third Cut", "Severing the Self", "True Severing"],
      blurb:
        "Each cut removes a weakness you thought was personality. Some emerge saints; some emerge monsters.",
    },
    {
      id: L.realm.voidRefining,
      order: 6,
      name: "Void Refining",
      epithet: "Body as Law",
      stages: ["Hollow Flesh", "Star Marrow", "Void Skin", "Law Echo", "Almost Immortal"],
      blurb:
        "The flesh learns to carry intent like scripture. Battles leave scars on space itself.",
    },
    {
      id: L.realm.daoIntegration,
      order: 7,
      name: "Dao Integration",
      epithet: "Writing Yourself Into Heaven",
      stages: ["Touching Intent", "Dao Seed", "Dao Branch", "Dao Fruit", "Heaven’s Acknowledgment"],
      blurb:
        "You stop imitating techniques and start resembling truth. The world becomes argumentative.",
    },
    {
      id: L.realm.immortalAscension,
      order: 8,
      name: "Ascension",
      epithet: "The Last Tribulation",
      stages: ["Calling the Bridge", "Walking the Bridge", "Beyond the Bridge"],
      blurb:
        "Not an endpoint for the setting—an exile upward. Those who remain speak of them like weather.",
    },
  ],

  regions: [
    {
      id: L.region.jadeThreshold,
      name: "The Jade Threshold",
      spiritualAspect: "wood / gentle yang",
      blurb:
        "Terraced spirit farms and courier roads. Safe enough to bore a sword cultivator to tears—until night markets open.",
      neighbors: [L.region.mistLake, L.region.ironRootRange],
    },
    {
      id: L.region.scarletWastes,
      name: "Scarlet Wastes",
      spiritualAspect: "fire / baleful yang",
      blurb:
        "Glassed valleys and ash storms where beast tides run like rivers. Ancient battlefields bake secrets into the sand.",
      neighbors: [L.region.ironRootRange, L.region.netherFen],
    },
    {
      id: L.region.mistLake,
      name: "Mist Lake Principalities",
      spiritualAspect: "water / yin veils",
      blurb:
        "Island sects and ferry alliances. Fog hides bandit formations, marriage pacts, and drowned ruins.",
      neighbors: [L.region.jadeThreshold, L.region.silentArchive],
    },
    {
      id: L.region.ironRootRange,
      name: "Iron Root Range",
      spiritualAspect: "metal / stubborn earth",
      blurb:
        "Cliff monasteries mine sword intent from stone. Earthquakes are sometimes just elders sparring.",
      neighbors: [L.region.jadeThreshold, L.region.scarletWastes, L.region.silentArchive],
    },
    {
      id: L.region.silentArchive,
      name: "Silent Archive Desert",
      spiritualAspect: "time / sealed wind",
      blurb:
        "Libraries buried like graves. Sand devils carry fragments of forbidden texts; reading them costs more than money.",
      neighbors: [L.region.mistLake, L.region.ironRootRange],
    },
    {
      id: L.region.netherFen,
      name: "Nether Fen",
      spiritualAspect: "corpse yin / miasma",
      blurb:
        "Swamps where orthodox maps end. Gu masters, corpse refiners, and desperate exiles negotiate a separate peace.",
      neighbors: [L.region.scarletWastes],
    },
  ],

  sects: [
    {
      id: L.sect.azurePeak,
      name: "Azure Peak Sword Court",
      alignment: "orthodox",
      seat: "Iron Root Range — Cloudneedle Cliff",
      doctrine:
        "The sword is honesty: if your cut is crooked, your Dao is crooked. " +
        "Mercy is a second edge.",
      signatureArts: ["Cloudneedle Seven Forms", "Heart Mirror Stance", "Thunder Draw"],
    },
    {
      id: L.sect.crimsonLotus,
      name: "Crimson Lotus Alchemy Hall",
      alignment: "neutral",
      seat: "Jade Threshold — Cinnabar Terrace",
      doctrine:
        "Fire obeys whoever feeds it precisely. Purity is measurable; failure is educational.",
      signatureArts: ["Nine-Beat Furnace Breath", "Lotus Coiling Seal", "Ashless Purification"],
    },
    {
      id: L.sect.ironPilgrim,
      name: "Iron Pilgrim Monastery",
      alignment: "pragmatic",
      seat: "Scarlet Wastes border — Walking Forge Temple",
      doctrine:
        "The body is the first treasure. Walk through hell until hell becomes a road.",
      signatureArts: ["Bone Bell Chant", "Furnace Body Tempering", "Step-Crater Stride"],
    },
    {
      id: L.sect.whiteTome,
      name: "White Tome Academy",
      alignment: "orthodox",
      seat: "Silent Archive — Paper Dune Enclave",
      doctrine:
        "Knowledge without restraint is a plague; restraint without knowledge is a cage. " +
        "We keep both.",
      signatureArts: ["Seal Script Arrays", "Silent Debate Palm", "Index of Forbidden Titles"],
    },
    {
      id: L.sect.boneRiver,
      name: "Bone River Sect",
      alignment: "heterodox",
      seat: "Nether Fen — Raft Citadel of Names",
      doctrine:
        "The dead owe the living nothing—so take what they left behind before someone ‘orthodox’ sells it.",
      signatureArts: ["Corpse Meridian Map", "River Ghost Step", "Debt-Tally Curse"],
    },
  ],

  taboos: [
    "Speaking a Nascent Soul’s true name within a binding array.",
    "Refining pills during your own tribulation—unless you enjoy lightning-flavored failure.",
    "Swearing brotherhood on a dao oath you intend to break (heaven remembers bad faith).",
  ],

  oaths: [
    "May my core shatter if I betray this sect’s granary seal.",
    "If I lie, let my meridians knot like wet rope.",
    "I borrow heaven’s qi; I repay with tribulation—when due.",
  ],
};
