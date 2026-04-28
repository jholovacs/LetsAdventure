using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    /// <summary>
    /// Gentle river mouths near the ocean: extra distributaries and striped sand / slight bar relief.
    /// </summary>
    private static void ApplyCoastalRiverDeltas(
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
        bool[,] deltaSandMask,
        double[,] riverCellHalfWidthWorld)
    {
        var distOcean = GridDistanceToOcean(isOcean, cols, rows);
        var gentle = Math.Max(spec.MaxLandStepOrthogonal * 0.26, spec.TerrainAmplitude * 0.0145);
        var distHalfW = spec.RiverChannelHalfWidthWorld * 0.38;
        var stripeSeed = spec.Seed ^ 0x4D656C74;
        var snapshot = riverPaths.ToArray();

        foreach (var path in snapshot)
        {
            if (path.Count < 10)
                continue;

            var lastLand = -1;
            for (var i = path.Count - 1; i >= 0; i--)
            {
                if (!isOcean[path[i].c, path[i].r])
                {
                    lastLand = i;
                    break;
                }
            }

            if (lastLand < 8)
                continue;

            var touchesOceanAhead = false;
            for (var j = lastLand; j < path.Count && j <= lastLand + 8; j++)
            {
                if (isOcean[path[j].c, path[j].r])
                {
                    touchesOceanAhead = true;
                    break;
                }
            }

            if (!touchesOceanAhead)
                continue;

            var lookback = Math.Min(14, Math.Max(6, lastLand / 2));
            var tailStart = Math.Max(1, lastLand - lookback);
            var steps = 0;
            double sumDrop = 0;
            for (var i = tailStart; i < lastLand; i++)
            {
                var a = path[i];
                var b = path[i + 1];
                sumDrop += Math.Abs(h[b.c, b.r] - h[a.c, a.r]);
                steps++;
            }

            if (steps == 0 || sumDrop / steps > gentle)
                continue;

            var forkIdx = tailStart + (lastLand - tailStart) / 2;
            forkIdx = Math.Clamp(forkIdx, 2, lastLand - 2);
            var (fc, fr) = path[forkIdx];
            if (isOcean[fc, fr])
                continue;

            var branchCount = 2 + rng.Next(0, 2);
            if (rng.NextDouble() > 0.52)
                branchCount++;
            branchCount = Math.Clamp(branchCount, 2, 5);

            for (var b = 0; b < branchCount; b++)
            {
                var sc = Math.Clamp(fc + rng.Next(-1, 2) + (b % 2 == 0 ? -1 : 1), 1, cols - 2);
                var sr = Math.Clamp(fr + rng.Next(-1, 2), 1, rows - 2);
                if (isOcean[sc, sr] || lakeId[sc, sr] != 0)
                    continue;
                var distributary = BuildDistributaryTowardOcean(h, isOcean, lakeId, cols, rows, (sc, sr), distOcean,
                    rng, cell, spec, stripeSeed + b * 9973);
                if (distributary is null || distributary.Count < 2)
                    continue;
                riverPaths.Add(distributary);
                var block = new bool[cols, rows];
                for (var c = 0; c < cols; c++)
                {
                    for (var r = 0; r < rows; r++)
                        block[c, r] = isOcean[c, r] || lakeId[c, r] != 0;
                }

                var dW = new double[distributary.Count];
                for (var di = 0; di < dW.Length; di++)
                    dW[di] = distHalfW;
                StampRiverCorridor(distributary, dW, cols, rows, cell, block, isRiver, riverCellHalfWidthWorld);
            }

            ApplyDeltaSandbarStripes(h, isOcean, isRiver, lakeId, cols, rows, fc, fr, distOcean, rng, cell, spec,
                deltaSandMask, stripeSeed + forkIdx * 131);
        }
    }

    private static List<(int c, int r)>? BuildDistributaryTowardOcean(
        double[,] h,
        bool[,] isOcean,
        int[,] lakeId,
        int cols,
        int rows,
        (int c, int r) start,
        int[,] distOcean,
        Random rng,
        double cell,
        ProceduralWorldSpec spec,
        int pathSeed)
    {
        if (isOcean[start.c, start.r] || lakeId[start.c, start.r] != 0)
            return null;

        var path = new List<(int c, int r)>();
        var cur = start;
        var seen = new HashSet<(int, int)> { cur };
        var maxSteps = 5 + rng.Next(0, 11);
        for (var s = 0; s < maxSteps; s++)
        {
            if (isOcean[cur.c, cur.r])
                break;
            path.Add(cur);
            var candidates = new List<((int nc, int nr) cell, double w)>();
            for (var d = 0; d < 8; d++)
            {
                var dc = d % 3 - 1;
                var dr = d / 3 - 1;
                if (dc == 0 && dr == 0)
                    continue;
                var nc = cur.c + dc;
                var nr = cur.r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                if (lakeId[nc, nr] != 0)
                    continue;
                if (seen.Contains((nc, nr)))
                    continue;
                var dOc = distOcean[nc, nr];
                var dh = h[nc, nr] - h[cur.c, cur.r];
                if (!isOcean[nc, nr] && dh > cell * 0.095)
                    continue;
                var nh = SmoothNoise(nc * 0.31 + pathSeed * 0.001, nr * 0.29, pathSeed) - 0.5;
                var w = 12.0 / (1 + dOc) + (isOcean[nc, nr] ? 8.5 : 0) + nh * 0.85;
                if (dh < 0)
                    w *= 1.42;
                w *= 0.75 + rng.NextDouble() * 0.55;
                candidates.Add(((nc, nr), Math.Max(0.02, w)));
            }

            if (candidates.Count == 0)
                break;
            double total = 0;
            foreach (var t in candidates)
                total += t.w;
            var roll = rng.NextDouble() * total;
            (int nc, int nr) next = candidates[^1].cell;
            foreach (var (cellT, w) in candidates)
            {
                roll -= w;
                if (roll <= 0)
                {
                    next = cellT;
                    break;
                }
            }

            if (seen.Contains(next))
                break;
            seen.Add(next);
            cur = next;
        }

        return path.Count >= 2 ? path : null;
    }

    private static void ApplyDeltaSandbarStripes(
        double[,] h,
        bool[,] isOcean,
        bool[,] isRiver,
        int[,] lakeId,
        int cols,
        int rows,
        int fc,
        int fr,
        int[,] distOcean,
        Random rng,
        double cell,
        ProceduralWorldSpec spec,
        bool[,] deltaSandMask,
        int stripeSeed)
    {
        var R = 9 + rng.Next(0, 7);
        for (var c = Math.Max(0, fc - R); c < Math.Min(cols, fc + R + 1); c++)
        {
            for (var r = Math.Max(0, fr - R); r < Math.Min(rows, fr + R + 1); r++)
            {
                if (isOcean[c, r] || isRiver[c, r] || lakeId[c, r] != 0)
                    continue;
                if (distOcean[c, r] > 24)
                    continue;
                var stripe = SmoothNoise(c * 0.41 + fr * 0.02, r * 0.39 + fc * 0.02, stripeSeed);
                var band = Math.Abs(stripe - 0.5);
                var sandChance = band < 0.14 ? 0.58 : 0.26;
                if (rng.NextDouble() > sandChance)
                    continue;
                deltaSandMask[c, r] = true;
                h[c, r] += spec.TerrainAmplitude * (0.0035 + rng.NextDouble() * 0.007);
            }
        }
    }
}