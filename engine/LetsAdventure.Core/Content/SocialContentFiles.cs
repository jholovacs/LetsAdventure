using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Content;

internal sealed class FactionListFile
{
    public List<FactionDefinition> Factions { get; set; } = [];
}

internal sealed class FactionReputationFile
{
    public List<FactionReputationPair> Pairs { get; set; } = [];
}

internal sealed class RelationshipSeedsFile
{
    public List<RelationshipSeed> Pairs { get; set; } = [];
}

internal sealed class PlayerSocialFile
{
    public string CharacterId { get; set; } = CharacterIds.Player;
    public List<string> FactionIds { get; set; } = [];
    public CharacterIntimacyProfileData? Intimacy { get; set; }
    public CharacterCultivationData? Cultivation { get; set; }

    /// <summary>Optional; defaults from <see cref="FaceCultureRules.DefaultFace"/>.</summary>
    public double? Face { get; set; }
}

internal sealed class IntimacyBondsFile
{
    public List<IntimacyFriendshipSeed> Friendship { get; set; } = [];
    public List<IntimacyRomanticSeed> RomanticInterest { get; set; } = [];
}
