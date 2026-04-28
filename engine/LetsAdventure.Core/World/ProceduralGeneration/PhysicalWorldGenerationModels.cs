namespace LetsAdventure.Core.World;

/// <summary>Diagnostics produced by <see cref="ProceduralPhysicalWorldGenerator.Generate"/> (hydrology, slopes, connectivity).</summary>
public sealed class PhysicalWorldGenerationReport
{
    /// <summary>
    /// Composite hydrology quality flag (major rivers, connectivity, lake–ocean paths, downstream profiles).
    /// </summary>
    public bool RiverTerminatesInLakeRegion { get; set; }

    public int MajorRiversFormed { get; set; }
    public bool AllFlowingCellsReachStandingWater { get; set; }
    public double MaxObservedLandOrthogonalSlope { get; set; }
    public int LandSlopeViolationCount { get; set; }
    public int WaterConnectivityFailures { get; set; }

    /// <summary>
    /// Coastal only: lake cells whose 4-connected water component never reaches the ocean (endorheic / outlet failure).
    /// </summary>
    public int LakeBasinDisconnectedFromOceanCells { get; set; }

    /// <summary>
    /// Coastal centerline check: segments where water surface rises moving downstream (head → mouth) beyond tolerance.
    /// </summary>
    public int RiverCenterlineDownstreamGradientViolations { get; set; }

    public List<string> Messages { get; } = [];

    public bool IsSufficientlyNavigable =>
        LandSlopeViolationCount == 0
        && RiverTerminatesInLakeRegion
        && AllFlowingCellsReachStandingWater
        && WaterConnectivityFailures == 0
        && LakeBasinDisconnectedFromOceanCells == 0
        && RiverCenterlineDownstreamGradientViolations == 0;
}

/// <summary>Completed world definition plus the generation <see cref="PhysicalWorldGenerationReport"/>.</summary>
public sealed class PhysicalWorldGenerationResult
{
    public required PhysicalWorldDefinition World { get; init; }
    public required PhysicalWorldGenerationReport Report { get; init; }
}