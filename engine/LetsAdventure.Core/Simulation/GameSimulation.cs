namespace LetsAdventure.Core.Simulation;

public enum TimeBand
{
    Dawn,
    Day,
    Dusk,
    Night,
}

public readonly struct Vec3
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}

public sealed class PlayerSnapshot
{
    /// <summary>When empty, live cultivation falls back to player social JSON or Qi Refinement stage 0.</summary>
    public string RealmId { get; set; } = "";

    public int StageIndex { get; set; }
    public string? SectId { get; set; }
    public List<string> Traits { get; set; } = [];
}

public sealed class WorldSnapshot
{
    public string RegionId { get; set; } = "";
    public TimeBand TimeBand { get; set; }
    public string Weather { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}

public sealed class GameContext
{
    public PlayerSnapshot Player { get; set; } = new();
    public WorldSnapshot World { get; set; } = new();
    public Vec3? PlayerPosition { get; set; }
}

public static class GameContextExtensions
{
    public static bool WorldHasTag(this GameContext ctx, string tag) =>
        ctx.World.Tags.Contains(tag);

    public static bool PlayerHasTrait(this GameContext ctx, string trait) =>
        ctx.Player.Traits.Contains(trait);
}
