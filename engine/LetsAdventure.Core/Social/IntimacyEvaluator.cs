namespace LetsAdventure.Core.Social;

public static class IntimacyEvaluator
{
    /// <summary>
    /// How strongly <paramref name="observer"/> is romantically drawn to someone with <paramref name="targetPresentation"/> (0–1).
    /// </summary>
    public static double RomanticPresentationMatch(
        SexualOrientationSpectrum observerOrientation,
        GenderPresentation targetPresentation)
    {
        observerOrientation.NormalizeAttractionAxes();
        return targetPresentation switch
        {
            GenderPresentation.Masculine => observerOrientation.TowardMasculinePresentation,
            GenderPresentation.Feminine => observerOrientation.TowardFemininePresentation,
            GenderPresentation.NonBinary or GenderPresentation.Fluid or GenderPresentation.Other =>
                (observerOrientation.TowardMasculinePresentation + observerOrientation.TowardFemininePresentation) / 2,
            GenderPresentation.Agender =>
                Math.Max(observerOrientation.TowardMasculinePresentation, observerOrientation.TowardFemininePresentation) * 0.65,
            GenderPresentation.Unknown => 0.45,
            _ => 0.45,
        } * observerOrientation.RomanticDrive;
    }

    /// <summary>
    /// Combines directed romantic meter + presentation fit + explicit crush list.
    /// </summary>
    public static double EffectiveRomanticPull(
        string observerId,
        string targetId,
        CharacterIntimacyRegistry profiles,
        IntimacyWorldState bonds)
    {
        var obs = profiles.TryGet(observerId);
        var tgt = profiles.TryGet(targetId);
        if (obs is null || tgt is null) return bonds.GetRomanticInterest(observerId, targetId);

        var baseInterest = bonds.GetRomanticInterest(observerId, targetId);
        var match = RomanticPresentationMatch(obs.Orientation, tgt.GenderPresentation);
        var explicitBoost = obs.RomanticInterestTargets.Exists(x => string.Equals(x, targetId, StringComparison.Ordinal))
            ? 12
            : 0;
        return Math.Clamp(baseInterest + explicitBoost * 0.25 + match * 15, 0, 100);
    }

    /// <summary>
    /// ~0–1 score: higher means stronger narrative pressure toward infidelity with <paramref name="thirdPartyId"/>.
    /// Married/committed partners with high loyalty stay low unless romantic pull + disloyalty align.
    /// </summary>
    public static double ComputeInfidelityPressure(
        string characterId,
        string thirdPartyId,
        CharacterIntimacyRegistry profiles,
        IntimacyWorldState bonds,
        IntimacyRules? rules = null)
    {
        rules ??= IntimacyRules.Default;
        var self = profiles.TryGet(characterId);
        var other = profiles.TryGet(thirdPartyId);
        if (self is null || other is null) return 0;

        if (self.SpouseCharacterIds.Exists(x => string.Equals(x, thirdPartyId, StringComparison.Ordinal)))
            return 0;

        var bound = self.RelationshipStatus is RelationshipStatus.Married or RelationshipStatus.Committed
            or RelationshipStatus.PolyCommitted;
        if (!bound) return 0;

        var pull = EffectiveRomanticPull(characterId, thirdPartyId, profiles, bonds);
        var match = RomanticPresentationMatch(self.Orientation, other.GenderPresentation);
        var loyalty = Math.Clamp(self.LoyaltyIntegrity, rules.MinLoyalty, rules.MaxLoyalty);

        var loyaltyLeak = (rules.MarriedLoyaltyBaseline - loyalty) / 100;
        if (loyaltyLeak < 0) loyaltyLeak = 0;

        var disloyaltyFactor = (100 - loyalty) / 100;
        var raw = pull * match * (0.15 + 0.85 * disloyaltyFactor) * (0.2 + 0.8 * loyaltyLeak);

        if (self.RelationshipStatus == RelationshipStatus.Married)
            raw *= 1.15;

        return Math.Clamp(raw * rules.InfidelityPressureScale, 0, 1);
    }

    public static bool HasExplicitRomanticInterest(string observerId, string targetId, CharacterIntimacyRegistry profiles) =>
        profiles.TryGet(observerId)?.RomanticInterestTargets.Exists(x => string.Equals(x, targetId, StringComparison.Ordinal))
        ?? false;
}
