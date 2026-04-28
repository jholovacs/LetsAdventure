using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public static partial class ProceduralPhysicalWorldGenerator
{
    private static List<PhysicalTerrainFeature> BuildCoastalFeatures(
        ProceduralWorldSpec spec,
        List<List<(int c, int r)>> riverPaths,
        double cell,
        int[,] lakeId,
        Dictionary<int, double> lakeLevels,
        double oceanWaterZ,
        GeoVec2 down)
    {
        var features = new List<PhysicalTerrainFeature>();
        for (var ri = 0; ri < riverPaths.Count; ri++)
        {
            var path = riverPaths[ri];
            var line = PathToWorldPolyline(spec, path, cell);
            if (line.Count < 2)
                continue;
            GeoVec2 flowDir;
            if (line.Count >= 2)
            {
                var a = line[^2];
                var b = line[^1];
                flowDir = NormalizeFlow(new GeoVec2 { X = b.X - a.X, Y = b.Y - a.Y });
            }
            else
                flowDir = down;

            features.Add(new FlowingWaterFeature
            {
                Id = $"feat.gen.river_{ri:000}",
                LayerPriority = 4,
                ChannelCenterline = line,
                ChannelHalfWidth = spec.RiverChannelHalfWidthWorld,
                WaterSurfaceZ = oceanWaterZ + 0.25,
                FlowDirection = flowDir,
                Medium = PhysicalMedium.FluidFlowing,
            });
        }

        var maxLakeId = 0;
        for (var c = 0; c < lakeId.GetLength(0); c++)
            for (var r = 0; r < lakeId.GetLength(1); r++)
                maxLakeId = Math.Max(maxLakeId, lakeId[c, r]);

        for (var id = 1; id <= maxLakeId; id++)
        {
            if (!lakeLevels.TryGetValue(id, out var lw))
                continue;
            double cx = 0, cy = 0;
            var n = 0;
            for (var c = 0; c < lakeId.GetLength(0); c++)
            {
                for (var r = 0; r < lakeId.GetLength(1); r++)
                {
                    if (lakeId[c, r] != id)
                        continue;
                    cx += spec.MinX + (c + 0.5) * cell;
                    cy += spec.MinY + (r + 0.5) * cell;
                    n++;
                }
            }

            if (n == 0)
                continue;
            cx /= n;
            cy /= n;
            var rad = EstimateLakeRadiusCells(lakeId, id) * cell * 0.92;
            features.Add(new StandingWaterFeature
            {
                Id = $"feat.gen.lake_{id:000}",
                LayerPriority = 3,
                Shoreline = OrganicLakeShorelinePolygon(cx, cy, Math.Max(rad, cell * 2.5), 34,
                    spec.Seed + id * 1699 + 1337),
                WaterSurfaceZ = lw,
                Depth = spec.LakeDepth,
                Medium = PhysicalMedium.FluidStanding,
            });
        }

        return features;
    }

    private static List<GeoVec2> PathToWorldPolyline(ProceduralWorldSpec spec, List<(int c, int r)> path, double cell)
    {
        var line = new List<GeoVec2>();
        var step = Math.Max(1, path.Count / 52);
        for (var i = 0; i < path.Count; i += step)
        {
            var (c, r) = path[i];
            line.Add(new GeoVec2 { X = spec.MinX + (c + 0.5) * cell, Y = spec.MinY + (r + 0.5) * cell });
        }

        var last = path[^1];
        var lwx = spec.MinX + (last.c + 0.5) * cell;
        var lwy = spec.MinY + (last.r + 0.5) * cell;
        if (line.Count == 0 || Math.Abs(line[^1].X - lwx) > 1e-3 || Math.Abs(line[^1].Y - lwy) > 1e-3)
            line.Add(new GeoVec2 { X = lwx, Y = lwy });
        return line;
    }

    private static double EstimateLakeRadiusCells(int[,] lakeId, int id)
    {
        var cx = 0.0;
        var cy = 0.0;
        var n = 0;
        var maxD = 0.0;
        for (var c = 0; c < lakeId.GetLength(0); c++)
        {
            for (var r = 0; r < lakeId.GetLength(1); r++)
            {
                if (lakeId[c, r] != id)
                    continue;
                cx += c;
                cy += r;
                n++;
            }
        }

        if (n == 0)
            return 3;
        cx /= n;
        cy /= n;
        for (var c = 0; c < lakeId.GetLength(0); c++)
        {
            for (var r = 0; r < lakeId.GetLength(1); r++)
            {
                if (lakeId[c, r] != id)
                    continue;
                var d = Math.Sqrt((c - cx) * (c - cx) + (r - cy) * (r - cy));
                maxD = Math.Max(maxD, d);
            }
        }

        return Math.Max(2, maxD);
    }
}