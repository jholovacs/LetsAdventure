namespace LetsAdventure.Core.Social;

/// <summary>
/// Tunable blending of personal opinion vs faction diplomacy, and attitude band cutoffs.
/// </summary>
public sealed class RelationshipRules
{
    /// <summary>How much faction-pair standing shifts effective disposition (0 = ignore factions).</summary>
    public double FactionInfluenceWeight { get; set; } = 0.45;

    /// <summary>
    /// Disposition shift per minor cultivation step (target minus observer). Higher cultivation targets earn respect;
    /// lower cultivation reads as mild condescension from the observer.
    /// </summary>
    public double CultivationRespectPerMinorStep { get; set; } = 3.5;

    /// <summary>Absolute cap on the cultivation contribution to disposition (each direction).</summary>
    public double CultivationRespectCap { get; set; } = 55;

    public double MinDisposition { get; set; } = -100;
    public double MaxDisposition { get; set; } = 100;

    public double HostileBelow { get; set; } = -40;
    public double WaryBelow { get; set; } = -12;
    public double NeutralBelow { get; set; } = 12;
    public double WarmBelow { get; set; } = 40;

    public static RelationshipRules Default { get; } = new();

    public SocialAttitude AttitudeFromDisposition(double effectiveDisposition)
    {
        if (effectiveDisposition < HostileBelow) return SocialAttitude.Hostile;
        if (effectiveDisposition < WaryBelow) return SocialAttitude.Wary;
        if (effectiveDisposition < NeutralBelow) return SocialAttitude.Neutral;
        if (effectiveDisposition < WarmBelow) return SocialAttitude.Warm;
        return SocialAttitude.Allied;
    }
}
