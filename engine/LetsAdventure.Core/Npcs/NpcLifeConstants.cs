namespace LetsAdventure.Core.Npcs;

/// <summary>
/// Baseline rules shared by every NPC; templates only tune rates and utilities.
/// </summary>
public static class NpcLifeConstants
{
    public const double EatHungerPerMinute = 4.5;
    public const double SocializeSocialPerMinute = 5;
    public const double RecreationFunPerMinute = 4.5;
    public const double WorkObligationReliefPerMinute = 6;

    /// <summary>Below this hunger, eating can stop early.</summary>
    public const double EatSatisfiedBelow = 18;

    /// <summary>Force food-seeking above this hunger unless commuting to food already.</summary>
    public const double HungerUrgent = 78;

    public const double FatigueUrgent = 82;

    /// <summary>Minimum minutes between full activity re-plans (performance + stability).</summary>
    public const double MinDecisionIntervalMinutes = 8;

    public const double DefaultCommuteMinutes = 6;
    public const double CommuteMinutesPerUnitDistance = 2.5;
}
