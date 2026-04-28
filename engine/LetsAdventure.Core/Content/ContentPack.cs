using System.Text.Json;
using LetsAdventure.Core.Events;
using LetsAdventure.Core.Json;
using LetsAdventure.Core.Lore;
using LetsAdventure.Core.Npcs;
using LetsAdventure.Core.Quests;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.Social;
using LetsAdventure.Core.World;

namespace LetsAdventure.Core.Content;

/// <summary>
/// Loads moddable JSON from a content root (lore, anchors, events, quests, NPC life data).
/// 3D assets can live alongside these folders; the runtime resolves paths from data files later.
/// </summary>
public sealed class ContentPack
{
    public ContentPack(
        LoreBible lore,
        IReadOnlyDictionary<string, Vec3> anchors,
        IReadOnlyDictionary<string, EventDefinition> events,
        IReadOnlyDictionary<string, QuestBlueprintData> quests,
        IReadOnlyDictionary<string, Establishment> establishments,
        IReadOnlyDictionary<string, NpcTemplateData> npcTemplates,
        IReadOnlyDictionary<string, NpcInstanceProfile> npcInstances,
        IReadOnlyDictionary<string, FactionDefinition> factionDefinitions,
        FactionReputationTable factionReputations,
        RelationshipRules relationshipRules,
        IReadOnlyList<RelationshipSeed> relationshipSeeds,
        string playerCharacterId,
        IReadOnlyList<string> playerFactionIds,
        IntimacyRules intimacyRules,
        IReadOnlyList<IntimacyFriendshipSeed> intimacyFriendshipSeeds,
        IReadOnlyList<IntimacyRomanticSeed> intimacyRomanticSeeds,
        CharacterIntimacyProfileData? playerIntimacyProfile,
        CharacterCultivationData? playerCultivationProfile,
        FaceCultureRules faceCultureRules,
        double? playerFace,
        PhysicalWorldDefinition? physicalWorld)
    {
        Lore = lore;
        Anchors = anchors;
        Events = events;
        Quests = quests;
        Establishments = establishments;
        NpcTemplates = npcTemplates;
        NpcInstances = npcInstances;
        FactionDefinitions = factionDefinitions;
        FactionReputations = factionReputations;
        RelationshipRules = relationshipRules;
        RelationshipSeeds = relationshipSeeds;
        PlayerCharacterId = playerCharacterId;
        PlayerFactionIds = playerFactionIds;
        IntimacyRules = intimacyRules;
        IntimacyFriendshipSeeds = intimacyFriendshipSeeds;
        IntimacyRomanticSeeds = intimacyRomanticSeeds;
        PlayerIntimacyProfile = playerIntimacyProfile;
        PlayerCultivationProfile = playerCultivationProfile;
        FaceCultureRules = faceCultureRules;
        PlayerFace = playerFace;
        PhysicalWorld = physicalWorld;
        LoreTable = new LoreTable(lore);
    }

    public LoreBible Lore { get; }
    public LoreTable LoreTable { get; }
    public IReadOnlyDictionary<string, Vec3> Anchors { get; }
    public IReadOnlyDictionary<string, EventDefinition> Events { get; }
    public IReadOnlyDictionary<string, QuestBlueprintData> Quests { get; }
    public IReadOnlyDictionary<string, Establishment> Establishments { get; }
    public IReadOnlyDictionary<string, NpcTemplateData> NpcTemplates { get; }
    public IReadOnlyDictionary<string, NpcInstanceProfile> NpcInstances { get; }
    public IReadOnlyDictionary<string, FactionDefinition> FactionDefinitions { get; }
    public FactionReputationTable FactionReputations { get; }
    public RelationshipRules RelationshipRules { get; }
    public IReadOnlyList<RelationshipSeed> RelationshipSeeds { get; }
    public string PlayerCharacterId { get; }
    public IReadOnlyList<string> PlayerFactionIds { get; }
    public IntimacyRules IntimacyRules { get; }
    public IReadOnlyList<IntimacyFriendshipSeed> IntimacyFriendshipSeeds { get; }
    public IReadOnlyList<IntimacyRomanticSeed> IntimacyRomanticSeeds { get; }
    public CharacterIntimacyProfileData? PlayerIntimacyProfile { get; }
    public CharacterCultivationData? PlayerCultivationProfile { get; }
    public FaceCultureRules FaceCultureRules { get; }
    public double? PlayerFace { get; }
    public PhysicalWorldDefinition? PhysicalWorld { get; }

    public PhysicalWorldIndex? CreatePhysicalWorldIndex() =>
        PhysicalWorld is null ? null : new PhysicalWorldIndex(PhysicalWorld);

    public CharacterFaceRegistry CreateFaceRegistry() =>
        CharacterFaceRegistry.FromContent(NpcInstances.Values, PlayerCharacterId, PlayerFace, FaceCultureRules);

    public CharacterCultivationRegistry CreateCultivationRegistry(PlayerSnapshot? livePlayer = null)
    {
        var reg = new CharacterCultivationRegistry();
        foreach (var npc in NpcInstances.Values)
        {
            var raw = npc.Cultivation ?? new CharacterCultivationData();
            reg.Set(npc.NpcId, LoreTable.NormalizeSnapshot(raw));
        }

        var playerRaw = new CharacterCultivationData();
        if (livePlayer is { RealmId: string rid } && rid.Length > 0)
            playerRaw = new CharacterCultivationData { RealmId = rid, StageIndex = livePlayer.StageIndex };
        else if (PlayerCultivationProfile is not null)
            playerRaw = PlayerCultivationProfile;
        reg.Set(PlayerCharacterId, LoreTable.NormalizeSnapshot(playerRaw));
        return reg;
    }

    public IntimacyWorldState CreateIntimacyWorldState() =>
        IntimacyWorldState.FromSeeds(IntimacyFriendshipSeeds, IntimacyRomanticSeeds);

    public CharacterIntimacyRegistry CreateIntimacyRegistry() =>
        CharacterIntimacyRegistry.FromContent(
            NpcInstances.Values,
            PlayerCharacterId,
            PlayerIntimacyProfile,
            IntimacyContentDefaults.PlayerFallback);

    public GameNarrativeContext CreateNarrativeContext(
        RelationshipWorldState relationships,
        CharacterFactionRegistry factions,
        IntimacyWorldState intimacyBonds,
        CharacterIntimacyRegistry intimacyProfiles,
        CharacterCultivationRegistry? cultivation = null,
        CharacterFaceRegistry? face = null) =>
        new()
        {
            Relationship = CreateRelationshipContext(relationships, factions, cultivation),
            Intimacy = new IntimacyEvalContext
            {
                Bonds = intimacyBonds,
                Profiles = intimacyProfiles,
                Rules = IntimacyRules,
            },
            Face = face is null
                ? null
                : new FaceEvalContext { Profiles = face, Rules = FaceCultureRules },
        };

    public CharacterFactionRegistry CreateFactionRegistry()
    {
        var r = new CharacterFactionRegistry();
        foreach (var npc in NpcInstances.Values)
            r.SetFactions(npc.NpcId, npc.FactionIds);
        r.SetFactions(PlayerCharacterId, PlayerFactionIds);
        return r;
    }

    public RelationshipEvalContext CreateRelationshipContext(
        RelationshipWorldState relationships,
        CharacterFactionRegistry factions,
        CharacterCultivationRegistry? cultivation = null) =>
        new()
        {
            Relationships = relationships,
            FactionReputations = FactionReputations,
            Factions = factions,
            Rules = RelationshipRules,
            Lore = LoreTable,
            Cultivation = cultivation,
        };

    /// <summary>Loads core data using <see cref="WorldContentIndex"/> (see <c>world/content_index.json</c>).</summary>
    internal static ContentPack LoadCore(string contentRoot, WorldContentIndex index) =>
        LoadContentRoot(contentRoot, index);

    /// <summary>Eager pack: core from the content index plus merged active region bundles.</summary>
    public static ContentPack Load(string contentRoot) =>
        WorldRuntimeSession.Load(contentRoot).ToContentPack();

    private static ContentPack LoadContentRoot(string contentRoot, WorldContentIndex streamIndex)
    {
        var lorePath = Path.Combine(contentRoot, "lore", "bible.json");
        var loreJson = File.ReadAllText(lorePath);
        var lore = JsonSerializer.Deserialize<LoreBible>(loreJson, GameJson.Options)
            ?? throw new InvalidOperationException($"Missing or invalid lore: {lorePath}");

        var anchorsPath = Path.Combine(contentRoot, "anchors.json");
        var anchorsJson = File.ReadAllText(anchorsPath);
        var anchors =
            JsonSerializer.Deserialize<Dictionary<string, Vec3>>(anchorsJson, GameJson.Options)
            ?? new Dictionary<string, Vec3>(StringComparer.Ordinal);

        var eventsDir = Path.Combine(contentRoot, "events");
        var events = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
        if (streamIndex.IncludeGlobalEventDirectory)
        {
            if (Directory.Exists(eventsDir))
            {
                foreach (var path in Directory.EnumerateFiles(eventsDir, "*.json", SearchOption.AllDirectories))
                {
                    var json = File.ReadAllText(path);
                    var ev = JsonSerializer.Deserialize<EventDefinition>(json, GameJson.Options);
                    if (ev is null) continue;
                    events[ev.Id] = ev;
                }
            }
        }

        if (streamIndex.CoreEventFiles is { Count: > 0 } coreEv)
        {
            foreach (var rel in coreEv)
            {
                var path = Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    continue;
                var json = File.ReadAllText(path);
                var ev = JsonSerializer.Deserialize<EventDefinition>(json, GameJson.Options);
                if (ev is null) continue;
                events[ev.Id] = ev;
            }
        }

        var questsDir = Path.Combine(contentRoot, "quests");
        var quests = new Dictionary<string, QuestBlueprintData>(StringComparer.Ordinal);
        if (streamIndex.IncludeGlobalQuestDirectory)
        {
            if (Directory.Exists(questsDir))
            {
                foreach (var path in Directory.EnumerateFiles(questsDir, "*.json", SearchOption.AllDirectories))
                {
                    var json = File.ReadAllText(path);
                    var q = JsonSerializer.Deserialize<QuestBlueprintData>(json, GameJson.Options);
                    if (q is null) continue;
                    quests[q.QuestId] = q;
                }
            }
        }

        if (streamIndex.CoreQuestFiles is { Count: > 0 } coreQ)
        {
            foreach (var rel in coreQ)
            {
                var path = Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    continue;
                var json = File.ReadAllText(path);
                var q = JsonSerializer.Deserialize<QuestBlueprintData>(json, GameJson.Options);
                if (q is null) continue;
                quests[q.QuestId] = q;
            }
        }

        var estPath = Path.Combine(contentRoot, "world", "establishments.json");
        var establishments = new Dictionary<string, Establishment>(StringComparer.Ordinal);
        if (streamIndex.LoadGlobalEstablishmentsFile && File.Exists(estPath))
        {
            var estJson = File.ReadAllText(estPath);
            var list = JsonSerializer.Deserialize<EstablishmentListFile>(estJson, GameJson.Options);
            if (list?.Establishments is { Count: > 0 })
            {
                foreach (var e in list.Establishments)
                    establishments[e.Id] = e;
            }
        }

        var tmplPath = Path.Combine(contentRoot, "world", "npc_templates.json");
        var npcTemplates = new Dictionary<string, NpcTemplateData>(StringComparer.Ordinal);
        if (File.Exists(tmplPath))
        {
            var tJson = File.ReadAllText(tmplPath);
            var tFile = JsonSerializer.Deserialize<NpcTemplateListFile>(tJson, GameJson.Options);
            if (tFile?.Templates is { Count: > 0 })
            {
                foreach (var t in tFile.Templates)
                    npcTemplates[t.Id] = t;
            }
        }

        var instPath = Path.Combine(contentRoot, "world", "npc_instances.json");
        var npcInstances = new Dictionary<string, NpcInstanceProfile>(StringComparer.Ordinal);
        if (streamIndex.LoadGlobalNpcInstancesFile && File.Exists(instPath))
        {
            var iJson = File.ReadAllText(instPath);
            var iFile = JsonSerializer.Deserialize<NpcInstanceListFile>(iJson, GameJson.Options);
            if (iFile?.Instances is { Count: > 0 })
            {
                foreach (var i in iFile.Instances)
                    npcInstances[i.NpcId] = i;
            }
        }

        var factionsPath = Path.Combine(contentRoot, "world", "factions.json");
        var factionDefinitions = new Dictionary<string, FactionDefinition>(StringComparer.Ordinal);
        if (File.Exists(factionsPath))
        {
            var fJson = File.ReadAllText(factionsPath);
            var fFile = JsonSerializer.Deserialize<FactionListFile>(fJson, GameJson.Options);
            if (fFile?.Factions is { Count: > 0 })
            {
                foreach (var f in fFile.Factions)
                    factionDefinitions[f.Id] = f;
            }
        }

        var repPath = Path.Combine(contentRoot, "world", "faction_reputation.json");
        var factionReputations = new FactionReputationTable();
        if (File.Exists(repPath))
        {
            var repJson = File.ReadAllText(repPath);
            var repFile = JsonSerializer.Deserialize<FactionReputationFile>(repJson, GameJson.Options);
            if (repFile?.Pairs is { Count: > 0 })
            {
                foreach (var p in repFile.Pairs)
                    factionReputations.Set(p.FromFactionId, p.ToFactionId, p.Value);
            }
        }

        var rulesPath = Path.Combine(contentRoot, "world", "relationship_rules.json");
        var relationshipRules = RelationshipRules.Default;
        if (File.Exists(rulesPath))
        {
            var rulesJson = File.ReadAllText(rulesPath);
            var rr = JsonSerializer.Deserialize<RelationshipRules>(rulesJson, GameJson.Options);
            if (rr is not null)
                relationshipRules = rr;
        }

        var seedsPath = Path.Combine(contentRoot, "world", "relationship_seeds.json");
        List<RelationshipSeed> relationshipSeeds = [];
        if (File.Exists(seedsPath))
        {
            var sJson = File.ReadAllText(seedsPath);
            var sFile = JsonSerializer.Deserialize<RelationshipSeedsFile>(sJson, GameJson.Options);
            if (sFile?.Pairs is { Count: > 0 })
                relationshipSeeds = sFile.Pairs;
        }

        var playerPath = Path.Combine(contentRoot, "world", "player_social.json");
        var playerCharacterId = CharacterIds.Player;
        List<string> playerFactionIds = [];
        CharacterIntimacyProfileData? playerIntimacy = null;
        CharacterCultivationData? playerCultivation = null;
        double? playerFace = null;
        if (File.Exists(playerPath))
        {
            var pJson = File.ReadAllText(playerPath);
            var pFile = JsonSerializer.Deserialize<PlayerSocialFile>(pJson, GameJson.Options);
            if (pFile is not null)
            {
                if (!string.IsNullOrEmpty(pFile.CharacterId))
                    playerCharacterId = pFile.CharacterId;
                playerFactionIds = pFile.FactionIds ?? [];
                playerIntimacy = pFile.Intimacy;
                playerCultivation = pFile.Cultivation;
                playerFace = pFile.Face;
            }
        }

        var faceRulesPath = Path.Combine(contentRoot, "world", "face_rules.json");
        var faceCultureRules = FaceCultureRules.Default;
        if (File.Exists(faceRulesPath))
        {
            var frJson = File.ReadAllText(faceRulesPath);
            var fr = JsonSerializer.Deserialize<FaceCultureRules>(frJson, GameJson.Options);
            if (fr is not null)
                faceCultureRules = fr;
        }

        var intimacyRulesPath = Path.Combine(contentRoot, "world", "intimacy_rules.json");
        var intimacyRules = IntimacyRules.Default;
        if (File.Exists(intimacyRulesPath))
        {
            var irJson = File.ReadAllText(intimacyRulesPath);
            var ir = JsonSerializer.Deserialize<IntimacyRules>(irJson, GameJson.Options);
            if (ir is not null)
                intimacyRules = ir;
        }

        var bondsPath = Path.Combine(contentRoot, "world", "intimacy_bonds.json");
        List<IntimacyFriendshipSeed> intimacyFriendship = [];
        List<IntimacyRomanticSeed> intimacyRomantic = [];
        if (File.Exists(bondsPath))
        {
            var bJson = File.ReadAllText(bondsPath);
            var bFile = JsonSerializer.Deserialize<IntimacyBondsFile>(bJson, GameJson.Options);
            if (bFile?.Friendship is { Count: > 0 })
                intimacyFriendship = bFile.Friendship;
            if (bFile?.RomanticInterest is { Count: > 0 })
                intimacyRomantic = bFile.RomanticInterest;
        }

        var physicalWorldPath = Path.Combine(contentRoot, "world", "physical_world.json");
        PhysicalWorldDefinition? physicalWorld = null;
        if (File.Exists(physicalWorldPath))
        {
            var pwJson = File.ReadAllText(physicalWorldPath);
            physicalWorld = JsonSerializer.Deserialize<PhysicalWorldDefinition>(pwJson, GameJson.Options);
        }

        var physicalFeaturesOverlay = Path.Combine(contentRoot, "world", "physical_world.features.json");
        if (physicalWorld is not null && File.Exists(physicalFeaturesOverlay))
        {
            var fxJson = File.ReadAllText(physicalFeaturesOverlay);
            var extra = JsonSerializer.Deserialize<List<PhysicalTerrainFeature>>(fxJson, GameJson.Options);
            if (extra is { Count: > 0 })
                physicalWorld.Features.AddRange(extra);
        }

        return new ContentPack(
            lore,
            anchors,
            events,
            quests,
            establishments,
            npcTemplates,
            npcInstances,
            factionDefinitions,
            factionReputations,
            relationshipRules,
            relationshipSeeds,
            playerCharacterId,
            playerFactionIds,
            intimacyRules,
            intimacyFriendship,
            intimacyRomantic,
            playerIntimacy,
            playerCultivation,
            faceCultureRules,
            playerFace,
            physicalWorld);
    }
}
