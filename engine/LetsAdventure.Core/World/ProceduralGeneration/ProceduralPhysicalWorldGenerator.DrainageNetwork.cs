using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>
    /// D8 flow directions + flow accumulation on terrain (rivers not yet carved). Area draining through each land cell in m².
    /// </summary>
    private static DrainageField ComputeDrainageField(
        double[,] h,
        bool[,] isOcean,
        int cols,
        int rows,
        double cell,
        IProgress<string>? progress)
    {
        var cellArea = cell * cell;
        var downC = new int[cols, rows];
        var downR = new int[cols, rows];

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Drainage: D8 flow directions (parallel full grid)…");

        ParallelForCols(cols, rows, (c, r) =>
        {
            downC[c, r] = FlowDirNone;
            downR[c, r] = FlowDirNone;
            if (isOcean[c, r])
                return;

            var bestNc = -1;
            var bestNr = -1;
            var bestDrop = 0.0;
            for (var d = 0; d < 8; d++)
            {
                var dc = d % 3 - 1;
                var dr = d / 3 - 1;
                if (dc == 0 && dr == 0)
                    continue;
                var nc = c + dc;
                var nr = r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                var step = Math.Abs(dc) + Math.Abs(dr) == 2 ? Math.Sqrt(2) : 1.0;
                var drop = (h[c, r] - h[nc, nr]) / step;
                if (drop > bestDrop + 1e-9)
                {
                    bestDrop = drop;
                    bestNc = nc;
                    bestNr = nr;
                }
            }

            if (bestNc >= 0 && isOcean[bestNc, bestNr])
            {
                downC[c, r] = FlowDirOcean;
                downR[c, r] = FlowDirOcean;
                return;
            }

            if (bestNc < 0 || bestDrop <= 1e-12)
            {
                var minH = double.PositiveInfinity;
                for (var d = 0; d < 8; d++)
                {
                    var dc = d % 3 - 1;
                    var dr = d / 3 - 1;
                    if (dc == 0 && dr == 0)
                        continue;
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || isOcean[nc, nr])
                        continue;
                    if (h[nc, nr] < minH - 1e-9)
                    {
                        minH = h[nc, nr];
                        bestNc = nc;
                        bestNr = nr;
                    }
                }
            }

            if (bestNc < 0)
                return;
            if (isOcean[bestNc, bestNr])
            {
                downC[c, r] = FlowDirOcean;
                downR[c, r] = FlowDirOcean;
            }
            else
            {
                downC[c, r] = bestNc;
                downR[c, r] = bestNr;
            }
        });

        var colLandCount = new int[cols];
        Parallel.For(0, cols, c =>
        {
            var n = 0;
            for (var r = 0; r < rows; r++)
            {
                if (!isOcean[c, r])
                    n++;
            }

            colLandCount[c] = n;
        });

        var colStart = new int[cols + 1];
        for (var c = 0; c < cols; c++)
            colStart[c + 1] = colStart[c] + colLandCount[c];

        var landTotal = colStart[cols];
        var order = new (int c, int r, double z)[landTotal];

        Parallel.For(0, cols, c =>
        {
            var w = colStart[c];
            for (var r = 0; r < rows; r++)
            {
                if (isOcean[c, r])
                    continue;
                order[w] = (c, r, h[c, r]);
                w++;
            }
        });

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, $"[coastal] Drainage: sorting {landTotal:N0} land cells by elevation…");

        Array.Sort(order, 0, landTotal, Comparer<(int c, int r, double z)>.Create((a, b) => b.z.CompareTo(a.z)));

        var acc = new double[cols, rows];
        ParallelForCols(cols, rows, (c, r) => acc[c, r] = isOcean[c, r] ? 0 : cellArea);

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Drainage: accumulating upstream area (single downstream pass)…");

        for (var i = 0; i < landTotal; i++)
        {
            var (c, r, _) = order[i];
            var tc = downC[c, r];
            if (tc == FlowDirNone || tc == FlowDirOcean)
                continue;
            var tr = downR[c, r];
            acc[tc, tr] += acc[c, r];
        }

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Drainage accumulation finished.");

        return new DrainageField(acc, downC, downR);
    }

    private static bool LandChannelCell(
        double[,] acc,
        bool[,] isOcean,
        int[,] lakeId,
        int c,
        int r,
        double thresholdM2) =>
        !isOcean[c, r]
        && lakeId[c, r] == 0
        && acc[c, r] >= thresholdM2;

    private static bool IsChannelHeadwater(
        double[,] acc,
        bool[,] isOcean,
        int[,] lakeId,
        int[,] downC,
        int[,] downR,
        int cols,
        int rows,
        int c,
        int r,
        double thresholdM2)
    {
        if (!LandChannelCell(acc, isOcean, lakeId, c, r, thresholdM2))
            return false;
        for (var d = 0; d < 8; d++)
        {
            var dc = d % 3 - 1;
            var dr = d / 3 - 1;
            if (dc == 0 && dr == 0)
                continue;
            var nc = c + dc;
            var nr = r + dr;
            if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                continue;
            if (!LandChannelCell(acc, isOcean, lakeId, nc, nr, thresholdM2))
                continue;
            if (downC[nc, nr] == c && downR[nc, nr] == r)
                return false;
        }

        return true;
    }

    private static double MaxAccumOnPath(List<(int c, int r)> path, double[,] acc)
    {
        var m = 0.0;
        foreach (var (c, r) in path)
            m = Math.Max(m, acc[c, r]);
        return m;
    }

    private static List<(int c, int r)> TraceFlowPathToOutlet(
        int sc,
        int sr,
        int cols,
        int rows,
        bool[,] isOcean,
        int[,] lakeId,
        int[,] downC,
        int[,] downR)
    {
        var path = new List<(int c, int r)>();
        var seen = new HashSet<(int, int)>();
        (int c, int r) cur = (sc, sr);
        var guard = 0;
        while (guard++ < cols * rows + 8)
        {
            if (!seen.Add(cur))
                break;
            path.Add(cur);
            if (isOcean[cur.c, cur.r])
                break;
            var tc = downC[cur.c, cur.r];
            var tr = downR[cur.c, cur.r];
            if (tc == FlowDirOcean || tc == FlowDirNone)
                break;
            (int c, int r) next = (tc, tr);
            if ((uint)next.c >= (uint)cols || (uint)next.r >= (uint)rows)
                break;
            if (lakeId[next.c, next.r] != 0 && !isOcean[next.c, next.r])
                break;
            cur = next;
        }

        return path;
    }

    private static List<(int c, int r)> CollectChannelHeadwaters(
        double[,] acc,
        bool[,] isOcean,
        int[,] lakeId,
        int[,] downC,
        int[,] downR,
        int cols,
        int rows,
        double thresholdM2)
    {
        var list = new List<(int c, int r)>();
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!IsChannelHeadwater(acc, isOcean, lakeId, downC, downR, cols, rows, c, r, thresholdM2))
                    continue;
                list.Add((c, r));
            }
        }

        return list;
    }

    /// <summary>
    /// Dendritic channels from D8 flow: low-saddle headwaters trace downslope; confluences merge into larger rivers automatically.
    /// </summary>
    private static (int majorStemCount, int channelPaths) BuildAndStampDrainageRiverNetwork(
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
        double[,] contributingAreaM2,
        int[,] downC,
        int[,] downR,
        double[,] riverCellHalfWidthWorld,
        double[,]? terrainRuggedness,
        IProgress<string>? progress)
    {
        double maxAcc = 0;
        long landCells = 0;
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (isOcean[c, r])
                    continue;
                landCells++;
                maxAcc = Math.Max(maxAcc, contributingAreaM2[c, r]);
            }
        }

        if (landCells < 8 || maxAcc <= 0)
            return (0, 0);

        var cellArea = cell * cell;
        var floor = Math.Max(
            cellArea * 5.0 * Math.Max(0.25, spec.DrainageMinCatchmentAreaFactor),
            maxAcc * 0.0032 * Math.Max(0.12, spec.DrainageTributaryDensity));
        var maxHeadwaters = Math.Max(24,
            (int)(Math.Sqrt(landCells) * Math.Max(0.5, spec.DrainageHeadwaterBudgetFactor)));

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Drainage network: auto-calibrating tributary threshold…");

        var tLow = floor;
        var headwaters = CollectChannelHeadwaters(contributingAreaM2, isOcean, lakeId, downC, downR, cols, rows, tLow);
        for (var iter = 0; iter < 56 && headwaters.Count > maxHeadwaters; iter++)
        {
            tLow *= 1.065;
            headwaters = CollectChannelHeadwaters(contributingAreaM2, isOcean, lakeId, downC, downR, cols, rows, tLow);
        }

        if (headwaters.Count == 0)
        {
            tLow = Math.Min(floor, cellArea * 2.5);
            headwaters = CollectChannelHeadwaters(contributingAreaM2, isOcean, lakeId, downC, downR, cols, rows, tLow);
        }

        var frac = Math.Clamp(spec.DrainageMainStemAccumFraction, 0.018, 0.42);
        var tHigh = Math.Max(tLow * 5.5, maxAcc * frac);

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, $"[coastal] Drainage network: tracing {headwaters.Count} channel headwaters (parallel)…");

        var traced = new ConcurrentBag<List<(int c, int r)>>();
        Parallel.ForEach(headwaters, hw =>
        {
            var path = TraceFlowPathToOutlet(hw.c, hw.r, cols, rows, isOcean, lakeId, downC, downR);
            if (path.Count >= 2)
                traced.Add(path);
        });

        var pathList = traced.ToList();
        if (pathList.Count == 0)
            return (0, 0);

        var pathMaxes = new double[pathList.Count];
        var globalMaxPathAcc = 0.0;
        for (var i = 0; i < pathList.Count; i++)
        {
            pathMaxes[i] = MaxAccumOnPath(pathList[i], contributingAreaM2);
            globalMaxPathAcc = Math.Max(globalMaxPathAcc, pathMaxes[i]);
        }

        var tHighEff = tHigh;
        var majorCount = 0;
        for (var i = 0; i < pathList.Count; i++)
        {
            if (pathMaxes[i] >= tHighEff)
                majorCount++;
        }

        if (majorCount == 0 && globalMaxPathAcc > 0)
        {
            tHighEff = globalMaxPathAcc * 0.48;
            majorCount = 0;
            for (var i = 0; i < pathList.Count; i++)
            {
                if (pathMaxes[i] >= tHighEff)
                    majorCount++;
            }
        }

        var indexed = new List<(List<(int c, int r)> path, double pmax)>(pathList.Count);
        for (var i = 0; i < pathList.Count; i++)
            indexed.Add((pathList[i], pathMaxes[i]));
        indexed.Sort((a, b) => a.pmax.CompareTo(b.pmax));

        var block = new bool[cols, rows];
        ParallelForCols(cols, rows, (c, r) =>
            block[c, r] = isOcean[c, r] || lakeId[c, r] != 0);

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Drainage network: stamping channel corridors (narrow→wide)…");

        var stamped = 0;
        foreach (var (path, _) in indexed)
        {
            if (path.Count < 2)
                continue;
            var wiggle = WiggleRiverPath(path, cols, rows, rng, isOcean, lakeId, terrainRuggedness);
            var widths = ComputePathVertexHalfWidthsWorld(h, contributingAreaM2, wiggle, cell, spec);
            StampRiverCorridor(wiggle, widths, cols, rows, cell, block, isRiver, riverCellHalfWidthWorld);
            riverPaths.Add(wiggle);
            stamped++;
        }

        return (majorCount, stamped);
    }
}