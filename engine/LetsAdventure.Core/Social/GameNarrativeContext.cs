namespace LetsAdventure.Core.Social;

/// <summary>
/// Optional bundle for quest/event conditions and effects (faction disposition + intimacy + face).
/// </summary>
public sealed class GameNarrativeContext
{
    public RelationshipEvalContext? Relationship { get; init; }
    public IntimacyEvalContext? Intimacy { get; init; }
    public FaceEvalContext? Face { get; init; }
}
