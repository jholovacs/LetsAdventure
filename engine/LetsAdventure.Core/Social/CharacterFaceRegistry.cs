using LetsAdventure.Core.Npcs;

namespace LetsAdventure.Core.Social;

public sealed class CharacterFaceState
{
    public string CharacterId { get; set; } = "";

    /// <summary>Perceived dignity and stake in public regard (0–100 by default rules).</summary>
    public double Face { get; set; } = 50;
}

public sealed class CharacterFaceRegistry
{
    private readonly Dictionary<string, CharacterFaceState> _byId = new(StringComparer.Ordinal);
    private readonly FaceCultureRules _rules;

    private CharacterFaceRegistry(FaceCultureRules rules) => _rules = rules;

    public static CharacterFaceRegistry FromContent(
        IEnumerable<NpcInstanceProfile> npcInstances,
        string playerCharacterId,
        double? playerFaceAuthored,
        FaceCultureRules rules)
    {
        var reg = new CharacterFaceRegistry(rules);
        foreach (var npc in npcInstances)
        {
            var f = npc.Face ?? rules.DefaultFace;
            f = Math.Clamp(f, rules.MinFace, rules.MaxFace);
            reg._byId[npc.NpcId] = new CharacterFaceState { CharacterId = npc.NpcId, Face = f };
        }

        var pf = playerFaceAuthored ?? rules.DefaultFace;
        pf = Math.Clamp(pf, rules.MinFace, rules.MaxFace);
        reg._byId[playerCharacterId] = new CharacterFaceState { CharacterId = playerCharacterId, Face = pf };
        return reg;
    }

    public CharacterFaceState? TryGet(string characterId) =>
        _byId.TryGetValue(characterId, out var s) ? s : null;

    public double GetFace(string characterId)
    {
        if (_byId.TryGetValue(characterId, out var s))
            return s.Face;
        return Math.Clamp(_rules.DefaultFace, _rules.MinFace, _rules.MaxFace);
    }

    public bool IsHighFace(string characterId) =>
        GetFace(characterId) >= _rules.HighFaceThreshold;

    public void AddDelta(string characterId, double delta)
    {
        if (!_byId.TryGetValue(characterId, out var s))
            return;
        s.Face = Math.Clamp(s.Face + delta, _rules.MinFace, _rules.MaxFace);
    }

    public void SetFace(string characterId, double value)
    {
        if (!_byId.TryGetValue(characterId, out var s))
            return;
        s.Face = Math.Clamp(value, _rules.MinFace, _rules.MaxFace);
    }
}
