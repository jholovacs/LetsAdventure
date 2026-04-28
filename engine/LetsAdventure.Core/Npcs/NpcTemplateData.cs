using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Npcs;

/// <summary>
/// Shared routine parameters; instances reference a template and override home/work.
/// </summary>
public sealed class NpcTemplateData
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Minute of day when sleep is preferred to start (e.g. 1320 = 22:00).</summary>
    public int SleepStartMinute { get; set; } = 22 * 60;

    /// <summary>Minute of day when sleep normally ends (may wrap past midnight; e.g. 360 = 06:00).</summary>
    public int SleepEndMinute { get; set; } = 6 * 60;

    public int WorkStartMinute { get; set; } = 8 * 60;
    public int WorkEndMinute { get; set; } = 17 * 60;

    /// <summary>Per simulated hour while awake (non-sleep activity).</summary>
    public double HungerPerHour { get; set; } = 10;

    public double FatiguePerHourAwake { get; set; } = 7;
    public double FatiguePerHourSleeping { get; set; } = -35;
    public double SocialPerHour { get; set; } = 4;
    public double RecreationPerHour { get; set; } = 5;

    /// <summary>Multiplier on global baseline (ascetic 0.7, indulgent 1.3).</summary>
    public double NeedIntensity { get; set; } = 1;

    /// <summary>How strongly work hours pull toward <see cref="NpcActivityKind.Work"/>.</summary>
    public double WorkEthic { get; set; } = 1;

    /// <summary>Per hour while not working during work window (obligation builds).</summary>
    public double WorkObligationPerHour { get; set; } = 12;

    /// <summary>Utility multipliers by activity id (work, eat, sleep, socialize, recreation, worship, train).</summary>
    public Dictionary<string, double> ActivityUtilityBias { get; set; } = new(StringComparer.Ordinal);

    public List<EstablishmentKind> PreferredFoodKinds { get; set; } = [EstablishmentKind.FoodVendor, EstablishmentKind.Market, EstablishmentKind.Inn];

    public List<EstablishmentKind> PreferredSleepKinds { get; set; } = [EstablishmentKind.Home, EstablishmentKind.Inn];

    public List<EstablishmentKind> PreferredRecreationKinds { get; set; } = [EstablishmentKind.Teahouse, EstablishmentKind.TrainingYard, EstablishmentKind.SocialHall];
}

/// <summary>
/// Binds a world NPC id to a template and fixed home/work locations.
/// </summary>
public sealed class NpcInstanceProfile
{
    public string NpcId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string HomeEstablishmentId { get; set; } = "";
    public string WorkEstablishmentId { get; set; } = "";
    public List<string> FactionIds { get; set; } = [];
    public List<string> ExtraTraits { get; set; } = [];
    public CharacterIntimacyProfileData? Intimacy { get; set; }

    /// <summary>When omitted, treated as first stage of Qi Refinement (<see cref="CultivationDefaults.EntryRealmId"/>).</summary>
    public CharacterCultivationData? Cultivation { get; set; }

    /// <summary>面子: stake in dignity and public regard; high values drive stronger reputation-defense behavior.</summary>
    public double? Face { get; set; }
}
