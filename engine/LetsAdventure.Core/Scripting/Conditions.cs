using System.Text.Json;
using System.Text.Json.Serialization;
using LetsAdventure.Core.Lore;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Scripting;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(TrueCondition), "true")]
[JsonDerivedType(typeof(FalseCondition), "false")]
[JsonDerivedType(typeof(AllCondition), "all")]
[JsonDerivedType(typeof(AnyCondition), "any")]
[JsonDerivedType(typeof(NotCondition), "not")]
[JsonDerivedType(typeof(FlagEqCondition), "flag_eq")]
[JsonDerivedType(typeof(FlagTrueCondition), "flag_true")]
[JsonDerivedType(typeof(FlagGteCondition), "flag_gte")]
[JsonDerivedType(typeof(QuestCompletedCondition), "quest_completed")]
[JsonDerivedType(typeof(QuestFailedCondition), "quest_failed")]
[JsonDerivedType(typeof(RealmOrderGteCondition), "realm_order_gte")]
[JsonDerivedType(typeof(InRegionCondition), "in_region")]
[JsonDerivedType(typeof(SectIsCondition), "sect_is")]
[JsonDerivedType(typeof(PlayerTraitCondition), "player_trait")]
[JsonDerivedType(typeof(WorldTagCondition), "world_tag")]
[JsonDerivedType(typeof(WeatherIsCondition), "weather_is")]
[JsonDerivedType(typeof(TimeAnyCondition), "time_any")]
[JsonDerivedType(typeof(DispositionGteCondition), "disposition_gte")]
[JsonDerivedType(typeof(SocialAttitudeIsCondition), "social_attitude_is")]
[JsonDerivedType(typeof(FriendshipGteCondition), "friendship_gte")]
[JsonDerivedType(typeof(RomanticInterestGteCondition), "romantic_interest_gte")]
[JsonDerivedType(typeof(InfidelityPressureGteCondition), "infidelity_pressure_gte")]
[JsonDerivedType(typeof(RelationshipStatusIsCondition), "relationship_status_is")]
[JsonDerivedType(typeof(LoyaltyIntegrityGteCondition), "loyalty_integrity_gte")]
[JsonDerivedType(typeof(LoyaltyIntegrityLteCondition), "loyalty_integrity_lte")]
[JsonDerivedType(typeof(FaceGteCondition), "face_gte")]
[JsonDerivedType(typeof(FaceLteCondition), "face_lte")]
[JsonDerivedType(typeof(HighFaceCondition), "high_face")]
public abstract class Condition;

public sealed class TrueCondition : Condition;

public sealed class FalseCondition : Condition;

public sealed class AllCondition : Condition
{
    public List<Condition> Items { get; set; } = [];
}

public sealed class AnyCondition : Condition
{
    public List<Condition> Items { get; set; } = [];
}

public sealed class NotCondition : Condition
{
    public Condition Item { get; set; } = null!;
}

public sealed class FlagEqCondition : Condition
{
    public string Key { get; set; } = "";
    public JsonElement Value { get; set; }
}

public sealed class FlagTrueCondition : Condition
{
    public string Key { get; set; } = "";
}

public sealed class FlagGteCondition : Condition
{
    public string Key { get; set; } = "";
    public double Value { get; set; }
}

public sealed class QuestCompletedCondition : Condition
{
    public string QuestId { get; set; } = "";
}

public sealed class QuestFailedCondition : Condition
{
    public string QuestId { get; set; } = "";
}

public sealed class RealmOrderGteCondition : Condition
{
    public string RealmId { get; set; } = "";
}

public sealed class InRegionCondition : Condition
{
    public string RegionId { get; set; } = "";
}

public sealed class SectIsCondition : Condition
{
    public string SectId { get; set; } = "";
}

public sealed class PlayerTraitCondition : Condition
{
    public string Trait { get; set; } = "";
}

public sealed class WorldTagCondition : Condition
{
    public string Tag { get; set; } = "";
}

public sealed class WeatherIsCondition : Condition
{
    public string Weather { get; set; } = "";
}

public sealed class TimeAnyCondition : Condition
{
    public List<TimeBand> Bands { get; set; } = [];
}

public sealed class DispositionGteCondition : Condition
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double MinValue { get; set; }
}

public sealed class SocialAttitudeIsCondition : Condition
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public SocialAttitude Attitude { get; set; }
}

public sealed class FriendshipGteCondition : Condition
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double MinValue { get; set; }
}

public sealed class RomanticInterestGteCondition : Condition
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double MinValue { get; set; }
}

public sealed class InfidelityPressureGteCondition : Condition
{
    public string CharacterId { get; set; } = "";
    public string ThirdPartyId { get; set; } = "";
    public double MinPressure { get; set; }
}

public sealed class RelationshipStatusIsCondition : Condition
{
    public string CharacterId { get; set; } = "";
    public RelationshipStatus Status { get; set; }
}

public sealed class LoyaltyIntegrityGteCondition : Condition
{
    public string CharacterId { get; set; } = "";
    public double MinValue { get; set; }
}

public sealed class LoyaltyIntegrityLteCondition : Condition
{
    public string CharacterId { get; set; } = "";
    public double MaxValue { get; set; }
}

public sealed class FaceGteCondition : Condition
{
    public string CharacterId { get; set; } = "";
    public double MinValue { get; set; }
}

public sealed class FaceLteCondition : Condition
{
    public string CharacterId { get; set; } = "";
    public double MaxValue { get; set; }
}

/// <summary>True when face is at or above culture <see cref="FaceCultureRules.HighFaceThreshold"/>.</summary>
public sealed class HighFaceCondition : Condition
{
    public string CharacterId { get; set; } = "";
}

public static class ConditionEvaluator
{
    public static bool Eval(
        Condition cond,
        GameContext ctx,
        ScriptRuntimeState state,
        LoreTable lore,
        GameNarrativeContext? narrative = null)
    {
        var rel = narrative?.Relationship;
        switch (cond)
        {
            case TrueCondition:
                return true;
            case FalseCondition:
                return false;
            case AllCondition all:
                return all.Items.TrueForAll(c => Eval(c, ctx, state, lore, narrative));
            case AnyCondition any:
                return any.Items.Exists(c => Eval(c, ctx, state, lore, narrative));
            case NotCondition n:
                return !Eval(n.Item, ctx, state, lore, narrative);
            case FlagEqCondition fe:
                return state.Flags.TryGetValue(fe.Key, out var v) && JsonValueEquals(v, fe.Value);
            case FlagTrueCondition ft:
                return state.Flags.TryGetValue(ft.Key, out var fv) && IsTruthy(fv);
            case FlagGteCondition fg:
                return state.Flags.TryGetValue(fg.Key, out var gv) && gv is IConvertible conv
                    && ToDouble(conv) >= fg.Value;
            case QuestCompletedCondition qc:
                return state.CompletedQuests.Contains(qc.QuestId);
            case QuestFailedCondition qf:
                return state.FailedQuests.Contains(qf.QuestId);
            case RealmOrderGteCondition rg:
                return lore.RealmOrder(ctx.Player.RealmId) >= lore.RealmOrder(rg.RealmId);
            case InRegionCondition ir:
                return ctx.World.RegionId == ir.RegionId;
            case SectIsCondition si:
                return ctx.Player.SectId == si.SectId;
            case PlayerTraitCondition pt:
                return ctx.PlayerHasTrait(pt.Trait);
            case WorldTagCondition wt:
                return ctx.WorldHasTag(wt.Tag);
            case WeatherIsCondition wi:
                return ctx.World.Weather == wi.Weather;
            case TimeAnyCondition ta:
                return ta.Bands.Contains(ctx.World.TimeBand);
            case DispositionGteCondition dg:
                if (rel is null) return false;
                return RelationshipEvaluator.GetEffectiveDisposition(rel, dg.ObserverId, dg.TargetId) >= dg.MinValue;
            case SocialAttitudeIsCondition sa:
                if (rel is null) return false;
                return RelationshipEvaluator.GetAttitude(rel, sa.ObserverId, sa.TargetId) == sa.Attitude;
            case FriendshipGteCondition friend:
                if (narrative?.Intimacy is null) return false;
                return narrative.Intimacy.Bonds.GetFriendship(friend.ObserverId, friend.TargetId) >= friend.MinValue;
            case RomanticInterestGteCondition rom:
                if (narrative?.Intimacy is null) return false;
                return narrative.Intimacy.Bonds.GetRomanticInterest(rom.ObserverId, rom.TargetId) >= rom.MinValue;
            case InfidelityPressureGteCondition ip:
                if (narrative?.Intimacy is null) return false;
                return IntimacyEvaluator.ComputeInfidelityPressure(
                    ip.CharacterId,
                    ip.ThirdPartyId,
                    narrative.Intimacy.Profiles,
                    narrative.Intimacy.Bonds,
                    narrative.Intimacy.Rules) >= ip.MinPressure;
            case RelationshipStatusIsCondition rs:
                if (narrative?.Intimacy is null) return false;
                return narrative.Intimacy.Profiles.TryGet(rs.CharacterId)?.RelationshipStatus == rs.Status;
            case LoyaltyIntegrityGteCondition lg:
                if (narrative?.Intimacy is null) return false;
                return (narrative.Intimacy.Profiles.TryGet(lg.CharacterId)?.LoyaltyIntegrity ?? 0) >= lg.MinValue;
            case LoyaltyIntegrityLteCondition ll:
                if (narrative?.Intimacy is null) return false;
                return (narrative.Intimacy.Profiles.TryGet(ll.CharacterId)?.LoyaltyIntegrity ?? 100) <= ll.MaxValue;
            case FaceGteCondition fgFace:
                if (narrative?.Face is null) return false;
                return narrative.Face.Profiles.GetFace(fgFace.CharacterId) >= fgFace.MinValue;
            case FaceLteCondition flFace:
                if (narrative?.Face is null) return false;
                return narrative.Face.Profiles.GetFace(flFace.CharacterId) <= flFace.MaxValue;
            case HighFaceCondition hf:
                if (narrative?.Face is null) return false;
                return narrative.Face.Profiles.GetFace(hf.CharacterId) >= narrative.Face.Rules.HighFaceThreshold;
            default:
                throw new InvalidOperationException($"Unknown condition: {cond.GetType().Name}");
        }
    }

    private static double ToDouble(IConvertible v) => Convert.ToDouble(v, null);

    private static bool IsTruthy(object? v) => v switch
    {
        true => true,
        "true" => true,
        _ => false,
    };

    private static bool JsonValueEquals(object? stored, JsonElement expected)
    {
        var kind = expected.ValueKind;
        if (kind is JsonValueKind.True or JsonValueKind.False)
            return stored is bool b && b == expected.GetBoolean();
        if (kind == JsonValueKind.Number && expected.TryGetDouble(out var d))
            return stored is IConvertible conv && Math.Abs(ToDouble(conv) - d) < 1e-9;
        if (kind == JsonValueKind.String)
            return stored as string == expected.GetString();
        return false;
    }
}
