namespace LetsAdventure.Core.Social;

/// <summary>Default tier for anyone without explicit cultivation (including ordinary mortals): first stage of Qi Refinement.</summary>
public static class CultivationDefaults
{
    public const string EntryRealmId = "realm.qi_vein";
    public const int EntryStageIndex = 0;
}

/// <summary>Realm + stage from content or live <see cref="Simulation.PlayerSnapshot"/>.</summary>
public sealed class CharacterCultivationData
{
    public string RealmId { get; set; } = CultivationDefaults.EntryRealmId;
    public int StageIndex { get; set; } = CultivationDefaults.EntryStageIndex;
}

/// <summary>Per-character cultivation for disposition and scripting.</summary>
public sealed class CharacterCultivationRegistry
{
    private readonly Dictionary<string, CharacterCultivationData> _byId = new(StringComparer.Ordinal);

    public void Set(string characterId, CharacterCultivationData data) =>
        _byId[characterId] = data;

    public CharacterCultivationData GetOrBaseline(string characterId) =>
        _byId.TryGetValue(characterId, out var d)
            ? d
            : new CharacterCultivationData();
}
