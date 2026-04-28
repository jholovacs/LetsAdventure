using LetsAdventure.Core.Content;
using LetsAdventure.Core.Events;
using LetsAdventure.Core.Npcs;
using LetsAdventure.Core.Quests;
using LetsAdventure.Core.Scripting;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.Social;
using LetsAdventure.Core.World;

var contentRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "content"));

if (!Directory.Exists(contentRoot))
{
    Console.Error.WriteLine($"Content root not found: {contentRoot}");
    Console.Error.WriteLine("Usage: LetsAdventure.Shell [path-to-content]");
    return 1;
}

var pack = ContentPack.Load(contentRoot);
Console.WriteLine($"Loaded lore: {pack.Lore.Cosmology.WorldName}");
Console.WriteLine(
    $"Realms: {pack.Lore.CultivationRealms.Count}, Events: {pack.Events.Count}, Quests: {pack.Quests.Count}, " +
    $"Establishments: {pack.Establishments.Count}, NPC instances: {pack.NpcInstances.Count}, " +
    $"Factions: {pack.FactionDefinitions.Count}, Physical world: {(pack.PhysicalWorld is null ? "no" : "yes")}");

var ctx = new GameContext
{
    Player = new PlayerSnapshot
    {
        RealmId = "realm.foundation",
        StageIndex = 2,
        SectId = "sect.azure_peak",
        Traits = ["spirit_root_fire"],
    },
    World = new WorldSnapshot
    {
        RegionId = "region.jade_threshold",
        TimeBand = TimeBand.Day,
        Weather = "clear",
        Tags = ["market_open"],
    },
    PlayerPosition = new Vec3 { X = 122, Y = 41, Z = 0 },
};

if (pack.PhysicalWorld is { } phys && pack.CreatePhysicalWorldIndex() is { } physIdx && ctx.PlayerPosition is { } pPos)
{
    Console.WriteLine(
        $"Physical @ player: loreRegion={physIdx.ResolveLoreRegionId(pPos)}, " +
        $"territoryFaction={physIdx.ResolveTerritory(pPos)?.ClaimantFactionId ?? "none"}");
    if (phys.Navigation.Graph is { } navGraph)
    {
        var gPath = WorldPathfinder.FindPathOnGraph(navGraph, "nav.jade_gate", "nav.mist_ferry");
        Console.WriteLine($"  Nav graph (gate→ferry): {(gPath is null ? "unreachable" : string.Join(" → ", gPath))}");
    }

    if (phys.Navigation.Grid is { } gridDef)
    {
        var grid = new TerrainNavGrid(gridDef);
        var gate = new Vec3 { X = 120, Y = 40, Z = 0 };
        var chen = new Vec3 { X = 122, Y = 41, Z = 0 };
        var gridPath = WorldPathfinder.FindPathOnGrid(grid, gate, chen);
        Console.WriteLine($"  Nav grid (gate→Chen): {(gridPath is null ? "blocked" : $"{gridPath.Count} waypoints")}");
    }
}

var state = ScriptRuntimeState.Empty();
state.Flags["quest.jade_courier.active"] = true;

var engine = EventEngineState.Empty();

var factionRegistry = pack.CreateFactionRegistry();
var relationships = RelationshipWorldState.FromSeeds(pack.RelationshipSeeds);
var intimacyWorld = pack.CreateIntimacyWorldState();
var intimacyReg = pack.CreateIntimacyRegistry();
var cultivationReg = pack.CreateCultivationRegistry(ctx.Player);
var faceReg = pack.CreateFaceRegistry();
var narrative = pack.CreateNarrativeContext(relationships, factionRegistry, intimacyWorld, intimacyReg, cultivationReg, faceReg);

var compiled = QuestCompiler.Compile(pack.Quests["quest.jade_courier"], ctx, state, pack.LoreTable, narrative);
Console.WriteLine($"Quest '{compiled.Title}' available={compiled.Available}, objectives={compiled.Objectives.Count}");

var cycle = EventEngine.ProcessCycle(pack.Events, ctx, state, engine, pack.LoreTable, pack.Anchors, narrative, pack.Quests);
Console.WriteLine($"Event cycle fired: {string.Join(", ", cycle.FiredEventIds)}");

Console.WriteLine(
    $"Face (dignity / public regard): Elder Chen={faceReg.GetFace("npc.gate_elder_chen"):F0} " +
    $"(high-face threshold {pack.FaceCultureRules.HighFaceThreshold:F0}: {faceReg.IsHighFace("npc.gate_elder_chen")}), " +
    $"merchant={faceReg.GetFace("npc.merchant_lu"):F0}, disciple={faceReg.GetFace("npc.outer_disciple"):F0}");

Console.WriteLine("Social standing (personal + faction blend):");
PrintStanding("npc.gate_elder_chen", CharacterIds.Player, pack, relationships, factionRegistry, narrative);
PrintStanding(CharacterIds.Player, "npc.gate_elder_chen", pack, relationships, factionRegistry, narrative);
PrintStanding("npc.merchant_lu", "npc.outer_disciple", pack, relationships, factionRegistry, narrative);
PrintStanding("npc.outer_disciple", "npc.merchant_lu", pack, relationships, factionRegistry, narrative);

var cheatP = IntimacyEvaluator.ComputeInfidelityPressure(
    "npc.merchant_lu",
    "npc.outer_disciple",
    intimacyReg,
    intimacyWorld,
    pack.IntimacyRules);
Console.WriteLine(
    $"Intimacy: merchant→disciple romantic meter={intimacyWorld.GetRomanticInterest("npc.merchant_lu", "npc.outer_disciple"):F0}, " +
    $"infidelity pressure≈{cheatP:F3} (married + low loyalty + pull)");

var npcWorld = new NpcWorldState();
NpcWorldBootstrap.RegisterInstances(npcWorld, pack.NpcInstances.Values);
npcWorld.Clock.MinuteOfDay = 9 * 60;
var lifeSim = new NpcDailyLifeSimulator(pack.Establishments, pack.Anchors);
lifeSim.Tick(npcWorld, pack.NpcInstances, pack.NpcTemplates, elapsedMinutes: 180);
Console.WriteLine($"NPC life (after 3h sim, clock={npcWorld.Clock.MinuteOfDay:F0}m):");
foreach (var a in npcWorld.Agents.Values.OrderBy(x => x.NpcId, StringComparer.Ordinal))
{
    Console.WriteLine(
        $"  {a.NpcId}: {a.CurrentActivity} @ {a.CurrentEstablishmentId} | " +
        $"H{a.Needs.Hunger:F0} F{a.Needs.Fatigue:F0} S{a.Needs.Social:F0} R{a.Needs.Recreation:F0} job{a.WorkObligation:F0}");
}

return 0;

void PrintStanding(
    string a,
    string b,
    ContentPack pack,
    RelationshipWorldState rel,
    CharacterFactionRegistry reg,
    GameNarrativeContext narrative)
{
    var relCtx = narrative.Relationship;
    var d = relCtx is not null
        ? RelationshipEvaluator.GetEffectiveDisposition(relCtx, a, b)
        : RelationshipEvaluator.GetEffectiveDisposition(a, b, rel, pack.FactionReputations, reg, pack.RelationshipRules);
    var att = pack.RelationshipRules.AttitudeFromDisposition(d);
    Console.WriteLine($"  {a} → {b}: disposition {d:F1} ({att})");
}
