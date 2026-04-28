using LetsAdventure.Core.Npcs;

namespace LetsAdventure.Core.Social;

public sealed class CharacterIntimacyRegistry
{
    private readonly Dictionary<string, CharacterIntimacyState> _byId = new(StringComparer.Ordinal);

    public void Register(CharacterIntimacyState state)
    {
        _byId[state.CharacterId] = state;
    }

    public CharacterIntimacyState? TryGet(string characterId) =>
        _byId.TryGetValue(characterId, out var s) ? s : null;

    public CharacterIntimacyState GetRequired(string characterId) =>
        TryGet(characterId) ?? throw new KeyNotFoundException($"No intimacy profile for {characterId}");

    public CharacterIntimacyRegistry Clone()
    {
        var c = new CharacterIntimacyRegistry();
        foreach (var kv in _byId)
            c._byId[kv.Key] = kv.Value.Clone();
        return c;
    }

    public static CharacterIntimacyRegistry FromContent(
        IEnumerable<NpcInstanceProfile> npcInstances,
        string playerCharacterId,
        CharacterIntimacyProfileData? playerIntimacy,
        CharacterIntimacyProfileData defaultPlayerIntimacy)
    {
        var reg = new CharacterIntimacyRegistry();
        foreach (var npc in npcInstances)
        {
            if (npc.Intimacy is not null)
                reg.Register(CharacterIntimacyState.FromProfile(npc.NpcId, npc.Intimacy));
            else
                reg.Register(MinimalNpcState(npc.NpcId));
        }

        var p = playerIntimacy ?? defaultPlayerIntimacy;
        reg.Register(CharacterIntimacyState.FromProfile(playerCharacterId, p));
        return reg;
    }

    private static CharacterIntimacyState MinimalNpcState(string npcId) => new()
    {
        CharacterId = npcId,
        GenderPresentation = GenderPresentation.Unknown,
        Orientation = new SexualOrientationSpectrum
        {
            TowardMasculinePresentation = 0.45,
            TowardFemininePresentation = 0.45,
            RomanticDrive = 0.45,
        },
        LoyaltyIntegrity = 60,
        RelationshipStatus = RelationshipStatus.Single,
    };
}
