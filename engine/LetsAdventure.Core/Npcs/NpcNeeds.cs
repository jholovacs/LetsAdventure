namespace LetsAdventure.Core.Npcs;

/// <summary>
/// Drives urgency. 0 = satisfied, 100 = critical (eat/sleep/social now).
/// </summary>
public struct NpcNeeds
{
    public double Hunger;
    public double Fatigue;
    public double Social;
    public double Recreation;

    public static NpcNeeds Balanced() => new()
    {
        Hunger = 25,
        Fatigue = 20,
        Social = 15,
        Recreation = 15,
    };

    public void Clamp()
    {
        Hunger = Clamp(Hunger);
        Fatigue = Clamp(Fatigue);
        Social = Clamp(Social);
        Recreation = Clamp(Recreation);
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 100 ? 100 : v;
}
