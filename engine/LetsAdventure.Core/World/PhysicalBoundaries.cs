using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public sealed class AxisAlignedBounds
{
    public Vec3 Min { get; set; }
    public Vec3 Max { get; set; }

    public bool Contains(Vec3 p) =>
        p.X >= Min.X && p.X <= Max.X
        && p.Y >= Min.Y && p.Y <= Max.Y
        && p.Z >= Min.Z && p.Z <= Max.Z;
}

/// <summary>Vertical extrusion of a 2D polygon: used for lore region borders and territory claims.</summary>
public sealed class PolygonColumnBounds
{
    public List<GeoVec2> Vertices { get; set; } = [];
    public double ZMin { get; set; }
    public double ZMax { get; set; } = 512;
}

public sealed class RegionBoundaryEntry
{
    public string LoreRegionId { get; set; } = "";
    public PolygonColumnBounds Boundary { get; set; } = new();
}

public sealed class TerritoryClaim
{
    public string Id { get; set; } = "";
    public string ClaimantFactionId { get; set; } = "";
    /// <summary>Optional link to narrative region; territory geometry may still be a sub-polygon.</summary>
    public string? LoreRegionId { get; set; }
    public int Priority { get; set; }
    public PolygonColumnBounds Boundary { get; set; } = new();
}
