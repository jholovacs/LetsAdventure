using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
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
        ProceduralWorldSpec spec,
        IProgress<string>? progress)
    {
        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Water surfaces: applying ocean / lake / river Z (parallel)…");

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

        if (riverPaths.Count > 0)
        {
            var surfPathLens = new double[riverPaths.Count];
            for (var pi = 0; pi < riverPaths.Count; pi++)
                surfPathLens[pi] = RiverPathWorldArcLength(riverPaths[pi], spec);

            var fallbackHalfW = Math.Max(spec.RiverChannelHalfWidthWorld, cell * 0.5);
            var hwMax = spec.RiverChannelHalfWidthMaxWorld > 1e-6
                ? spec.RiverChannelHalfWidthMaxWorld
                : Math.Max(fallbackHalfW * 2.2, spec.RiverChannelHalfWidthMinWorld * 2.0);
            var cellCapM = cell * RiverCorridorHalfWidthCapCellMultiple;
            hwMax = Math.Min(Math.Min(hwMax, cellCapM), RiverCorridorHalfWidthAbsoluteMaxM);
            var expandCells = (int)Math.Ceiling(hwMax / Math.Max(cell, 1e-9)) + 8;

            if (!RiverChannelCarveVulkan.TryApplyRiverWaterSurfaceZ(h, cols, rows, cell, spec, riverPaths,
                    isRiver, isOcean, lakeId, oceanWaterZ, expandCells, surfPathLens, progress))
            {
                ParallelForCols(cols, rows, (c, r) =>
                {
                    if (!isRiver[c, r] || isOcean[c, r] || lakeId[c, r] != 0)
                        return;
                    var wx = spec.MinX + (c + 0.5) * cell;
                    var wy = spec.MinY + (r + 0.5) * cell;
                    var bestDist = double.PositiveInfinity;
                    var bestAlong = 0.0;
                    var bestLen = 1.0;
                    for (var pi = 0; pi < riverPaths.Count; pi++)
                    {
                        var path = riverPaths[pi];
                        if (path.Count == 0)
                            continue;
                        ClosestPointOnRiverPath(wx, wy, path, spec, out var d, out var sAlong);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestAlong = sAlong;
                            bestLen = Math.Max(surfPathLens[pi], 1e-6);
                        }
                    }

                    var tLong = bestLen > 1e-9 ? Math.Clamp(bestAlong / bestLen, 0, 1) : 1.0;
                    var zSurf = oceanWaterZ + (1 - tLong) * Math.Max(0.55, spec.TerrainAmplitude * 0.07);
                    h[c, r] = zSurf;
                });
            }
        }

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Water surfaces: river neighbor consistency (sequential pass)…");

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

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Water surface finalization finished.");
    }
}