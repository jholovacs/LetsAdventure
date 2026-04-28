namespace LetsAdventure.Core.Social;

/// <summary>
/// Characters (player + NPC ids) belong to zero or more factions; used for diplomacy blending.
/// </summary>
public sealed class CharacterFactionRegistry
{
    private readonly Dictionary<string, List<string>> _byCharacter = new(StringComparer.Ordinal);

    public CharacterFactionRegistry Clone()
    {
        var c = new CharacterFactionRegistry();
        foreach (var kv in _byCharacter)
            c._byCharacter[kv.Key] = [.. kv.Value];
        return c;
    }

    public void SetFactions(string characterId, IReadOnlyList<string> factionIds)
    {
        _byCharacter[characterId] = [.. factionIds];
    }

    public void AddFaction(string characterId, string factionId)
    {
        if (!_byCharacter.TryGetValue(characterId, out var list))
        {
            list = [];
            _byCharacter[characterId] = list;
        }

        if (!list.Contains(factionId, StringComparer.Ordinal))
            list.Add(factionId);
    }

    public bool RemoveFaction(string characterId, string factionId)
    {
        if (!_byCharacter.TryGetValue(characterId, out var list)) return false;
        return list.RemoveAll(x => string.Equals(x, factionId, StringComparison.Ordinal)) > 0;
    }

    public IReadOnlyList<string> GetFactions(string characterId) =>
        _byCharacter.TryGetValue(characterId, out var list) ? list : [];
}
