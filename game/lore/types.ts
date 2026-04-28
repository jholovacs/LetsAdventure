declare const loreIdBrand: unique symbol;

/** Branded string ids for lore entities (quests, NPCs, regions reuse the same pattern). */
export type LoreId = string & { readonly [loreIdBrand]: true };

export type SectAlignment =
  | "orthodox"
  | "neutral"
  | "pragmatic"
  | "heterodox"
  | "demonic";

export type TreasureGrade =
  | "mortal"
  | "spirit"
  | "mystic"
  | "earth"
  | "heaven"
  | "immortal";

export type PillGrade = 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;

export interface CultivationRealm {
  readonly id: LoreId;
  /** Monotonic; higher is stronger. */
  readonly order: number;
  readonly name: string;
  /** Poetic or scholarly name used in dialog and UI. */
  readonly epithet: string;
  readonly stages: readonly string[];
  /** Short codex entry. */
  readonly blurb: string;
}

export interface Region {
  readonly id: LoreId;
  readonly name: string;
  /** Dominant element or spiritual “color” of the land. */
  readonly spiritualAspect: string;
  readonly blurb: string;
  /** Neighboring region ids for open-world graph edges. */
  readonly neighbors: readonly LoreId[];
}

export interface Sect {
  readonly id: LoreId;
  readonly name: string;
  readonly alignment: SectAlignment;
  readonly seat: string;
  readonly doctrine: string;
  readonly signatureArts: readonly string[];
}

export interface Cosmology {
  readonly worldName: string;
  readonly worldEpithet: string;
  readonly summary: string;
  readonly upperRealmsNote: string;
  readonly daoConcepts: readonly { term: string; gloss: string }[];
}

export interface LoreBible {
  readonly cosmology: Cosmology;
  readonly cultivationRealms: readonly CultivationRealm[];
  readonly regions: readonly Region[];
  readonly sects: readonly Sect[];
  readonly taboos: readonly string[];
  readonly oaths: readonly string[];
}
