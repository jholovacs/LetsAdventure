using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

/// <summary>Spatial queries over authored physical world data (regions, territory, bounds).</summary>
public sealed class PhysicalWorldIndex
{
    public PhysicalWorldDefinition Definition { get; }

    public PhysicalWorldIndex(PhysicalWorldDefinition definition) => Definition = definition;

    public string? ResolveLoreRegionId(Vec3 position)
    {
        foreach (var r in Definition.RegionBoundaries)
        {
            if (PolygonContainment.Contains(r.Boundary, position.X, position.Y, position.Z))
                return r.LoreRegionId;
        }

        return null;
    }

    /// <summary>Winning territory claim at a point (highest <see cref="TerritoryClaim.Priority"/>).</summary>
    public TerritoryClaim? ResolveTerritory(Vec3 position)
    {
        TerritoryClaim? best = null;
        foreach (var t in Definition.Territories)
        {
            if (!PolygonContainment.Contains(t.Boundary, position.X, position.Y, position.Z))
                continue;
            if (best is null || t.Priority > best.Priority)
                best = t;
        }

        return best;
    }

    public bool IsWithinGlobalBounds(Vec3 position) => Definition.GlobalBounds.Contains(position);
}
