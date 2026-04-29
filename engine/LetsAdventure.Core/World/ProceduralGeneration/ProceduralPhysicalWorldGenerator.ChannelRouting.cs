using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>
    /// Nearest river cell in grid indices (Euclidean on cell centers). Buckets + expanding rings with
    /// rectangle lower-bound culling so large grids avoid O(|lake|×|rivers|) scans.
    /// </summary>
    private sealed class RiverNearestIndex
    {
        private readonly int _bucket;
        private readonly int _cols;
        private readonly int _rows;
        private readonly int _bxMax;
        private readonly int _byMax;
        private readonly Dictionary<(int bx, int by), List<(int c, int r)>> _map;

        public RiverNearestIndex(int cols, int rows, List<(int c, int r)> rivers)
        {
            _cols = cols;
            _rows = rows;
            _bucket = Math.Clamp(Math.Max(32, Math.Max(cols, rows) / 40), 48, 220);
            _bxMax = Math.Max(0, (cols - 1) / _bucket);
            _byMax = Math.Max(0, (rows - 1) / _bucket);
            _map = new Dictionary<(int, int), List<(int, int)>>();
            foreach (var p in rivers)
            {
                var k = (p.c / _bucket, p.r / _bucket);
                if (!_map.TryGetValue(k, out var list))
                {
                    list = new List<(int, int)>(8);
                    _map[k] = list;
                }

                list.Add(p);
            }
        }

        /// <summary>Minimum squared Euclidean distance from grid point <paramref name="lc"/>,<paramref name="lr"/> to any cell in the bucket's cell rectangle (inclusive).</summary>
        private static double MinDistSqToBucketRect(int lc, int lr, int bx, int by, int bucket, int cols, int rows)
        {
            var c0 = bx * bucket;
            var c1 = Math.Min((bx + 1) * bucket - 1, cols - 1);
            var r0 = by * bucket;
            var r1 = Math.Min((by + 1) * bucket - 1, rows - 1);
            if (c1 < c0 || r1 < r0)
                return double.PositiveInfinity;
            var dx = 0.0;
            if (lc < c0)
                dx = lc - c0;
            else if (lc > c1)
                dx = lc - c1;
            var dy = 0.0;
            if (lr < r0)
                dy = lr - r0;
            else if (lr > r1)
                dy = lr - r1;
            return dx * dx + dy * dy;
        }

        /// <summary>Update best squared cell-distance and river cell if some river is closer to (lc,lr).</summary>
        public void QueryNearest(int lc, int lr, ref double bestD2, ref int bestRc, ref int bestRr)
        {
            var bc = lc / _bucket;
            var br = lr / _bucket;
            var maxRing = 2 + Math.Max(Math.Max(bc, _bxMax - bc), Math.Max(br, _byMax - br));
            for (var rad = 0; rad <= maxRing; rad++)
            {
                for (var dbc = -rad; dbc <= rad; dbc++)
                {
                    for (var dbr = -rad; dbr <= rad; dbr++)
                    {
                        if (rad > 0 && Math.Max(Math.Abs(dbc), Math.Abs(dbr)) != rad)
                            continue;
                        var bx = bc + dbc;
                        var by = br + dbr;
                        if (bx < 0 || by < 0 || bx > _bxMax || by > _byMax)
                            continue;
                        if (bestD2 < double.PositiveInfinity &&
                            MinDistSqToBucketRect(lc, lr, bx, by, _bucket, _cols, _rows) > bestD2)
                            continue;
                        if (!_map.TryGetValue((bx, by), out var list))
                            continue;
                        foreach (var (rc, rr) in list)
                        {
                            var dxc = lc - rc;
                            var dyr = lr - rr;
                            var d2 = (double)dxc * dxc + (double)dyr * dyr;
                            if (d2 < bestD2)
                            {
                                bestD2 = d2;
                                bestRc = rc;
                                bestRr = rr;
                            }
                        }
                    }
                }
            }
        }
    }

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
        var minW = Math.Clamp(Math.Max(0.35, spec.RiverChannelHalfWidthMinWorld), 0.35,
            RiverCorridorHalfWidthAbsoluteMaxM * 0.92);
        var maxW = spec.RiverChannelHalfWidthMaxWorld > 1e-6
            ? spec.RiverChannelHalfWidthMaxWorld
            : Math.Max(minW * 1.35, spec.RiverChannelHalfWidthWorld * 2.2);
        var cellCapM = cell * RiverCorridorHalfWidthCapCellMultiple;
        maxW = Math.Min(Math.Min(maxW, cellCapM), RiverCorridorHalfWidthAbsoluteMaxM);
        maxW = Math.Max(maxW, minW);
        var rain = Math.Max(1e-6, spec.HydrologyDesignRainfallM);
        var runoff = Math.Clamp(spec.HydrologyRunoffFraction, 0.04, 0.98);
        var scale = Math.Max(0.12, spec.HydrologyWidthCurveScale);
        var refHalf = Math.Max(minW, Math.Min(spec.RiverChannelHalfWidthWorld, RiverCorridorHalfWidthAbsoluteMaxM * 0.88));
        // Larger divisor → narrower typical widths for a given catchment (most channels stay 20–100 m across).
        var qRef = cell * cell * rain * runoff * 320;

        for (var i = 0; i < n; i++)
        {
            var (c, r) = path[i];
            var a = Math.Max(contributingAreaM2[c, r], cell * cell);
            var qStorm = rain * a * runoff;
            var s = PathStreamSlopeMpm(h, path, i, cell);
            var hw = minW + scale * refHalf * 0.36 * Math.Pow(1 + qStorm / Math.Max(qRef, 1e-9), 0.3)
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
        {
            for (var r = 0; r < rows; r++)
                maxId = Math.Max(maxId, lakeId[c, r]);
        }

        if (maxId < 1)
            return;

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Lake outlets: grouping lake cells by basin id…");

        var lakeCellsById = new List<List<(int c, int r)>>(maxId + 1);
        for (var i = 0; i <= maxId; i++)
            lakeCellsById.Add(new List<(int c, int r)>(32));

        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                var id = lakeId[c, r];
                if (id > 0)
                    lakeCellsById[id].Add((c, r));
            }
        }

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress,
                $"[coastal] Lake outlets: spatial index for {riverCells.Count:N0} river cells; connecting basins…");

        var riverIndex = new RiverNearestIndex(cols, rows, riverCells);
        var sw = Stopwatch.StartNew();
        var reportStep = Math.Max(1, maxId / 16);
        var nextReportMs = 2000L;
        var considered = 0;
        var connected = 0;

        for (var id = 1; id <= maxId; id++)
        {
            var lakeCells = lakeCellsById[id];
            if (lakeCells.Count == 0)
                continue;
            if (lakeCells.Any(p => isRiver[p.c, p.r]))
                continue;

            considered++;
            var nearestQueryCells = lakeCells;
            if (lakeCells.Count > 600)
            {
                var boundary = new List<(int c, int r)>(Math.Min(lakeCells.Count, cols + rows));
                foreach (var (c, r) in lakeCells)
                {
                    var edge = c == 0 || r == 0 || c + 1 >= cols || r + 1 >= rows
                               || lakeId[c - 1, r] != id
                               || lakeId[c + 1, r] != id
                               || lakeId[c, r - 1] != id
                               || lakeId[c, r + 1] != id;
                    if (edge)
                        boundary.Add((c, r));
                }

                if (boundary.Count > 0)
                    nearestQueryCells = boundary;
            }

            (int c, int r)? lakePt = null;
            (int c, int r)? riverPt = null;
            var best = double.PositiveInfinity;
            foreach (var lp in nearestQueryCells)
            {
                var d2 = double.PositiveInfinity;
                var rc = 0;
                var rr = 0;
                riverIndex.QueryNearest(lp.c, lp.r, ref d2, ref rc, ref rr);
                if (d2 < best)
                {
                    best = d2;
                    lakePt = lp;
                    riverPt = (rc, rr);
                }
            }

            if (ExpectLongRunningGridPhase(cols, rows) && progress is not null &&
                (id % reportStep == 0 || id == maxId || sw.ElapsedMilliseconds >= nextReportMs))
            {
                Report(progress,
                    $"[coastal] Lake outlets: basin ids {id}/{maxId} scanned ({considered} landlocked, {connected} outlets carved so far)…");
                nextReportMs = sw.ElapsedMilliseconds + 2000L;
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
            connected++;
        }

        if (ExpectLongRunningGridPhase(cols, rows) && progress is not null)
            Report(progress,
                $"[coastal] Lake outlets: finished ({connected} new outlets from {considered} landlocked basins).");
    }
}