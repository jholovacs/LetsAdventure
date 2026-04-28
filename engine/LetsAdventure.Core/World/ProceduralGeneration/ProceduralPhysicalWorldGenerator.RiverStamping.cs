using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    private static double RiverPathWorldArcLength(List<(int c, int r)> path, ProceduralWorldSpec spec)
    {
        if (path.Count < 2)
            return 0;
        var cellSize = spec.CellSize;
        var len = 0.0;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var (c0, r0) = path[i];
            var (c1, r1) = path[i + 1];
            var dx = (c1 - c0) * cellSize;
            var dy = (r1 - r0) * cellSize;
            len += Math.Sqrt(dx * dx + dy * dy);
        }

        return len;
    }

    /// <summary>
    /// Carve the full stamped river mask (not just polyline vertices): thalweg lowest, smooth cross-profile to banks.
    /// </summary>
    private static void CarveAllRiverChannelBeds(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        List<List<(int c, int r)>> riverPaths,
        bool[,] isRiver,
        bool[,] isOcean,
        int[,] lakeId,
        double oceanWaterZ,
        double[,] riverCellHalfWidthWorld,
        IProgress<string>? progress)
    {
        if (riverPaths.Count == 0)
            return;

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Carving river channel beds (parallel; width-aware cross-section)…");

        var effCarve = spec.RiverBedCarve * 0.55;
        var fallbackHalfW = Math.Max(spec.RiverChannelHalfWidthWorld, cell * 0.5);

        var pathLens = new double[riverPaths.Count];
        for (var pi = 0; pi < riverPaths.Count; pi++)
            pathLens[pi] = RiverPathWorldArcLength(riverPaths[pi], spec);

        ParallelForCols(cols, rows, (c, r) =>
        {
            if (!isRiver[c, r] || isOcean[c, r] || lakeId[c, r] != 0)
                return;

            var halfW = riverCellHalfWidthWorld[c, r] > 1e-6
                ? riverCellHalfWidthWorld[c, r]
                : fallbackHalfW;
            var maxLateral = halfW + cell * 1.15;

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
                    bestLen = Math.Max(pathLens[pi], 1e-6);
                }
            }

            if (bestDist > maxLateral)
                return;

            var t = bestLen > 1e-9 ? Math.Clamp(bestAlong / bestLen, 0, 1) : 0;
            var longitudinal = oceanWaterZ + (1 - t) * spec.TerrainAmplitude * 0.32;
            var thalweg = longitudinal - effCarve;
            var bank = longitudinal - effCarve * 0.26;
            var u = Math.Clamp(bestDist / Math.Max(halfW, 1e-3), 0, 1);
            var cross = Math.Cos(0.5 * Math.PI * u);
            cross *= cross;
            var target = bank + (thalweg - bank) * cross;
            h[c, r] = Math.Min(h[c, r], target);
        });

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] River channel bed carve finished.");
    }

    /// <summary>Mild Laplacian-style smooth on river cells only (4-neighbors that are also river), reduces centerline ridges.</summary>
    private static void SmoothRiverOnlyHeights(
        double[,] h,
        int cols,
        int rows,
        bool[,] isRiver,
        bool[,] isOcean,
        int[,] lakeId,
        int passes,
        double alpha,
        IProgress<string>? progress)
    {
        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Smoothing river bathymetry (parallel passes)…");

        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var p = 0; p < passes; p++)
        {
            var copy = (double[,])h.Clone();
            ParallelForCols(cols, rows, (c, r) =>
            {
                if (!isRiver[c, r] || isOcean[c, r] || lakeId[c, r] != 0)
                    return;
                double s = 0;
                var n = 0;
                foreach (var (dc, dr) in orth)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                        continue;
                    if (!isRiver[nc, nr] || isOcean[nc, nr] || lakeId[nc, nr] != 0)
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

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] River bathymetry smoothing finished.");
    }

    /// <param name="blocking">Cells that must not receive river stamp (e.g. lakes, or other lakes only for outlets).</param>
    /// <param name="riverCellHalfWidthWorld">Per-cell max stamped half-width (world m); updated with Math.Max for overlaps.</param>
    private static void StampRiverCorridor(
        List<(int c, int r)> path,
        IReadOnlyList<double> halfWidthWorldPerVertex,
        int cols,
        int rows,
        double cell,
        bool[,] blocking,
        bool[,] isRiver,
        double[,] riverCellHalfWidthWorld)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var hw = halfWidthWorldPerVertex[i];
            if (hw < 1e-6)
                continue;
            var riverHalfCells = Math.Max(1, (int)Math.Floor(hw / cell));
            var (pc, pr) = path[i];
            for (var c = Math.Max(0, pc - riverHalfCells - 1); c < Math.Min(cols, pc + riverHalfCells + 2); c++)
            {
                for (var r = Math.Max(0, pr - riverHalfCells - 1); r < Math.Min(rows, pr + riverHalfCells + 2); r++)
                {
                    if (blocking[c, r])
                        continue;
                    if (Math.Sqrt((c - pc) * (c - pc) + (r - pr) * (r - pr)) <= riverHalfCells + 0.35)
                    {
                        isRiver[c, r] = true;
                        if (riverCellHalfWidthWorld[c, r] < hw)
                            riverCellHalfWidthWorld[c, r] = hw;
                    }
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
}