using LetsAdventure.Core.Lore;
using LetsAdventure.Core.Scripting;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Events;

public static class EventTriggerEvaluator
{
    public static bool Eval(
        EventTriggerExpr expr,
        GameContext ctx,
        ScriptRuntimeState state,
        EventEngineState engine,
        LoreTable lore,
        IReadOnlyDictionary<string, Vec3> anchors,
        GameNarrativeContext? narrative = null)
    {
        switch (expr)
        {
            case AllTrigger all:
                return all.Items.TrueForAll(x => Eval(x, ctx, state, engine, lore, anchors, narrative));
            case AnyTrigger any:
                return any.Items.Exists(x => Eval(x, ctx, state, engine, lore, anchors, narrative));
            case NotTrigger n:
                return !Eval(n.Item, ctx, state, engine, lore, anchors, narrative);
            case ConditionTrigger c:
                return ConditionEvaluator.Eval(c.Condition, ctx, state, lore, narrative);
            case InRegionTrigger ir:
                return ctx.World.RegionId == ir.RegionId;
            case NearAnchorTrigger na:
                {
                    if (ctx.PlayerPosition is not { } pos || !anchors.TryGetValue(na.AnchorId, out var ap))
                        return false;
                    var dx = pos.X - ap.X;
                    var dy = pos.Y - ap.Y;
                    var dz = pos.Z - ap.Z;
                    var d2 = dx * dx + dy * dy + dz * dz;
                    return d2 <= na.MaxDistance * na.MaxDistance;
                }
            case GameTimeGteTrigger gt:
                return engine.GameTime >= gt.Time;
            case AfterEventDelayTrigger ae:
                return engine.LastFiredAt.TryGetValue(ae.EventId, out var t0)
                    && engine.GameTime >= t0 + ae.Delay;
            case QuestOutcomeTrigger qo:
                return MatchesQuestOutcome(state, qo);
            default:
                throw new InvalidOperationException($"Unknown trigger {expr.GetType().Name}");
        }
    }

    private static bool MatchesQuestOutcome(ScriptRuntimeState state, QuestOutcomeTrigger qo)
    {
        if (qo.OutcomeAfterGameTime is { } minT)
        {
            return state.QuestOutcomeLog.Exists(e =>
                e.QuestId == qo.QuestId
                && e.GameTime >= minT
                && (qo.Outcome == EventQuestOutcome.Any
                    || (qo.Outcome == EventQuestOutcome.Success && e.Outcome == QuestOutcomeKind.Success)
                    || (qo.Outcome == EventQuestOutcome.Failure && e.Outcome == QuestOutcomeKind.Failure)));
        }

        var ok = qo.Outcome switch
        {
            EventQuestOutcome.Success => state.CompletedQuests.Contains(qo.QuestId),
            EventQuestOutcome.Failure => state.FailedQuests.Contains(qo.QuestId),
            EventQuestOutcome.Any => state.CompletedQuests.Contains(qo.QuestId) || state.FailedQuests.Contains(qo.QuestId),
            _ => false,
        };
        return ok;
    }
}
