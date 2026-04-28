using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    private static double EstimateOceanSurfaceZ(double[,] h, bool[,] isOcean, int cols, int rows)
    {
        var samples = new List<double>();
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var c = 0; c < cols; c++)
        {
            for (var r = 0; r < rows; r++)
            {
                if (!isOcean[c, r])
                    continue;
                foreach (var (dc, dr) in orth)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows || isOcean[nc, nr])
                        continue;
                    samples.Add(h[nc, nr]);
                }
            }
        }

        if (samples.Count == 0)
        {
            var min = double.PositiveInfinity;
            for (var c = 0; c < cols; c++)
                for (var r = 0; r < rows; r++)
                    if (!isOcean[c, r])
                        min = Math.Min(min, h[c, r]);
            return min + 2;
        }

        return samples.Min() - Math.Max(6, samples.Min() * 0.004 + 4);
    }

    private static void CarveOceanFloor(double[,] h, int cols, int rows, bool[,] isOcean, double seaZ, double depth)
    {
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (isOcean[c, r])
                h[c, r] = Math.Min(h[c, r], seaZ - depth);
        });
    }

    private static Dictionary<int, double> ComputeLakeWaterLevels(double[,] h, int[,] lakeId, int cols, int rows)
    {
        var dict = new Dictionary<int, double>();
        var maxId = 0;
        for (var c = 0; c < cols; c++)
            for (var r = 0; r < rows; r++)
                maxId = Math.Max(maxId, lakeId[c, r]);
        var orth = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var id = 1; id <= maxId; id++)
        {
            var ring = new List<double>();
            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    if (lakeId[c, r] != id)
                        continue;
                    foreach (var (dc, dr) in orth)
                    {
                        var nc = c + dc;
                        var nr = r + dr;
                        if ((uint)nc < (uint)cols && (uint)nr < (uint)rows && lakeId[nc, nr] != id)
                            ring.Add(h[nc, nr]);
                    }
                }
            }

            dict[id] = ring.Count > 0 ? ring.Average() - 0.35 : 0;
        }

        return dict;
    }

    private static void CarveLakeBedForId(double[,] h, int cols, int rows, int[,] lakeId, int id, double waterZ,
        double depth)
    {
        ParallelForCols(cols, rows, (c, r) =>
        {
            if (lakeId[c, r] == id)
                h[c, r] = Math.Min(h[c, r], waterZ - depth);
        });
    }
}