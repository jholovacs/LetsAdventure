namespace LetsAdventure.Core.Social;

public static class IntimacyContentDefaults
{
    public static CharacterIntimacyProfileData PlayerFallback { get; } = new()
    {
        GenderPresentation = GenderPresentation.Masculine,
        Orientation = new SexualOrientationSpectrum
        {
            TowardMasculinePresentation = 0.35,
            TowardFemininePresentation = 0.65,
            RomanticDrive = 0.55,
        },
        LoyaltyIntegrity = 72,
        RelationshipStatus = RelationshipStatus.Single,
    };
}
