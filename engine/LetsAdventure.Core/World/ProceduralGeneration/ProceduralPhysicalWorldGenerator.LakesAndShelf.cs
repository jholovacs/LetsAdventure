using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>
    /// Natural ponds/lakes from land contour bowls: local lows between higher neighbors, sized by bowl depth, spaced apart.
    /// Count emerges from relief; <see cref="ProceduralWorldSpec.DepressionLakeMaxCount"/> caps placement (0 = auto cap ∝ √land).
    /// </summary>
    private static void PlaceContourDepressionLakes(
        double[,] h,
        int cols,
        int rows,
        ProceduralWorldSpec spec,
        double cell,
        Random rng,
        bool[,] isOcean,
        int[,] lakeId)
    {
        var distOcean = GridDistanceToOcean(isOcean, cols, rows);
        var minOceanCells = Math.Max(18, cols / 12);
        var minEdgeCells = Math.Max(14, cols / 16);
        var minDepression = Math.Max(spec.DepressionLakeMinDepthM,
            Math.Max(spec.TerrainAmplitude * 0.012, spec.MaxLandStepOrthogonal * 0.35));
        long landCellTotal = 0;
        for (var cc = 0; cc < cols; cc++)
        {
            for (var rr = 0; rr < rows; rr++)
            {
                if (!isOcean[cc, rr])
                    landCellTotal++;
            }
        }

        var autoLakeCap = Math.Min(512, Math.Max(12, (int)(Math.Sqrt(landCellTotal) * 0.92)));
        var maxLakes = spec.DepressionLakeMaxCount > 0 ? spec.DepressionLakeMaxCount : autoLakeCap;
        var sepScale = Math.Max(0.35, spec.DepressionLakeSpacingFactor);

        var candidates = new List<(int c, int r, double score)>();
        for (var c = 1; c < cols - 1; c++)
        {
            for (var r = 1; r < rows - 1; r++)
            {
                if (isOcean[c, r])
                    continue;
                if (distOcean[c, r] < minOceanCells)
                    continue;
                var edgeDist = Math.Min(Math.Min(c, cols - 1 - c), Math.Min(r, rows - 1 - r));
                if (edgeDist < minEdgeCells)
                    continue;

                var h0 = h[c, r];
                var hn0 = h[c - 1, r];
                var hn1 = h[c + 1, r];
                var hn2 = h[c, r - 1];
                var hn3 = h[c, r + 1];
                if (h0 > hn0 || h0 > hn1 || h0 > hn2 || h0 > hn3)
                    continue;

                var bowl = (hn0 + hn1 + hn2 + hn3) * 0.25 - h0;
                if (bowl < minDepression)
                    continue;
                candidates.Add((c, r, bowl));
            }
        }

        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        var placed = new List<(int lc, int lr, double meanRCells)>();
        for (var k = 0; k < maxLakes && candidates.Count > 0;)
        {
            var found = false;
            for (var idx = 0; idx < candidates.Count; idx++)
            {
                var (lc, lr, score) = candidates[idx];
                if (lakeId[lc, lr] != 0 || isOcean[lc, lr])
                    continue;

                var baseR = Math.Clamp(score / (cell * 0.11 + 1e-6), 1.8, spec.LakeRadiusWorld * 0.95 / cell);
                var meanRCells = baseR * (0.72 + rng.NextDouble() * 0.55);
                meanRCells = Math.Max(1.5, meanRCells);

                var tooClose = false;
                foreach (var p in placed)
                {
                    if (LakeDistance(lc, lr, p.lc, p.lr) < ((meanRCells + p.meanRCells) * 1.38 + 5) * sepScale)
                        tooClose = true;
                }

                if (tooClose)
                    continue;

                var lakeSeed = spec.Seed + k * 374761393 + rng.Next();
                ApplyLakeBowlOrganic(h, cols, rows, lc, lr, meanRCells, spec.TerrainAmplitude * 0.29, lakeSeed);
                for (var c = 0; c < cols; c++)
                {
                    for (var r = 0; r < rows; r++)
                    {
                        if (isOcean[c, r] || lakeId[c, r] != 0)
                            continue;
                        var d = LakeDistance(c, r, lc, lr);
                        if (d > meanRCells * 1.85 + 2.5)
                            continue;
                        var ang = Math.Atan2(r - lr, c - lc);
                        var rEdge = meanRCells * LakeRadiusWobble(ang, c, r, lakeSeed);
                        if (d <= rEdge + 0.35)
                            lakeId[c, r] = k + 1;
                    }
                }

                placed.Add((lc, lr, meanRCells));
                k++;
                found = true;
                break;
            }

            if (!found)
                break;
        }
    }

    /// <summary>
    /// Second hydrology pass: standing-water pools in orthographic bowls that intersect the stamped channel mask.
    /// Heavy steps report via <paramref name="progress"/> on large grids.
    /// </summary>
    private static int PoolLakesAlongRiverBowls(
        double[,] h,
        bool[,] isRiver,
        double[,] riverCellHalfWidthWorld,
        int[,] lakeId,
        bool[,] isOcean,
        int cols,
        int rows,
        ProceduralWorldSpec spec,
        IProgress<string>? progress)
    {
        if (!spec.EnableRiverCorridorPoolLakes)
            return 0;

        long landCells = 0;
        var maxLakeId = 0;
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                maxLakeId = Math.Max(maxLakeId, lakeId[c, r]);
                if (!isOcean[c, r])
                    landCells++;
            }
        }

        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] River pools: orthogonal bowl depth (parallel)…");

        var bowlOrth = new double[cols, rows];
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (c == 0 || r == 0 || c == cols - 1 || r == rows - 1 || isOcean[c, r])
            {
                bowlOrth[c, r] = 0;
                return;
            }

            if (isOcean[c - 1, r] || isOcean[c + 1, r] || isOcean[c, r - 1] || isOcean[c, r + 1])
            {
                bowlOrth[c, r] = 0;
                return;
            }

            bowlOrth[c, r] = (h[c - 1, r] + h[c + 1, r] + h[c, r - 1] + h[c, r + 1]) * 0.25 - h[c, r];
        });

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] River pools: river corridor adjacency + seeds (parallel)…");

        var touchesRiver = new bool[cols, rows];
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (isRiver[c, r])
            {
                touchesRiver[c, r] = true;
                return;
            }

            foreach (var (dc, dr) in orth)
            {
                var nc = c + dc;
                var nr = r + dr;
                if ((uint)nc < (uint)cols && (uint)nr < (uint)rows && isRiver[nc, nr])
                {
                    touchesRiver[c, r] = true;
                    return;
                }
            }
        });

        var minSeedBowl = Math.Max(1e-4, spec.RiverPoolMinBowlDepthM);
        var floodMin = Math.Max(1e-4, spec.RiverPoolFloodMinBowlM);
        var minLakeCells = Math.Max(2, spec.RiverPoolMinLakeCells);

        var seed = new bool[cols, rows];
        ParallelForCols(cols, rows, (c, r) =>
        {
            seed[c, r] = touchesRiver[c, r] && !isOcean[c, r] && lakeId[c, r] == 0 && bowlOrth[c, r] >= minSeedBowl;
        });

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] River pools: flood-fill labeling bowl components…");

        var visited = new bool[cols, rows];
        var components = new List<List<(int c, int r)>>(128);
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!seed[c, r] || visited[c, r])
                    continue;
                var list = new List<(int, int)>(64);
                var q = new Queue<(int, int)>();
                q.Enqueue((c, r));
                visited[c, r] = true;
                while (q.Count > 0)
                {
                    var (cc, rr) = q.Dequeue();
                    list.Add((cc, rr));
                    foreach (var (dc, dr) in orth)
                    {
                        var nc = cc + dc;
                        var nr = rr + dr;
                        if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                            continue;
                        if (visited[nc, nr] || isOcean[nc, nr] || lakeId[nc, nr] != 0)
                            continue;
                        if (bowlOrth[nc, nr] < floodMin)
                            continue;
                        visited[nc, nr] = true;
                        q.Enqueue((nc, nr));
                    }
                }

                if (list.Count >= minLakeCells)
                    components.Add(list);
            }
        }

        if (components.Count == 0)
            return 0;

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress,
                $"[coastal] River pools: {components.Count} basin(s) meet minimum size (≥{minLakeCells} cells); applying cap…");

        var autoCap = Math.Min(96, Math.Max(6, (int)(Math.Sqrt(landCells) * 0.22)));
        var maxNew = spec.RiverPoolMaxNewLakes > 0 ? spec.RiverPoolMaxNewLakes : autoCap;

        components.Sort((a, b) => b.Count.CompareTo(a.Count));
        var take = Math.Min(maxNew, components.Count);

        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, $"[coastal] River pools: committing {take} basins (parallel per-basin terrain tweak)…");

        var committed = 0;
        for (var i = 0; i < take; i++)
        {
            if (ExpectLongRunningGridPhase(cols, rows) && take >= 18 && committed > 0 && committed % 12 == 0)
                Report(progress, $"[coastal] River pools: carved {committed}/{take} basins…");

            var comp = components[i];
            var id = maxLakeId + 1 + committed;
            committed++;

            var dropSum = 0.0;
            foreach (var (cc, rr) in comp)
            {
                lakeId[cc, rr] = id;
                isRiver[cc, rr] = false;
                riverCellHalfWidthWorld[cc, rr] = 0;
                var b = Math.Max(bowlOrth[cc, rr], minSeedBowl * 0.35);
                dropSum += b;
            }

            var meanBowl = dropSum / comp.Count;
            var rawDrop = meanBowl * 0.42;
            var maxDrop = Math.Min(spec.TerrainAmplitude * 0.11, spec.LakeDepth * 0.55);
            // MaxLandStepOrthogonal can be cell-scaled (e.g. baseline worlds); do not let min exceed max for Math.Clamp.
            var minDrop = Math.Min(spec.MaxLandStepOrthogonal * 0.08, maxDrop);
            double drop;
            if (maxDrop <= 0)
                drop = 0;
            else if (minDrop >= maxDrop)
                drop = Math.Min(Math.Max(0, rawDrop), maxDrop);
            else
                drop = Math.Clamp(rawDrop, minDrop, maxDrop);
            Parallel.For(0, comp.Count, idx =>
            {
                var (cc, rr) = comp[idx];
                var b = Math.Max(bowlOrth[cc, rr], minSeedBowl * 0.35);
                var w = Math.Clamp(b / Math.Max(meanBowl, 1e-6), 0.35, 1.15);
                h[cc, rr] -= drop * w;
            });
        }

        return committed;
    }

    private static double LakeRadiusWobble(double ang, int c, int r, int lakeSeed)
    {
        var a = SmoothNoise(ang * 1.12 + lakeSeed * 0.0007, lakeSeed * 0.0011, lakeSeed);
        var b = SmoothNoise(ang * 2.4 + 0.6, ang * 0.38 - 0.2, lakeSeed + 31);
        var cN = SmoothNoise(c * 0.19 + lakeSeed * 0.02, r * 0.17 - lakeSeed * 0.02, lakeSeed + 107);
        var w = 0.5 + 0.5 * (a * 0.4 + b * 0.35 + cN * 0.25);
        return Math.Clamp(w, 0.4, 1.75);
    }

    private static void ApplyLakeBowlOrganic(
        double[,] h,
        int cols,
        int rows,
        int lc,
        int lr,
        double meanRadiusCells,
        double drop,
        int lakeSeed)
    {
        var maxD = meanRadiusCells * 1.85 + 3;
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                var d = LakeDistance(c, r, lc, lr);
                if (d > maxD)
                    continue;
                var ang = Math.Atan2(r - lr, c - lc);
                var rEdge = meanRadiusCells * LakeRadiusWobble(ang, c, r, lakeSeed);
                if (d > rEdge + 1.2)
                    continue;
                var t = 1 - Math.Clamp(d / Math.Max(0.35, rEdge), 0, 1);
                var w = t * t * (3 - 2 * t);
                h[c, r] -= drop * w;
            }
        }
    }

    private static List<GeoVec2> OrganicLakeShorelinePolygon(double cx, double cy, double meanRadiusWorld, int segments,
        int seed)
    {
        var list = new List<GeoVec2>(segments);
        for (var i = 0; i < segments; i++)
        {
            var t = 2 * Math.PI * i / segments;
            var w = LakeRadiusWobble(t, i * 3 + seed % 97, -i * 2 + seed % 89, seed + i * 17);
            var rad = meanRadiusWorld * w;
            list.Add(new GeoVec2 { X = cx + rad * Math.Cos(t), Y = cy + rad * Math.Sin(t) });
        }

        return list;
    }

    private static void ApplyCoastalShelfLowering(double[,] h, int cols, int rows, ProceduralWorldSpec spec,
        double cell, bool[,] isOcean)
    {
        var width = Math.Max(1, spec.CoastalShelfCells);
        var dist = GridDistanceToOcean(isOcean, cols, rows);
        var drop = spec.TerrainAmplitude * 0.14 + spec.RiverBedCarve * 0.35;
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (isOcean[c, r])
                return;
            var d = dist[c, r];
            if (d > width + 2)
                return;
            var t = 1.0 - Math.Clamp(d / (width + 1.5), 0, 1);
            var w = t * t * (3 - 2 * t);
            h[c, r] -= drop * w;
        });
    }

    private static int[,] GridDistanceToOcean(bool[,] isOcean, int cols, int rows)
    {
        var dist = new int[cols, rows];
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                dist[c, r] = isOcean[c, r] ? 0 : 1_000_000;
        var q = new Queue<(int c, int r)>();
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (isOcean[c, r])
                    q.Enqueue((c, r));
            }
        }

        while (q.Count > 0)
        {
            var (c, r) = q.Dequeue();
            var d0 = dist[c, r];
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nc = c + dc;
                var nr = r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                if (isOcean[nc, nr])
                    continue;
                if (dist[nc, nr] <= d0 + 1)
                    continue;
                dist[nc, nr] = d0 + 1;
                q.Enqueue((nc, nr));
            }
        }

        return dist;
    }
}