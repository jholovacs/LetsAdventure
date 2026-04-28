using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
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
        double terrainAmplitude = 22,
        IProgress<string>? progress = null)
    {
        report.LakeBasinDisconnectedFromOceanCells = 0;
        report.RiverCenterlineDownstreamGradientViolations = 0;

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Water validation: connectivity labeling (components + ocean reach)…");

        var water = new bool[cols, rows];
        ParallelForCols(cols, rows, (c, r) =>
            water[c, r] = isRiver[c, r] || isLake[c, r] || isOcean[c, r]);

        var comp = new int[cols, rows];
        ParallelForCols(cols, rows, (c, r) => comp[c, r] = -1);

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
            if (ExpectLongRunningGridPhase(cols, rows))
                Report(progress, "[coastal] Water validation: river centerline downstream gradients (parallel)…");

            var tol = Math.Max(cellSize * 0.055, terrainAmplitude * 0.0042);
            var gradViol = 0;
            Parallel.ForEach(riverCenterlines, path =>
            {
                if (path.Count < 2)
                    return;
                var local = 0;
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
                        local++;
                }

                if (local > 0)
                    Interlocked.Add(ref gradViol, local);
            });

            report.RiverCenterlineDownstreamGradientViolations = gradViol;
        }

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Water network validation finished.");
    }
}