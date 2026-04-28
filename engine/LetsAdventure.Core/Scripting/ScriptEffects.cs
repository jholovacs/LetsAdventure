using System.Text.Json;
using System.Text.Json.Serialization;
using LetsAdventure.Core.Quests;
using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Scripting;

public sealed class ScriptEffectContext
{
    public RelationshipWorldState? Relationships { get; init; }
    public IntimacyWorldState? Intimacy { get; init; }
    public CharacterIntimacyRegistry? IntimacyProfiles { get; init; }
    public CharacterFaceRegistry? FaceProfiles { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetFlagEffect), "set_flag")]
[JsonDerivedType(typeof(ClearFlagEffect), "clear_flag")]
[JsonDerivedType(typeof(AddFlagNumberEffect), "add_flag_number")]
[JsonDerivedType(typeof(CompleteQuestEffect), "complete_quest")]
[JsonDerivedType(typeof(FailQuestEffect), "fail_quest")]
[JsonDerivedType(typeof(AddPersonalRelationshipEffect), "add_personal_relationship")]
[JsonDerivedType(typeof(SetPersonalRelationshipEffect), "set_personal_relationship")]
[JsonDerivedType(typeof(AddFriendshipEffect), "add_friendship")]
[JsonDerivedType(typeof(AddRomanticInterestEffect), "add_romantic_interest")]
[JsonDerivedType(typeof(AddLoyaltyIntegrityEffect), "add_loyalty_integrity")]
[JsonDerivedType(typeof(SetRelationshipStatusEffect), "set_relationship_status")]
[JsonDerivedType(typeof(AddFaceEffect), "add_face")]
[JsonDerivedType(typeof(SetFaceEffect), "set_face")]
public abstract class ScriptEffect;

public sealed class SetFlagEffect : ScriptEffect
{
    public string Key { get; set; } = "";
    public JsonElement Value { get; set; }
}

public sealed class ClearFlagEffect : ScriptEffect
{
    public string Key { get; set; } = "";
}

public sealed class AddFlagNumberEffect : ScriptEffect
{
    public string Key { get; set; } = "";
    public double Delta { get; set; }
}

public sealed class CompleteQuestEffect : ScriptEffect
{
    public string QuestId { get; set; } = "";
}

public sealed class FailQuestEffect : ScriptEffect
{
    public string QuestId { get; set; } = "";
}

public sealed class AddPersonalRelationshipEffect : ScriptEffect
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double Delta { get; set; }
}

public sealed class SetPersonalRelationshipEffect : ScriptEffect
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double Value { get; set; }
}

public sealed class AddFriendshipEffect : ScriptEffect
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double Delta { get; set; }
}

public sealed class AddRomanticInterestEffect : ScriptEffect
{
    public string ObserverId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public double Delta { get; set; }
}

public sealed class AddLoyaltyIntegrityEffect : ScriptEffect
{
    public string CharacterId { get; set; } = "";
    public double Delta { get; set; }
}

public sealed class SetRelationshipStatusEffect : ScriptEffect
{
    public string CharacterId { get; set; } = "";
    public RelationshipStatus Status { get; set; }
}

public sealed class AddFaceEffect : ScriptEffect
{
    public string CharacterId { get; set; } = "";
    public double Delta { get; set; }
}

public sealed class SetFaceEffect : ScriptEffect
{
    public string CharacterId { get; set; } = "";
    public double Value { get; set; }
}

public static class ScriptApplier
{
    private const int QuestLogCap = 512;

    public static void Apply(
        ScriptRuntimeState state,
        IReadOnlyList<ScriptEffect> effects,
        double? gameTimeForQuestLog = null,
        ScriptEffectContext? fx = null,
        IReadOnlyDictionary<string, QuestBlueprintData>? questBlueprints = null,
        bool cascadeQuestOutcomeFollowUp = true)
    {
        foreach (var e in effects)
        {
            switch (e)
            {
                case SetFlagEffect sf:
                    state.Flags[sf.Key] = JsonElementToObj(sf.Value);
                    break;
                case ClearFlagEffect cf:
                    state.Flags.Remove(cf.Key);
                    break;
                case AddFlagNumberEffect af:
                    {
                        var cur = state.Flags.TryGetValue(af.Key, out var v) ? ToDouble(v) : 0;
                        state.Flags[af.Key] = cur + af.Delta;
                        break;
                    }
                case CompleteQuestEffect cq:
                    state.CompletedQuests.Add(cq.QuestId);
                    state.FailedQuests.Remove(cq.QuestId);
                    state.Flags[$"{cq.QuestId}.complete"] = true;
                    state.Flags[$"{cq.QuestId}.failed"] = false;
                    state.Flags[$"{cq.QuestId}.active"] = false;
                    if (gameTimeForQuestLog is { } gt)
                        AppendLog(state, cq.QuestId, QuestOutcomeKind.Success, gt);
                    if (cascadeQuestOutcomeFollowUp)
                        ApplyQuestOutcomeFollowUp(state, cq.QuestId, success: true, gameTimeForQuestLog, fx, questBlueprints);
                    break;
                case FailQuestEffect fq:
                    state.FailedQuests.Add(fq.QuestId);
                    state.CompletedQuests.Remove(fq.QuestId);
                    state.Flags[$"{fq.QuestId}.failed"] = true;
                    state.Flags[$"{fq.QuestId}.complete"] = false;
                    state.Flags[$"{fq.QuestId}.active"] = false;
                    if (gameTimeForQuestLog is { } gt2)
                        AppendLog(state, fq.QuestId, QuestOutcomeKind.Failure, gt2);
                    if (cascadeQuestOutcomeFollowUp)
                        ApplyQuestOutcomeFollowUp(state, fq.QuestId, success: false, gameTimeForQuestLog, fx, questBlueprints);
                    break;
                case AddPersonalRelationshipEffect ar:
                    fx?.Relationships?.AddPersonalDelta(ar.ObserverId, ar.TargetId, ar.Delta);
                    break;
                case SetPersonalRelationshipEffect sr:
                    fx?.Relationships?.SetPersonal(sr.ObserverId, sr.TargetId, sr.Value);
                    break;
                case AddFriendshipEffect bf:
                    fx?.Intimacy?.AddFriendshipDelta(bf.ObserverId, bf.TargetId, bf.Delta);
                    break;
                case AddRomanticInterestEffect ri:
                    fx?.Intimacy?.AddRomanticInterestDelta(ri.ObserverId, ri.TargetId, ri.Delta);
                    break;
                case AddLoyaltyIntegrityEffect ly:
                    {
                        var p = fx?.IntimacyProfiles?.TryGet(ly.CharacterId);
                        if (p is not null)
                        {
                            p.LoyaltyIntegrity = Math.Clamp(p.LoyaltyIntegrity + ly.Delta, 0, 100);
                            if (p.RelationshipStatus is RelationshipStatus.Married or RelationshipStatus.Committed)
                                p.LoyaltyIntegrity = Math.Min(p.LoyaltyIntegrity, 98);
                        }

                        break;
                    }
                case SetRelationshipStatusEffect rs:
                    {
                        var p = fx?.IntimacyProfiles?.TryGet(rs.CharacterId);
                        if (p is not null)
                        {
                            p.RelationshipStatus = rs.Status;
                            if (p.RelationshipStatus is RelationshipStatus.Married or RelationshipStatus.Committed)
                                p.LoyaltyIntegrity = Math.Min(p.LoyaltyIntegrity, 98);
                        }

                        break;
                    }
                case AddFaceEffect af:
                    fx?.FaceProfiles?.AddDelta(af.CharacterId, af.Delta);
                    break;
                case SetFaceEffect sf:
                    fx?.FaceProfiles?.SetFace(sf.CharacterId, sf.Value);
                    break;
            }
        }
    }

    private static void ApplyQuestOutcomeFollowUp(
        ScriptRuntimeState state,
        string questId,
        bool success,
        double? gameTimeForQuestLog,
        ScriptEffectContext? fx,
        IReadOnlyDictionary<string, QuestBlueprintData>? questBlueprints)
    {
        if (questBlueprints is null || !questBlueprints.TryGetValue(questId, out var bp))
            return;
        var extra = success ? bp.OnQuestSuccessEffects : bp.OnQuestFailureEffects;
        if (extra is not { Count: > 0 })
            return;
        Apply(state, extra, gameTimeForQuestLog, fx, questBlueprints, cascadeQuestOutcomeFollowUp: false);
    }

    private static void AppendLog(ScriptRuntimeState state, string questId, QuestOutcomeKind outcome, double gt)
    {
        state.QuestOutcomeLog.Add(new QuestOutcomeEntry { QuestId = questId, Outcome = outcome, GameTime = gt });
        if (state.QuestOutcomeLog.Count > QuestLogCap)
            state.QuestOutcomeLog.RemoveRange(0, state.QuestOutcomeLog.Count - QuestLogCap);
    }

    private static object? JsonElementToObj(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonValueKind.String => je.GetString(),
        _ => null,
    };

    private static double ToDouble(object? v) => v switch
    {
        IConvertible c => Convert.ToDouble(c, null),
        _ => 0,
    };
}
