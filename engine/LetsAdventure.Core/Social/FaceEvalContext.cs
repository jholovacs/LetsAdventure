namespace LetsAdventure.Core.Social;

public sealed class FaceEvalContext
{
    public required CharacterFaceRegistry Profiles { get; init; }
    public FaceCultureRules Rules { get; init; } = FaceCultureRules.Default;
}
