using System.Numerics;
using System.Runtime.InteropServices;

namespace LetsAdventure.Core.World;

/// <summary>CPU-side river polyline flattening + tile→segment index lists for Vulkan grid passes.</summary>
internal static class RiverPathGpuGeometry
{
    public const int TileW = 64;
    public const int TileH = 64;
    public const int MaxSegments = 4_000_000;
    public const int MaxTileIndexEntries = 32_000_000;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    public struct GpuRiverSeg
    {
        public Vector4 Endpoints;
        public float ArcBase;
        public float SegLen;
        public int PathIdx;
        public int Pad;
    }

    /// <summary>Build segment list and tile-culled segment indices; <paramref name="pathLens"/> is unused but kept for API symmetry.</summary>
    public static bool TryBuild(
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        List<List<(int c, int r)>> riverPaths,
        int expandCells,
        double[]? pathLens,
        out List<GpuRiverSeg> segments,
        out uint[] tileOffsets,
        out uint[] packedTileSegIdx)
    {
        segments = new List<GpuRiverSeg>(256);
        var pathCount = riverPaths.Count;
        var segStart = new int[pathCount];
        var segPerPath = new int[pathCount];
        var c0 = new int[pathCount];
        var c1 = new int[pathCount];
        var r0 = new int[pathCount];
        var r1 = new int[pathCount];

        var cellF = Math.Max(cell, 1e-9);
        for (var pi = 0; pi < pathCount; pi++)
        {
            segStart[pi] = segments.Count;
            var path = riverPaths[pi];
            if (path.Count == 0)
            {
                segPerPath[pi] = 0;
                c0[pi] = c1[pi] = r0[pi] = r1[pi] = 0;
                continue;
            }

            var pc0 = int.MaxValue;
            var pc1 = int.MinValue;
            var pr0 = int.MaxValue;
            var pr1 = int.MinValue;
            foreach (var (ac, ar) in path)
            {
                pc0 = Math.Min(pc0, ac);
                pc1 = Math.Max(pc1, ac);
                pr0 = Math.Min(pr0, ar);
                pr1 = Math.Max(pr1, ar);
            }

            pc0 = Math.Clamp(pc0 - expandCells, 0, cols - 1);
            pc1 = Math.Clamp(pc1 + expandCells, 0, cols - 1);
            pr0 = Math.Clamp(pr0 - expandCells, 0, rows - 1);
            pr1 = Math.Clamp(pr1 + expandCells, 0, rows - 1);
            c0[pi] = pc0;
            c1[pi] = pc1;
            r0[pi] = pr0;
            r1[pi] = pr1;

            if (path.Count == 1)
            {
                var (cc, rr) = path[0];
                var x = (float)(spec.MinX + (cc + 0.5) * cell);
                var y = (float)(spec.MinY + (rr + 0.5) * cell);
                segments.Add(new GpuRiverSeg
                {
                    Endpoints = new Vector4(x, y, x, y),
                    ArcBase = 0,
                    SegLen = 0,
                    PathIdx = pi,
                    Pad = 0
                });
                segPerPath[pi] = 1;
                continue;
            }

            var acc = 0.0;
            for (var i = 0; i < path.Count - 1; i++)
            {
                var (gc0, gr0) = path[i];
                var (gc1, gr1) = path[i + 1];
                var x0 = (float)(spec.MinX + (gc0 + 0.5) * cell);
                var y0 = (float)(spec.MinY + (gr0 + 0.5) * cell);
                var x1 = (float)(spec.MinX + (gc1 + 0.5) * cell);
                var y1 = (float)(spec.MinY + (gr1 + 0.5) * cell);
                var dx = (gc1 - gc0) * cellF;
                var dy = (gr1 - gr0) * cellF;
                var sl = Math.Sqrt(dx * dx + dy * dy);
                segments.Add(new GpuRiverSeg
                {
                    Endpoints = new Vector4(x0, y0, x1, y1),
                    ArcBase = (float)acc,
                    SegLen = (float)sl,
                    PathIdx = pi,
                    Pad = 0
                });
                acc += sl;
            }

            segPerPath[pi] = path.Count - 1;
        }

        _ = pathLens;

        if (segments.Count > MaxSegments)
        {
            segments = new List<GpuRiverSeg>();
            tileOffsets = Array.Empty<uint>();
            packedTileSegIdx = Array.Empty<uint>();
            return false;
        }

        var tilesX = (cols + TileW - 1) / TileW;
        var tilesY = (rows + TileH - 1) / TileH;
        var tileCount = tilesX * tilesY;
        var lists = new List<uint>[tileCount];
        for (var i = 0; i < tileCount; i++)
            lists[i] = new List<uint>();

        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                var tid = ty * tilesX + tx;
                var tc0 = tx * TileW;
                var tr0 = ty * TileH;
                var tc1Ex = Math.Min((tx + 1) * TileW, cols);
                var tr1Ex = Math.Min((ty + 1) * TileH, rows);
                var tc0w = Math.Max(0, tc0 - expandCells);
                var tc1xw = Math.Min(cols, tc1Ex + expandCells);
                var tr0w = Math.Max(0, tr0 - expandCells);
                var tr1xw = Math.Min(rows, tr1Ex + expandCells);

                for (var pi = 0; pi < pathCount; pi++)
                {
                    if (segPerPath[pi] == 0)
                        continue;
                    if (c1[pi] < tc0w || c0[pi] >= tc1xw || r1[pi] < tr0w || r0[pi] >= tr1xw)
                        continue;
                    var s0 = segStart[pi];
                    for (var k = 0; k < segPerPath[pi]; k++)
                        lists[tid].Add((uint)(s0 + k));
                }
            }
        }

        var totalIdx = 0;
        foreach (var list in lists)
            totalIdx += list.Count;
        if (totalIdx > MaxTileIndexEntries)
        {
            segments = new List<GpuRiverSeg>();
            tileOffsets = Array.Empty<uint>();
            packedTileSegIdx = Array.Empty<uint>();
            return false;
        }

        tileOffsets = new uint[tileCount + 1];
        packedTileSegIdx = totalIdx == 0 ? Array.Empty<uint>() : new uint[totalIdx];
        var w = 0;
        for (var i = 0; i < tileCount; i++)
        {
            tileOffsets[i] = (uint)w;
            foreach (var ix in lists[i])
                packedTileSegIdx[w++] = ix;
        }

        tileOffsets[tileCount] = (uint)w;
        return true;
    }
}
