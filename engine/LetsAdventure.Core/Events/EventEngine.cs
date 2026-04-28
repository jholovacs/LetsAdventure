using LetsAdventure.Core.Lore;
using LetsAdventure.Core.Quests;
using LetsAdventure.Core.Scripting;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Events;

public sealed class EventCycleResult
{
    public required ScriptRuntimeState ScriptState { get; init; }
    public required EventEngineState EngineState { get; init; }
    public required List<string> FiredEventIds { get; init; }
}

public static class EventEngine
{
    private const int MaxChain = 64;

    public static EventEngineState AdvanceClock(EventEngineState engine, double delta)
    {
        var e = engine.Clone();
        e.GameTime += delta;
        return e;
    }

    public static EventCycleResult ProcessCycle(
        IReadOnlyDictionary<string, EventDefinition> definitions,
        GameContext ctx,
        ScriptRuntimeState scriptState,
        EventEngineState engineState,
        LoreTable lore,
        IReadOnlyDictionary<string, Vec3> anchors,
        GameNarrativeContext? narrative = null,
        IReadOnlyDictionary<string, QuestBlueprintData>? questBlueprints = null)
    {
        var script = scriptState.Clone();
        var gameTime = engineState.GameTime;
        var firedOneShot = new HashSet<string>(engineState.FiredOneShot, StringComparer.Ordinal);
        var lastFiredAt = new Dictionary<string, double>(engineState.LastFiredAt, StringComparer.Ordinal);
        var nextSeq = engineState.NextScheduleSeq;
        var pending = engineState.PendingSchedules.ToList();
        var firedIds = new List<string>();

        var progressed = true;
        var guard = 0;

        while (progressed && guard++ < MaxChain)
        {
            progressed = false;

            var due = pending
                .Where(s => s.FireAt <= gameTime)
                .OrderBy(s => s.FireAt)
                .ThenBy(s => s.ScheduleId, StringComparer.Ordinal)
                .ToList();
            pending = pending.Where(s => s.FireAt > gameTime).ToList();

            foreach (var s in due)
            {
                if (!definitions.TryGetValue(s.EventId, out var def))
                    continue;

                if (def.Once != false && firedOneShot.Contains(def.Id))
                    continue;

                var engineView = new EventEngineState
                {
                    GameTime = gameTime,
                    FiredOneShot = firedOneShot,
                    LastFiredAt = lastFiredAt,
                    PendingSchedules = pending,
                    NextScheduleSeq = nextSeq,
                };

                if (s.Mode == ScheduleFireMode.ReevaluateTriggers
                    && !EventTriggerEvaluator.Eval(def.Triggers, ctx, script, engineView, lore, anchors, narrative))
                    continue;

                Fire(def, script, ref pending, ref firedOneShot, ref lastFiredAt, ref nextSeq, gameTime, narrative, questBlueprints);
                firedIds.Add(def.Id);
                progressed = true;
            }

            foreach (var def in definitions.Values)
            {
                if (def.Once != false && firedOneShot.Contains(def.Id))
                    continue;

                var engineView = new EventEngineState
                {
                    GameTime = gameTime,
                    FiredOneShot = firedOneShot,
                    LastFiredAt = lastFiredAt,
                    PendingSchedules = pending,
                    NextScheduleSeq = nextSeq,
                };

                if (!EventTriggerEvaluator.Eval(def.Triggers, ctx, script, engineView, lore, anchors, narrative))
                    continue;

                Fire(def, script, ref pending, ref firedOneShot, ref lastFiredAt, ref nextSeq, gameTime, narrative, questBlueprints);
                firedIds.Add(def.Id);
                progressed = true;
            }
        }

        return new EventCycleResult
        {
            ScriptState = script,
            EngineState = new EventEngineState
            {
                GameTime = gameTime,
                FiredOneShot = firedOneShot,
                LastFiredAt = lastFiredAt,
                PendingSchedules = pending,
                NextScheduleSeq = nextSeq,
            },
            FiredEventIds = firedIds,
        };
    }

    private static void Fire(
        EventDefinition def,
        ScriptRuntimeState script,
        ref List<PendingSchedule> pending,
        ref HashSet<string> firedOneShot,
        ref Dictionary<string, double> lastFiredAt,
        ref int nextSeq,
        double gameTime,
        GameNarrativeContext? narrative,
        IReadOnlyDictionary<string, QuestBlueprintData>? questBlueprints)
    {
        var fx = narrative is null
            ? null
            : new ScriptEffectContext
            {
                Relationships = narrative.Relationship?.Relationships,
                Intimacy = narrative.Intimacy?.Bonds,
                IntimacyProfiles = narrative.Intimacy?.Profiles,
                FaceProfiles = narrative.Face?.Profiles,
            };
        ScriptApplier.Apply(script, def.Actions, gameTime, fx, questBlueprints);

        if (def.Once != false)
            firedOneShot.Add(def.Id);

        lastFiredAt[def.Id] = gameTime;

        if (def.ScheduleAfterFire is not { Count: > 0 } list)
            return;

        foreach (var sf in list)
        {
            pending.Add(new PendingSchedule
            {
                ScheduleId = $"{sf.EventId}#{nextSeq++}",
                EventId = sf.EventId,
                FireAt = gameTime + sf.Delay,
                Mode = sf.Mode ?? ScheduleFireMode.ReevaluateTriggers,
            });
        }
    }
}
