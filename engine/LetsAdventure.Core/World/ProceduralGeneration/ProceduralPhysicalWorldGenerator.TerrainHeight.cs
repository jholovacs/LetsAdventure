using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    private static double[,] BuildBaseHeights(int cols, int rows, ProceduralWorldSpec spec, Random rng)
    {
        var h = new double[cols, rows];
        var seed = spec.Seed ^ (rng.Next() << 1);
        ParallelForCols(cols, rows, (c, r) =>
        {
            var nx = c / (double)Math.Max(cols - 1, 1);
            var ny = r / (double)Math.Max(rows - 1, 1);
            var z = Fbm(nx * 3.1, ny * 3.1, spec.NoiseOctaves, seed) * spec.TerrainAmplitude;
            z += 0.24 * spec.TerrainAmplitude * Math.Sin(nx * Math.PI) * Math.Sin(ny * Math.PI);
            h[c, r] = z;
        });

        return h;
    }

    private static double Fbm(double x, double y, int octaves, int seed)
    {
        var sum = 0.0;
        var amp = 0.5;
        var freq = 1.0;
        for (var o = 0; o < octaves; o++)
        {
            sum += amp * SmoothNoise(x * freq, y * freq, seed + o * 101);
            freq *= 2;
            amp *= 0.5;
        }

        return sum;
    }

    private static double SmoothNoise(double x, double y, int seed)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var tx = x - x0;
        var ty = y - y0;
        var u = tx * tx * (3 - 2 * tx);
        var v = ty * ty * (3 - 2 * ty);
        var a = ValueNoise(x0, y0, seed);
        var b = ValueNoise(x0 + 1, y0, seed);
        var c = ValueNoise(x0, y0 + 1, seed);
        var d = ValueNoise(x0 + 1, y0 + 1, seed);
        return Lerp(Lerp(a, b, u), Lerp(c, d, u), v);
    }

    private static double ValueNoise(int x, int y, int seed)
    {
        var n = Hash(x, y, seed);
        return (n & 0xffff) / 65535.0;
    }

    private static int Hash(int x, int y, int s)
    {
        unchecked
        {
            var h = s + x * 374761393 + y * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            return h ^ (h >> 16);
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static void WorldToGrid(
        ProceduralWorldSpec spec,
        double cell,
        double wx,
        double wy,
        int cols,
        int rows,
        out int c,
        out int r)
    {
        c = (int)Math.Clamp(Math.Floor((wx - spec.MinX) / cell), 0, cols - 1);
        r = (int)Math.Clamp(Math.Floor((wy - spec.MinY) / cell), 0, rows - 1);
    }

    private static GeoVec2 NormalizeFlow(GeoVec2 v)
    {
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1e-6)
            return new GeoVec2 { X = 1, Y = 0 };
        return new GeoVec2 { X = v.X / len, Y = v.Y / len };
    }


    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private static double DistanceToMapEdgeWorld(double wx, double wy, ProceduralWorldSpec spec)
    {
        var dx = Math.Min(wx - spec.MinX, spec.MaxX - wx);
        var dy = Math.Min(wy - spec.MinY, spec.MaxY - wy);
        return Math.Min(dx, dy);
    }

    private static bool[,] BuildPerimeterOceanMask(int cols, int rows, ProceduralWorldSpec spec, double cell,
        Random rng, double[,] h)
    {
        _ = rng;
        var mask = new bool[cols, rows];
        var seed = spec.Seed ^ 0x6D2B79F5;
        var spanX = Math.Max(spec.MaxX - spec.MinX, 1e-6);
        var spanY = Math.Max(spec.MaxY - spec.MinY, 1e-6);
        var edgeZ = new List<double>();
        for (var c = 0; c < cols; c++)
        {
            edgeZ.Add(h[c, 0]);
            edgeZ.Add(h[c, rows - 1]);
        }

        for (var r = 0; r < rows; r++)
        {
            edgeZ.Add(h[0, r]);
            edgeZ.Add(h[cols - 1, r]);
        }

        var hRef = edgeZ.Count > 0 ? edgeZ.Average() : 0;
        var amp = Math.Max(spec.TerrainAmplitude, 60);
        ParallelForCols(cols, rows, (c, r) =>
        {
            var wx = spec.MinX + (c + 0.5) * cell;
            var wy = spec.MinY + (r + 0.5) * cell;
            var dEdge = DistanceToMapEdgeWorld(wx, wy, spec);
            var nx = (wx - spec.MinX) / spanX;
            var ny = (wy - spec.MinY) / spanY;
            var fine = (SmoothNoise(nx * 6.2, ny * 6.2, seed) - 0.5) * 2.0;
            var bay = (SmoothNoise(nx * 2.05 + 0.3, ny * 2.05 - 0.2, seed + 911) - 0.5) * 2.0;
            var cove = (SmoothNoise(nx * 11.0, ny * 11.0, seed + 413) - 0.5) * 0.65;
            var threshold = spec.OceanBandMinWorld
                + spec.OceanBandVariationWorld * (0.45 * fine + 0.42 * bay + 0.28 * cove);
            var hRel = (h[c, r] - hRef) / amp;
            threshold -= hRel * spec.OceanBandVariationWorld * 0.38;
            threshold = Math.Max(cell * 0.35, threshold);
            mask[c, r] = dEdge < threshold;
        });

        return mask;
    }

    /// <summary>
    /// Lets the sea lap slightly into low ground along the coast so the shore follows terrain more than a pure geometric band.
    /// </summary>
    private static void CreepOceanIntoLowCoastalTerrain(
        double[,] h,
        bool[,] isOcean,
        int[,] lakeId,
        double seaZ,
        int cols,
        int rows,
        ProceduralWorldSpec spec,
        IProgress<string>? progress)
    {
        var band = Math.Max(spec.TerrainAmplitude * 0.038 + 20, 16);
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        if (ExpectLongRunningGridPhase(cols, rows))
            Report(progress, "[coastal] Coastal ocean creep into low terrain (parallel candidate scan per pass)…");

        for (var pass = 0; pass < 9; pass++)
        {
            if (ExpectLongRunningGridPhase(cols, rows))
                Report(progress, $"[coastal] Coastal creep: iteration {pass + 1}/9…");

            var add = new ConcurrentBag<(int c, int r)>();
            ParallelForCols(cols, rows, (c, r) =>
            {
                if (isOcean[c, r] || lakeId[c, r] != 0)
                    return;
                var touchesOcean = false;
                foreach (var (dc, dr) in orth)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                        continue;
                    if (isOcean[nc, nr])
                        touchesOcean = true;
                }

                if (!touchesOcean)
                    return;
                if (h[c, r] <= seaZ + band)
                    add.Add((c, r));
            });

            if (add.IsEmpty)
                break;
            foreach (var (c, r) in add)
            {
                isOcean[c, r] = true;
                h[c, r] = Math.Min(h[c, r], seaZ + band * 0.08);
            }
        }
    }

    private static void ApplyContinentalDome(double[,] h, int cols, int rows, ProceduralWorldSpec spec)
    {
        if (spec.ContinentalDomeAmplitude <= 1e-6)
            return;
        var cx = (cols - 1) * 0.5;
        var cy = (rows - 1) * 0.5;
        var maxR = Math.Sqrt(Math.Max(cx * cx + cy * cy, 1e-6));
        ParallelForCols(cols, rows, (c, r) =>
        {
            var dx = (c + 0.5) - cx;
            var dy = (r + 0.5) - cy;
            var rd = Math.Sqrt(dx * dx + dy * dy) / maxR;
            var t = 1.0 - Math.Clamp(rd, 0, 1);
            var lift = spec.ContinentalDomeAmplitude * t * t * (1.0 + 0.35 * t);
            h[c, r] += lift;
        });
    }

    private static void ApplyTerrainRidges(double[,] h, int cols, int rows, ProceduralWorldSpec spec, Random rng)
    {
        if (spec.TerrainRidgeWeight <= 1e-6)
            return;
        var seed = spec.Seed ^ rng.Next();
        ParallelForCols(cols, rows, (c, r) =>
        {
            var nx = c / (double)Math.Max(cols - 1, 1);
            var ny = r / (double)Math.Max(rows - 1, 1);
            var n0 = SmoothNoise(nx * 5.1, ny * 5.1, seed);
            var ridge = 1.0 - Math.Abs(n0 * 2.0 - 1.0);
            h[c, r] += spec.TerrainAmplitude * spec.TerrainRidgeWeight * ridge;
        });
    }

    /// <summary>
    /// Spatially varying smooth↔rugged terrain, optional ridged detail, and steep mountain massifs
    /// (cores marked in <paramref name="mountainMask"/>).
    /// </summary>
    private static void ApplyRegionalRuggednessMountainsAndRidges(
        double[,] h,
        double[,] terrainRuggedness,
        bool[,] mountainMask,
        int cols,
        int rows,
        ProceduralWorldSpec spec,
        Random rng)
    {
        var seed = spec.Seed ^ rng.Next();
        ParallelForCols(cols, rows, (c, r) =>
        {
            var nx = c / (double)Math.Max(cols - 1, 1);
            var ny = r / (double)Math.Max(rows - 1, 1);
            var R = 0.5 * (1.0 + SmoothNoise(nx * 1.55 + 0.2, ny * 1.48 - 0.11, seed));
            R = Math.Clamp(R, 0, 1);
            terrainRuggedness[c, r] = R;

            var ridged = RidgedFbm(nx * 7.2, ny * 7.2, 5, seed + 101);
            var detailSmooth = SmoothNoise(nx * 4.1, ny * 4.1, seed + 303) - 0.5;
            var roughAmt = spec.TerrainAmplitude * (0.11 + 0.41 * R);
            h[c, r] += R * ridged * roughAmt + (1 - R) * detailSmooth * spec.TerrainAmplitude * 0.045;
        });

        if (spec.TerrainRidgeWeight > 1e-6)
        {
            ParallelForCols(cols, rows, (c, r) =>
            {
                var nx = c / (double)Math.Max(cols - 1, 1);
                var ny = r / (double)Math.Max(rows - 1, 1);
                var n0 = SmoothNoise(nx * 5.1, ny * 5.1, seed + 17);
                var ridge = 1.0 - Math.Abs(n0 * 2.0 - 1.0);
                var R = terrainRuggedness[c, r];
                h[c, r] += spec.TerrainAmplitude * spec.TerrainRidgeWeight * ridge * (0.32 + 0.68 * R);
            });
        }

        if (spec.MountainPeakCount <= 0)
            return;

        var peaks = new List<(int c, int r)>();
        var span = Math.Min(cols, rows);
        var minPeakSep = Math.Max(6.0, span * 0.088);
        var minPeakSepSq = minPeakSep * minPeakSep;
        for (var p = 0; p < spec.MountainPeakCount; p++)
        {
            var bestC = -1;
            var bestR = -1;
            var bestScore = double.NegativeInfinity;
            for (var attempt = 0; attempt < 110; attempt++)
            {
                var mc = rng.Next(Math.Max(2, cols / 10), Math.Max(3, cols - cols / 10));
                var mr = rng.Next(Math.Max(2, rows / 10), Math.Max(3, rows - rows / 10));
                var R = terrainRuggedness[mc, mr];
                var score = h[mc, mr] * (0.48 + 0.92 * R);
                foreach (var prev in peaks)
                {
                    var dx = mc - prev.c;
                    var dy = mr - prev.r;
                    if (dx * dx + dy * dy < minPeakSepSq)
                    {
                        score = double.NegativeInfinity;
                        break;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestC = mc;
                    bestR = mr;
                }
            }

            if (bestC < 0 || bestScore < 0)
                continue;
            peaks.Add((bestC, bestR));
            var cellSz = Math.Max(0.5, spec.CellSize);
            var basePeakZ = h[bestC, bestR];
            var inclineDeg = Math.Clamp(spec.MountainMinInclineTowardPeakDegrees, 5, 85);
            var tanMinSlope = Math.Tan(inclineDeg * (Math.PI / 180.0));
            var maxPeakAbs = spec.MaxZBound - spec.MountainPeakClearanceBelowMaxZM;
            var minPeakAbs = spec.MountainPeakMinAbsoluteZM;
            var canPinAbsolute = maxPeakAbs > minPeakAbs + 1e-3;

            const double minSpikey = 0.76;
            const double maxSpikey = 1.24;
            double coneHeight;
            if (canPinAbsolute)
            {
                if (basePeakZ >= maxPeakAbs - 1e-3)
                    continue;
                var targetPeak = Math.Clamp(Math.Max(basePeakZ + 1.0, minPeakAbs), minPeakAbs, maxPeakAbs);
                coneHeight = Math.Max(1.0, targetPeak - basePeakZ);
                coneHeight = Math.Max(coneHeight, (minPeakAbs - basePeakZ) / minSpikey);
                coneHeight = Math.Min(coneHeight, (maxPeakAbs - basePeakZ) / maxSpikey);
                if (coneHeight < 1.0)
                    continue;
                if (basePeakZ + coneHeight * minSpikey > maxPeakAbs + 1e-3)
                    continue;
                if (basePeakZ + coneHeight * maxSpikey < minPeakAbs - 1e-3)
                    continue;
            }
            else
            {
                coneHeight = spec.TerrainAmplitude * spec.MountainLiftScale * (0.8 + rng.NextDouble() * 0.52);
            }

            var radiusCellsLoose = Math.Max(3.6, span * (0.044 + rng.NextDouble() * 0.054));
            var radiusWorldLoose = radiusCellsLoose * cellSz;
            var maxRadiusWorld = coneHeight * minSpikey / Math.Max(tanMinSlope, 1e-6);
            var radiusWorld = Math.Min(radiusWorldLoose, maxRadiusWorld);
            if (radiusWorld < cellSz * 2.5)
                radiusWorld = Math.Min(maxRadiusWorld, cellSz * 2.5);
            var radiusCells = radiusWorld / cellSz;
            var peakIndex = p;
            ParallelForCols(cols, rows, (c, r) =>
            {
                var dx = c - bestC;
                var dy = r - bestR;
                var rhoWorld = Math.Sqrt(dx * dx + dy * dy) * cellSz;
                if (rhoWorld > radiusWorld)
                    return;
                var cone = coneHeight * (1.0 - rhoWorld / radiusWorld);
                var nx = c / (double)Math.Max(cols - 1, 1);
                var ny = r / (double)Math.Max(rows - 1, 1);
                var spikey = 1.0 + 0.24 * RidgedFbm(nx * 14.0, ny * 14.0, 3, seed + peakIndex * 173);
                h[c, r] += cone * spikey;
                if (rhoWorld <= radiusWorld * 0.5)
                    mountainMask[c, r] = true;
            });
        }
    }

    private static double RidgedFbm(double x, double y, int octaves, int seed)
    {
        var sum = 0.0;
        var amp = 0.55;
        var freq = 1.0;
        for (var o = 0; o < octaves; o++)
        {
            var n = SmoothNoise(x * freq, y * freq, seed + o * 59);
            var ridge = 1.0 - Math.Abs(n * 2.0 - 1.0);
            sum += amp * ridge * ridge;
            freq *= 2.08;
            amp *= 0.52;
        }

        return sum;
    }
}