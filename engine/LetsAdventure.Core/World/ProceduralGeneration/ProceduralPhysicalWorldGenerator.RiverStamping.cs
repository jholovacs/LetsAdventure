using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>
    /// Maps grid buckets to river path indices whose (expanded) bounding boxes touch the bucket.
    /// Avoids scanning every path at every river cell during channel bed carving.
    /// </summary>
    private sealed class RiverPathBucketIndex
    {
        private readonly int _bucketCells;
        private readonly int _bxMax;
        private readonly int _byMax;
        private readonly Dictionary<(int bx, int by), List<int>> _pathsByBucket = new();

        public RiverPathBucketIndex(
            int cols,
            int rows,
            List<List<(int c, int r)>> riverPaths,
            int expandCells)
        {
            _bucketCells = Math.Clamp(Math.Max(48, Math.Max(cols, rows) / 48), 56, 200);
            _bxMax = Math.Max(0, (cols - 1) / _bucketCells);
            _byMax = Math.Max(0, (rows - 1) / _bucketCells);
            var ex = Math.Max(0, expandCells);

            for (var pi = 0; pi < riverPaths.Count; pi++)
            {
                var path = riverPaths[pi];
                if (path.Count == 0)
                    continue;

                var c0 = int.MaxValue;
                var c1 = int.MinValue;
                var r0 = int.MaxValue;
                var r1 = int.MinValue;
                foreach (var (c, r) in path)
                {
                    c0 = Math.Min(c0, c);
                    c1 = Math.Max(c1, c);
                    r0 = Math.Min(r0, r);
                    r1 = Math.Max(r1, r);
                }

                c0 = Math.Clamp(c0 - ex, 0, cols - 1);
                c1 = Math.Clamp(c1 + ex, 0, cols - 1);
                r0 = Math.Clamp(r0 - ex, 0, rows - 1);
                r1 = Math.Clamp(r1 + ex, 0, rows - 1);

                var bc0 = c0 / _bucketCells;
                var bc1 = c1 / _bucketCells;
                var br0 = r0 / _bucketCells;
                var br1 = r1 / _bucketCells;

                for (var bx = bc0; bx <= bc1; bx++)
                {
                    for (var by = br0; by <= br1; by++)
                        Add(bx, by, pi);
                }
            }
        }

        private void Add(int bx, int by, int pi)
        {
            if (bx < 0 || by < 0 || bx > _bxMax || by > _byMax)
                return;
            var k = (bx, by);
            if (!_pathsByBucket.TryGetValue(k, out var list))
            {
                list = new List<int>(4);
                _pathsByBucket[k] = list;
            }

            list.Add(pi);
        }

        public int BucketCells => _bucketCells;

        public int MaxRing(int bc, int br) =>
            2 + Math.Max(Math.Max(bc, _bxMax - bc), Math.Max(br, _byMax - br));

        public void ForEachPathInRing(int bc, int br, int rad, Action<int> onPath)
        {
            void Visit(int bx, int by)
            {
                if (bx < 0 || by < 0 || bx > _bxMax || by > _byMax)
                    return;
                if (!_pathsByBucket.TryGetValue((bx, by), out var list))
                    return;
                for (var i = 0; i < list.Count; i++)
                    onPath(list[i]);
            }

            if (rad == 0)
            {
                Visit(bc, br);
                return;
            }

            for (var dbc = -rad; dbc <= rad; dbc++)
            {
                Visit(bc + dbc, br + rad);
                Visit(bc + dbc, br - rad);
            }

            for (var dbr = -rad + 1; dbr <= rad - 1; dbr++)
            {
                Visit(bc + rad, br + dbr);
                Visit(bc - rad, br + dbr);
            }
        }
    }

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

        var effCarve = spec.RiverBedCarve * 0.55;
        var fallbackHalfW = Math.Max(spec.RiverChannelHalfWidthWorld, cell * 0.5);
        var hwMax = spec.RiverChannelHalfWidthMaxWorld > 1e-6
            ? spec.RiverChannelHalfWidthMaxWorld
            : Math.Max(fallbackHalfW * 2.75, spec.RiverChannelHalfWidthMinWorld * 2.2);
        var expandCells = (int)Math.Ceiling(hwMax / Math.Max(cell, 1e-9)) + 8;

        var pathLens = new double[riverPaths.Count];
        for (var pi = 0; pi < riverPaths.Count; pi++)
            pathLens[pi] = RiverPathWorldArcLength(riverPaths[pi], spec);

        if (RiverChannelCarveVulkan.TryCarve(h, cols, rows, cell, spec, riverPaths, isRiver, isOcean, lakeId,
                oceanWaterZ, riverCellHalfWidthWorld, effCarve, fallbackHalfW, hwMax, expandCells, pathLens,
                progress))
            return;

        var report = ExpectLongRunningGridPhase(cols, rows) && progress != null;
        if (report)
            Report(progress, "[coastal] Carving river channel beds (parallel columns; spatial path index + cross-section)…");

        const int brutePathCap = 72;
        RiverPathBucketIndex? index = null;
        if (riverPaths.Count > brutePathCap)
            index = new RiverPathBucketIndex(cols, rows, riverPaths, expandCells);

        var nonEmptyRiverPaths = 0;
        for (var pi = 0; pi < riverPaths.Count; pi++)
        {
            if (riverPaths[pi].Count > 0)
                nonEmptyRiverPaths++;
        }

        using var pathTagTls = new ThreadLocal<long[]>(() => new long[riverPaths.Count]);
        var colReportStep = cols >= 384 ? Math.Max(1, cols / 12) : Math.Max(1, cols / 4);
        var sw = Stopwatch.StartNew();
        var nextReportMs = 2000L;

        void CarveCell(int c, int r)
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

            if (index is null)
            {
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
            }
            else
            {
                var tags = pathTagTls.Value!;
                var tag = (long)c * rows + r + 1L;
                var bc = c / index.BucketCells;
                var br = r / index.BucketCells;
                var maxRing = index.MaxRing(bc, br);
                var pathsEvaluated = 0;

                for (var rad = 0; rad <= maxRing; rad++)
                {
                    index.ForEachPathInRing(bc, br, rad, pi =>
                    {
                        if ((uint)pi >= (uint)riverPaths.Count)
                            return;
                        if (tags[pi] == tag)
                            return;
                        tags[pi] = tag;

                        var path = riverPaths[pi];
                        if (path.Count == 0)
                            return;
                        pathsEvaluated++;
                        ClosestPointOnRiverPath(wx, wy, path, spec, out var d, out var sAlong);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestAlong = sAlong;
                            bestLen = Math.Max(pathLens[pi], 1e-6);
                        }
                    });

                    if (pathsEvaluated >= nonEmptyRiverPaths)
                        break;
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
        }

        void CarveColumn(int c)
        {
            for (var r = 0; r < rows; r++)
                CarveCell(c, r);
        }

        if (ShouldParallelize(cols, rows))
        {
            var finished = 0;
            Parallel.For(0, cols, c =>
            {
                CarveColumn(c);
                if (!report)
                    return;
                var v = Interlocked.Increment(ref finished);
                if (v == 1 || v == cols || v % colReportStep == 0 || sw.ElapsedMilliseconds >= nextReportMs)
                {
                    Report(progress!,
                        $"[coastal] Carving river channel beds: columns {v}/{cols} ({riverPaths.Count:N0} paths, bucket index={(index is not null ? "on" : "brute")})…");
                    nextReportMs = sw.ElapsedMilliseconds + 2000L;
                }
            });
        }
        else
        {
            for (var c = 0; c < cols; c++)
            {
                CarveColumn(c);
                if (report && (c == 0 || c == cols - 1 || (c + 1) % colReportStep == 0 ||
                               sw.ElapsedMilliseconds >= nextReportMs))
                {
                    Report(progress!,
                        $"[coastal] Carving river channel beds: columns {c + 1}/{cols} ({riverPaths.Count:N0} paths)…");
                    nextReportMs = sw.ElapsedMilliseconds + 2000L;
                }
            }
        }

        if (report)
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