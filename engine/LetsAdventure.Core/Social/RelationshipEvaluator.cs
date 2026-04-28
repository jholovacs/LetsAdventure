using LetsAdventure.Core.Lore;

namespace LetsAdventure.Core.Social;

public static class RelationshipEvaluator
{
    /// <summary>
    /// Average directed faction standing across all observer×target faction pairs.
    /// </summary>
    public static double ComputeFactionStand(
        IReadOnlyList<string> observerFactions,
        IReadOnlyList<string> targetFactions,
        FactionReputationTable table)
    {
        if (observerFactions.Count == 0 || targetFactions.Count == 0)
            return 0;

        double sum = 0;
        var n = 0;
        foreach (var fo in observerFactions)
        {
            foreach (var ft in targetFactions)
            {
                sum += table.Get(fo, ft);
                n++;
            }
        }

        return n == 0 ? 0 : sum / n;
    }

    public static double GetEffectiveDisposition(
        string observerCharacterId,
        string targetCharacterId,
        RelationshipWorldState relationships,
        FactionReputationTable factionReputations,
        CharacterFactionRegistry factions,
        RelationshipRules? rules = null,
        LoreTable? lore = null,
        CharacterCultivationRegistry? cultivation = null)
    {
        rules ??= RelationshipRules.Default;
        var personal = relationships.GetPersonal(observerCharacterId, targetCharacterId);
        var stand = ComputeFactionStand(
            factions.GetFactions(observerCharacterId),
            factions.GetFactions(targetCharacterId),
            factionReputations);
        var blended = personal + stand * rules.FactionInfluenceWeight;
        if (lore is not null && cultivation is not null)
        {
            var obs = cultivation.GetOrBaseline(observerCharacterId);
            var tgt = cultivation.GetOrBaseline(targetCharacterId);
            var minorObs = lore.MinorCultivationLevel(obs.RealmId, obs.StageIndex);
            var minorTgt = lore.MinorCultivationLevel(tgt.RealmId, tgt.StageIndex);
            var delta = minorTgt - minorObs;
            var cult = delta * rules.CultivationRespectPerMinorStep;
            cult = Math.Clamp(cult, -rules.CultivationRespectCap, rules.CultivationRespectCap);
            blended += cult;
        }

        return Math.Clamp(blended, rules.MinDisposition, rules.MaxDisposition);
    }

    public static double GetEffectiveDisposition(RelationshipEvalContext ctx, string observerCharacterId, string targetCharacterId) =>
        GetEffectiveDisposition(
            observerCharacterId,
            targetCharacterId,
            ctx.Relationships,
            ctx.FactionReputations,
            ctx.Factions,
            ctx.Rules,
            ctx.Lore,
            ctx.Cultivation);

    public static SocialAttitude GetAttitude(
        string observerCharacterId,
        string targetCharacterId,
        RelationshipWorldState relationships,
        FactionReputationTable factionReputations,
        CharacterFactionRegistry factions,
        RelationshipRules? rules = null,
        LoreTable? lore = null,
        CharacterCultivationRegistry? cultivation = null)
    {
        var d = GetEffectiveDisposition(
            observerCharacterId,
            targetCharacterId,
            relationships,
            factionReputations,
            factions,
            rules,
            lore,
            cultivation);
        return (rules ?? RelationshipRules.Default).AttitudeFromDisposition(d);
    }

    public static SocialAttitude GetAttitude(RelationshipEvalContext ctx, string observerCharacterId, string targetCharacterId) =>
        GetAttitude(
            observerCharacterId,
            targetCharacterId,
            ctx.Relationships,
            ctx.FactionReputations,
            ctx.Factions,
            ctx.Rules,
            ctx.Lore,
            ctx.Cultivation);
}
