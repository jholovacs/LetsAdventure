namespace LetsAdventure.Core.Npcs;

public enum EstablishmentKind
{
    Home,
    Workplace,
    Inn,
    FoodVendor,
    Market,
    Teahouse,
    Temple,
    TrainingYard,
    Bathhouse,
    SocialHall,
}

/// <summary>
/// A place NPCs commute to for sleep, meals, work, or leisure. Anchor ties to world position.
/// </summary>
public sealed class Establishment
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public EstablishmentKind Kind { get; set; }
    public string RegionId { get; set; } = "";
    public string AnchorId { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}
