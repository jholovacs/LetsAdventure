namespace LetsAdventure.Core.Scripting;

public sealed class QuestOutcomeEntry
{
    public string QuestId { get; set; } = "";
    public QuestOutcomeKind Outcome { get; set; }
    public double GameTime { get; set; }
}

public enum QuestOutcomeKind
{
    Success,
    Failure,
}

public sealed class ScriptRuntimeState
{
    public Dictionary<string, object?> Flags { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> CompletedQuests { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> FailedQuests { get; set; } = new(StringComparer.Ordinal);
    public List<QuestOutcomeEntry> QuestOutcomeLog { get; set; } = [];

    public ScriptRuntimeState Clone()
    {
        return new ScriptRuntimeState
        {
            Flags = new Dictionary<string, object?>(Flags, StringComparer.Ordinal),
            CompletedQuests = new HashSet<string>(CompletedQuests, StringComparer.Ordinal),
            FailedQuests = new HashSet<string>(FailedQuests, StringComparer.Ordinal),
            QuestOutcomeLog = [.. QuestOutcomeLog],
        };
    }

    public static ScriptRuntimeState Empty() => new();
}
