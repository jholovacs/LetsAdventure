using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    private static double LakeDistance(int c, int r, int lc, int lr) => Math.Sqrt((c - lc) * (c - lc) + (r - lr) * (r - lr));

    /// <param name="progressLabel">When set with <paramref name="progress"/>, reports pass progress (e.g. "[coastal] Land-only slope (pre-valley").</param>
    private static void EnforceLandOnlySlope(
        double[,] h,
        bool[,] isWater,
        int cols,
        int rows,
        double maxStep,
        int iterations,
        IProgress<string>? progress = null,
        string? progressLabel = null)
    {
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        var reportEvery = iterations <= 8 ? 1 : Math.Max(1, iterations / 5);
        for (var it = 0; it < iterations; it++)
        {
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    if (isWater[c, r])
                        continue;
                    foreach (var (dc, dr) in dirs)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || isWater[nc, nr])
                            continue;
                        var diff = h[c, r] - h[nc, nr];
                        if (diff > maxStep)
                        {
                            var adj = (diff - maxStep) * 0.5;
                            h[c, r] -= adj;
                            h[nc, nr] += adj;
                        }
                        else if (diff < -maxStep)
                        {
                            var adj = (-diff - maxStep) * 0.5;
                            h[c, r] += adj;
                            h[nc, nr] -= adj;
                        }
                    }
                }
            }

            if (progress != null && progressLabel != null &&
                (it == 0 || it == iterations - 1 || (it + 1) % reportEvery == 0))
                Report(progress, $"{progressLabel}: relax {it + 1}/{iterations}…");
        }
    }

    /// <param name="preserveSteepTerrain">When set, skips relaxation for edges touching these cells (mountain cores).</param>
    private static void EnforceMaxOrthogonalSlope(double[,] h, int cols, int rows, double maxStep, int iterations,
        bool[,]? preserveSteepTerrain = null)
    {
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var it = 0; it < iterations; it++)
        {
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    foreach (var (dc, dr) in dirs)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                            continue;
                        if (preserveSteepTerrain != null &&
                            (preserveSteepTerrain[c, r] || preserveSteepTerrain[nc, nr]))
                            continue;
                        var diff = h[c, r] - h[nc, nr];
                        if (diff > maxStep)
                        {
                            var adj = (diff - maxStep) * 0.5;
                            h[c, r] -= adj;
                            h[nc, nr] += adj;
                        }
                        else if (diff < -maxStep)
                        {
                            var adj = (-diff - maxStep) * 0.5;
                            h[c, r] += adj;
                            h[nc, nr] -= adj;
                        }
                    }
                }
            }
        }
    }
}