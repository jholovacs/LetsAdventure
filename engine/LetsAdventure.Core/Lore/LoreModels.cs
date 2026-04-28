using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Lore;

public sealed class LoreBible
{
    public Cosmology Cosmology { get; set; } = new();
    public List<CultivationRealm> CultivationRealms { get; set; } = [];
    public List<Region> Regions { get; set; } = [];
    public List<Sect> Sects { get; set; } = [];
    public List<string> Taboos { get; set; } = [];
    public List<string> Oaths { get; set; } = [];
}

public sealed class Cosmology
{
    public string WorldName { get; set; } = "";
    public string WorldEpithet { get; set; } = "";
    public string Summary { get; set; } = "";
    public string UpperRealmsNote { get; set; } = "";
    public List<DaoConcept> DaoConcepts { get; set; } = [];
}

public sealed class DaoConcept
{
    public string Term { get; set; } = "";
    public string Gloss { get; set; } = "";
}

public sealed class CultivationRealm
{
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public string Epithet { get; set; } = "";
    public List<string> Stages { get; set; } = [];
    public string Blurb { get; set; } = "";
}

public sealed class Region
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SpiritualAspect { get; set; } = "";
    public string Blurb { get; set; } = "";
    public List<string> Neighbors { get; set; } = [];
}

public sealed class Sect
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Alignment { get; set; } = "";
    public string Seat { get; set; } = "";
    public string Doctrine { get; set; } = "";
    public List<string> SignatureArts { get; set; } = [];
}

public sealed class LoreTable
{
    private readonly Dictionary<string, int> _realmOrder;
    private readonly Dictionary<string, CultivationRealm> _realmById;
    private readonly List<CultivationRealm> _realmsByOrder;

    public LoreTable(LoreBible bible)
    {
        _realmOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        _realmById = new Dictionary<string, CultivationRealm>(StringComparer.Ordinal);
        foreach (var r in bible.CultivationRealms)
        {
            _realmOrder[r.Id] = r.Order;
            _realmById[r.Id] = r;
        }

        _realmsByOrder = bible.CultivationRealms.OrderBy(r => r.Order).ToList();
    }

    public int RealmOrder(string realmId) => _realmOrder.GetValueOrDefault(realmId, -1);

    /// <summary>Canonical realm row; unknown ids map to the lowest-order realm in lore (Qi Refinement).</summary>
    public CultivationRealm ResolveRealm(string realmId)
    {
        if (!string.IsNullOrEmpty(realmId) && _realmById.TryGetValue(realmId, out var found))
            return found;
        return _realmsByOrder.Count > 0
            ? _realmsByOrder[0]
            : new CultivationRealm { Id = CultivationDefaults.EntryRealmId, Order = 1, Stages = [""] };
    }

    /// <summary>Monotonic level across all realms (sum of prior stages + current stage index).</summary>
    public int MinorCultivationLevel(string realmId, int stageIndex)
    {
        var realm = ResolveRealm(realmId);
        var idx = _realmsByOrder.FindIndex(r => string.Equals(r.Id, realm.Id, StringComparison.Ordinal));
        if (idx < 0)
            idx = 0;

        var minor = 0;
        for (var i = 0; i < idx; i++)
        {
            var c = _realmsByOrder[i].Stages.Count;
            minor += Math.Max(1, c);
        }

        if (realm.Stages.Count <= 0)
            return minor;
        var cap = realm.Stages.Count - 1;
        var s = Math.Clamp(stageIndex, 0, cap);
        return minor + s;
    }

    public CharacterCultivationData NormalizeSnapshot(CharacterCultivationData raw)
    {
        var realm = ResolveRealm(raw.RealmId);
        if (realm.Stages.Count <= 0)
            return new CharacterCultivationData { RealmId = realm.Id, StageIndex = 0 };
        var s = Math.Clamp(raw.StageIndex, 0, realm.Stages.Count - 1);
        return new CharacterCultivationData { RealmId = realm.Id, StageIndex = s };
    }
}
