using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

/// <summary>
/// Entry point and master pipeline for coastal procedural worlds (<see cref="Generate"/>).
/// Helper implementations live in other <c>partial</c> files in this folder; see <c>README.md</c>.
/// </summary>
public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>
    /// River half-width (m) is also limited to <c>cell × this</c> so coarse high-res grids do not paint huge channels.
    /// </summary>
    private const double RiverCorridorHalfWidthCapCellMultiple = 5.5;

    /// <summary>
    /// Hard cap on channel half-width (m). Most natural rivers are ~10–50 m half (~20–100 m across); only a few exceed that.
    /// </summary>
    private const double RiverCorridorHalfWidthAbsoluteMaxM = 52;

    private sealed record DrainageField(double[,] ContributingAreaM2, int[,] DownC, int[,] DownR);

    /// <summary>D8 flow has no steeper neighbor (sink / flat).</summary>
    private const int FlowDirNone = -1;

    /// <summary>Cell drains off-map into the modeled ocean sink.</summary>
    private const int FlowDirOcean = -2;

    /// <summary>Embarrassingly parallel grid work uses TPL at or above this cell count to avoid overhead on tiny grids.</summary>
    private const int ParallelGridCellThreshold = 65_536;

    /// <summary>
    /// Above this land-cell count, several full-grid passes typically exceed ~1s on typical hardware; emit granular progress.
    /// </summary>
    private const long LongRunningGridCellThreshold = 600_000;

    private static bool ShouldParallelize(int cols, int rows) => (long)cols * rows >= ParallelGridCellThreshold;

    internal static bool ExpectLongRunningGridPhase(int cols, int rows) =>
        (long)cols * rows >= LongRunningGridCellThreshold;

    /// <summary>Parallel over column index; inner loop is row (matches most <c>h[c,r]</c> passes).</summary>
    private static void ParallelForCols(int cols, int rows, Action<int, int> body)
    {
        if (!ShouldParallelize(cols, rows))
        {
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                    body(c, r);
            }

            return;
        }

        Parallel.For(0, cols, c =>
        {
            for (var r = 0; r < rows; r++)
                body(c, r);
        });
    }

    /// <summary>Parallel over row index (for code that scans row-outer).</summary>
    private static void ParallelForRows(int cols, int rows, Action<int, int> body)
    {
        if (!ShouldParallelize(cols, rows))
        {
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                    body(c, r);
            }

            return;
        }

        Parallel.For(0, rows, r =>
        {
            for (var c = 0; c < cols; c++)
                body(c, r);
        });
    }

    public static PhysicalWorldGenerationResult Generate(ProceduralWorldSpec spec, IProgress<string>? progress = null) =>
        GenerateCoastalOceanDrainage(spec, progress);

    internal static void Report(IProgress<string>? progress, string message) => progress?.Report(message);

    private static PhysicalWorldGenerationResult GenerateCoastalOceanDrainage(ProceduralWorldSpec spec,
        IProgress<string>? progress)
    {
        var report = new PhysicalWorldGenerationReport();
        var messages = report.Messages;

        var cell = Math.Max(0.5, spec.CellSize);
        var cols = Math.Max(4, (int)Math.Floor((spec.MaxX - spec.MinX) / cell));
        var rows = Math.Max(4, (int)Math.Floor((spec.MaxY - spec.MinY) / cell));
        var rng = new Random(spec.Seed);
        var spanX = spec.MaxX - spec.MinX;
        var spanY = spec.MaxY - spec.MinY;
        Report(progress,
            $"[coastal] Phase 1 — land: nav/hydrology sampling grid {cols}×{rows} (~{cols * rows:N0} cells) across entire world XY span ({spanX:F0} × {spanY:F0} world units); each cell is ~{cell:F0} world units — resolution, not “map size”.");
        if (ShouldParallelize(cols, rows))
            Report(progress, "[coastal] Height-field synthesis uses multithreaded grid passes where safe (slope relaxation & graph search stay single-threaded).");

        // --- Phase 1: land only (ruggedness, mountains, gentle trail slopes elsewhere) ---
        Report(progress, "[coastal] Base FBM heights + continental dome…");
        var h = BuildBaseHeights(cols, rows, spec, rng);
        ApplyContinentalDome(h, cols, rows, spec);
        var terrainRuggedness = new double[cols, rows];
        var mountainMask = new bool[cols, rows];
        Report(progress,
            $"[coastal] Regional ruggedness, ridges, and {spec.MountainPeakCount} mountain massifs (steep cores preserved)…");
        ApplyRegionalRuggednessMountainsAndRidges(h, terrainRuggedness, mountainMask, cols, rows, spec, rng);
        Report(progress,
            $"[coastal] Slope relaxation ({spec.SlopeRelaxationIterations} iters, skipping mountain mask edges)…");
        EnforceMaxOrthogonalSlope(h, cols, rows, spec.MaxLandStepOrthogonal, spec.SlopeRelaxationIterations,
            mountainMask);

        Report(progress, "[coastal] Perimeter ocean mask (noise shoreline vs terrain)…");
        var isOcean = BuildPerimeterOceanMask(cols, rows, spec, cell, rng, h);
        var lakeId = new int[cols, rows];

        Report(progress, "[coastal] Second slope pass after ocean mask…");
        EnforceMaxOrthogonalSlope(h, cols, rows, spec.MaxLandStepOrthogonal, Math.Max(8, spec.SlopeRelaxationIterations / 2),
            mountainMask);

        Report(progress, "[coastal] Coastal shelf lowering…");
        ApplyCoastalShelfLowering(h, cols, rows, spec, cell, isOcean);
        var freezeTerrainSmooth = new bool[cols, rows];
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
                freezeTerrainSmooth[c, r] = isOcean[c, r] || mountainMask[c, r];
        }

        Report(progress, "[coastal] Smoothing land only (10 passes, ocean + mountains frozen, parallel)…");
        SmoothMaskedHeightField(h, freezeTerrainSmooth, cols, rows, passes: 10, alpha: 0.34, progress,
            "[coastal] Land smooth (pre-rivers)");

        Report(progress, "[coastal] Terrain depression lakes (contour bowls, inland; count from relief)…");
        PlaceContourDepressionLakes(h, cols, rows, spec, cell, rng, isOcean, lakeId);

        Report(progress, "[coastal] Slope pass after lakes…");
        EnforceMaxOrthogonalSlope(h, cols, rows, spec.MaxLandStepOrthogonal, Math.Max(8, spec.SlopeRelaxationIterations / 2),
            mountainMask);

        // --- Phase 2: drainage from terrain toward the ocean ---
        Report(progress, "[coastal] D8 flow + contributing area (terrain-only discharge proxy for channel widths)…");
        var drainage = ComputeDrainageField(h, isOcean, cols, rows, cell, progress);
        var contributingAreaM2 = drainage.ContributingAreaM2;
        var downC = drainage.DownC;
        var downR = drainage.DownR;
        var riverCellHalfWidthWorld = new double[cols, rows];

        Report(progress,
            "[coastal] Phase 2 — hydrology: drainage network from contours (tributaries merge down to main stems)…");
        var isRiver = new bool[cols, rows];
        var riverPaths = new List<List<(int c, int r)>>();
        var deltaSandMask = new bool[cols, rows];
        var (majorStemCount, channelPaths) = BuildAndStampDrainageRiverNetwork(
            h, cols, rows, cell, spec, rng, isOcean, lakeId, isRiver, riverPaths, contributingAreaM2, downC, downR,
            riverCellHalfWidthWorld, terrainRuggedness, progress);
        report.MajorRiversFormed = majorStemCount;
        Report(progress,
            $"[coastal] Drainage channels: {channelPaths} traced paths, {majorStemCount} main-stem-scale reaches (after calibration).");

        if (spec.EnableRiverCorridorPoolLakes)
        {
            Report(progress, "[coastal] River corridor pool lakes (second pass: depressions along channels)…");
            var poolLakes = PoolLakesAlongRiverBowls(h, isRiver, riverCellHalfWidthWorld, lakeId, isOcean, cols, rows,
                spec, progress);
            Report(progress, $"[coastal] River corridor pool lakes committed: {poolLakes}.");
        }

        Report(progress, "[coastal] Lake outlet channels to nearest river…");
        ConnectLakesToNearestRiver(h, cols, rows, cell, spec, isOcean, lakeId, isRiver, riverPaths,
            contributingAreaM2, riverCellHalfWidthWorld, progress);

        if (spec.EnableRiverDeltaSandbars)
        {
            Report(progress, "[coastal] River deltas (gentle mouths → distributaries + sandbar mask)…");
            ApplyCoastalRiverDeltas(h, cols, rows, cell, spec, rng, isOcean, lakeId, isRiver, riverPaths, deltaSandMask,
                riverCellHalfWidthWorld);
        }

        var isLake = new bool[cols, rows];
        ParallelForCols(cols, rows, (c, r) => isLake[c, r] = lakeId[c, r] > 0);

        Report(progress, "[coastal] Sea level, coastal creep, ocean floor carve…");
        var oceanWaterZ = EstimateOceanSurfaceZ(h, isOcean, cols, rows);
        CreepOceanIntoLowCoastalTerrain(h, isOcean, lakeId, oceanWaterZ, cols, rows, spec, progress);
        CarveOceanFloor(h, cols, rows, isOcean, oceanWaterZ, spec.OceanDepth);

        var lakeLevels = ComputeLakeWaterLevels(h, lakeId, cols, rows);
        Report(progress, $"[coastal] Carving lake beds ({lakeLevels.Count} lakes) and river channels…");
        foreach (var kv in lakeLevels)
            CarveLakeBedForId(h, cols, rows, lakeId, kv.Key, kv.Value, spec.LakeDepth);

        CarveAllRiverChannelBeds(h, cols, rows, cell, spec, riverPaths, isRiver, isOcean, lakeId, oceanWaterZ,
            riverCellHalfWidthWorld, progress);
        SmoothRiverOnlyHeights(h, cols, rows, isRiver, isOcean, lakeId, passes: 2, alpha: 0.3, progress);

        var bedBeforeSurfaces = (double[,])h.Clone();
        Report(progress, "[coastal] Finalizing ocean / lake / river water surfaces…");
        FinalizeCoastalWaterSurfaces(h, cols, rows, isOcean, isLake, isRiver, lakeId, lakeLevels, oceanWaterZ,
            riverPaths, cell, spec, progress);

        var isWater = new bool[cols, rows];
        ParallelForCols(cols, rows, (c, r) =>
            isWater[c, r] = isOcean[c, r] || isLake[c, r] || isRiver[c, r]);

        Report(progress, "[coastal] Land slope enforcement + river valley bias (all main stems)…");
        EnforceLandOnlySlope(h, isWater, cols, rows, spec.MaxLandStepOrthogonal, 24, progress,
            "[coastal] Land-only slope before river valleys");
        var landNearRiver = new bool[cols, rows];
        var stemTotal = riverPaths.Count;
        for (var si = 0; si < stemTotal; si++)
        {
            var path = riverPaths[si];
            ApplyRiverValleyLandBias(h, isWater, path, cols, rows, spec, landNearRiver, progress,
                $"[coastal] River valley bias stem {si + 1}/{stemTotal}");
        }

        EnforceLandOnlySlope(h, isWater, cols, rows, spec.MaxLandStepOrthogonal, 18, progress,
            "[coastal] Land-only slope after river valleys");
        var freezePostWater = new bool[cols, rows];
        ParallelForCols(cols, rows, (c, r) =>
            freezePostWater[c, r] = isWater[c, r] || mountainMask[c, r]);

        Report(progress, "[coastal] Post-water smooth (8 passes, water + mountains frozen, parallel)…");
        SmoothMaskedHeightField(h, freezePostWater, cols, rows, passes: 8, alpha: 0.22, progress,
            "[coastal] Post-water smooth");

        Report(progress, "[coastal] Water network validation (ocean drainage + river profiles)…");
        ValidateWaterNetwork(isRiver, isLake, isOcean, cols, rows, report, h, riverPaths, cell, spec.TerrainAmplitude,
            progress);
        report.RiverTerminatesInLakeRegion = report.AllFlowingCellsReachStandingWater
                                              && report.MajorRiversFormed > 0
                                              && report.LakeBasinDisconnectedFromOceanCells == 0
                                              && report.RiverCenterlineDownstreamGradientViolations == 0;
        if (!report.AllFlowingCellsReachStandingWater)
            messages.Add(
                "Some river cells are not 4-connected through water to the ocean; widen channels or fix lake outlets.");
        if (report.LakeBasinDisconnectedFromOceanCells > 0)
            messages.Add(
                $"{report.LakeBasinDisconnectedFromOceanCells} lake cell(s) have no 4-connected water path to the ocean.");
        if (report.RiverCenterlineDownstreamGradientViolations > 0)
            messages.Add(
                $"{report.RiverCenterlineDownstreamGradientViolations} river centerline segment(s) climb downstream beyond tolerance.");

        Report(progress, "[coastal] Building navigation grid (surface/bed Z, walkability, composition)…");
        var grid = BuildNavGrid(spec, cols, rows, cell, h, bedBeforeSurfaces, isWater, spec.MaxLandStepOrthogonal,
            report, landNearRiver, deltaSandMask, progress);

        Report(progress, "[coastal] Procedural synthesis finished.");
        var world = new PhysicalWorldDefinition
        {
            SchemaVersion = 1,
            CoordinateDescription =
                "X/Y horizontal plane, Z vertical (up). 1 unit = 1 m (SI). Sea level = (MinZ+MaxZ)/2 in world Z; terrain and water surfaces are heights relative to that. Procedurally generated (coastal ocean).",
            GlobalBounds = new AxisAlignedBounds
            {
                Min = new Vec3 { X = spec.MinX, Y = spec.MinY, Z = spec.MinZBound },
                Max = new Vec3 { X = spec.MaxX, Y = spec.MaxY, Z = spec.MaxZBound },
            },
            RegionBoundaries =
            [
                new RegionBoundaryEntry
                {
                    LoreRegionId = spec.GeneratedRegionId,
                    Boundary = new PolygonColumnBounds
                    {
                        ZMin = spec.MinZBound,
                        ZMax = spec.MaxZBound,
                        Vertices =
                        [
                            new GeoVec2 { X = spec.MinX, Y = spec.MinY },
                            new GeoVec2 { X = spec.MaxX, Y = spec.MinY },
                            new GeoVec2 { X = spec.MaxX, Y = spec.MaxY },
                            new GeoVec2 { X = spec.MinX, Y = spec.MaxY },
                        ],
                    },
                },
            ],
            Territories = [],
            Features = BuildCoastalFeatures(spec, riverPaths, cell, lakeId, lakeLevels, oceanWaterZ,
                NormalizeFlow(spec.FlowDirectionDownstream)),
            Navigation = new NavigationBundle
            {
                Graph = new NavigationGraphDefinition(),
                Grid = grid,
            },
        };

        return new PhysicalWorldGenerationResult { World = world, Report = report };
    }
}