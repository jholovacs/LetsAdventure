using System.Text.Json.Serialization;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SolidVolumeFeature), "solid_volume")]
[JsonDerivedType(typeof(MountainRidgeFeature), "mountain_ridge")]
[JsonDerivedType(typeof(StandingWaterFeature), "water_standing")]
[JsonDerivedType(typeof(FlowingWaterFeature), "water_flowing")]
[JsonDerivedType(typeof(PathCorridorFeature), "path")]
[JsonDerivedType(typeof(BuildingFootprintFeature), "building")]
[JsonDerivedType(typeof(VegetationVolumeFeature), "vegetation")]
[JsonDerivedType(typeof(GroundPlateauFeature), "ground_plateau")]
[JsonDerivedType(typeof(SkyVolumeFeature), "sky_volume")]
public abstract class PhysicalTerrainFeature
{
    public string Id { get; set; } = "";

    /// <summary>Higher wins when resolving overlapping authored features for sampling hints.</summary>
    public int LayerPriority { get; set; }
}

/// <summary>Axis-aligned impassable mass (bedrock, building interior fill, cliff core).</summary>
public sealed class SolidVolumeFeature : PhysicalTerrainFeature
{
    public AxisAlignedBounds Bounds { get; set; } = new();
    public SurfaceComposition Composition { get; set; } = SurfaceComposition.Rock;
    public bool BlocksNavigation { get; set; } = true;
    public PhysicalMedium Medium { get; set; } = PhysicalMedium.Solid;
}

/// <summary>Approximate mountain as ridge polyline with width and elevation ramp.</summary>
public sealed class MountainRidgeFeature : PhysicalTerrainFeature
{
    public List<GeoVec2> RidgeLine { get; set; } = [];
    public double CorridorHalfWidth { get; set; } = 40;
    public double BaseElevationZ { get; set; }
    public double PeakElevationZ { get; set; } = 120;
    public SurfaceComposition SurfaceComposition { get; set; } = SurfaceComposition.Rock;
}

public sealed class StandingWaterFeature : PhysicalTerrainFeature
{
    public List<GeoVec2> Shoreline { get; set; } = [];
    public double WaterSurfaceZ { get; set; }
    public double Depth { get; set; } = 3;
    public PhysicalMedium Medium { get; set; } = PhysicalMedium.FluidStanding;
}

public sealed class FlowingWaterFeature : PhysicalTerrainFeature
{
    public List<GeoVec2> ChannelCenterline { get; set; } = [];
    public double ChannelHalfWidth { get; set; } = 8;
    public double WaterSurfaceZ { get; set; }
    public GeoVec2 FlowDirection { get; set; } = new();
    public PhysicalMedium Medium { get; set; } = PhysicalMedium.FluidFlowing;
}

/// <summary>Preferred travel corridor (road, trail); used for nav cost hints and authoring.</summary>
public sealed class PathCorridorFeature : PhysicalTerrainFeature
{
    public List<GeoVec2> Centerline { get; set; } = [];
    public double HalfWidth { get; set; } = 3;
    public SurfaceComposition Surface { get; set; } = SurfaceComposition.Pavement;
    public double MovementCostMultiplier { get; set; } = 0.85;
}

public sealed class BuildingFootprintFeature : PhysicalTerrainFeature
{
    public List<GeoVec2> Footprint { get; set; } = [];
    public double BaseZ { get; set; }
    public double RoofZ { get; set; } = 12;
    public SurfaceComposition WallComposition { get; set; } = SurfaceComposition.Pavement;
    public bool BlocksNavigation { get; set; } = true;
}

/// <summary>Vegetation occupies a vertical column over a polygon (understory to canopy).</summary>
public sealed class VegetationVolumeFeature : PhysicalTerrainFeature
{
    public List<GeoVec2> Boundary { get; set; } = [];
    public double ZMin { get; set; }
    public double ZMax { get; set; } = 20;
    public double Density01 { get; set; } = 0.5;
    public double MovementCostMultiplier { get; set; } = 1.25;
}

public sealed class GroundPlateauFeature : PhysicalTerrainFeature
{
    public List<GeoVec2> Boundary { get; set; } = [];
    public double ElevationZ { get; set; }
    public SurfaceComposition Composition { get; set; } = SurfaceComposition.Soil;
}

/// <summary>Open air / sky shell for flight, weather, or ceiling limits.</summary>
public sealed class SkyVolumeFeature : PhysicalTerrainFeature
{
    public AxisAlignedBounds Bounds { get; set; } = new();
}
