using System.Collections.Generic;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>Pull land elevation down near one river polyline; ORs into <paramref name="landNearRiver"/>.</summary>
    /// <param name="landNearRiver">Accumulates across stems: cells within the valley mask are set true (do not clear between calls).</param>
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
        if (path.Count < 2)
            return;

        var cell = spec.CellSize;
        var refW = Math.Max(spec.RiverChannelHalfWidthWorld * 1.15, spec.RiverChannelHalfWidthMinWorld);
        var influence = Math.Max(refW * 9.5, cell * 18);
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
        var reportPrefix = !string.IsNullOrEmpty(progressPrefix);
        var parallelSweep = ShouldParallelize(cols, rows);
        // Do not call IProgress from inside Parallel.For: Progress<T> often synchronously dispatches
        // to the UI thread (e.g. WPF), which can deadlock or freeze when many worker threads report.

        // Column-only buckets fail for long reaches that share similar grid columns (meanders, N–S
        // rails): almost every segment lands in the same few buckets → back to rows×all-segments.
        // Use a 2D uniform grid: each segment is inserted into every bucket its expanded bbox
        // touches; each land cell checks a 3×3 neighborhood of buckets.
        var inflCells = (int)Math.Ceiling(influence / Math.Max(cell, 1e-9)) + 2;
        var bw = Math.Clamp(Math.Max(inflCells + 6, 28), 28, 112);
        var bh = bw;
        var nbX = Math.Max(1, (cols + bw - 1) / bw);
        var nbY = Math.Max(1, (rows + bh - 1) / bh);
        var nBuckets = nbX * nbY;
        var segmentBuckets = new List<int>[nBuckets];
        for (var bi = 0; bi < nBuckets; bi++)
            segmentBuckets[bi] = new List<int>(64);

        var sx0 = new double[segCount];
        var sy0 = new double[segCount];
        var sx1 = new double[segCount];
        var sy1 = new double[segCount];
        var segLensW = new double[segCount];
        var arcAtSeg = new double[segCount];
        var accW = 0.0;
        for (var i = 0; i < segCount; i++)
        {
            var (gc0, gr0) = path[i];
            var (gc1, gr1) = path[i + 1];
            sx0[i] = spec.MinX + (gc0 + 0.5) * cell;
            sy0[i] = spec.MinY + (gr0 + 0.5) * cell;
            sx1[i] = spec.MinX + (gc1 + 0.5) * cell;
            sy1[i] = spec.MinY + (gr1 + 0.5) * cell;
            var dx = sx1[i] - sx0[i];
            var dy = sy1[i] - sy0[i];
            segLensW[i] = Math.Sqrt(dx * dx + dy * dy);
            arcAtSeg[i] = accW;
            accW += segLensW[i];

            var cMin = Math.Min(gc0, gc1) - inflCells;
            var cMax = Math.Max(gc0, gc1) + inflCells;
            var rMin = Math.Min(gr0, gr1) - inflCells;
            var rMax = Math.Max(gr0, gr1) + inflCells;
            if (cMin > cols - 1 || cMax < 0 || rMin > rows - 1 || rMax < 0)
                continue;
            cMin = Math.Clamp(cMin, 0, cols - 1);
            cMax = Math.Clamp(cMax, 0, cols - 1);
            rMin = Math.Clamp(rMin, 0, rows - 1);
            rMax = Math.Clamp(rMax, 0, rows - 1);
            var bx0 = cMin / bw;
            var bx1 = cMax / bw;
            var by0 = rMin / bh;
            var by1 = rMax / bh;
            bx0 = Math.Clamp(bx0, 0, nbX - 1);
            bx1 = Math.Clamp(bx1, 0, nbX - 1);
            by0 = Math.Clamp(by0, 0, nbY - 1);
            by1 = Math.Clamp(by1, 0, nbY - 1);
            for (var by = by0; by <= by1; by++)
            {
                var rowBase = by * nbX;
                for (var bx = bx0; bx <= bx1; bx++)
                    segmentBuckets[rowBase + bx].Add(i);
            }
        }

        void ProcessColumn(int c)
        {
            for (var r = 0; r < rows; r++)
            {
                if (isWater[c, r])
                    continue;

                var wx = spec.MinX + (c + 0.5) * cell;
                var wy = spec.MinY + (r + 0.5) * cell;
                var bc = Math.Min(c / bw, nbX - 1);
                var br = Math.Min(r / bh, nbY - 1);
                var bestDist = double.PositiveInfinity;
                var bestAlong = 0.0;

                for (var db = -1; db <= 1; db++)
                {
                    var brq = br + db;
                    if ((uint)brq >= (uint)nbY)
                        continue;
                    var rowOff = brq * nbX;
                    for (var dc = -1; dc <= 1; dc++)
                    {
                        var bcq = bc + dc;
                        if ((uint)bcq >= (uint)nbX)
                            continue;
                        var list = segmentBuckets[rowOff + bcq];
                        for (var k = 0; k < list.Count; k++)
                        {
                            var si = list[k];
                            ClosestOnSegment2D(wx, wy, sx0[si], sy0[si], sx1[si], sy1[si], out var d, out var u);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestAlong = arcAtSeg[si] + u * segLensW[si];
                            }
                        }
                    }
                }

                if (bestDist > influence)
                    continue;

                var t = bestAlong / totalLen;
                t = Math.Clamp(t, 0, 1);
                var w = Math.Exp(-(bestDist * bestDist) / (2 * sigma * sigma));
                h[c, r] -= amplitude * w * (0.28 + 0.72 * t);
                if (w > 0.14)
                    landNearRiver[c, r] = true;
            }
        }

        if (parallelSweep)
            Parallel.For(0, cols, ProcessColumn);
        else
        {
            for (var c = 0; c < cols; c++)
                ProcessColumn(c);
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
        var cellSize = spec.CellSize;

        if (path.Count == 0)
            return;

        if (path.Count == 1)
        {
            var (c0, r0) = path[0];
            var x0 = spec.MinX + (c0 + 0.5) * cellSize;
            var y0 = spec.MinY + (r0 + 0.5) * cellSize;
            var dx = wx - x0;
            var dy = wy - y0;
            distance = Math.Sqrt(dx * dx + dy * dy);
            return;
        }

        var acc = 0.0;
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

    private static void SmoothMaskedHeightField(
        double[,] h,
        bool[,] freeze,
        int cols,
        int rows,
        int passes,
        double alpha,
        IProgress<string>? progress = null,
        string? progressLabel = null)
    {
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        var reportPasses = ExpectLongRunningGridPhase(cols, rows) && passes >= 6 && progress != null &&
                           !string.IsNullOrEmpty(progressLabel);
        var passReportStep = reportPasses ? Math.Max(1, passes / 4) : 0;
        for (var p = 0; p < passes; p++)
        {
            if (reportPasses && (p == 0 || p == passes - 1 || (p + 1) % passReportStep == 0))
                Report(progress, $"{progressLabel}: pass {p + 1}/{passes} (parallel)…");

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
}