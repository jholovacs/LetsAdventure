# Content authoring guide (AI / human)

This document describes the **data models** under `content/` and how they connect, so a language model (or designer) can generate **lore**, **worldbuilding**, **NPCs**, and **quests** that load in the LetsAdventure C# engine. All JSON uses **camelCase** property names. Enums are **snake_case** strings (e.g. `realm.foundation`, `single`, `training_yard`).

**Authoritative implementation:** `engine/LetsAdventure.Core` (deserialize with the same shapes as below).

---

## 1. Repository layout

| Path | Role |
|------|------|
| `content/lore/bible.json` | World name, **cultivation realms** (ordered), **regions**, **sects**, cosmology text |
| `content/anchors.json` | Named 3D positions `{ "id": { "x", "y", "z" } }` for anchors and NPC home pins |
| `content/world/establishments.json` | Places NPCs commute to (home, work, market, inn, …) |
| `content/world/npc_templates.json` | Archetypes: sleep/work schedules, needs, activity biases |
| `content/world/npc_instances.json` | Concrete NPCs: id, template, home/work, factions, traits, intimacy, cultivation, face |
| `content/world/factions.json` | Faction definitions |
| `content/world/faction_reputation.json` | Directed faction–faction standing |
| `content/world/relationship_rules.json` | Blend of personal vs faction disposition; cultivation respect tuning |
| `content/world/relationship_seeds.json` | **Directed** personal disposition seeds (observer → target, numeric) |
| `content/world/player_social.json` | Player character id, factions, intimacy, cultivation, face |
| `content/world/intimacy_rules.json` | Intimacy / infidelity pressure tuning |
| `content/world/intimacy_bonds.json` | Friendship and romantic interest seeds between characters |
| `content/world/face_rules.json` | Default face range, **highFaceThreshold** for “high face” behavior hooks |
| `content/quests/*.json` | Quest blueprints |
| `content/events/*.json` | Scheduled / conditional **events** (triggers + actions) |
| `content/world/physical_world.json` | Optional **3D world**: regions, territory, terrain features, nav graph/grid |

### 1.1 Region-scaled loading (`content/world/content_index.json`)

**Required.** The file must exist at `content/world/content_index.json`. It selects the **core** slice (still from `content/`) and optional **per-region bundles** under folders you declare; `ToContentPack()` merges active regions for gameplay. An index with empty `regions` loads core-only (global paths above).

| Path | Role |
|------|------|
| `content/world/content_index.json` | `schemaVersion`, boolean flags, optional `coreEventFiles` / `coreQuestFiles` (paths under `content/`), and `regions[]` |

**Flags** (set explicitly in JSON; `System.Text.Json` does not apply C# property defaults for missing keys): `loadGlobalNpcInstancesFile`, `loadGlobalEstablishmentsFile`, `includeGlobalEventDirectory`, `includeGlobalQuestDirectory`. When a flag is `false`, the corresponding global file or directory is skipped at core load so that content can live only in regions.

**Each region object:** `loreRegionId` (e.g. `region.jade_threshold`), `directory` (e.g. `world/regions/jade_threshold`), `npcInstancesFile` (default `npcs.json`), optional `establishmentsFile` (default `establishments.json`; omit or set `null` to skip), optional `physicalOverlayFile` (default `physical.json`), plus `eventFiles` / `questFiles` — arrays of paths **relative to `content/`** (same as `coreEventFiles`).

**Authoring shapes:** regional `npcs.json` uses the same `{ "instances": [ ... ] }` wrapper as global `npc_instances.json`; regional `establishments.json` matches global establishments. Regional `physical.json` is a **partial** `PhysicalWorldDefinition` merged onto core `physical_world.json` (boundaries, territories, features, nav).

**Merge rules:** Regions are applied in **index order**; later entries **override** the same id for events, quests, NPC instances, and establishments. Physical overlays are merged in that order via the engine’s `PhysicalWorldMerger`.

**Runtime:** `WorldRuntimeSession.Load(contentRoot)` loads core + index; `ToContentPack()` returns the **merged** `ContentPack`. Omit the second argument to activate **all** regions in the index, or pass a subset of `loreRegionId` strings (e.g. the player’s current region) to **lazy-load** only those bundles. Call `SetActiveLoreRegions` when the active set changes; inactive bundles may be dropped from the session cache. Use `CoreContent` for the core pack without regional overlays.

---

## 2. ID conventions

Use stable, dotted lowercase ids:

- **Player:** `character.player` (or custom `characterId` in `player_social.json`).
- **NPCs:** `npc.<role_or_name>` (e.g. `npc.mist_broker_ai`).
- **Regions / realms / sects:** must match `lore/bible.json` (`region.*`, `realm.*`, `sect.*`) unless you **extend the bible** in the same edit.
- **Establishments:** `est.*`; **templates:** `tpl.*`; **quests:** `quest.*`; **factions:** `faction.*`; **anchors:** `anchor.*` or reuse `npc.*` when the anchor id equals an NPC home pin (see existing content).

New NPCs need **new anchors** (or reuse existing) and usually **establishments** for `home` / `work` if they are simulated.

---

## 3. Lore bible (`content/lore/bible.json`)

Top-level object:

- **cosmology:** `worldName`, `worldEpithet`, `summary`, `upperRealmsNote`, `daoConcepts[]` (`term`, `gloss`).
- **cultivationRealms[]:** each has `id`, `order` (integer sort key), `name`, `epithet`, `stages[]` (ordered strings), `blurb`.
- **regions[]:** `id`, `name`, `spiritualAspect`, `blurb`, `neighbors[]` (region ids).
- **sects[]:** `id`, `name`, `alignment`, `seat`, `doctrine`, `signatureArts[]`.
- **taboos[]**, **oaths[]:** flavor strings.

**Cultivation default for mortals:** anyone without explicit data is treated as **`realm.qi_vein`**, **stage index 0** (first stage of Qi Refinement). Use `realmId` + `stageIndex` (0-based within `stages`) on NPCs and player.

When generating content, **prefer referencing existing** `realm.*` / `region.*` / `sect.*` ids from the bible so quests and `GameContext` stay consistent.

---

## 4. Anchors (`content/anchors.json`)

Object map: `"anchorId": { "x": number, "y": number, "z": number }`.

Used by **establishments** (`anchorId`) and **events** (`near_anchor`). NPC daily life can use an establishment whose `anchorId` matches an **npc id** for a private home pin.

---

## 5. Establishments (`content/world/establishments.json`)

Wrapper: `{ "establishments": [ ... ] }`.

Each establishment:

| Field | Type | Notes |
|-------|------|--------|
| `id` | string | Unique `est.*` |
| `name` | string | Display |
| `kind` | enum | `home`, `workplace`, `inn`, `food_vendor`, `market`, `teahouse`, `temple`, `training_yard`, `bathhouse`, `social_hall` |
| `regionId` | string | From bible `regions` |
| `anchorId` | string | Key in `anchors.json` |
| `tags` | string[] | Optional hints (`food`, `social`, `sleep`, …) |

---

## 6. NPC templates (`content/world/npc_templates.json`)

Wrapper: `{ "templates": [ ... ] }`.

Shared **routine / needs** for many instances. Important fields:

| Field | Meaning |
|-------|---------|
| `id` | `tpl.*` |
| `displayName` | Archetype label |
| `sleepStartMinute`, `sleepEndMinute` | Minutes 0–1440 (e.g. 1320 = 22:00) |
| `workStartMinute`, `workEndMinute` | Job window |
| `hungerPerHour`, `fatiguePerHourAwake`, `fatiguePerHourSleeping`, `socialPerHour`, `recreationPerHour` | Need drift |
| `needIntensity`, `workEthic`, `workObligationPerHour` | Personality-ish tuning |
| `activityUtilityBias` | Map of activity id → multiplier (`work`, `eat`, `sleep`, `socialize`, `recreation`, `worship`, `train`, …) |
| `preferredFoodKinds`, `preferredSleepKinds`, `preferredRecreationKinds` | Arrays of establishment **kind** enums (same strings as `kind` on establishments) |

---

## 7. NPC instances (`content/world/npc_instances.json`)

Wrapper: `{ "instances": [ ... ] }`.

| Field | Required | Notes |
|-------|----------|--------|
| `npcId` | yes | Stable `npc.*` |
| `templateId` | yes | `tpl.*` from templates |
| `homeEstablishmentId` | yes | `est.*` |
| `workEstablishmentId` | yes | `est.*` |
| `factionIds` | yes | From `factions.json` |
| `extraTraits` | optional | Strings for design / future mechanics |
| `cultivation` | optional | `{ "realmId", "stageIndex" }`; omit → qi vein stage 0 |
| `face` | optional | Number; omit → `face_rules.defaultFace` |
| `intimacy` | optional | See §10 |

---

## 8. Factions and diplomacy

**`factions.json`:** `{ "factions": [ { "id", "name", "tags": [] } ] }`.

**`faction_reputation.json`:** directed pairs: `{ "fromFactionId", "toFactionId", "value" }` (numeric standing).

**`relationship_seeds.json`:** directed **personal** seeds before faction blend:

```json
{ "pairs": [ { "observerId": "npc.example", "targetId": "character.player", "value": 5 } ] }
```

**`relationship_rules.json`:** `factionInfluenceWeight`, `cultivationRespectPerMinorStep`, `cultivationRespectCap`, disposition bands (`hostileBelow`, `waryBelow`, …).

---

## 9. Player (`content/world/player_social.json`)

| Field | Notes |
|-------|--------|
| `characterId` | Default `character.player` |
| `factionIds` | Player factions |
| `cultivation` | Optional; overridden at runtime if `GameContext.Player` has a non-empty `realmId` |
| `face` | Optional |
| `intimacy` | Gender, orientation, loyalty, relationship status, spouses, romantic interest targets |

---

## 10. Intimacy (NPC / player block)

JSON object under `intimacy` on an instance or player file:

| Field | Type / notes |
|-------|----------------|
| `genderPresentation` | `unknown`, `masculine`, `feminine`, `non_binary`, `agender`, `fluid`, `other` |
| `orientation` | `{ "towardMasculinePresentation", "towardFemininePresentation", "romanticDrive" }` each 0–1 |
| `loyaltyIntegrity` | 0–100; married/committed caps apply in engine |
| `relationshipStatus` | `single`, `courting`, `committed`, `married`, `poly_committed`, `widowed` |
| `spouseCharacterIds` | string[] |
| `romanticInterestTargets` | string[] explicit crushes |

**`intimacy_bonds.json`:** seeds for `friendship` and `romanticInterest` arrays (observer/target/value).

---

## 11. Face (`content/world/face_rules.json`)

| Field | Role |
|-------|------|
| `minFace`, `maxFace` | Clamp range |
| `defaultFace` | When NPC/player omits `face` |
| `highFaceThreshold` | Condition `high_face` uses this |

---

## 12. Quests (`content/quests/*.json`)

Quest file = one **blueprint** object:

| Field | Type | Notes |
|-------|------|--------|
| `questId` | string | Unique `quest.*` |
| `title`, `summary` | strings | Player-facing |
| `baseObjectives` | array | Always included when quest is offered |
| `fragments` | array | Optional branches; each has `when` (Condition), `objectives`, optional `journalNotes` |
| `prerequisites` | Condition | Availability (default: always true if omitted) |
| `onQuestSuccessEffects` | ScriptEffect[] | Run when `complete_quest` fires for this `questId` |
| `onQuestFailureEffects` | ScriptEffect[] | Run when `fail_quest` fires |

### Objective shape (`baseObjectives` / `fragments[].objectives`)

| Field | Notes |
|-------|--------|
| `id` | Stable slug for code / `QuestStoryEffects` |
| `kind` | Engine stores semantics; typical: `go_region`, `collect_flag`, `custom` (gameplay layer interprets) |
| `description` | Journal text |
| `targetId` | e.g. `region.*` for `go_region` |
| `flagKey` | for `collect_flag`-style content |
| `count` | optional |
| `onCompleteEffects` | ScriptEffect[]; applied when your **quest runner** calls `QuestStoryEffects.TryApplyObjectiveCompleteEffects` |

**Compilation:** `fragments` whose `when` is true add their objectives and journal lines to the **compiled** quest for the current `GameContext` / narrative state.

---

## 13. Conditions (shared: quests, events)

Polymorphic JSON: discriminator field **`op`**. Common nodes:

| `op` | Purpose |
|------|---------|
| `true`, `false` | Constants |
| `all`, `any` | `items`: Condition[] |
| `not` | `item`: Condition |
| `flag_eq`, `flag_true`, `flag_gte` | Script flags |
| `quest_completed`, `quest_failed` | `questId` |
| `realm_order_gte` | Player realm order vs `realmId` |
| `in_region`, `sect_is`, `player_trait`, `world_tag`, `weather_is`, `time_any` | World / player |
| `disposition_gte` | `observerId`, `targetId`, `minValue` (uses narrative relationship + cultivation) |
| `social_attitude_is` | `observerId`, `targetId`, `attitude` (`hostile`, `wary`, `neutral`, `warm`, `allied`) |
| `friendship_gte`, `romantic_interest_gte` | Intimacy bonds |
| `infidelity_pressure_gte` | Narrative intimacy |
| `relationship_status_is`, `loyalty_integrity_gte`, `loyalty_integrity_lte` | Intimacy profile |
| `face_gte`, `face_lte`, `high_face` | Face (`high_face` uses `face_rules.highFaceThreshold`) |

---

## 14. Script effects (`type` discriminator)

Used in quest `onQuest*` / `onCompleteEffects`, and in **event** `actions`:

| `type` | Fields (camelCase) |
|--------|---------------------|
| `set_flag` | `key`, `value` (JSON) |
| `clear_flag` | `key` |
| `add_flag_number` | `key`, `delta` |
| `complete_quest`, `fail_quest` | `questId` |
| `add_personal_relationship` | `observerId`, `targetId`, `delta` |
| `set_personal_relationship` | `observerId`, `targetId`, `value` |
| `add_friendship`, `add_romantic_interest` | `observerId`, `targetId`, `delta` |
| `add_loyalty_integrity` | `characterId`, `delta` |
| `set_relationship_status` | `characterId`, `status` (enum snake_case) |
| `add_face`, `set_face` | `characterId`, `delta` or `value` |

**Note:** `complete_quest` / `fail_quest` in **events** should be used with the content pack’s quest dictionary so **`onQuestSuccessEffects` / `onQuestFailureEffects`** on the blueprint also run.

---

## 15. Events (`content/events/*.json`)

| Field | Notes |
|-------|--------|
| `id` | Unique event id |
| `triggers` | Polymorphic **`op`**: `all`, `any`, `not`, `condition`, `in_region`, `near_anchor`, `game_time_gte`, `after_event_delay`, `quest_outcome` |
| `actions` | ScriptEffect[] |
| `scheduleAfterFire` | Optional `[{ "eventId", "delay", "mode" }]` |
| `once` | Default true: one-shot |

`condition` trigger wraps any **Condition** (same as quests). `quest_outcome` uses `questId`, `outcome` (`success` | `failure` | `any`), optional `outcomeAfterGameTime`.

---

## 16. Prompt patterns for LLMs

### 16.1 Generate a themed NPC batch

Ask the model to output **valid JSON fragments** only, following §§3–11:

1. List **new** `tpl.*` if needed, or reuse existing templates.
2. Add **anchors** for homes/work if new locations.
3. Add **establishments** referencing those anchors and a **regionId** from the bible (or extend bible with a new region and neighbors).
4. Add **npc_instances** with consistent `factionIds`, optional `intimacy`, `cultivation`, `face`.
5. Add **relationship_seeds** and optional **intimacy_bonds** so the group has history.
6. If introducing a new **faction**, update `factions.json` and **faction_reputation** edges.

Constraint: every `npcId` referenced in bonds/seeds must exist in `npc_instances` (or be `character.player`).

### 16.2 Generate a player-involving quest

1. Choose `questId`, title, summary.
2. `prerequisites`: e.g. region, sect, flag, **not** `quest_completed` for same id.
3. `baseObjectives`: at least one clear objective with unique `id`.
4. Optional `fragments` with `when` for weather, realm, sect, etc.
5. `onQuestSuccessEffects` / `onQuestFailureEffects` for social payoffs (`add_friendship`, `add_face`, `add_personal_relationship`, …).
6. Add an **event** (or gameplay) that fires `complete_quest` with the same `questId` when objectives are done; optional follow-up event with `quest_outcome` trigger.

### 16.3 Consistency checklist (automated or manual)

- [ ] All ids referenced exist (regions, realms, factions, establishments, anchors, NPCs).
- [ ] `realmId` / `stageIndex` valid for `cultivationRealms[].stages` length.
- [ ] Enum strings use **snake_case** as in engine (`poly_committed`, `non_binary`, …).
- [ ] Quest `complete_quest` appears somewhere when the story should finish (event or future gameplay).
- [ ] No duplicate `questId` / `npcId` / `est.*` ids.
- [ ] If using `physical_world.json`: region polygons wind consistently; nav graph ids on edges exist; grid `cells` length matches `columns * rows` when provided.

---

## 17. Minimal cross-reference example

- **Lore** defines `region.jade_threshold` and sect `sect.azure_peak`.
- **Anchors** define `anchor.jade_gate`.
- **Establishments** `est.jade_gate_post` uses that anchor.
- **NPC** `npc.gate_elder_chen` uses template `tpl.gate_warden`, home `est.chen_hut`, work `est.jade_gate_post`, factions including `faction.jade_admin`.
- **Quest** `quest.jade_courier` references those regions and uses `onQuestSuccessEffects` for `add_friendship` between `character.player` and the elder.

Use the same pattern for new regions and cast lists.

---

## 18. Physical world (`content/world/physical_world.json`)

Optional file. **Coordinate convention:** horizontal **X/Y** (meters), **Z** up—aligned with `anchors.json` and `Vec3` in events (`near_anchor`).

| Section | Purpose |
|---------|---------|
| `globalBounds` | Axis-aligned world limits (`min` / `max` `Vec3`). |
| `regionBoundaries` | Maps **lore** `region.*` ids to a vertical **polygon column** (`vertices` XY loop, `zMin`, `zMax`). |
| `territories` | **Owned ground:** `claimantFactionId`, optional `loreRegionId`, `priority` (higher wins overlaps), same polygon column shape. |
| `features` | Polymorphic terrain (`kind` discriminator)—see table below. |
| `navigation` | **`graph`** (waypoint A*) and/or **`grid`** (uniform 8-connected A* on walkable cells). |

### Feature `kind` values

| `kind` | Role |
|--------|------|
| `ground_plateau` | Base land: `boundary` polygon, `elevationZ`, `composition`. |
| `path` | Road/trail: `centerline`, `halfWidth`, `surface`, `movementCostMultiplier`. |
| `mountain_ridge` | Ridge polyline: `ridgeLine`, `corridorHalfWidth`, `baseElevationZ`, `peakElevationZ`, `surfaceComposition`. |
| `water_standing` | Pond/lake: `shoreline` polygon, `waterSurfaceZ`, `depth`. |
| `water_flowing` | River: `channelCenterline`, `channelHalfWidth`, `waterSurfaceZ`, `flowDirection` `{x,y}`. |
| `building` | Solid extrusion: `footprint`, `baseZ`, `roofZ`, `blocksNavigation`, `wallComposition`. |
| `vegetation` | Canopy column: `boundary`, `zMin`, `zMax`, `density01`, `movementCostMultiplier`. |
| `solid_volume` | Axis-aligned blocker: `bounds`, `composition`, `blocksNavigation`, `medium`. |
| `sky_volume` | Air shell: `bounds` (weather/flight ceilings). |

Shared on all features: `id`, `layerPriority` (ordering for future sampling / tooling).

**Surface / medium enums** (JSON snake_case): `surfaceComposition` / `composition` / `surface` use values such as `soil`, `rock`, `grass`, `pavement`, `water`, `air`, … (`SurfaceComposition` in engine).

### Navigation

- **Graph:** `nodes[]` with `id`, `position` `{x,y,z}`, optional `tags`. `edges[]` with `fromId`, `toId`, `traverseCost`, `bidirectional`.
- **Grid:** `originX`, `originY`, `cellSize`, `columns`, `rows`, optional `cells` row-major (`row * columns + col`). Each cell: `walkable`, `elevationZ`, `movementCostMultiplier`, `composition`, `fluidDepth`. Omitted cells default to walkable soil at `elevationZ` 0.

**Runtime:** `PhysicalWorldIndex` resolves `loreRegionId` and winning `TerritoryClaim` at a point. `WorldPathfinder.FindPathOnGraph` / `FindPathOnGrid` return paths for agents.

### 18.1 World Editor (desktop)

Separate **WPF** app: `editor/LetsAdventure.WorldEditor` (in `LetsAdventure.sln`). It reads/writes the same JSON as the engine (`GameJson.Options`).

- **File → Open/Save** targets `physical_world.json` (e.g. under `content/world/`).
- **Export features overlay** writes `physical_world.features.json` (array of features); the engine **merges** this after the main file.
- Tabs: metadata & bounds, **regions** / **territories** (polygon vertex lists), **features** (add by kind + JSON editor), **nav graph** (nodes/edges grid), **nav grid** scalar fields, **map preview** (2D X/Y sketch).
- Shortcuts: **Ctrl+N/O/S** (new / open / save).

Run from repo root:

`dotnet run --project editor/LetsAdventure.WorldEditor/LetsAdventure.WorldEditor.csproj`

---

*End of guide. For engine entry points, see `ContentPack.Load`, `WorldRuntimeSession`, `WorldContentIndex`, `PhysicalWorldDefinition`, and `GameNarrativeContext` in `LetsAdventure.Core`.*
