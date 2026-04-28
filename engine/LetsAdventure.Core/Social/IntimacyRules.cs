namespace LetsAdventure.Core.Social;

public sealed class IntimacyRules
{
    /// <summary>Reference loyalty for married characters when computing vulnerability to infidelity pressure.</summary>
    public double MarriedLoyaltyBaseline { get; set; } = 88;

    public double MinLoyalty { get; set; } = 0;
    public double MaxLoyalty { get; set; } = 100;

    public double MinFriendship { get; set; } = -100;
    public double MaxFriendship { get; set; } = 100;

    public double MinRomanticInterest { get; set; } = 0;
    public double MaxRomanticInterest { get; set; } = 100;

    /// <summary>Scales <see cref="IntimacyEvaluator.ComputeInfidelityPressure"/> into ~0–1.</summary>
    public double InfidelityPressureScale { get; set; } = 0.012;

    public static IntimacyRules Default { get; } = new();
}
