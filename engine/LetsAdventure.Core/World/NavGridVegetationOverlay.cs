using System.Threading.Tasks;

namespace LetsAdventure.Core.World;

/// <summary>
/// Derives per-cell vegetation density, community, and stratum variety from nav terrain (composition, relief,
/// hydrology). Intended for baseline worlds after procedural synthesis and elevation normalize.
/// </summary>
public static class NavGridVegetationOverlay
{
    private const int MaxWaterDistanceCells = 96;
    private const int ParallelCellThreshold = 65_536;

    private static bool ShouldParallelize(int cols, int rows) => (long)cols * rows >= ParallelCellThreshold;

    public static void Apply(TerrainNavGridDefinition? grid, Random rng)
    {
        if (grid?.Cells is null || grid.Columns < 3 || grid.Rows < 3)
            return;

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cells = grid.Cells;
        var jitterSalt = rng.Next();

        var distWater = ComputeDistanceToWaterCells(cols, rows, cells);
        var (zMin, zMax) = LandElevationRange(cols, rows, cells);
        var zSpan = Math.Max(1e-3, zMax - zMin);

        void Body(int c, int r)
        {
            var i = r * cols + c;
            var cell = cells[i];
            if (IsOpenWater(cell))
            {
                cell.VegetationDensity01 = 0;
                cell.VegetationCommunity = VegetationCommunityKind.None;
                cell.VegetationStrata = VegetationStratum.None;
                return;
            }

            if (!cell.Walkable)
            {
                cell.VegetationDensity01 = 0;
                cell.VegetationCommunity = VegetationCommunityKind.None;
                cell.VegetationStrata = VegetationStratum.None;
                return;
            }

            var ez = cell.ElevationZ;
            var zRel = (ez - zMin) / zSpan;
            var maxOrtho = MaxOrthogonalStep(cells, cols, rows, c, r);
            var slopeNorm = Math.Clamp(maxOrtho / (zSpan * 0.14 + 1e-6), 0, 3);
            var dep = DepressionVsNeighbors(cells, cols, rows, c, r);
            var depNorm = Math.Clamp(dep / (zSpan * 0.22 + 1e-6), 0, 2);
            var dW = distWater[i];
            var dWn = Math.Clamp(dW / 48.0, 0, 1);
            var moist = Math.Exp(-dW * 0.085);

            var comp = cell.Composition;
            var alpine = comp is SurfaceComposition.Rock or SurfaceComposition.Ice
                         || zRel > 0.76
                         || slopeNorm > 1.05;
            var sandCoastal = comp == SurfaceComposition.Sand;

            VegetationCommunityKind community;
            VegetationStratum strata;
            double density;

            if (alpine)
            {
                community = VegetationCommunityKind.AlpineSparse;
                strata = VegetationStratum.Shrub | VegetationStratum.ForbWildflower | VegetationStratum.WeedyHerb;
                density = 0.1 + 0.22 * moist + 0.08 * (1 - zRel);
                if (comp == SurfaceComposition.Rock)
                    density *= 0.65;
            }
            else if (sandCoastal && !alpine)
            {
                community = VegetationCommunityKind.CoastalSandSparse;
                strata = VegetationStratum.Grass | VegetationStratum.Shrub | VegetationStratum.ForbWildflower;
                density = 0.18 + 0.35 * moist + 0.06 * (1 - dWn);
            }
            else if (dW <= 4)
            {
                community = VegetationCommunityKind.RiparianWoodland;
                strata = VegetationStratum.Grass | VegetationStratum.Shrub | VegetationStratum.Tree;
                density = 0.72 + 0.22 * (1 - dWn * 0.35) + 0.06 * depNorm;
                density = Math.Min(0.98, density);
            }
            else if (zRel < 0.48 && depNorm > 0.18 && slopeNorm < 0.72 && dW is > 3 and < 38)
            {
                community = VegetationCommunityKind.LowlandForest;
                strata = VegetationStratum.Tree | VegetationStratum.Shrub | VegetationStratum.Grass;
                density = 0.62 + 0.28 * moist + 0.12 * depNorm;
                density = Math.Min(0.97, density);
            }
            else if (slopeNorm < 0.55 && zRel < 0.7 && dW > 5)
            {
                community = VegetationCommunityKind.PlainsHerbaceous;
                strata = VegetationStratum.Grass | VegetationStratum.Shrub;
                if (Hash01(c, r, jitterSalt) < 0.22)
                    strata |= VegetationStratum.ForbWildflower;
                density = 0.58 + 0.26 * (1 - zRel * 0.35) + 0.08 * (1 - dWn);
                density = Math.Min(0.94, density);
            }
            else
            {
                community = VegetationCommunityKind.TransitionMixed;
                strata = VegetationStratum.Grass | VegetationStratum.Shrub;
                if (zRel < 0.55 && moist > 0.35)
                    strata |= VegetationStratum.Tree;
                if (Hash01(c, r, jitterSalt + 17) < 0.35)
                    strata |= VegetationStratum.ForbWildflower;
                density = 0.35 + 0.35 * moist + 0.12 * (1 - slopeNorm * 0.4);
                density = Math.Clamp(density, 0.12, 0.88);
            }

            var jitter = (Hash01(c, r, jitterSalt + 911) - 0.5) * 0.08;
            cell.VegetationDensity01 = Math.Clamp(density + jitter, 0, 1);
            cell.VegetationCommunity = community;
            cell.VegetationStrata = strata;
        }

        if (ShouldParallelize(cols, rows))
        {
            Parallel.For(1, rows - 1, r =>
            {
                for (var c = 1; c < cols - 1; c++)
                    Body(c, r);
            });
        }
        else
        {
            for (var r = 1; r < rows - 1; r++)
            {
                for (var c = 1; c < cols - 1; c++)
                    Body(c, r);
            }
        }

        SmoothDensityPass(cols, rows, cells, alpha: 0.34);
    }

    private static void SmoothDensityPass(int cols, int rows, List<NavCellDefinition> cells, double alpha)
    {
        var n = cols * rows;
        var copy = new double[n];
        if (ShouldParallelize(cols, rows))
        {
            Parallel.For(0, rows, r =>
            {
                for (var c = 0; c < cols; c++)
                {
                    var i = r * cols + c;
                    copy[i] = cells[i].VegetationDensity01;
                }
            });

            Parallel.For(1, rows - 1, r =>
            {
                for (var c = 1; c < cols - 1; c++)
                {
                    var i = r * cols + c;
                    if (IsOpenWater(cells[i]) || !cells[i].Walkable)
                        continue;
                    var sum = 0.0;
                    var w = 0.0;
                    for (var dr = -1; dr <= 1; dr++)
                    {
                        for (var dc = -1; dc <= 1; dc++)
                        {
                            var j = (r + dr) * cols + (c + dc);
                            if (IsOpenWater(cells[j]) || !cells[j].Walkable)
                                continue;
                            var wt = dc == 0 || dr == 0 ? 1.0 : 0.7;
                            sum += copy[j] * wt;
                            w += wt;
                        }
                    }

                    if (w > 1e-6)
                    {
                        var blurred = sum / w;
                        cells[i].VegetationDensity01 =
                            Math.Clamp((1 - alpha) * copy[i] + alpha * blurred, 0, 1);
                    }
                }
            });
        }
        else
        {
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var i = r * cols + c;
                    copy[i] = cells[i].VegetationDensity01;
                }
            }

            for (var r = 1; r < rows - 1; r++)
            {
                for (var c = 1; c < cols - 1; c++)
                {
                    var i = r * cols + c;
                    if (IsOpenWater(cells[i]) || !cells[i].Walkable)
                        continue;
                    var sum = 0.0;
                    var w = 0.0;
                    for (var dr = -1; dr <= 1; dr++)
                    {
                        for (var dc = -1; dc <= 1; dc++)
                        {
                            var j = (r + dr) * cols + (c + dc);
                            if (IsOpenWater(cells[j]) || !cells[j].Walkable)
                                continue;
                            var wt = dc == 0 || dr == 0 ? 1.0 : 0.7;
                            sum += copy[j] * wt;
                            w += wt;
                        }
                    }

                    if (w > 1e-6)
                    {
                        var blurred = sum / w;
                        cells[i].VegetationDensity01 =
                            Math.Clamp((1 - alpha) * copy[i] + alpha * blurred, 0, 1);
                    }
                }
            }
        }
    }

    private static int[] ComputeDistanceToWaterCells(int cols, int rows, List<NavCellDefinition> cells)
    {
        var n = cols * rows;
        var dist = new int[n];
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<(int c, int r)>();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var i = r * cols + c;
                if (!IsOpenWater(cells[i]))
                    continue;
                dist[i] = 0;
                q.Enqueue((c, r));
            }
        }

        while (q.Count > 0)
        {
            var (c, r) = q.Dequeue();
            var i = r * cols + c;
            var d0 = dist[i];
            if (d0 >= MaxWaterDistanceCells)
                continue;
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nc = c + dc;
                var nr = r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                var j = nr * cols + nc;
                var nd = d0 + 1;
                if (nd >= dist[j])
                    continue;
                dist[j] = nd;
                q.Enqueue((nc, nr));
            }
        }

        return dist;
    }

    private static (double zMin, double zMax) LandElevationRange(int cols, int rows, List<NavCellDefinition> cells)
    {
        var zMin = double.MaxValue;
        var zMax = double.MinValue;
        for (var i = 0; i < cols * rows; i++)
        {
            var c = cells[i];
            if (IsOpenWater(c) || !c.Walkable)
                continue;
            var z = c.ElevationZ;
            zMin = Math.Min(zMin, z);
            zMax = Math.Max(zMax, z);
        }

        if (zMin > zMax)
            return (0, 1);
        return (zMin, zMax);
    }

    private static bool IsOpenWater(NavCellDefinition c) =>
        c.Composition == SurfaceComposition.Water
        || (!c.Walkable && c.FluidDepth > 0.01);

    private static double MaxOrthogonalStep(List<NavCellDefinition> cells, int cols, int rows, int c, int r)
    {
        var i = r * cols + c;
        var z = cells[i].ElevationZ;
        var max = 0.0;
        foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var nc = c + dc;
            var nr = r + dr;
            if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                continue;
            var j = nr * cols + nc;
            if (IsOpenWater(cells[j]) || !cells[j].Walkable)
                continue;
            max = Math.Max(max, Math.Abs(cells[j].ElevationZ - z));
        }

        return max;
    }

    private static double DepressionVsNeighbors(List<NavCellDefinition> cells, int cols, int rows, int c, int r)
    {
        var i = r * cols + c;
        var z = cells[i].ElevationZ;
        var sum = 0.0;
        var count = 0;
        foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            var nc = c + dc;
            var nr = r + dr;
            if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                continue;
            var j = nr * cols + nc;
            if (IsOpenWater(cells[j]) || !cells[j].Walkable)
                continue;
            sum += cells[j].ElevationZ;
            count++;
        }

        if (count == 0)
            return 0;
        return sum / count - z;
    }

    private static double Hash01(int c, int r, int salt)
    {
        var x = (uint)(c * 73856093 ^ r * 19349663 ^ salt * 83492791);
        x ^= x >> 16;
        x *= 0x85ebca6b;
        x ^= x >> 13;
        return (x & 0xFFFFFF) / (double)0x1000000;
    }
}
