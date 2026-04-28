namespace LetsAdventure.Core.Social;

public enum GenderPresentation
{
    Unknown,
    Masculine,
    Feminine,
    NonBinary,
    Agender,
    Fluid,
    Other,
}

/// <summary>
/// Spectrum model: independent attraction toward masculine- and feminine-presenting people (0–1 each).
/// Both can be high (e.g. bi/pan spectrum), both low (e.g. ace-leaning), or asymmetric.
/// </summary>
public sealed class SexualOrientationSpectrum
{
    public double TowardMasculinePresentation { get; set; }
    public double TowardFemininePresentation { get; set; }

    /// <summary>Overall strength of romantic/erotic drive (0 = very low, 1 = high).</summary>
    public double RomanticDrive { get; set; } = 0.55;

    public void NormalizeAttractionAxes()
    {
        TowardMasculinePresentation = Math.Clamp(TowardMasculinePresentation, 0, 1);
        TowardFemininePresentation = Math.Clamp(TowardFemininePresentation, 0, 1);
        RomanticDrive = Math.Clamp(RomanticDrive, 0, 1);
    }
}

public enum RelationshipStatus
{
    Single,
    Courting,
    Committed,
    Married,
    PolyCommitted,
    Widowed,
}

/// <summary>
/// Authored intimacy row (embedded on NPC instance or player social JSON).
/// </summary>
public sealed class CharacterIntimacyProfileData
{
    public GenderPresentation GenderPresentation { get; set; } = GenderPresentation.Unknown;
    public SexualOrientationSpectrum Orientation { get; set; } = new();
    /// <summary>0–100. Married templates should start high (e.g. 88–92) but not 100.</summary>
    public double LoyaltyIntegrity { get; set; } = 55;
    public RelationshipStatus RelationshipStatus { get; set; } = RelationshipStatus.Single;
    public List<string> SpouseCharacterIds { get; set; } = [];
    /// <summary>Explicit romantic focus; runtime interest can grow beyond this list.</summary>
    public List<string> RomanticInterestTargets { get; set; } = [];
}

/// <summary>
/// Runtime per-character intimacy (mutable loyalty, status, spouses; orientation/gender usually static).
/// </summary>
public sealed class CharacterIntimacyState
{
    public string CharacterId { get; set; } = "";
    public GenderPresentation GenderPresentation { get; set; }
    public SexualOrientationSpectrum Orientation { get; set; } = new();
    public double LoyaltyIntegrity { get; set; }
    public RelationshipStatus RelationshipStatus { get; set; }
    public List<string> SpouseCharacterIds { get; set; } = [];
    public List<string> RomanticInterestTargets { get; set; } = [];

    public static CharacterIntimacyState FromProfile(string characterId, CharacterIntimacyProfileData p)
    {
        p.Orientation.NormalizeAttractionAxes();
        var loyalty = p.LoyaltyIntegrity;
        if (p.RelationshipStatus is RelationshipStatus.Married or RelationshipStatus.Committed)
            loyalty = Math.Min(loyalty, 98);

        return new CharacterIntimacyState
        {
            CharacterId = characterId,
            GenderPresentation = p.GenderPresentation,
            Orientation = p.Orientation,
            LoyaltyIntegrity = Math.Clamp(loyalty, 0, 100),
            RelationshipStatus = p.RelationshipStatus,
            SpouseCharacterIds = [.. p.SpouseCharacterIds],
            RomanticInterestTargets = [.. p.RomanticInterestTargets],
        };
    }

    public CharacterIntimacyState Clone() => new()
    {
        CharacterId = CharacterId,
        GenderPresentation = GenderPresentation,
        Orientation = new SexualOrientationSpectrum
        {
            TowardMasculinePresentation = Orientation.TowardMasculinePresentation,
            TowardFemininePresentation = Orientation.TowardFemininePresentation,
            RomanticDrive = Orientation.RomanticDrive,
        },
        LoyaltyIntegrity = LoyaltyIntegrity,
        RelationshipStatus = RelationshipStatus,
        SpouseCharacterIds = [.. SpouseCharacterIds],
        RomanticInterestTargets = [.. RomanticInterestTargets],
    };
}
