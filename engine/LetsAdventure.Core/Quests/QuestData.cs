using LetsAdventure.Core.Lore;
using LetsAdventure.Core.Scripting;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.Social;

namespace LetsAdventure.Core.Quests;

public sealed class QuestObjective
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public string? TargetId { get; set; }
    public string? FlagKey { get; set; }
    public int? Count { get; set; }

    /// <summary>Applied when the game marks this objective complete (see <see cref="QuestStoryEffects"/>).</summary>
    public List<ScriptEffect>? OnCompleteEffects { get; set; }
}

public sealed class QuestFragmentRule
{
    public Condition When { get; set; } = null!;
    public List<QuestObjective> Objectives { get; set; } = [];
    public List<string>? JournalNotes { get; set; }
}

public sealed class QuestBlueprintData
{
    public string QuestId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<QuestObjective> BaseObjectives { get; set; } = [];
    public List<QuestFragmentRule> Fragments { get; set; } = [];
    public Condition? Prerequisites { get; set; }

    /// <summary>Run after <c>complete_quest</c> for this <see cref="QuestId"/> (friendship, loyalty, flags, etc.).</summary>
    public List<ScriptEffect>? OnQuestSuccessEffects { get; set; }

    /// <summary>Run after <c>fail_quest</c> for this <see cref="QuestId"/>.</summary>
    public List<ScriptEffect>? OnQuestFailureEffects { get; set; }
}

public sealed class CompiledQuest
{
    public string QuestId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<QuestObjective> Objectives { get; set; } = [];
    public List<string> JournalNotes { get; set; } = [];
    public bool Available { get; set; }
}

public static class QuestCompiler
{
    public static CompiledQuest Compile(
        QuestBlueprintData blueprint,
        GameContext ctx,
        ScriptRuntimeState state,
        LoreTable lore,
        GameNarrativeContext? narrative = null)
    {
        var prerequisites = blueprint.Prerequisites ?? new TrueCondition();
        var available = ConditionEvaluator.Eval(prerequisites, ctx, state, lore, narrative);
        var objectives = new List<QuestObjective>(blueprint.BaseObjectives);
        var notes = new List<string>();

        foreach (var frag in blueprint.Fragments)
        {
            if (!ConditionEvaluator.Eval(frag.When, ctx, state, lore, narrative))
                continue;
            objectives.AddRange(frag.Objectives);
            if (frag.JournalNotes is { Count: > 0 } j)
                notes.AddRange(j);
        }

        return new CompiledQuest
        {
            QuestId = blueprint.QuestId,
            Title = blueprint.Title,
            Summary = blueprint.Summary,
            Objectives = objectives,
            JournalNotes = notes,
            Available = available,
        };
    }
}

public static class QuestStoryEffects
{
    /// <summary>
    /// Applies <see cref="QuestObjective.OnCompleteEffects"/> for the given objective id if present.
    /// Call from quest progression when an objective is satisfied (same <see cref="ScriptEffectContext"/> as events).
    /// </summary>
    /// <returns><c>true</c> if an objective with <paramref name="objectiveId"/> exists on <paramref name="quest"/>.</returns>
    public static bool TryApplyObjectiveCompleteEffects(
        CompiledQuest quest,
        string objectiveId,
        ScriptRuntimeState state,
        double? gameTimeForQuestLog = null,
        ScriptEffectContext? fx = null,
        IReadOnlyDictionary<string, QuestBlueprintData>? questBlueprints = null)
    {
        foreach (var o in quest.Objectives)
        {
            if (!string.Equals(o.Id, objectiveId, StringComparison.Ordinal))
                continue;
            if (o.OnCompleteEffects is { Count: > 0 } effects)
                ScriptApplier.Apply(state, effects, gameTimeForQuestLog, fx, questBlueprints);
            return true;
        }

        return false;
    }
}
