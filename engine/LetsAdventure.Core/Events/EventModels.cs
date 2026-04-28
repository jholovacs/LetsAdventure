using System.Text.Json.Serialization;
using LetsAdventure.Core.Scripting;

namespace LetsAdventure.Core.Events;

public enum EventQuestOutcome
{
    Success,
    Failure,
    Any,
}

public enum ScheduleFireMode
{
    ReevaluateTriggers,
    FireActionsOnly,
}

public sealed class PendingSchedule
{
    public string ScheduleId { get; set; } = "";
    public string EventId { get; set; } = "";
    public double FireAt { get; set; }
    public ScheduleFireMode Mode { get; set; }
}

public sealed class EventEngineState
{
    public double GameTime { get; set; }
    public HashSet<string> FiredOneShot { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> LastFiredAt { get; set; } = new(StringComparer.Ordinal);
    public List<PendingSchedule> PendingSchedules { get; set; } = [];
    public int NextScheduleSeq { get; set; }

    public EventEngineState Clone() => new()
    {
        GameTime = GameTime,
        FiredOneShot = new HashSet<string>(FiredOneShot, StringComparer.Ordinal),
        LastFiredAt = new Dictionary<string, double>(LastFiredAt, StringComparer.Ordinal),
        PendingSchedules = [.. PendingSchedules],
        NextScheduleSeq = NextScheduleSeq,
    };

    public static EventEngineState Empty(double gameTime = 0) => new() { GameTime = gameTime };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(AllTrigger), "all")]
[JsonDerivedType(typeof(AnyTrigger), "any")]
[JsonDerivedType(typeof(NotTrigger), "not")]
[JsonDerivedType(typeof(ConditionTrigger), "condition")]
[JsonDerivedType(typeof(InRegionTrigger), "in_region")]
[JsonDerivedType(typeof(NearAnchorTrigger), "near_anchor")]
[JsonDerivedType(typeof(GameTimeGteTrigger), "game_time_gte")]
[JsonDerivedType(typeof(AfterEventDelayTrigger), "after_event_delay")]
[JsonDerivedType(typeof(QuestOutcomeTrigger), "quest_outcome")]
public abstract class EventTriggerExpr;

public sealed class AllTrigger : EventTriggerExpr
{
    public List<EventTriggerExpr> Items { get; set; } = [];
}

public sealed class AnyTrigger : EventTriggerExpr
{
    public List<EventTriggerExpr> Items { get; set; } = [];
}

public sealed class NotTrigger : EventTriggerExpr
{
    public EventTriggerExpr Item { get; set; } = null!;
}

public sealed class ConditionTrigger : EventTriggerExpr
{
    public Condition Condition { get; set; } = null!;
}

public sealed class InRegionTrigger : EventTriggerExpr
{
    public string RegionId { get; set; } = "";
}

public sealed class NearAnchorTrigger : EventTriggerExpr
{
    public string AnchorId { get; set; } = "";
    public double MaxDistance { get; set; }
}

public sealed class GameTimeGteTrigger : EventTriggerExpr
{
    public double Time { get; set; }
}

public sealed class AfterEventDelayTrigger : EventTriggerExpr
{
    public string EventId { get; set; } = "";
    public double Delay { get; set; }
}

public sealed class QuestOutcomeTrigger : EventTriggerExpr
{
    public string QuestId { get; set; } = "";
    public EventQuestOutcome Outcome { get; set; }
    public double? OutcomeAfterGameTime { get; set; }
}

public sealed class ScheduleAfterFire
{
    public string EventId { get; set; } = "";
    public double Delay { get; set; }
    public ScheduleFireMode? Mode { get; set; }
}

public sealed class EventDefinition
{
    public string Id { get; set; } = "";
    public EventTriggerExpr Triggers { get; set; } = null!;
    public List<ScriptEffect> Actions { get; set; } = [];
    public List<ScheduleAfterFire>? ScheduleAfterFire { get; set; }
    public bool? Once { get; set; }
}
