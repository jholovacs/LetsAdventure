using System.Text.Json;
using LetsAdventure.Core.Events;
using LetsAdventure.Core.Json;
using LetsAdventure.Core.Npcs;
using LetsAdventure.Core.Quests;
using LetsAdventure.Core.World;

namespace LetsAdventure.Core.Content;

/// <summary>
/// Loads world content from <c>world/content_index.json</c> (core slice + optional region bundles).
/// </summary>
public sealed class WorldRuntimeSession
{
    private readonly string _contentRoot;
    private readonly WorldContentIndex _index;
    private readonly ContentPack _core;
    private readonly List<RegionContentRef> _regionRefs;
    private readonly Dictionary<string, RegionBundle> _bundles = new(StringComparer.Ordinal);
    private HashSet<string> _activeLoreRegionIds = new(StringComparer.Ordinal);

    private WorldRuntimeSession(
        string contentRoot,
        WorldContentIndex index,
        ContentPack core,
        List<RegionContentRef> regionRefs)
    {
        _contentRoot = contentRoot;
        _index = index;
        _core = core;
        _regionRefs = regionRefs;
    }

    /// <param name="contentRoot">Content root directory (contains lore, world, events, …).</param>
    /// <param name="initialActiveLoreRegions">
    /// Subset of <see cref="RegionContentRef.LoreRegionId"/> to load and merge.
    /// When null or empty, all regions listed in the index are active.
    /// </param>
    public static WorldRuntimeSession Load(
        string contentRoot,
        IReadOnlyCollection<string>? initialActiveLoreRegions = null)
    {
        var indexPath = Path.Combine(contentRoot, "world", "content_index.json");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException($"Required world content index not found: {indexPath}", indexPath);

        var json = File.ReadAllText(indexPath);
        var index = JsonSerializer.Deserialize<WorldContentIndex>(json, GameJson.Options)
            ?? new WorldContentIndex();

        var indexedCore = ContentPack.LoadCore(contentRoot, index);
        var refs = index.Regions ?? [];
        var indexedSession = new WorldRuntimeSession(contentRoot, index, indexedCore, refs);
        indexedSession.SetActiveLoreRegions(initialActiveLoreRegions);
        return indexedSession;
    }

    public ContentPack CoreContent => _core;

    public IReadOnlyCollection<string> ActiveLoreRegionIds => _activeLoreRegionIds;

    public void SetActiveLoreRegions(IReadOnlyCollection<string>? loreRegionIds)
    {
        HashSet<string> next;
        if (loreRegionIds is null || loreRegionIds.Count == 0)
        {
            next = _regionRefs
                .Select(r => r.LoreRegionId)
                .Where(static id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            next = loreRegionIds
                .Where(static id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.Ordinal);
        }

        _activeLoreRegionIds = next;

        foreach (var key in _bundles.Keys.ToArray())
        {
            if (!_activeLoreRegionIds.Contains(key))
                _bundles.Remove(key);
        }

        foreach (var id in _activeLoreRegionIds)
            EnsureBundleLoaded(id);
    }

    private void EnsureBundleLoaded(string loreRegionId)
    {
        if (_bundles.ContainsKey(loreRegionId))
            return;

        var rref = _regionRefs.Find(r => string.Equals(r.LoreRegionId, loreRegionId, StringComparison.Ordinal));
        if (rref is null)
            return;

        _bundles[loreRegionId] = RegionBundle.Load(_contentRoot, rref);
    }

    /// <summary>
    /// Merged view: core plus active region bundles (same key order as <see cref="WorldContentIndex.Regions"/>; later entries win).
    /// </summary>
    public ContentPack ToContentPack()
    {
        var events = new Dictionary<string, EventDefinition>(_core.Events, StringComparer.Ordinal);
        var quests = new Dictionary<string, QuestBlueprintData>(_core.Quests, StringComparer.Ordinal);
        var establishments = new Dictionary<string, Establishment>(_core.Establishments, StringComparer.Ordinal);
        var npcInstances = new Dictionary<string, NpcInstanceProfile>(_core.NpcInstances, StringComparer.Ordinal);
        var physicalOverlays = new List<PhysicalWorldDefinition>();

        foreach (var rref in _regionRefs)
        {
            if (string.IsNullOrEmpty(rref.LoreRegionId) || !_activeLoreRegionIds.Contains(rref.LoreRegionId))
                continue;

            if (!_bundles.TryGetValue(rref.LoreRegionId, out var bundle))
                continue;

            foreach (var kv in bundle.Events)
                events[kv.Key] = kv.Value;
            foreach (var kv in bundle.Quests)
                quests[kv.Key] = kv.Value;
            foreach (var kv in bundle.Establishments)
                establishments[kv.Key] = kv.Value;
            foreach (var kv in bundle.NpcInstances)
                npcInstances[kv.Key] = kv.Value;

            if (bundle.PhysicalOverlay is not null)
                physicalOverlays.Add(bundle.PhysicalOverlay);
        }

        var mergedPhysical = physicalOverlays.Count > 0
            ? PhysicalWorldMerger.Merge(_core.PhysicalWorld, physicalOverlays)
            : _core.PhysicalWorld;

        return new ContentPack(
            _core.Lore,
            _core.Anchors,
            events,
            quests,
            establishments,
            _core.NpcTemplates,
            npcInstances,
            _core.FactionDefinitions,
            _core.FactionReputations,
            _core.RelationshipRules,
            _core.RelationshipSeeds,
            _core.PlayerCharacterId,
            _core.PlayerFactionIds,
            _core.IntimacyRules,
            _core.IntimacyFriendshipSeeds,
            _core.IntimacyRomanticSeeds,
            _core.PlayerIntimacyProfile,
            _core.PlayerCultivationProfile,
            _core.FaceCultureRules,
            _core.PlayerFace,
            mergedPhysical);
    }
}

internal sealed class RegionBundle
{
    public Dictionary<string, EventDefinition> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, QuestBlueprintData> Quests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Establishment> Establishments { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NpcInstanceProfile> NpcInstances { get; } = new(StringComparer.Ordinal);
    public PhysicalWorldDefinition? PhysicalOverlay { get; private set; }

    public static RegionBundle Load(string contentRoot, RegionContentRef r)
    {
        var bundle = new RegionBundle();
        var dir = Path.Combine(contentRoot, r.Directory.Replace('/', Path.DirectorySeparatorChar));

        var npcPath = Path.Combine(dir, r.NpcInstancesFile.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(npcPath))
        {
            var json = File.ReadAllText(npcPath);
            var file = JsonSerializer.Deserialize<NpcInstanceListFile>(json, GameJson.Options);
            if (file?.Instances is { Count: > 0 })
            {
                foreach (var i in file.Instances)
                    bundle.NpcInstances[i.NpcId] = i;
            }
        }

        if (!string.IsNullOrEmpty(r.EstablishmentsFile))
        {
            var estPath = Path.Combine(dir, r.EstablishmentsFile.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(estPath))
            {
                var estJson = File.ReadAllText(estPath);
                var list = JsonSerializer.Deserialize<EstablishmentListFile>(estJson, GameJson.Options);
                if (list?.Establishments is { Count: > 0 })
                {
                    foreach (var e in list.Establishments)
                        bundle.Establishments[e.Id] = e;
                }
            }
        }

        if (!string.IsNullOrEmpty(r.PhysicalOverlayFile))
        {
            var physPath = Path.Combine(dir, r.PhysicalOverlayFile.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(physPath))
            {
                var pJson = File.ReadAllText(physPath);
                bundle.PhysicalOverlay = JsonSerializer.Deserialize<PhysicalWorldDefinition>(pJson, GameJson.Options);
            }
        }

        foreach (var rel in r.EventFiles ?? [])
        {
            var path = Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                continue;
            var json = File.ReadAllText(path);
            var ev = JsonSerializer.Deserialize<EventDefinition>(json, GameJson.Options);
            if (ev is not null)
                bundle.Events[ev.Id] = ev;
        }

        foreach (var rel in r.QuestFiles ?? [])
        {
            var path = Path.Combine(contentRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                continue;
            var json = File.ReadAllText(path);
            var q = JsonSerializer.Deserialize<QuestBlueprintData>(json, GameJson.Options);
            if (q is not null)
                bundle.Quests[q.QuestId] = q;
        }

        return bundle;
    }
}
