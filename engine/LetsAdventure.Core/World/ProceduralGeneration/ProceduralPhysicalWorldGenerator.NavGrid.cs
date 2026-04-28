using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
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
        bool[,]? deltaSandPreferred,
        IProgress<string>? progress)
    {
        var orthDirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        var dryGrad = new double[cols, rows];

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Nav grid: dry-land gradient field (parallel)…");

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

        var cellsArr = new NavCellDefinition[cols * rows];
        var slopeLock = new object();
        var maxSlope = 0.0;
        var violations = 0;

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Nav grid: assembling walkability and composition (parallel)…");

        Parallel.For(0, rows, r =>
        {
            var localMax = 0.0;
            var localViol = 0;
            var baseIdx = r * cols;
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
                        localMax = Math.Max(localMax, dz);
                        var plateau =
                            dryGrad[c, r] <= maxWalk * 2.4 && dryGrad[nc, nr] <= maxWalk * 2.4;
                        if (dz > maxLandStep + 1e-4 && plateau)
                            localViol++;
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

                cellsArr[baseIdx + c] = new NavCellDefinition
                {
                    Walkable = walk,
                    ElevationZ = elev,
                    BedElevationZ = bed,
                    WaterSurfaceZ = waterSurfZ,
                    MovementCostMultiplier = water ? 2.5 : 1,
                    Composition = composition,
                    FluidDepth = fluidD,
                };
            }

            lock (slopeLock)
            {
                maxSlope = Math.Max(maxSlope, localMax);
                violations += localViol;
            }
        });

        report.MaxObservedLandOrthogonalSlope = maxSlope;
        report.LandSlopeViolationCount = violations / 2;
        if (violations > 0)
            report.Messages.Add(
                $"Land slope violations (orth. neighbors): {report.LandSlopeViolationCount} edges exceed {maxLandStep:F2}.");

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Navigation grid assembly finished.");

        return new TerrainNavGridDefinition
        {
            OriginX = spec.MinX,
            OriginY = spec.MinY,
            CellSize = cell,
            Columns = cols,
            Rows = rows,
            Cells = new List<NavCellDefinition>(cellsArr),
        };
    }
}