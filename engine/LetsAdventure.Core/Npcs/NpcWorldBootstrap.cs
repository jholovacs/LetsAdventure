namespace LetsAdventure.Core.Npcs;

/// <summary>
/// Initializes per-NPC runtime state from authored instance profiles (home/work placement).
/// </summary>
public static class NpcWorldBootstrap
{
    public static void RegisterInstances(NpcWorldState world, IEnumerable<NpcInstanceProfile> instances)
    {
        foreach (var p in instances)
        {
            var a = world.GetOrCreate(p.NpcId);
            a.CurrentEstablishmentId = !string.IsNullOrEmpty(p.HomeEstablishmentId)
                ? p.HomeEstablishmentId
                : p.WorkEstablishmentId;
            a.CurrentActivity = NpcActivityKind.Idle;
            a.Needs = NpcNeeds.Balanced();
        }
    }
}
