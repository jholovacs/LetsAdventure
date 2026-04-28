using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>Along-stream slope |dh|/ds (m/m) at a path vertex from terrain heights.</summary>
    private static double PathStreamSlopeMpm(double[,] h, List<(int c, int r)> path, int i, double cell)
    {
        if (path.Count < 2)
            return 0.02;
        double dz, distWorld;
        if (i == 0)
        {
            var (c0, r0) = path[0];
            var (c1, r1) = path[1];
            dz = Math.Abs(h[c1, r1] - h[c0, r0]);
            distWorld = Math.Sqrt((c1 - c0) * (c1 - c0) + (r1 - r0) * (r1 - r0)) * cell;
        }
        else if (i == path.Count - 1)
        {
            var n = path.Count;
            var (c0, r0) = path[n - 2];
            var (c1, r1) = path[n - 1];
            dz = Math.Abs(h[c1, r1] - h[c0, r0]);
            distWorld = Math.Sqrt((c1 - c0) * (c1 - c0) + (r1 - r0) * (r1 - r0)) * cell;
        }
        else
        {
            var (ca, ra) = path[i - 1];
            var (cb, rb) = path[i + 1];
            dz = Math.Abs(h[cb, rb] - h[ca, ra]);
            distWorld = Math.Sqrt((cb - ca) * (cb - ca) + (rb - ra) * (rb - ra)) * cell;
        }

        var s = distWorld > 1e-9 ? dz / distWorld : 0.02;
        return Math.Clamp(s, 0.0001, 0.5);
    }

    /// <summary>
    /// Half-width (m) per vertex from storm discharge proxy (area × rain × runoff) and slope (steeper → narrower).
    /// </summary>
    private static double[] ComputePathVertexHalfWidthsWorld(
        double[,] h,
        double[,] contributingAreaM2,
        List<(int c, int r)> path,
        double cell,
        ProceduralWorldSpec spec)
    {
        var n = path.Count;
        var widths = new double[n];
        var minW = Math.Max(0.35, spec.RiverChannelHalfWidthMinWorld);
        var maxW = spec.RiverChannelHalfWidthMaxWorld > 1e-6
            ? spec.RiverChannelHalfWidthMaxWorld
            : Math.Max(minW * 1.5, spec.RiverChannelHalfWidthWorld * 2.75);
        var rain = Math.Max(1e-6, spec.HydrologyDesignRainfallM);
        var runoff = Math.Clamp(spec.HydrologyRunoffFraction, 0.04, 0.98);
        var scale = Math.Max(0.12, spec.HydrologyWidthCurveScale);
        var refHalf = Math.Max(minW, spec.RiverChannelHalfWidthWorld);
        var qRef = cell * cell * rain * runoff * 180;

        for (var i = 0; i < n; i++)
        {
            var (c, r) = path[i];
            var a = Math.Max(contributingAreaM2[c, r], cell * cell);
            var qStorm = rain * a * runoff;
            var s = PathStreamSlopeMpm(h, path, i, cell);
            var hw = minW + scale * refHalf * 0.52 * Math.Pow(1 + qStorm / Math.Max(qRef, 1e-9), 0.38)
                     / Math.Pow(Math.Max(s, 0.00008), 0.27);
            widths[i] = Math.Clamp(hw, minW, maxW);
        }

        return widths;
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

    private static void ConnectLakesToNearestRiver(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        bool[,] isOcean,
        int[,] lakeId,
        bool[,] isRiver,
        List<List<(int c, int r)>> riverPaths,
        double[,] contributingAreaM2,
        double[,] riverCellHalfWidthWorld,
        IProgress<string>? progress)
    {
        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Lake outlets: indexing river cells for fast nearest search…");

        var riverBag = new ConcurrentBag<(int c, int r)>();
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (!isRiver[c, r] || isOcean[c, r] || lakeId[c, r] != 0)
                return;
            riverBag.Add((c, r));
        });

        var riverCells = riverBag.ToList();
        if (riverCells.Count == 0)
            return;

        var maxId = 0;
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                maxId = Math.Max(maxId, lakeId[c, r]);

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Lake outlets: connecting basins to nearest river…");

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
                foreach (var (rc, rr) in riverCells)
                {
                    var dx = (lp.c - rc) * cell;
                    var dy = (lp.r - rr) * cell;
                    var d = dx * dx + dy * dy;
                    if (d < best)
                    {
                        best = d;
                        lakePt = lp;
                        riverPt = (rc, rr);
                    }
                }
            }

            if (lakePt is null || riverPt is null)
                continue;
            var connector = StraightLinePath(lakePt.Value, riverPt.Value);
            var widths = ComputePathVertexHalfWidthsWorld(h, contributingAreaM2, connector, cell, spec);
            for (var i = 0; i < widths.Length; i++)
                widths[i] *= 0.68;
            var block = new bool[cols, rows];
            ParallelForCols(cols, rows, (c, r) =>
                block[c, r] = isOcean[c, r] || (lakeId[c, r] != 0 && lakeId[c, r] != id));

            StampRiverCorridor(connector, widths, cols, rows, cell, block, isRiver, riverCellHalfWidthWorld);
            riverPaths.Add(connector);
        }
    }
}