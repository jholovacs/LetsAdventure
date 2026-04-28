using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

/// <summary>
/// Authoring-time procedural terrain with hydrology and nav constraints.
/// World axes X, Y (horizontal) and Z (up) use <b>SI meters</b>: one integer step in coordinates is 1 m
/// (fractional values are meters). All lengths in this spec — cell size, slopes, channel widths, depths — are meters.
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

    /// <summary>Peak lift relative to <see cref="TerrainAmplitude"/> (Gaussian massifs).</summary>
    public double MountainLiftScale { get; set; } = 0.62;

    /// <summary>Small streams merging into major channels or the ocean.</summary>
    public int TributaryStreamCount { get; set; }

    /// <summary>
    /// Extra path cost jitter (× cell size) so drainage routes meander with the terrain instead of
    /// hugging a single cost ridge.
    /// </summary>
    public double HydrologyMeanderNoise { get; set; } = 0.14;

    public int InteriorLakeCount { get; set; } = 1;

    /// <summary>Minimum major rivers (headwater → ocean or merge into an existing major stem).</summary>
    public int MajorRiverCountMin { get; set; } = 3;

    /// <summary>Maximum major rivers (inclusive).</summary>
    public int MajorRiverCountMax { get; set; } = 7;

    /// <summary>When true, gentle coastal mouths may spawn distributaries and patterned sandbars.</summary>
    public bool EnableRiverDeltaSandbars { get; set; } = true;

    /// <summary>Land cells from true ocean (used to blend beaches).</summary>
    public int CoastalShelfCells { get; set; } = 3;
}

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

public sealed class PhysicalWorldGenerationResult
{
    public required PhysicalWorldDefinition World { get; init; }
    public required PhysicalWorldGenerationReport Report { get; init; }
}

public static class ProceduralPhysicalWorldGenerator
{
    /// <summary>Embarrassingly parallel grid work uses TPL at or above this cell count to avoid overhead on tiny grids.</summary>
    private const int ParallelGridCellThreshold = 65_536;

    private static bool ShouldParallelize(int cols, int rows) => (long)cols * rows >= ParallelGridCellThreshold;

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

    private static void Report(IProgress<string>? progress, string message) => progress?.Report(message);

    private static int PickMajorRiverCount(ProceduralWorldSpec spec, Random rng)
    {
        var lo = Math.Max(1, spec.MajorRiverCountMin);
        var hi = Math.Max(lo, spec.MajorRiverCountMax);
        if (hi <= lo)
            return lo;
        return rng.Next(lo, hi + 1);
    }

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

        Report(progress, "[coastal] Smoothing land only (10 passes, ocean + mountains frozen)…");
        SmoothMaskedHeightField(h, freezeTerrainSmooth, cols, rows, passes: 10, alpha: 0.34);

        Report(progress, $"[coastal] Placing {spec.InteriorLakeCount} depression lakes (inland)…");
        PlaceInteriorLakes(h, cols, rows, spec, cell, rng, isOcean, lakeId);

        Report(progress, "[coastal] Slope pass after lakes…");
        EnforceMaxOrthogonalSlope(h, cols, rows, spec.MaxLandStepOrthogonal, Math.Max(8, spec.SlopeRelaxationIterations / 2),
            mountainMask);

        // --- Phase 2: drainage from terrain toward the ocean ---
        Report(progress,
            $"[coastal] Phase 2 — hydrology: {spec.MajorRiverCountMin}–{spec.MajorRiverCountMax} major rivers (random count; may merge into existing stems)…");
        var isRiver = new bool[cols, rows];
        var riverPaths = new List<List<(int c, int r)>>();
        var deltaSandMask = new bool[cols, rows];
        var riversPlaced = PlaceMajorRiversToOcean(h, cols, rows, cell, spec, rng, isOcean, lakeId, isRiver, riverPaths,
            terrainRuggedness);
        report.MajorRiversFormed = riversPlaced;
        Report(progress, $"[coastal] Major rivers placed: {riversPlaced}.");

        Report(progress, $"[coastal] Tributary streams (count {spec.TributaryStreamCount})…");
        PlaceTributaryStreams(h, cols, rows, cell, spec, rng, isOcean, lakeId, isRiver, riverPaths);

        Report(progress, "[coastal] Lake outlet channels to nearest river…");
        ConnectLakesToNearestRiver(h, cols, rows, cell, spec, isOcean, lakeId, isRiver, riverPaths);

        if (spec.EnableRiverDeltaSandbars)
        {
            Report(progress, "[coastal] River deltas (gentle mouths → distributaries + sandbar mask)…");
            ApplyCoastalRiverDeltas(h, cols, rows, cell, spec, rng, isOcean, lakeId, isRiver, riverPaths, deltaSandMask);
        }

        var isLake = new bool[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                isLake[c, r] = lakeId[c, r] > 0;

        Report(progress, "[coastal] Sea level, coastal creep, ocean floor carve…");
        var oceanWaterZ = EstimateOceanSurfaceZ(h, isOcean, cols, rows);
        CreepOceanIntoLowCoastalTerrain(h, isOcean, lakeId, oceanWaterZ, cols, rows, spec);
        CarveOceanFloor(h, cols, rows, isOcean, oceanWaterZ, spec.OceanDepth);

        var lakeLevels = ComputeLakeWaterLevels(h, lakeId, cols, rows);
        Report(progress, $"[coastal] Carving lake beds ({lakeLevels.Count} lakes) and river channels…");
        foreach (var kv in lakeLevels)
            CarveLakeBedForId(h, cols, rows, lakeId, kv.Key, kv.Value, spec.LakeDepth);

        foreach (var path in riverPaths)
            CarveRiverBed(h, path, isRiver, oceanWaterZ, spec.RiverBedCarve, spec);

        var bedBeforeSurfaces = (double[,])h.Clone();
        Report(progress, "[coastal] Finalizing ocean / lake / river water surfaces…");
        FinalizeCoastalWaterSurfaces(h, cols, rows, isOcean, isLake, isRiver, lakeId, lakeLevels, oceanWaterZ,
            riverPaths, cell, spec);

        var isWater = new bool[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                isWater[c, r] = isOcean[c, r] || isLake[c, r] || isRiver[c, r];

        Report(progress, "[coastal] Land slope enforcement + river valley bias (all main stems)…");
        EnforceLandOnlySlope(h, isWater, cols, rows, spec.MaxLandStepOrthogonal, 24, progress,
            "[coastal] Land-only slope before river valleys");
        var landNearRiver = new bool[cols, rows];
        var valleyScratch = new bool[cols, rows];
        var stemTotal = riverPaths.Count;
        for (var si = 0; si < stemTotal; si++)
        {
            var path = riverPaths[si];
            ApplyRiverValleyLandBias(h, isWater, path, cols, rows, spec, valleyScratch, progress,
                $"[coastal] River valley bias stem {si + 1}/{stemTotal}");
            for (var c = 0; c < cols; c++)
                for (var r = 0; r < rows; r++)
                    landNearRiver[c, r] |= valleyScratch[c, r];
        }

        EnforceLandOnlySlope(h, isWater, cols, rows, spec.MaxLandStepOrthogonal, 18, progress,
            "[coastal] Land-only slope after river valleys");
        var freezePostWater = new bool[cols, rows];
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
                freezePostWater[c, r] = isWater[c, r] || mountainMask[c, r];
        }

        Report(progress, "[coastal] Post-water smooth (8 passes, water + mountains frozen)…");
        SmoothMaskedHeightField(h, freezePostWater, cols, rows, passes: 8, alpha: 0.22);

        Report(progress, "[coastal] Water network validation (ocean drainage + river profiles)…");
        ValidateWaterNetwork(isRiver, isLake, isOcean, cols, rows, report, h, riverPaths, cell, spec.TerrainAmplitude);
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
            report, landNearRiver, deltaSandMask);

        Report(progress, "[coastal] Procedural synthesis finished.");
        var world = new PhysicalWorldDefinition
        {
            SchemaVersion = 1,
            CoordinateDescription =
                "X/Y horizontal plane, Z vertical (up). 1 unit = 1 m (SI). Procedurally generated (coastal ocean).",
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

    private static void ApplyRiverValleyLandBias(
        double[,] h,
        bool[,] isWater,
        List<(int c, int r)> path,
        int cols,
        int rows,
        ProceduralWorldSpec spec,
        bool[,] landNearRiver,
        IProgress<string>? progress = null,
        string progressPrefix = "")
    {
        if (ShouldParallelize(cols, rows))
            ParallelForCols(cols, rows, (c, r) => landNearRiver[c, r] = false);
        else
        {
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                    landNearRiver[c, r] = false;
            }
        }

        if (path.Count < 2)
            return;

        var cell = spec.CellSize;
        var influence = Math.Max(spec.RiverChannelHalfWidthWorld * 9.5, cell * 18);
        var sigma = Math.Max(influence * 0.68, cell * 6.5);
        var amplitude = spec.TerrainAmplitude * 0.022 + spec.RiverBedCarve * 0.065;
        if (amplitude < 1e-6)
            return;

        var totalLen = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var (c0, r0) = path[i - 1];
            var (c1, r1) = path[i];
            var dx = (c1 - c0) * cell;
            var dy = (r1 - r0) * cell;
            totalLen += Math.Sqrt(dx * dx + dy * dy);
        }

        if (totalLen < 1e-6)
            return;

        var segCount = path.Count - 1;
        var colReportStep = cols >= 384 ? Math.Max(1, cols / 12) : Math.Max(1, cols / 4);
        var reportPrefix = !string.IsNullOrEmpty(progressPrefix);
        var parallelSweep = ShouldParallelize(cols, rows);
        if (progress != null && reportPrefix)
            Report(progress,
                $"{progressPrefix}: scanning land columns for path distance ({segCount} segments, {rows} rows/column{(parallelSweep ? "; multithreaded" : "")})…");

        void ProcessColumn(int c)
        {
            for (var r = 0; r < rows; r++)
            {
                if (isWater[c, r])
                    continue;

                var wx = spec.MinX + (c + 0.5) * cell;
                var wy = spec.MinY + (r + 0.5) * cell;
                ClosestPointOnRiverPath(wx, wy, path, spec, out var dist, out var sAlong);
                if (dist > influence)
                    continue;

                var t = sAlong / totalLen;
                t = Math.Clamp(t, 0, 1);
                var w = Math.Exp(-(dist * dist) / (2 * sigma * sigma));
                h[c, r] -= amplitude * w * (0.28 + 0.72 * t);
                if (w > 0.14)
                    landNearRiver[c, r] = true;
            }
        }

        if (parallelSweep)
        {
            var finishedCols = 0;
            Parallel.For(0, cols, c =>
            {
                ProcessColumn(c);
                if (progress != null && reportPrefix)
                {
                    var v = Interlocked.Increment(ref finishedCols);
                    if (v == 1 || v == cols || v % colReportStep == 0)
                        Report(progress, $"{progressPrefix}: finished {v}/{cols} columns…");
                }
            });
        }
        else
        {
            for (var c = 0; c < cols; c++)
            {
                if (progress != null && reportPrefix &&
                    (c == 0 || c == cols - 1 || (c + 1) % colReportStep == 0))
                    Report(progress, $"{progressPrefix}: columns {c + 1}/{cols}…");

                ProcessColumn(c);
            }
        }

        if (progress != null && reportPrefix)
            Report(progress, $"{progressPrefix}: column sweep done.");
    }

    private static void ClosestPointOnRiverPath(
        double wx,
        double wy,
        List<(int c, int r)> path,
        ProceduralWorldSpec spec,
        out double distance,
        out double arcLengthAlong)
    {
        distance = double.PositiveInfinity;
        arcLengthAlong = 0;
        var acc = 0.0;
        var cellSize = spec.CellSize;

        for (var i = 0; i < path.Count - 1; i++)
        {
            var (c0, r0) = path[i];
            var (c1, r1) = path[i + 1];
            var x0 = spec.MinX + (c0 + 0.5) * cellSize;
            var y0 = spec.MinY + (r0 + 0.5) * cellSize;
            var x1 = spec.MinX + (c1 + 0.5) * cellSize;
            var y1 = spec.MinY + (r1 + 0.5) * cellSize;
            var segLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            ClosestOnSegment2D(wx, wy, x0, y0, x1, y1, out var d, out var u);
            if (d < distance)
            {
                distance = d;
                arcLengthAlong = acc + u * segLen;
            }

            acc += segLen;
        }
    }

    private static void ClosestOnSegment2D(
        double px,
        double py,
        double x0,
        double y0,
        double x1,
        double y1,
        out double dist,
        out double u)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-18)
        {
            dist = Math.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0));
            u = 0;
            return;
        }

        u = ((px - x0) * dx + (py - y0) * dy) / lenSq;
        u = Math.Clamp(u, 0, 1);
        var qx = x0 + u * dx;
        var qy = y0 + u * dy;
        dist = Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }

    private static void SmoothMaskedHeightField(double[,] h, bool[,] freeze, int cols, int rows, int passes,
        double alpha)
    {
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var p = 0; p < passes; p++)
        {
            var copy = (double[,])h.Clone();
            ParallelForCols(cols, rows, (c, r) =>
            {
                if (freeze[c, r])
                    return;
                double s = 0;
                var n = 0;
                foreach (var (dc, dr) in orth)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                        continue;
                    s += copy[nc, nr];
                    n++;
                }

                if (n == 0)
                    return;
                var avg = s / n;
                h[c, r] = (1 - alpha) * copy[c, r] + alpha * avg;
            });
        }
    }

    private static TerrainNavGridDefinition BuildNavGrid(
        ProceduralWorldSpec spec,
        int cols,
        int rows,
        double cell,
        double[,] h,
        double[,] bedBeforeWaterSurface,
        bool[,] isWater,
        double maxLandStep,
        PhysicalWorldGenerationReport report,
        bool[,] landNearRiver,
        bool[,]? deltaSandPreferred)
    {
        var cells = new List<NavCellDefinition>(cols * rows);
        var maxSlope = 0.0;
        var violations = 0;
        var orthDirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        var dryGrad = new double[cols, rows];
        ParallelForRows(cols, rows, (c, r) =>
        {
            var gDry = 0.0;
            foreach (var (dc, dr) in orthDirs)
            {
                var nc = c + dc;
                var nr = r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || isWater[nc, nr])
                    continue;
                gDry = Math.Max(gDry, Math.Abs(h[c, r] - h[nc, nr]));
            }

            dryGrad[c, r] = gDry;
        });

        var maxWalk = spec.MaxWalkableOrthogonalStep > 1e-6
            ? spec.MaxWalkableOrthogonalStep
            : spec.MaxLandStepOrthogonal;

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var water = isWater[c, r];
                var elev = h[c, r];
                double bed;
                double waterSurfZ;
                double fluidD;
                if (water)
                {
                    bed = bedBeforeWaterSurface[c, r];
                    waterSurfZ = elev;
                    fluidD = Math.Max(0, waterSurfZ - bed);
                }
                else
                {
                    bed = elev;
                    waterSurfZ = 0;
                    fluidD = 0;
                }

                var walk = !water && dryGrad[c, r] <= maxWalk + 1e-3;
                var grad = 0.0;
                var nearWater = false;
                foreach (var (dc, dr) in orthDirs)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                        continue;
                    if (isWater[nc, nr])
                        nearWater = true;
                    var dz = Math.Abs(h[c, r] - h[nc, nr]);
                    grad = Math.Max(grad, dz);
                }

                if (!water)
                {
                    foreach (var (dc, dr) in orthDirs)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || isWater[nc, nr])
                            continue;
                        var dz = Math.Abs(h[c, r] - h[nc, nr]);
                        maxSlope = Math.Max(maxSlope, dz);
                        var plateau =
                            dryGrad[c, r] <= maxWalk * 2.4 && dryGrad[nc, nr] <= maxWalk * 2.4;
                        if (dz > maxLandStep + 1e-4 && plateau)
                            violations++;
                    }
                }

                SurfaceComposition composition;
                if (water)
                {
                    composition = SurfaceComposition.Water;
                }
                else if (deltaSandPreferred != null && deltaSandPreferred[c, r])
                {
                    composition = SurfaceComposition.Sand;
                }
                else if (landNearRiver[c, r] && grad < maxLandStep * 0.55)
                {
                    composition = SurfaceComposition.Mud;
                }
                else if (grad > maxLandStep * 0.72)
                {
                    composition = SurfaceComposition.Rock;
                }
                else if (grad > maxLandStep * 0.26)
                {
                    composition = SurfaceComposition.ForestFloor;
                }
                else if (nearWater)
                {
                    composition = SurfaceComposition.Sand;
                }
                else if (grad < maxLandStep * 0.11)
                {
                    composition = SurfaceComposition.Soil;
                }
                else
                {
                    composition = SurfaceComposition.Grass;
                }

                cells.Add(new NavCellDefinition
                {
                    Walkable = walk,
                    ElevationZ = elev,
                    BedElevationZ = bed,
                    WaterSurfaceZ = waterSurfZ,
                    MovementCostMultiplier = water ? 2.5 : 1,
                    Composition = composition,
                    FluidDepth = fluidD,
                });
            }
        }

        report.MaxObservedLandOrthogonalSlope = maxSlope;
        report.LandSlopeViolationCount = violations / 2;
        if (violations > 0)
            report.Messages.Add(
                $"Land slope violations (orth. neighbors): {report.LandSlopeViolationCount} edges exceed {maxLandStep:F2}.");

        return new TerrainNavGridDefinition
        {
            OriginX = spec.MinX,
            OriginY = spec.MinY,
            CellSize = cell,
            Columns = cols,
            Rows = rows,
            Cells = cells,
        };
    }

    /// <param name="flowSurfaceHeights">Finalized water surface / terrain Z; used for downstream gradient checks.</param>
    /// <param name="riverCenterlines">River paths ordered headwater → mouth (or merge); used for gradient checks.</param>
    private static void ValidateWaterNetwork(
        bool[,] isRiver,
        bool[,] isLake,
        bool[,] isOcean,
        int cols,
        int rows,
        PhysicalWorldGenerationReport report,
        double[,]? flowSurfaceHeights = null,
        List<List<(int c, int r)>>? riverCenterlines = null,
        double cellSize = 1,
        double terrainAmplitude = 22)
    {
        report.LakeBasinDisconnectedFromOceanCells = 0;
        report.RiverCenterlineDownstreamGradientViolations = 0;

        var water = new bool[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                water[c, r] = isRiver[c, r] || isLake[c, r] || isOcean[c, r];

        var comp = new int[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                comp[c, r] = -1;

        var compHasOcean = new List<bool>();
        var q = new Queue<(int c, int r)>();

        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!water[c, r] || comp[c, r] >= 0)
                    continue;
                var id = compHasOcean.Count;
                compHasOcean.Add(false);
                q.Enqueue((c, r));
                comp[c, r] = id;
                while (q.Count > 0)
                {
                    var (u, v) = q.Dequeue();
                    if (isOcean[u, v])
                        compHasOcean[id] = true;
                    foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        var nc = u + dc;
                        var nr = v + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || !water[nc, nr])
                            continue;
                        if (comp[nc, nr] >= 0)
                            continue;
                        comp[nc, nr] = id;
                        q.Enqueue((nc, nr));
                    }
                }
            }
        }

        var failures = 0;
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!isRiver[c, r])
                    continue;
                var id = comp[c, r];
                if (id < 0)
                {
                    failures++;
                    continue;
                }

                if (!compHasOcean[id])
                    failures++;
            }
        }

        report.WaterConnectivityFailures = failures;
        report.AllFlowingCellsReachStandingWater = failures == 0;

        var lakeIssues = 0;
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!isLake[c, r])
                    continue;
                var id = comp[c, r];
                if (id < 0 || !compHasOcean[id])
                    lakeIssues++;
            }
        }

        report.LakeBasinDisconnectedFromOceanCells = lakeIssues;

        if (riverCenterlines is { Count: > 0 } && flowSurfaceHeights is not null)
        {
            var tol = Math.Max(cellSize * 0.055, terrainAmplitude * 0.0042);
            var gradViol = 0;
            foreach (var path in riverCenterlines)
            {
                if (path.Count < 2)
                    continue;
                for (var i = 0; i < path.Count - 1; i++)
                {
                    var (ac, ar) = path[i];
                    var (bc, br) = path[i + 1];
                    if ((uint)ac >= (uint)cols || (uint)ar >= (uint)rows)
                        continue;
                    if ((uint)bc >= (uint)cols || (uint)br >= (uint)rows)
                        continue;
                    if (Math.Max(Math.Abs(ac - bc), Math.Abs(ar - br)) > 1)
                        continue;
                    var ha = flowSurfaceHeights[ac, ar];
                    var hb = flowSurfaceHeights[bc, br];
                    if (hb > ha + tol)
                        gradViol++;
                }
            }

            report.RiverCenterlineDownstreamGradientViolations = gradViol;
        }
    }

    private static void CarveRiverBed(
        double[,] h,
        List<(int c, int r)> path,
        bool[,] isRiver,
        double lakeWaterZ,
        double carve,
        ProceduralWorldSpec spec)
    {
        var effCarve = carve * 0.55;
        for (var i = 0; i < path.Count; i++)
        {
            var t = path.Count > 1 ? i / (double)(path.Count - 1) : 0;
            var target = lakeWaterZ + (1 - t) * spec.TerrainAmplitude * 0.32 - effCarve;
            var (c, r) = path[i];
            h[c, r] = Math.Min(h[c, r], target);
        }
    }

    /// <param name="blocking">Cells that must not receive river stamp (e.g. lakes, or other lakes only for outlets).</param>
    private static void StampRiverCorridor(
        List<(int c, int r)> path,
        int riverHalfCells,
        int cols,
        int rows,
        bool[,] blocking,
        bool[,] isRiver)
    {
        foreach (var (pc, pr) in path)
        {
            for (var c = Math.Max(0, pc - riverHalfCells - 1); c < Math.Min(cols, pc + riverHalfCells + 2); c++)
            {
                for (var r = Math.Max(0, pr - riverHalfCells - 1); r < Math.Min(rows, pr + riverHalfCells + 2); r++)
                {
                    if (blocking[c, r])
                        continue;
                    if (Math.Sqrt((c - pc) * (c - pc) + (r - pr) * (r - pr)) <= riverHalfCells + 0.35)
                        isRiver[c, r] = true;
                }
            }
        }
    }

    private static List<(int c, int r)> StraightLinePath((int c, int r) a, (int c, int r) b)
    {
        var list = new List<(int, int)>();
        var n = Math.Max(Math.Abs(b.c - a.c), Math.Abs(b.r - a.r));
        n = Math.Max(n, 1);
        for (var i = 0; i <= n; i++)
        {
            var t = i / (double)n;
            var c = (int)Math.Round(a.c + (b.c - a.c) * t);
            var r = (int)Math.Round(a.r + (b.r - a.r) * t);
            if (list.Count == 0 || list[^1] != (c, r))
                list.Add((c, r));
        }

        return list;
    }

    private static double LakeDistance(int c, int r, int lc, int lr) => Math.Sqrt((c - lc) * (c - lc) + (r - lr) * (r - lr));

    /// <param name="progressLabel">When set with <paramref name="progress"/>, reports pass progress (e.g. "[coastal] Land-only slope (pre-valley").</param>
    private static void EnforceLandOnlySlope(
        double[,] h,
        bool[,] isWater,
        int cols,
        int rows,
        double maxStep,
        int iterations,
        IProgress<string>? progress = null,
        string? progressLabel = null)
    {
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        var reportEvery = iterations <= 8 ? 1 : Math.Max(1, iterations / 5);
        for (var it = 0; it < iterations; it++)
        {
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    if (isWater[c, r])
                        continue;
                    foreach (var (dc, dr) in dirs)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || isWater[nc, nr])
                            continue;
                        var diff = h[c, r] - h[nc, nr];
                        if (diff > maxStep)
                        {
                            var adj = (diff - maxStep) * 0.5;
                            h[c, r] -= adj;
                            h[nc, nr] += adj;
                        }
                        else if (diff < -maxStep)
                        {
                            var adj = (-diff - maxStep) * 0.5;
                            h[c, r] += adj;
                            h[nc, nr] -= adj;
                        }
                    }
                }
            }

            if (progress != null && progressLabel != null &&
                (it == 0 || it == iterations - 1 || (it + 1) % reportEvery == 0))
                Report(progress, $"{progressLabel}: relax {it + 1}/{iterations}…");
        }
    }

    /// <param name="preserveSteepTerrain">When set, skips relaxation for edges touching these cells (mountain cores).</param>
    private static void EnforceMaxOrthogonalSlope(double[,] h, int cols, int rows, double maxStep, int iterations,
        bool[,]? preserveSteepTerrain = null)
    {
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var it = 0; it < iterations; it++)
        {
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    foreach (var (dc, dr) in dirs)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                            continue;
                        if (preserveSteepTerrain != null &&
                            (preserveSteepTerrain[c, r] || preserveSteepTerrain[nc, nr]))
                            continue;
                        var diff = h[c, r] - h[nc, nr];
                        if (diff > maxStep)
                        {
                            var adj = (diff - maxStep) * 0.5;
                            h[c, r] -= adj;
                            h[nc, nr] += adj;
                        }
                        else if (diff < -maxStep)
                        {
                            var adj = (-diff - maxStep) * 0.5;
                            h[c, r] += adj;
                            h[nc, nr] -= adj;
                        }
                    }
                }
            }
        }
    }

    private static double[,] BuildBaseHeights(int cols, int rows, ProceduralWorldSpec spec, Random rng)
    {
        var h = new double[cols, rows];
        var seed = spec.Seed ^ (rng.Next() << 1);
        ParallelForCols(cols, rows, (c, r) =>
        {
            var nx = c / (double)Math.Max(cols - 1, 1);
            var ny = r / (double)Math.Max(rows - 1, 1);
            var z = Fbm(nx * 3.1, ny * 3.1, spec.NoiseOctaves, seed) * spec.TerrainAmplitude;
            z += 0.24 * spec.TerrainAmplitude * Math.Sin(nx * Math.PI) * Math.Sin(ny * Math.PI);
            h[c, r] = z;
        });

        return h;
    }

    private static double Fbm(double x, double y, int octaves, int seed)
    {
        var sum = 0.0;
        var amp = 0.5;
        var freq = 1.0;
        for (var o = 0; o < octaves; o++)
        {
            sum += amp * SmoothNoise(x * freq, y * freq, seed + o * 101);
            freq *= 2;
            amp *= 0.5;
        }

        return sum;
    }

    private static double SmoothNoise(double x, double y, int seed)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var tx = x - x0;
        var ty = y - y0;
        var u = tx * tx * (3 - 2 * tx);
        var v = ty * ty * (3 - 2 * ty);
        var a = ValueNoise(x0, y0, seed);
        var b = ValueNoise(x0 + 1, y0, seed);
        var c = ValueNoise(x0, y0 + 1, seed);
        var d = ValueNoise(x0 + 1, y0 + 1, seed);
        return Lerp(Lerp(a, b, u), Lerp(c, d, u), v);
    }

    private static double ValueNoise(int x, int y, int seed)
    {
        var n = Hash(x, y, seed);
        return (n & 0xffff) / 65535.0;
    }

    private static int Hash(int x, int y, int s)
    {
        unchecked
        {
            var h = s + x * 374761393 + y * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            return h ^ (h >> 16);
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static void WorldToGrid(
        ProceduralWorldSpec spec,
        double cell,
        double wx,
        double wy,
        int cols,
        int rows,
        out int c,
        out int r)
    {
        c = (int)Math.Clamp(Math.Floor((wx - spec.MinX) / cell), 0, cols - 1);
        r = (int)Math.Clamp(Math.Floor((wy - spec.MinY) / cell), 0, rows - 1);
    }

    private static GeoVec2 NormalizeFlow(GeoVec2 v)
    {
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1e-6)
            return new GeoVec2 { X = 1, Y = 0 };
        return new GeoVec2 { X = v.X / len, Y = v.Y / len };
    }


    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private static double DistanceToMapEdgeWorld(double wx, double wy, ProceduralWorldSpec spec)
    {
        var dx = Math.Min(wx - spec.MinX, spec.MaxX - wx);
        var dy = Math.Min(wy - spec.MinY, spec.MaxY - wy);
        return Math.Min(dx, dy);
    }

    private static bool[,] BuildPerimeterOceanMask(int cols, int rows, ProceduralWorldSpec spec, double cell,
        Random rng, double[,] h)
    {
        _ = rng;
        var mask = new bool[cols, rows];
        var seed = spec.Seed ^ 0x6D2B79F5;
        var spanX = Math.Max(spec.MaxX - spec.MinX, 1e-6);
        var spanY = Math.Max(spec.MaxY - spec.MinY, 1e-6);
        var edgeZ = new List<double>();
        for (var c = 0; c < cols; c++)
        {
            edgeZ.Add(h[c, 0]);
            edgeZ.Add(h[c, rows - 1]);
        }

        for (var r = 0; r < rows; r++)
        {
            edgeZ.Add(h[0, r]);
            edgeZ.Add(h[cols - 1, r]);
        }

        var hRef = edgeZ.Count > 0 ? edgeZ.Average() : 0;
        var amp = Math.Max(spec.TerrainAmplitude, 60);
        ParallelForCols(cols, rows, (c, r) =>
        {
            var wx = spec.MinX + (c + 0.5) * cell;
            var wy = spec.MinY + (r + 0.5) * cell;
            var dEdge = DistanceToMapEdgeWorld(wx, wy, spec);
            var nx = (wx - spec.MinX) / spanX;
            var ny = (wy - spec.MinY) / spanY;
            var fine = (SmoothNoise(nx * 6.2, ny * 6.2, seed) - 0.5) * 2.0;
            var bay = (SmoothNoise(nx * 2.05 + 0.3, ny * 2.05 - 0.2, seed + 911) - 0.5) * 2.0;
            var cove = (SmoothNoise(nx * 11.0, ny * 11.0, seed + 413) - 0.5) * 0.65;
            var threshold = spec.OceanBandMinWorld
                + spec.OceanBandVariationWorld * (0.45 * fine + 0.42 * bay + 0.28 * cove);
            var hRel = (h[c, r] - hRef) / amp;
            threshold -= hRel * spec.OceanBandVariationWorld * 0.38;
            threshold = Math.Max(cell * 0.35, threshold);
            mask[c, r] = dEdge < threshold;
        });

        return mask;
    }

    /// <summary>
    /// Lets the sea lap slightly into low ground along the coast so the shore follows terrain more than a pure geometric band.
    /// </summary>
    private static void CreepOceanIntoLowCoastalTerrain(
        double[,] h,
        bool[,] isOcean,
        int[,] lakeId,
        double seaZ,
        int cols,
        int rows,
        ProceduralWorldSpec spec)
    {
        var band = Math.Max(spec.TerrainAmplitude * 0.038 + 20, 16);
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var pass = 0; pass < 9; pass++)
        {
            var add = new List<(int c, int r)>();
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    if (isOcean[c, r] || lakeId[c, r] != 0)
                        continue;
                    var touchesOcean = false;
                    foreach (var (dc, dr) in orth)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                            continue;
                        if (isOcean[nc, nr])
                            touchesOcean = true;
                    }

                    if (!touchesOcean)
                        continue;
                    if (h[c, r] <= seaZ + band)
                        add.Add((c, r));
                }
            }

            if (add.Count == 0)
                break;
            foreach (var (c, r) in add)
            {
                isOcean[c, r] = true;
                h[c, r] = Math.Min(h[c, r], seaZ + band * 0.08);
            }
        }
    }

    private static void ApplyContinentalDome(double[,] h, int cols, int rows, ProceduralWorldSpec spec)
    {
        if (spec.ContinentalDomeAmplitude <= 1e-6)
            return;
        var cx = (cols - 1) * 0.5;
        var cy = (rows - 1) * 0.5;
        var maxR = Math.Sqrt(Math.Max(cx * cx + cy * cy, 1e-6));
        ParallelForCols(cols, rows, (c, r) =>
        {
            var dx = (c + 0.5) - cx;
            var dy = (r + 0.5) - cy;
            var rd = Math.Sqrt(dx * dx + dy * dy) / maxR;
            var t = 1.0 - Math.Clamp(rd, 0, 1);
            var lift = spec.ContinentalDomeAmplitude * t * t * (1.0 + 0.35 * t);
            h[c, r] += lift;
        });
    }

    private static void ApplyTerrainRidges(double[,] h, int cols, int rows, ProceduralWorldSpec spec, Random rng)
    {
        if (spec.TerrainRidgeWeight <= 1e-6)
            return;
        var seed = spec.Seed ^ rng.Next();
        ParallelForCols(cols, rows, (c, r) =>
        {
            var nx = c / (double)Math.Max(cols - 1, 1);
            var ny = r / (double)Math.Max(rows - 1, 1);
            var n0 = SmoothNoise(nx * 5.1, ny * 5.1, seed);
            var ridge = 1.0 - Math.Abs(n0 * 2.0 - 1.0);
            h[c, r] += spec.TerrainAmplitude * spec.TerrainRidgeWeight * ridge;
        });
    }

    /// <summary>
    /// Spatially varying smooth↔rugged terrain, optional ridged detail, and steep mountain massifs
    /// (cores marked in <paramref name="mountainMask"/>).
    /// </summary>
    private static void ApplyRegionalRuggednessMountainsAndRidges(
        double[,] h,
        double[,] terrainRuggedness,
        bool[,] mountainMask,
        int cols,
        int rows,
        ProceduralWorldSpec spec,
        Random rng)
    {
        var seed = spec.Seed ^ rng.Next();
        ParallelForCols(cols, rows, (c, r) =>
        {
            var nx = c / (double)Math.Max(cols - 1, 1);
            var ny = r / (double)Math.Max(rows - 1, 1);
            var R = 0.5 * (1.0 + SmoothNoise(nx * 1.55 + 0.2, ny * 1.48 - 0.11, seed));
            R = Math.Clamp(R, 0, 1);
            terrainRuggedness[c, r] = R;

            var ridged = RidgedFbm(nx * 7.2, ny * 7.2, 5, seed + 101);
            var detailSmooth = SmoothNoise(nx * 4.1, ny * 4.1, seed + 303) - 0.5;
            var roughAmt = spec.TerrainAmplitude * (0.11 + 0.41 * R);
            h[c, r] += R * ridged * roughAmt + (1 - R) * detailSmooth * spec.TerrainAmplitude * 0.045;
        });

        if (spec.TerrainRidgeWeight > 1e-6)
        {
            ParallelForCols(cols, rows, (c, r) =>
            {
                var nx = c / (double)Math.Max(cols - 1, 1);
                var ny = r / (double)Math.Max(rows - 1, 1);
                var n0 = SmoothNoise(nx * 5.1, ny * 5.1, seed + 17);
                var ridge = 1.0 - Math.Abs(n0 * 2.0 - 1.0);
                var R = terrainRuggedness[c, r];
                h[c, r] += spec.TerrainAmplitude * spec.TerrainRidgeWeight * ridge * (0.32 + 0.68 * R);
            });
        }

        if (spec.MountainPeakCount <= 0)
            return;

        var peaks = new List<(int c, int r)>();
        var span = Math.Min(cols, rows);
        var minPeakSep = Math.Max(6.0, span * 0.088);
        var minPeakSepSq = minPeakSep * minPeakSep;
        for (var p = 0; p < spec.MountainPeakCount; p++)
        {
            var bestC = -1;
            var bestR = -1;
            var bestScore = double.NegativeInfinity;
            for (var attempt = 0; attempt < 110; attempt++)
            {
                var mc = rng.Next(Math.Max(2, cols / 10), Math.Max(3, cols - cols / 10));
                var mr = rng.Next(Math.Max(2, rows / 10), Math.Max(3, rows - rows / 10));
                var R = terrainRuggedness[mc, mr];
                var score = h[mc, mr] * (0.48 + 0.92 * R);
                foreach (var prev in peaks)
                {
                    var dx = mc - prev.c;
                    var dy = mr - prev.r;
                    if (dx * dx + dy * dy < minPeakSepSq)
                    {
                        score = double.NegativeInfinity;
                        break;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestC = mc;
                    bestR = mr;
                }
            }

            if (bestC < 0 || bestScore < 0)
                continue;
            peaks.Add((bestC, bestR));
            var lift = spec.TerrainAmplitude * spec.MountainLiftScale * (0.8 + rng.NextDouble() * 0.52);
            var radiusCells = Math.Max(3.6, span * (0.044 + rng.NextDouble() * 0.054));
            var peakIndex = p;
            ParallelForCols(cols, rows, (c, r) =>
            {
                var dx = c - bestC;
                var dy = r - bestR;
                var d = Math.Sqrt(dx * dx + dy * dy) / radiusCells;
                if (d > 1.14)
                    return;
                var t = Math.Clamp(d, 0, 1);
                var bump = lift * Math.Pow(1.0 - t, 1.94);
                var nx = c / (double)Math.Max(cols - 1, 1);
                var ny = r / (double)Math.Max(rows - 1, 1);
                var spikey = 1.0 + 0.24 * RidgedFbm(nx * 14.0, ny * 14.0, 3, seed + peakIndex * 173);
                h[c, r] += bump * spikey;
                if (d < 0.5)
                    mountainMask[c, r] = true;
            });
        }
    }

    private static double RidgedFbm(double x, double y, int octaves, int seed)
    {
        var sum = 0.0;
        var amp = 0.55;
        var freq = 1.0;
        for (var o = 0; o < octaves; o++)
        {
            var n = SmoothNoise(x * freq, y * freq, seed + o * 59);
            var ridge = 1.0 - Math.Abs(n * 2.0 - 1.0);
            sum += amp * ridge * ridge;
            freq *= 2.08;
            amp *= 0.52;
        }

        return sum;
    }

    /// <summary>Interior lakes only in land depressions, away from map edges and ocean, with varied radii.</summary>
    private static void PlaceInteriorLakes(
        double[,] h,
        int cols,
        int rows,
        ProceduralWorldSpec spec,
        double cell,
        Random rng,
        bool[,] isOcean,
        int[,] lakeId)
    {
        var want = Math.Max(0, spec.InteriorLakeCount);
        if (want == 0)
            return;

        var distOcean = GridDistanceToOcean(isOcean, cols, rows);
        var minOceanCells = Math.Max(18, cols / 12);
        var minEdgeCells = Math.Max(14, cols / 16);
        var minDepression = Math.Max(spec.TerrainAmplitude * 0.012, spec.MaxLandStepOrthogonal * 0.35);

        var candidates = new List<(int c, int r, double score)>();
        for (var c = 1; c < cols - 1; c++)
        {
            for (var r = 1; r < rows - 1; r++)
            {
                if (isOcean[c, r])
                    continue;
                if (distOcean[c, r] < minOceanCells)
                    continue;
                var edgeDist = Math.Min(Math.Min(c, cols - 1 - c), Math.Min(r, rows - 1 - r));
                if (edgeDist < minEdgeCells)
                    continue;

                var h0 = h[c, r];
                var hn0 = h[c - 1, r];
                var hn1 = h[c + 1, r];
                var hn2 = h[c, r - 1];
                var hn3 = h[c, r + 1];
                if (h0 > hn0 || h0 > hn1 || h0 > hn2 || h0 > hn3)
                    continue;

                var bowl = (hn0 + hn1 + hn2 + hn3) * 0.25 - h0;
                if (bowl < minDepression)
                    continue;
                candidates.Add((c, r, bowl));
            }
        }

        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        var placed = new List<(int lc, int lr, double meanRCells)>();
        for (var k = 0; k < want && candidates.Count > 0;)
        {
            var found = false;
            for (var idx = 0; idx < candidates.Count; idx++)
            {
                var (lc, lr, score) = candidates[idx];
                if (lakeId[lc, lr] != 0 || isOcean[lc, lr])
                    continue;

                var baseR = Math.Clamp(score / (cell * 0.11 + 1e-6), 1.8, spec.LakeRadiusWorld * 0.95 / cell);
                var meanRCells = baseR * (0.72 + rng.NextDouble() * 0.55);
                meanRCells = Math.Max(1.5, meanRCells);

                var tooClose = false;
                foreach (var p in placed)
                {
                    if (LakeDistance(lc, lr, p.lc, p.lr) < (meanRCells + p.meanRCells) * 1.38 + 5)
                        tooClose = true;
                }

                if (tooClose)
                    continue;

                var lakeSeed = spec.Seed + k * 374761393 + rng.Next();
                ApplyLakeBowlOrganic(h, cols, rows, lc, lr, meanRCells, spec.TerrainAmplitude * 0.29, lakeSeed);
                for (var c = 0; c < cols; c++)
                {
                    for (var r = 0; r < rows; r++)
                    {
                        if (isOcean[c, r] || lakeId[c, r] != 0)
                            continue;
                        var d = LakeDistance(c, r, lc, lr);
                        if (d > meanRCells * 1.85 + 2.5)
                            continue;
                        var ang = Math.Atan2(r - lr, c - lc);
                        var rEdge = meanRCells * LakeRadiusWobble(ang, c, r, lakeSeed);
                        if (d <= rEdge + 0.35)
                            lakeId[c, r] = k + 1;
                    }
                }

                placed.Add((lc, lr, meanRCells));
                k++;
                found = true;
                break;
            }

            if (!found)
                break;
        }
    }

    private static double LakeRadiusWobble(double ang, int c, int r, int lakeSeed)
    {
        var a = SmoothNoise(ang * 1.12 + lakeSeed * 0.0007, lakeSeed * 0.0011, lakeSeed);
        var b = SmoothNoise(ang * 2.4 + 0.6, ang * 0.38 - 0.2, lakeSeed + 31);
        var cN = SmoothNoise(c * 0.19 + lakeSeed * 0.02, r * 0.17 - lakeSeed * 0.02, lakeSeed + 107);
        var w = 0.5 + 0.5 * (a * 0.4 + b * 0.35 + cN * 0.25);
        return Math.Clamp(w, 0.4, 1.75);
    }

    private static void ApplyLakeBowlOrganic(
        double[,] h,
        int cols,
        int rows,
        int lc,
        int lr,
        double meanRadiusCells,
        double drop,
        int lakeSeed)
    {
        var maxD = meanRadiusCells * 1.85 + 3;
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                var d = LakeDistance(c, r, lc, lr);
                if (d > maxD)
                    continue;
                var ang = Math.Atan2(r - lr, c - lc);
                var rEdge = meanRadiusCells * LakeRadiusWobble(ang, c, r, lakeSeed);
                if (d > rEdge + 1.2)
                    continue;
                var t = 1 - Math.Clamp(d / Math.Max(0.35, rEdge), 0, 1);
                var w = t * t * (3 - 2 * t);
                h[c, r] -= drop * w;
            }
        }
    }

    private static List<GeoVec2> OrganicLakeShorelinePolygon(double cx, double cy, double meanRadiusWorld, int segments,
        int seed)
    {
        var list = new List<GeoVec2>(segments);
        for (var i = 0; i < segments; i++)
        {
            var t = 2 * Math.PI * i / segments;
            var w = LakeRadiusWobble(t, i * 3 + seed % 97, -i * 2 + seed % 89, seed + i * 17);
            var rad = meanRadiusWorld * w;
            list.Add(new GeoVec2 { X = cx + rad * Math.Cos(t), Y = cy + rad * Math.Sin(t) });
        }

        return list;
    }

    private static void ApplyCoastalShelfLowering(double[,] h, int cols, int rows, ProceduralWorldSpec spec,
        double cell, bool[,] isOcean)
    {
        var width = Math.Max(1, spec.CoastalShelfCells);
        var dist = GridDistanceToOcean(isOcean, cols, rows);
        var drop = spec.TerrainAmplitude * 0.14 + spec.RiverBedCarve * 0.35;
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (isOcean[c, r])
                return;
            var d = dist[c, r];
            if (d > width + 2)
                return;
            var t = 1.0 - Math.Clamp(d / (width + 1.5), 0, 1);
            var w = t * t * (3 - 2 * t);
            h[c, r] -= drop * w;
        });
    }

    private static int[,] GridDistanceToOcean(bool[,] isOcean, int cols, int rows)
    {
        var dist = new int[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                dist[c, r] = isOcean[c, r] ? 0 : 1_000_000;
        var q = new Queue<(int c, int r)>();
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (isOcean[c, r])
                    q.Enqueue((c, r));
            }
        }

        while (q.Count > 0)
        {
            var (c, r) = q.Dequeue();
            var d0 = dist[c, r];
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nc = c + dc;
                var nr = r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                if (isOcean[nc, nr])
                    continue;
                if (dist[nc, nr] <= d0 + 1)
                    continue;
                dist[nc, nr] = d0 + 1;
                q.Enqueue((nc, nr));
            }
        }

        return dist;
    }

    /// <summary>
    /// Gentle river mouths near the ocean: extra distributaries and striped sand / slight bar relief.
    /// </summary>
    private static void ApplyCoastalRiverDeltas(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        Random rng,
        bool[,] isOcean,
        int[,] lakeId,
        bool[,] isRiver,
        List<List<(int c, int r)>> riverPaths,
        bool[,] deltaSandMask)
    {
        var distOcean = GridDistanceToOcean(isOcean, cols, rows);
        var gentle = Math.Max(spec.MaxLandStepOrthogonal * 0.26, spec.TerrainAmplitude * 0.0145);
        var halfNarrow = Math.Max(1, (int)Math.Floor(spec.RiverChannelHalfWidthWorld * 0.38 / cell));
        var stripeSeed = spec.Seed ^ 0x4D656C74;
        var snapshot = riverPaths.ToArray();

        foreach (var path in snapshot)
        {
            if (path.Count < 10)
                continue;

            var lastLand = -1;
            for (var i = path.Count - 1; i >= 0; i--)
            {
                if (!isOcean[path[i].c, path[i].r])
                {
                    lastLand = i;
                    break;
                }
            }

            if (lastLand < 8)
                continue;

            var touchesOceanAhead = false;
            for (var j = lastLand; j < path.Count && j <= lastLand + 8; j++)
            {
                if (isOcean[path[j].c, path[j].r])
                {
                    touchesOceanAhead = true;
                    break;
                }
            }

            if (!touchesOceanAhead)
                continue;

            var lookback = Math.Min(14, Math.Max(6, lastLand / 2));
            var tailStart = Math.Max(1, lastLand - lookback);
            var steps = 0;
            double sumDrop = 0;
            for (var i = tailStart; i < lastLand; i++)
            {
                var a = path[i];
                var b = path[i + 1];
                sumDrop += Math.Abs(h[b.c, b.r] - h[a.c, a.r]);
                steps++;
            }

            if (steps == 0 || sumDrop / steps > gentle)
                continue;

            var forkIdx = tailStart + (lastLand - tailStart) / 2;
            forkIdx = Math.Clamp(forkIdx, 2, lastLand - 2);
            var (fc, fr) = path[forkIdx];
            if (isOcean[fc, fr])
                continue;

            var branchCount = 2 + rng.Next(0, 2);
            if (rng.NextDouble() > 0.52)
                branchCount++;
            branchCount = Math.Clamp(branchCount, 2, 5);

            for (var b = 0; b < branchCount; b++)
            {
                var sc = Math.Clamp(fc + rng.Next(-1, 2) + (b % 2 == 0 ? -1 : 1), 1, cols - 2);
                var sr = Math.Clamp(fr + rng.Next(-1, 2), 1, rows - 2);
                if (isOcean[sc, sr] || lakeId[sc, sr] != 0)
                    continue;
                var distributary = BuildDistributaryTowardOcean(h, isOcean, lakeId, cols, rows, (sc, sr), distOcean,
                    rng, cell, spec, stripeSeed + b * 9973);
                if (distributary is null || distributary.Count < 2)
                    continue;
                riverPaths.Add(distributary);
                var block = new bool[cols, rows];
                for (var c = 0; c < cols; c++)
                {
                    for (var r = 0; r < rows; r++)
                        block[c, r] = isOcean[c, r] || lakeId[c, r] != 0;
                }

                StampRiverCorridor(distributary, halfNarrow, cols, rows, block, isRiver);
            }

            ApplyDeltaSandbarStripes(h, isOcean, isRiver, lakeId, cols, rows, fc, fr, distOcean, rng, cell, spec,
                deltaSandMask, stripeSeed + forkIdx * 131);
        }
    }

    private static List<(int c, int r)>? BuildDistributaryTowardOcean(
        double[,] h,
        bool[,] isOcean,
        int[,] lakeId,
        int cols,
        int rows,
        (int c, int r) start,
        int[,] distOcean,
        Random rng,
        double cell,
        ProceduralWorldSpec spec,
        int pathSeed)
    {
        if (isOcean[start.c, start.r] || lakeId[start.c, start.r] != 0)
            return null;

        var path = new List<(int c, int r)>();
        var cur = start;
        var seen = new HashSet<(int, int)> { cur };
        var maxSteps = 5 + rng.Next(0, 11);
        for (var s = 0; s < maxSteps; s++)
        {
            if (isOcean[cur.c, cur.r])
                break;
            path.Add(cur);
            var candidates = new List<((int nc, int nr) cell, double w)>();
            for (var d = 0; d < 8; d++)
            {
                var dc = d % 3 - 1;
                var dr = d / 3 - 1;
                if (dc == 0 && dr == 0)
                    continue;
                var nc = cur.c + dc;
                var nr = cur.r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                if (lakeId[nc, nr] != 0)
                    continue;
                if (seen.Contains((nc, nr)))
                    continue;
                var dOc = distOcean[nc, nr];
                var dh = h[nc, nr] - h[cur.c, cur.r];
                if (!isOcean[nc, nr] && dh > cell * 0.095)
                    continue;
                var nh = SmoothNoise(nc * 0.31 + pathSeed * 0.001, nr * 0.29, pathSeed) - 0.5;
                var w = 12.0 / (1 + dOc) + (isOcean[nc, nr] ? 8.5 : 0) + nh * 0.85;
                if (dh < 0)
                    w *= 1.42;
                w *= 0.75 + rng.NextDouble() * 0.55;
                candidates.Add(((nc, nr), Math.Max(0.02, w)));
            }

            if (candidates.Count == 0)
                break;
            double total = 0;
            foreach (var t in candidates)
                total += t.w;
            var roll = rng.NextDouble() * total;
            (int nc, int nr) next = candidates[^1].cell;
            foreach (var (cellT, w) in candidates)
            {
                roll -= w;
                if (roll <= 0)
                {
                    next = cellT;
                    break;
                }
            }

            if (seen.Contains(next))
                break;
            seen.Add(next);
            cur = next;
        }

        return path.Count >= 2 ? path : null;
    }

    private static void ApplyDeltaSandbarStripes(
        double[,] h,
        bool[,] isOcean,
        bool[,] isRiver,
        int[,] lakeId,
        int cols,
        int rows,
        int fc,
        int fr,
        int[,] distOcean,
        Random rng,
        double cell,
        ProceduralWorldSpec spec,
        bool[,] deltaSandMask,
        int stripeSeed)
    {
        var R = 9 + rng.Next(0, 7);
        for (var c = Math.Max(0, fc - R); c < Math.Min(cols, fc + R + 1); c++)
        {
            for (var r = Math.Max(0, fr - R); r < Math.Min(rows, fr + R + 1); r++)
            {
                if (isOcean[c, r] || isRiver[c, r] || lakeId[c, r] != 0)
                    continue;
                if (distOcean[c, r] > 24)
                    continue;
                var stripe = SmoothNoise(c * 0.41 + fr * 0.02, r * 0.39 + fc * 0.02, stripeSeed);
                var band = Math.Abs(stripe - 0.5);
                var sandChance = band < 0.14 ? 0.58 : 0.26;
                if (rng.NextDouble() > sandChance)
                    continue;
                deltaSandMask[c, r] = true;
                h[c, r] += spec.TerrainAmplitude * (0.0035 + rng.NextDouble() * 0.007);
            }
        }
    }

    private static int PlaceMajorRiversToOcean(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        Random rng,
        bool[,] isOcean,
        int[,] lakeId,
        bool[,] isRiver,
        List<List<(int c, int r)>> riverPaths,
        double[,]? terrainRuggedness = null)
    {
        var want = PickMajorRiverCount(spec, rng);
        var starts = PickRiverHeadwaters(h, cols, rows, isOcean, lakeId, rng, want, terrainRuggedness);
        var placed = 0;
        foreach (var start in starts)
        {
            var allowMerge = placed > 0;
            var path = FindRiverPathToOcean(h, cols, rows, start, isOcean, lakeId, spec.UphillPathPenalty, cell, spec,
                allowMerge, isRiver);
            if (path.Count < 2)
                continue;
            path = WiggleRiverPath(path, cols, rows, rng, isOcean, lakeId, terrainRuggedness);
            riverPaths.Add(path);
            var half = Math.Max(1, (int)Math.Floor(spec.RiverChannelHalfWidthWorld / cell));
            var block = new bool[cols, rows];
            for (var c = 0; c < cols; c++)
                for (var r = 0; r < rows; r++)
                    block[c, r] = isOcean[c, r] || lakeId[c, r] != 0;
            StampRiverCorridor(path, half, cols, rows, block, isRiver);
            placed++;
        }

        return placed;
    }

    private static List<(int c, int r)> PickRiverHeadwaters(
        double[,] h,
        int cols,
        int rows,
        bool[,] isOcean,
        int[,] lakeId,
        Random rng,
        int count,
        double[,]? terrainRuggedness = null)
    {
        var land = new List<(int c, int r, double score)>();
        var margin = Math.Max(2, Math.Min(cols, rows) / 10);
        for (var c = margin; c < cols - margin; c++)
        {
            for (var r = margin; r < rows - margin; r++)
            {
                if (isOcean[c, r] || lakeId[c, r] != 0)
                    continue;
                var R = terrainRuggedness != null ? terrainRuggedness[c, r] : 0.35;
                var score = h[c, r] * (0.5 + 0.92 * R);
                land.Add((c, r, score));
            }
        }

        land.Sort((a, b) => b.score.CompareTo(a.score));
        var picks = new List<(int c, int r)>();
        var minSep = Math.Min(cols, rows) / (3.2 + count);
        var minSepSq = minSep * minSep;
        foreach (var t in land)
        {
            if (picks.Count >= count)
                break;
            if (picks.Any(p =>
                {
                    var dx = p.c - t.c;
                    var dr = p.r - t.r;
                    return dx * dx + dr * dr < minSepSq;
                }))
                continue;
            picks.Add((t.c, t.r));
        }

        for (var extra = 0; picks.Count < count && extra < land.Count * 2; extra++)
        {
            var t = land[rng.Next(land.Count)];
            if (picks.Any(p => p.c == t.c && p.r == t.r))
                continue;
            if (picks.Any(p =>
                {
                    var dx = p.c - t.c;
                    var dr = p.r - t.r;
                    return dx * dx + dr * dr < minSepSq * 0.45;
                }))
                continue;
            picks.Add((t.c, t.r));
        }

        return picks;
    }

    private static List<(int c, int r)> FindRiverPathToOcean(
        double[,] h,
        int cols,
        int rows,
        (int c, int r) start,
        bool[,] isOcean,
        int[,] lakeId,
        double uphillPenalty,
        double cellSize,
        ProceduralWorldSpec spec,
        bool allowMergeIntoExistingMajorRiver,
        bool[,] existingMajorRiver)
    {
        var dist = new double[cols, rows];
        var parent = new (int c, int r)[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                dist[c, r] = double.PositiveInfinity;

        if (isOcean[start.c, start.r])
            return [];

        dist[start.c, start.r] = 0;
        parent[start.c, start.r] = (-1, -1);
        var pq = new PriorityQueue<(int c, int r), double>();
        pq.Enqueue(start, 0);
        var meander = Math.Max(0, spec.HydrologyMeanderNoise);

        (int c, int r)? goal = null;
        while (pq.TryDequeue(out var u, out var du))
        {
            if (du > dist[u.c, u.r] + 1e-9)
                continue;
            if (isOcean[u.c, u.r])
            {
                goal = u;
                break;
            }

            if (allowMergeIntoExistingMajorRiver && existingMajorRiver[u.c, u.r])
            {
                goal = u;
                break;
            }

            for (var d = 0; d < 8; d++)
            {
                var dc = d % 3 - 1;
                var dr = d / 3 - 1;
                if (dc == 0 && dr == 0)
                    continue;
                var vc = u.c + dc;
                var vr = u.r + dr;
                if ((uint)vc >= (uint)cols || (uint)vr >= (uint)rows)
                    continue;
                if (!isOcean[vc, vr] && lakeId[vc, vr] != 0)
                    continue;

                var step = Math.Abs(dc) + Math.Abs(dr) == 2 ? Math.Sqrt(2) : 1.0;
                var dh = h[vc, vr] - h[u.c, u.r];
                var edge = step * cellSize * (1 + Math.Max(0, dh) * uphillPenalty + Math.Max(0, -dh) * 0.04);
                if (meander > 1e-8)
                {
                    var nh = SmoothNoise(vc * 0.37 + 1.9, vr * 0.35 - 1.1, spec.Seed + 424242) - 0.5;
                    edge += meander * cellSize * nh * 2.0;
                }

                var alt = du + edge;
                if (alt < dist[vc, vr])
                {
                    dist[vc, vr] = alt;
                    parent[vc, vr] = u;
                    pq.Enqueue((vc, vr), alt);
                }
            }
        }

        if (goal is null)
            return [];

        var path = new List<(int c, int r)>();
        var cur = goal.Value;
        while (cur.c >= 0)
        {
            path.Add(cur);
            var p = parent[cur.c, cur.r];
            if (p.c < 0)
                break;
            cur = p;
        }

        path.Reverse();
        return path;
    }

    private static List<(int c, int r)> WiggleRiverPath(
        List<(int c, int r)> path,
        int cols,
        int rows,
        Random rng,
        bool[,] isOcean,
        int[,] lakeId,
        double[,]? terrainRuggedness = null)
    {
        if (path.Count < 4)
            return path;
        var outPath = new List<(int c, int r)> { path[0] };
        for (var i = 1; i < path.Count - 1; i++)
        {
            var cur = path[i];
            outPath.Add(cur);
            var rv = terrainRuggedness != null ? terrainRuggedness[cur.c, cur.r] : 0.35;
            var wiggleChance = 0.26 + rv * 0.48;
            if (rng.NextDouble() > wiggleChance)
                continue;
            var prev = outPath[^2];
            var next = path[i + 1];
            var dc = next.c - cur.c;
            var dr = next.r - cur.r;
            var pc = -dr;
            var pr = dc;
            if (rng.NextDouble() < 0.5)
            {
                pc = dr;
                pr = -dc;
            }

            var sc = cur.c + pc;
            var sr = cur.r + pr;
            if ((uint)sc >= (uint)cols || (uint)sr >= (uint)rows || isOcean[sc, sr])
                continue;
            if (lakeId[sc, sr] != 0 && lakeId[sc, sr] != lakeId[cur.c, cur.r])
                continue;
            if (Math.Abs(sc - prev.c) + Math.Abs(sr - prev.r) > 2)
                continue;
            outPath.Add((sc, sr));
        }

        outPath.Add(path[^1]);
        return outPath;
    }

    private static bool CellTouchesRiver(bool[,] isRiver, int cols, int rows, int c, int r)
    {
        if (isRiver[c, r])
            return true;
        for (var d = 0; d < 8; d++)
        {
            var dc = d % 3 - 1;
            var dr = d / 3 - 1;
            if (dc == 0 && dr == 0)
                continue;
            var nc = c + dc;
            var nr = r + dr;
            if ((uint)nc < (uint)cols && (uint)nr < (uint)rows && isRiver[nc, nr])
                return true;
        }

        return false;
    }

    private static List<(int c, int r)>? BuildTributaryPathToRiver(
        double[,] h,
        int cols,
        int rows,
        double cell,
        (int c, int r) start,
        bool[,] isOcean,
        int[,] lakeId,
        bool[,] isRiver,
        Random rng)
    {
        if (isOcean[start.c, start.r] || lakeId[start.c, start.r] != 0 || isRiver[start.c, start.r])
            return null;

        var path = new List<(int c, int r)> { start };
        var cur = start;
        var uphillAllow = Math.Max(0.2, cell * 0.065);
        var temp = Math.Max(5.5, cell * 0.48);
        var maxSteps = Math.Min(cols * rows, 9000);
        var seen = new HashSet<(int, int)> { start };

        for (var step = 0; step < maxSteps; step++)
        {
            if (CellTouchesRiver(isRiver, cols, rows, cur.c, cur.r))
                return path;

            var candidates = new List<((int nc, int nr) cell, double w)>();
            for (var d = 0; d < 8; d++)
            {
                var dc = d % 3 - 1;
                var dr = d / 3 - 1;
                if (dc == 0 && dr == 0)
                    continue;
                var nc = cur.c + dc;
                var nr = cur.r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                if (isOcean[nc, nr] || lakeId[nc, nr] != 0)
                    continue;

                if (isRiver[nc, nr])
                {
                    candidates.Add(((nc, nr), 520.0));
                    continue;
                }

                var dh = h[nc, nr] - h[cur.c, cur.r];
                if (dh > uphillAllow)
                    continue;
                var w = Math.Exp(-Math.Max(0, dh) / temp) * (0.52 + rng.NextDouble());
                if (dh <= 0.025)
                    w *= 2.05;
                candidates.Add(((nc, nr), w));
            }

            if (candidates.Count == 0)
                return null;

            double total = 0;
            foreach (var t in candidates)
                total += t.w;
            var roll = rng.NextDouble() * total;
            (int nc, int nr) next = candidates[^1].cell;
            foreach (var (cellT, w) in candidates)
            {
                roll -= w;
                if (roll <= 0)
                {
                    next = cellT;
                    break;
                }
            }

            if (seen.Contains(next))
                return null;
            seen.Add(next);
            path.Add(next);
            cur = next;
        }

        return null;
    }

    private static void PlaceTributaryStreams(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        Random rng,
        bool[,] isOcean,
        int[,] lakeId,
        bool[,] isRiver,
        List<List<(int c, int r)>> riverPaths)
    {
        var want = Math.Max(0, spec.TributaryStreamCount);
        if (want == 0)
            return;

        var elevations = new List<(int c, int r, double z)>();
        for (var c = 2; c < cols - 2; c++)
        {
            for (var r = 2; r < rows - 2; r++)
            {
                if (isOcean[c, r] || lakeId[c, r] != 0 || isRiver[c, r])
                    continue;
                elevations.Add((c, r, h[c, r]));
            }
        }

        if (elevations.Count < 12)
            return;

        elevations.Sort((a, b) => a.z.CompareTo(b.z));
        var i0 = Math.Clamp((int)(elevations.Count * 0.2), 0, elevations.Count - 2);
        var i1 = Math.Clamp((int)(elevations.Count * 0.78), i0 + 1, elevations.Count - 1);

        var halfTrib = Math.Max(1, (int)Math.Floor(spec.RiverChannelHalfWidthWorld * 0.42 / cell));

        for (var k = 0; k < want; k++)
        {
            var idx = rng.Next(i0, i1 + 1);
            var (sc, sr, _) = elevations[idx];
            var path = BuildTributaryPathToRiver(h, cols, rows, cell, (sc, sr), isOcean, lakeId, isRiver, rng);
            if (path is null || path.Count < 3)
                continue;

            var block = new bool[cols, rows];
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                    block[c, r] = isOcean[c, r] || lakeId[c, r] != 0;
            }

            StampRiverCorridor(path, halfTrib, cols, rows, block, isRiver);
            riverPaths.Add(path);
        }
    }

    private static void ConnectLakesToNearestRiver(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        bool[,] isOcean,
        int[,] lakeId,
        bool[,] isRiver,
        List<List<(int c, int r)>> riverPaths)
    {
        var maxId = 0;
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                maxId = Math.Max(maxId, lakeId[c, r]);

        for (var id = 1; id <= maxId; id++)
        {
            var lakeCells = new List<(int c, int r)>();
            for (var c = 0; c < cols; c++)
                for (var r = 0; r < rows; r++)
                    if (lakeId[c, r] == id)
                        lakeCells.Add((c, r));
            if (lakeCells.Count == 0)
                continue;
            var touchesRiver = lakeCells.Any(p => isRiver[p.c, p.r]);
            if (touchesRiver)
                continue;
            (int c, int r)? lakePt = null;
            (int c, int r)? riverPt = null;
            var best = double.PositiveInfinity;
            foreach (var lp in lakeCells)
            {
                for (var c = 0; c < cols; c++)
                {
                    for (var r = 0; r < rows; r++)
                    {
                        if (!isRiver[c, r])
                            continue;
                        var dx = (lp.c - c) * cell;
                        var dy = (lp.r - r) * cell;
                        var d = dx * dx + dy * dy;
                        if (d < best)
                        {
                            best = d;
                            lakePt = lp;
                            riverPt = (c, r);
                        }
                    }
                }
            }

            if (lakePt is null || riverPt is null)
                continue;
            var connector = StraightLinePath(lakePt.Value, riverPt.Value);
            var half = Math.Max(1, (int)Math.Floor(spec.RiverChannelHalfWidthWorld * 0.45 / cell));
            var block = new bool[cols, rows];
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                    block[c, r] = isOcean[c, r] || (lakeId[c, r] != 0 && lakeId[c, r] != id);
            }

            StampRiverCorridor(connector, half, cols, rows, block, isRiver);
            riverPaths.Add(connector);
        }
    }

    private static double EstimateOceanSurfaceZ(double[,] h, bool[,] isOcean, int cols, int rows)
    {
        var samples = new List<double>();
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!isOcean[c, r])
                    continue;
                foreach (var (dc, dr) in orth)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || isOcean[nc, nr])
                        continue;
                    samples.Add(h[nc, nr]);
                }
            }
        }

        if (samples.Count == 0)
        {
            var min = double.PositiveInfinity;
            for (var c = 0; c < cols; c++)
                for (var r = 0; r < rows; r++)
                    if (!isOcean[c, r])
                        min = Math.Min(min, h[c, r]);
            return min + 2;
        }

        return samples.Min() - Math.Max(6, samples.Min() * 0.004 + 4);
    }

    private static void CarveOceanFloor(double[,] h, int cols, int rows, bool[,] isOcean, double seaZ, double depth)
    {
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (isOcean[c, r])
                h[c, r] = Math.Min(h[c, r], seaZ - depth);
        });
    }

    private static Dictionary<int, double> ComputeLakeWaterLevels(double[,] h, int[,] lakeId, int cols, int rows)
    {
        var dict = new Dictionary<int, double>();
        var maxId = 0;
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                maxId = Math.Max(maxId, lakeId[c, r]);
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var id = 1; id <= maxId; id++)
        {
            var ring = new List<double>();
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    if (lakeId[c, r] != id)
                        continue;
                    foreach (var (dc, dr) in orth)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc < (uint)cols && (uint)nr < (uint)rows && lakeId[nc, nr] != id)
                            ring.Add(h[nc, nr]);
                    }
                }
            }

            dict[id] = ring.Count > 0 ? ring.Average() - 0.35 : 0;
        }

        return dict;
    }

    private static void CarveLakeBedForId(double[,] h, int cols, int rows, int[,] lakeId, int id, double waterZ,
        double depth)
    {
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (lakeId[c, r] == id)
                h[c, r] = Math.Min(h[c, r], waterZ - depth);
        });
    }

    private static void FinalizeCoastalWaterSurfaces(
        double[,] h,
        int cols,
        int rows,
        bool[,] isOcean,
        bool[,] isLake,
        bool[,] isRiver,
        int[,] lakeId,
        Dictionary<int, double> lakeLevels,
        double oceanWaterZ,
        List<List<(int c, int r)>> riverPaths,
        double cell,
        ProceduralWorldSpec spec)
    {
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (isOcean[c, r])
                h[c, r] = oceanWaterZ;
        });

        ParallelForCols(cols, rows, (c, r) =>
        {
            var id = lakeId[c, r];
            if (id <= 0 || !lakeLevels.TryGetValue(id, out var lz))
                return;
            h[c, r] = lz;
        });

        foreach (var path in riverPaths)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var t = path.Count > 1 ? i / (double)(path.Count - 1) : 1;
                var zSurf = oceanWaterZ + (1 - t) * Math.Max(0.55, spec.TerrainAmplitude * 0.07);
                var (c, r) = path[i];
                if (isRiver[c, r] && !isOcean[c, r] && lakeId[c, r] == 0)
                    h[c, r] = zSurf;
            }
        }

        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!isRiver[c, r] || isOcean[c, r] || lakeId[c, r] != 0)
                    continue;
                for (var dc = -1; dc <= 1; dc++)
                {
                    for (var dr = -1; dr <= 1; dr++)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                            continue;
                        if (isRiver[nc, nr] && h[nc, nr] > h[c, r] - 0.01)
                            h[nc, nr] = Math.Min(h[nc, nr], h[c, r] - 0.02);
                    }
                }
            }
        }
    }

    private static List<PhysicalTerrainFeature> BuildCoastalFeatures(
        ProceduralWorldSpec spec,
        List<List<(int c, int r)>> riverPaths,
        double cell,
        int[,] lakeId,
        Dictionary<int, double> lakeLevels,
        double oceanWaterZ,
        GeoVec2 down)
    {
        var features = new List<PhysicalTerrainFeature>();
        for (var ri = 0; ri < riverPaths.Count; ri++)
        {
            var path = riverPaths[ri];
            var line = PathToWorldPolyline(spec, path, cell);
            if (line.Count < 2)
                continue;
            GeoVec2 flowDir;
            if (line.Count >= 2)
            {
                var a = line[^2];
                var b = line[^1];
                flowDir = NormalizeFlow(new GeoVec2 { X = b.X - a.X, Y = b.Y - a.Y });
            }
            else
                flowDir = down;

            features.Add(new FlowingWaterFeature
            {
                Id = $"feat.gen.river_{ri:000}",
                LayerPriority = 4,
                ChannelCenterline = line,
                ChannelHalfWidth = spec.RiverChannelHalfWidthWorld,
                WaterSurfaceZ = oceanWaterZ + 0.25,
                FlowDirection = flowDir,
                Medium = PhysicalMedium.FluidFlowing,
            });
        }

        var maxLakeId = 0;
        for (var c = 0; c < lakeId.GetLength(0); c++)
            for (var r = 0; r < lakeId.GetLength(1); r++)
                maxLakeId = Math.Max(maxLakeId, lakeId[c, r]);

        for (var id = 1; id <= maxLakeId; id++)
        {
            if (!lakeLevels.TryGetValue(id, out var lw))
                continue;
            double cx = 0, cy = 0;
            var n = 0;
            for (var c = 0; c < lakeId.GetLength(0); c++)
            {
                for (var r = 0; r < lakeId.GetLength(1); r++)
                {
                    if (lakeId[c, r] != id)
                        continue;
                    cx += spec.MinX + (c + 0.5) * cell;
                    cy += spec.MinY + (r + 0.5) * cell;
                    n++;
                }
            }

            if (n == 0)
                continue;
            cx /= n;
            cy /= n;
            var rad = EstimateLakeRadiusCells(lakeId, id) * cell * 0.92;
            features.Add(new StandingWaterFeature
            {
                Id = $"feat.gen.lake_{id:000}",
                LayerPriority = 3,
                Shoreline = OrganicLakeShorelinePolygon(cx, cy, Math.Max(rad, cell * 2.5), 34,
                    spec.Seed + id * 1699 + 1337),
                WaterSurfaceZ = lw,
                Depth = spec.LakeDepth,
                Medium = PhysicalMedium.FluidStanding,
            });
        }

        return features;
    }

    private static List<GeoVec2> PathToWorldPolyline(ProceduralWorldSpec spec, List<(int c, int r)> path, double cell)
    {
        var line = new List<GeoVec2>();
        var step = Math.Max(1, path.Count / 52);
        for (var i = 0; i < path.Count; i += step)
        {
            var (c, r) = path[i];
            line.Add(new GeoVec2 { X = spec.MinX + (c + 0.5) * cell, Y = spec.MinY + (r + 0.5) * cell });
        }

        var last = path[^1];
        var lwx = spec.MinX + (last.c + 0.5) * cell;
        var lwy = spec.MinY + (last.r + 0.5) * cell;
        if (line.Count == 0 || Math.Abs(line[^1].X - lwx) > 1e-3 || Math.Abs(line[^1].Y - lwy) > 1e-3)
            line.Add(new GeoVec2 { X = lwx, Y = lwy });
        return line;
    }

    private static double EstimateLakeRadiusCells(int[,] lakeId, int id)
    {
        var cx = 0.0;
        var cy = 0.0;
        var n = 0;
        var maxD = 0.0;
        for (var c = 0; c < lakeId.GetLength(0); c++)
        {
            for (var r = 0; r < lakeId.GetLength(1); r++)
            {
                if (lakeId[c, r] != id)
                    continue;
                cx += c;
                cy += r;
                n++;
            }
        }

        if (n == 0)
            return 3;
        cx /= n;
        cy /= n;
        for (var c = 0; c < lakeId.GetLength(0); c++)
        {
            for (var r = 0; r < lakeId.GetLength(1); r++)
            {
                if (lakeId[c, r] != id)
                    continue;
                var d = Math.Sqrt((c - cx) * (c - cx) + (r - cy) * (r - cy));
                maxD = Math.Max(maxD, d);
            }
        }

        return Math.Max(2, maxD);
    }
}
