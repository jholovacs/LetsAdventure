using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
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