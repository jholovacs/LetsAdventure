using LetsAdventure.Core.Lore;

namespace LetsAdventure.Core.Social;

/// <summary>
/// Optional bundle for scripting conditions that depend on social standing.
/// </summary>
public sealed class RelationshipEvalContext
{
    public required RelationshipWorldState Relationships { get; init; }
    public required FactionReputationTable FactionReputations { get; init; }
    public required CharacterFactionRegistry Factions { get; init; }
    public RelationshipRules Rules { get; init; } = RelationshipRules.Default;

    public LoreTable? Lore { get; init; }
    public CharacterCultivationRegistry? Cultivation { get; init; }
}
