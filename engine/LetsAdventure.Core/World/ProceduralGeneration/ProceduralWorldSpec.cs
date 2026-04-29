using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

/// <summary>
/// Authoring-time procedural terrain with hydrology and nav constraints.
/// World axes X, Y (horizontal) and Z (up) use <b>SI meters</b>: one integer step in coordinates is 1 m
/// (fractional values are meters). All lengths in this spec — cell size, slopes, channel widths, depths — are meters.
/// Pipeline layout and file responsibilities: see <c>World/ProceduralGeneration/README.md</c>.
/// </summary>
public sealed class ProceduralWorldSpec
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; } = 512;
    public double MaxY { get; set; } = 512;
    public double MinZBound { get; set; } = -10;
    public double MaxZBound { get; set; } = 128;
    public double CellSize { get; set; } = 4;

    public int Seed { get; set; } = 12345;

    /// <summary>Max |ΔZ| between orthogonal 4-neighbors on walkable land (world units).</summary>
    public double MaxLandStepOrthogonal { get; set; } = 1.25;

    public double TerrainAmplitude { get; set; } = 22;
    public int NoiseOctaves { get; set; } = 4;

    public double LakeRadiusWorld { get; set; } = 56;
    /// <summary>Reference channel half-width (m) for discharge/slope width curve; real rivers are often ~10–50 m half.</summary>
    public double RiverChannelHalfWidthWorld { get; set; } = 7;
    public double LakeDepth { get; set; } = 3.5;

    /// <summary>Horizontal XY unit vector pointing <b>downstream</b> (prevailing drainage direction).</summary>
    public GeoVec2 FlowDirectionDownstream { get; set; } = new() { X = 1, Y = 0 };

    public double UphillPathPenalty { get; set; } = 38;
    public int SlopeRelaxationIterations { get; set; } = 32;

    /// <summary>Depth carved below nominal terrain along the river bed (meters).</summary>
    public double RiverBedCarve { get; set; } = 1.8;

    public string GeneratedRegionId { get; set; } = "region.generated";

    /// <summary>Added elevation at map center, tapering to 0 at edges (meters Z).</summary>
    public double ContinentalDomeAmplitude { get; set; } = 0;

    /// <summary>Minimum strip width of ocean from each map edge (meters).</summary>
    public double OceanBandMinWorld { get; set; } = 48;

    /// <summary>Extra ocean intrusion beyond min, shaped by noise (bays/coves).</summary>
    public double OceanBandVariationWorld { get; set; } = 36;

    /// <summary>Seafloor below sea surface for ocean cells (meters).</summary>
    public double OceanDepth { get; set; } = 6;

    /// <summary>Ridged / macro roughness as a fraction of <see cref="TerrainAmplitude"/>.</summary>
    public double TerrainRidgeWeight { get; set; } = 0.1;

    /// <summary>
    /// Max |ΔZ| to a dry orthogonal neighbor for a land cell to stay walkable. When 0, uses
    /// <see cref="MaxLandStepOrthogonal"/> (steep mountain faces become impassable).
    /// </summary>
    public double MaxWalkableOrthogonalStep { get; set; }

    /// <summary>Volcanic-style peaks added on high, rugged terrain (0 = none).</summary>
    public int MountainPeakCount { get; set; }

    /// <summary>
    /// Minimum absolute summit Z (m). Applied when <see cref="MaxZBound"/> − <see cref="MountainPeakClearanceBelowMaxZM"/>
    /// is above this value; otherwise only slope geometry is enforced.
    /// </summary>
    public double MountainPeakMinAbsoluteZM { get; set; } = 1000;

    /// <summary>Summit Z must stay at least this far below <see cref="MaxZBound"/> (m).</summary>
    public double MountainPeakClearanceBelowMaxZM { get; set; } = 100;

    /// <summary>
    /// Minimum terrain incline (degrees from horizontal) on the massif flank toward the summit: cone rise/run ≥ tan(angle).
    /// </summary>
    public double MountainMinInclineTowardPeakDegrees { get; set; } = 30;

    /// <summary>Peak lift relative to <see cref="TerrainAmplitude"/> when absolute peak limits are not active.</summary>
    public double MountainLiftScale { get; set; } = 0.62;

    /// <summary>
    /// Scales the auto floor for smallest catchment that may start a traced channel (~cell² × 5 × factor).
    /// </summary>
    public double DrainageMinCatchmentAreaFactor { get; set; } = 1.0;

    /// <summary>
    /// Calibrated channel headwater budget is about √(land cells) × this; higher → more creeks / tributaries before
    /// thresholds are tightened.
    /// </summary>
    public double DrainageHeadwaterBudgetFactor { get; set; } = 2.35;

    /// <summary>
    /// Tributary density vs basin size. Higher values require relatively larger catchments (fewer small channels).
    /// </summary>
    public double DrainageTributaryDensity { get; set; } = 1.0;

    /// <summary>
    /// Fraction of the largest accumulated flow on the grid used to classify main-stem segments (after auto calibration).
    /// </summary>
    public double DrainageMainStemAccumFraction { get; set; } = 0.052;

    /// <summary>
    /// Multiplier on the D8 tributary headwater contributing-area floor (river channel extent on land). 1 = engine
    /// default. Higher → stricter headwaters → drier land (less freshwater routing). Baseline Map tab maps a percent P to
    /// <c>100 / P</c> (100% = default, 50% ≈ twice as strict).
    /// </summary>
    public double LandFreshwaterStrictness { get; set; } = 1.0;

    /// <summary>Minimum bowl depth (m) for a land depression to become a pond/lake.</summary>
    public double DepressionLakeMinDepthM { get; set; } = 0.12;

    /// <summary>Maximum depression lakes to place. 0 = auto cap from grid (√land, max 512).</summary>
    public int DepressionLakeMaxCount { get; set; } = 0;

    /// <summary>Multiplier on minimum spacing between lake centers (1 = default).</summary>
    public double DepressionLakeSpacingFactor { get; set; } = 1.0;

    /// <summary>
    /// Second pass: pool standing water where channel corridors cross terrain bowls (flood-fill in orthographic
    /// depressions along rivers).
    /// </summary>
    public bool EnableRiverCorridorPoolLakes { get; set; } = true;

    /// <summary>Minimum orthogonal bowl depth (m) at a river (or 4-neighbor of river) cell to seed a pool.</summary>
    public double RiverPoolMinBowlDepthM { get; set; } = 0.09;

    /// <summary>Minimum bowl depth (m) for 4-neighbor expansion from seeds (typically lower than seed threshold).</summary>
    public double RiverPoolFloodMinBowlM { get; set; } = 0.026;

    /// <summary>Minimum cells in one pooled depression to become a lake.</summary>
    public int RiverPoolMinLakeCells { get; set; } = 5;

    /// <summary>Maximum additional river-bowl lakes (0 = auto cap from grid extent).</summary>
    public int RiverPoolMaxNewLakes { get; set; } = 0;

    /// <summary>
    /// Design storm rainfall depth (meters) for discharge proxy: storm volume ≈ contributing area × this ×
    /// <see cref="HydrologyRunoffFraction"/>.
    /// </summary>
    public double HydrologyDesignRainfallM { get; set; } = 0.065;

    /// <summary>Runoff coefficient (0–1) for storm-volume estimate.</summary>
    public double HydrologyRunoffFraction { get; set; } = 0.42;

    /// <summary>Scales half-width from discharge / slope curve (1 = default).</summary>
    public double HydrologyWidthCurveScale { get; set; } = 1.0;

    /// <summary>Minimum channel half-width (m) at any vertex after hydrology scaling.</summary>
    /// <summary>Minimum half-width (m) after hydrology; clamped with an engine absolute max (~50 m half).</summary>
    public double RiverChannelHalfWidthMinWorld { get; set; } = 1.85;

    /// <summary>Maximum channel half-width (m) per vertex; if ≤ 0, uses 2.75 × <see cref="RiverChannelHalfWidthWorld"/>.</summary>
    public double RiverChannelHalfWidthMaxWorld { get; set; }

    /// <summary>
    /// Extra path cost jitter (× cell size) so drainage routes meander with the terrain instead of
    /// hugging a single cost ridge.
    /// </summary>
    public double HydrologyMeanderNoise { get; set; } = 0.14;

    /// <summary>When true, gentle coastal mouths may spawn distributaries and patterned sandbars.</summary>
    public bool EnableRiverDeltaSandbars { get; set; } = true;

    /// <summary>Land cells from true ocean (used to blend beaches).</summary>
    public int CoastalShelfCells { get; set; } = 3;
}