using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public sealed class PhysicalWorldDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string CoordinateDescription { get; set; } = "X/Y horizontal plane, Z vertical (up).";
    public AxisAlignedBounds GlobalBounds { get; set; } = new();
    public List<RegionBoundaryEntry> RegionBoundaries { get; set; } = [];
    public List<TerritoryClaim> Territories { get; set; } = [];
    public List<PhysicalTerrainFeature> Features { get; set; } = [];
    public NavigationBundle Navigation { get; set; } = new();
}

public sealed class NavigationBundle
{
    public NavigationGraphDefinition? Graph { get; set; }
    public TerrainNavGridDefinition? Grid { get; set; }
}
