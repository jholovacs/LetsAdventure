using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.Npcs;

/// <summary>
/// Per-NPC mutable simulation state tracked by the world engine.
/// </summary>
public sealed class NpcAgentState
{
    public string NpcId { get; set; } = "";
    public NpcNeeds Needs { get; set; } = NpcNeeds.Balanced();
    public NpcActivityKind CurrentActivity { get; set; } = NpcActivityKind.Idle;

    /// <summary>Where the NPC is currently allowed to perform local actions.</summary>
    public string CurrentEstablishmentId { get; set; } = "";

    public string? CommuteTargetEstablishmentId { get; set; }
    public double CommuteRemainingMinutes { get; set; }

    public double WorkObligation { get; set; }
    public double MinutesInCurrentActivity { get; set; }
    public double MinutesSinceLastDecision { get; set; }
}

/// <summary>
/// Aggregate world state for all civilian NPCs (separate from quest/script state).
/// </summary>
public sealed class NpcWorldState
{
    public WorldClock Clock { get; } = new();
    public Dictionary<string, NpcAgentState> Agents { get; } = new(StringComparer.Ordinal);

    public NpcAgentState GetOrCreate(string npcId)
    {
        if (Agents.TryGetValue(npcId, out var a))
            return a;
        a = new NpcAgentState { NpcId = npcId };
        Agents[npcId] = a;
        return a;
    }
}
