namespace LetsAdventure.Core.Social;

public sealed class IntimacyEvalContext
{
    public required IntimacyWorldState Bonds { get; init; }
    public required CharacterIntimacyRegistry Profiles { get; init; }
    public IntimacyRules Rules { get; init; } = IntimacyRules.Default;
}
