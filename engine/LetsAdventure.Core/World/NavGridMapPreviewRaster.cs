namespace LetsAdventure.Core.World;

/// <summary>CPU nav-cell coloring for 2D map previews (no WPF dependency).</summary>
public static class NavGridMapPreviewRaster
{
    /// <summary>Encode a nav-cell rectangle at <paramref name="superSample"/> BGRA pixels per nav cell (row-major BGRA32).</summary>
    /// <param name="globalZMin">When both <paramref name="globalZMin"/> and <paramref name="globalZMax"/> are set, used for elevation coloring instead of patch-local range (consistent across tiles).</param>
    public static byte[]? TryEncodePatchBgra(
        IReadOnlyList<NavCellDefinition> cells,
        int cols,
        int rows,
        int patchC0,
        int patchC1,
        int patchR0,
        int patchR1,
        int superSample,
        bool hillshade,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        double? globalZMin = null,
        double? globalZMax = null)
    {
        var pc = patchC1 - patchC0 + 1;
        var pr = patchR1 - patchR0 + 1;
        if (pc < 1 || pr < 1)
            return null;
        superSample = Math.Clamp(superSample, 1, 8);
        var bw = pc * superSample;
        var bh = pr * superSample;
        if (bw < 1 || bh < 1 || bw > 20000 || bh > 20000)
            return null;

        double zMin, zMax;
        if (globalZMin is not null && globalZMax is not null && globalZMax.Value > globalZMin.Value - 1e-9)
        {
            zMin = globalZMin.Value;
            zMax = globalZMax.Value;
        }
        else
            (zMin, zMax) = ElevRange(cells, cols, rows);
        var zSpan = Math.Max(1e-3, zMax - zMin);

        var stride = bw * 4;
        var buffer = new byte[stride * bh];
        var total = (long)bw * bh;
        long p = 0;
        for (var jr = 0; jr < bh; jr++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var ic = 0; ic < bw; ic++)
            {
                var nc = patchC0 + ic / superSample;
                var nr = patchR0 + jr / superSample;
                nc = Math.Clamp(nc, 0, cols - 1);
                nr = Math.Clamp(nr, 0, rows - 1);
                var cellIdx = nr * cols + nc;
                if ((uint)cellIdx >= (uint)cells.Count)
                    continue;
                var cell = cells[cellIdx];
                var (b, g, r8, a) = PixelBgra(cell, nc, nr, cols, rows, cells, zMin, zSpan, hillshade);
                var o = jr * stride + ic * 4;
                buffer[o] = b;
                buffer[o + 1] = g;
                buffer[o + 2] = r8;
                buffer[o + 3] = a;
                p++;
                if ((p & 4095) == 0 && total > 0)
                    progress?.Report((int)Math.Clamp(p * 100 / total, 0, 99));
            }
        }

        progress?.Report(100);
        return buffer;
    }

    public static (double zMin, double zMax) ElevRange(IReadOnlyList<NavCellDefinition> cells, int cols, int rows)
    {
        var zMin = double.MaxValue;
        var zMax = double.MinValue;
        for (var i = 0; i < cols * rows; i++)
        {
            var c = cells[i];
            var z = c.ElevationZ;
            if (!c.Walkable && c.FluidDepth > 0.01)
                z = c.WaterSurfaceZ;
            zMin = Math.Min(zMin, z);
            zMax = Math.Max(zMax, z);
        }

        if (zMin > zMax)
            return (0, 1);
        return (zMin, zMax);
    }

    public static (byte b, byte g, byte r, byte a) PixelBgra(
        NavCellDefinition cell,
        int c,
        int r,
        int cols,
        int rows,
        IReadOnlyList<NavCellDefinition> cells,
        double zMin,
        double zSpan,
        bool hillshade)
    {
        var water = cell.Composition == SurfaceComposition.Water
                    || (!cell.Walkable && cell.FluidDepth > 0.01);

        double R, G, B;
        if (water)
        {
            B = 150;
            G = 110;
            R = 55;
        }
        else
        {
            BaseRgbFromComposition(cell.Composition, out R, out G, out B);
            var zRel = (cell.ElevationZ - zMin) / zSpan;
            var lum = 0.72 + 0.28 * (1 - zRel);
            R *= lum;
            G *= lum;
            B *= lum;

            if (!water && cell.Walkable)
            {
                CommunityTint(cell.VegetationCommunity, out var tr, out var tg, out var tb);
                var d = cell.VegetationDensity01;
                var mix = 0.5 * d;
                R = R * (1 - mix) + tr * mix;
                G = G * (1 - mix) + tg * mix;
                B = B * (1 - mix) + tb * mix;

                if ((cell.VegetationStrata & VegetationStratum.Tree) != 0)
                {
                    R *= 0.9;
                    G *= 0.92;
                    B *= 0.94;
                }

                if ((cell.VegetationStrata & VegetationStratum.ForbWildflower) != 0)
                {
                    var bump = 0.22 * d;
                    R = Math.Min(255, R + 28 * bump);
                    G = Math.Min(255, G + 22 * bump);
                }

                if ((cell.VegetationStrata & VegetationStratum.Shrub) != 0)
                {
                    G = Math.Min(255, G + 8 * d);
                    B = Math.Min(255, B + 4 * d);
                }
            }
        }

        if (hillshade && cols > 2 && rows > 2)
        {
            var zC = SampleDisplayZ(cell);
            var zW = c > 0 ? SampleDisplayZ(cells[r * cols + (c - 1)]) : zC;
            var zE = c < cols - 1 ? SampleDisplayZ(cells[r * cols + (c + 1)]) : zC;
            var zN = r > 0 ? SampleDisplayZ(cells[(r - 1) * cols + c]) : zC;
            var zS = r < rows - 1 ? SampleDisplayZ(cells[(r + 1) * cols + c]) : zC;
            var dx = zE - zW;
            var dy = zN - zS;
            var nx = -dx;
            var ny = -dy;
            var nz = zSpan * 0.9;
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-6)
            {
                nx /= len;
                ny /= len;
                nz /= len;
                var lx = 0.55;
                var ly = -0.35;
                var lz = 0.76;
                var shade = nx * lx + ny * ly + nz * lz;
                shade = Math.Clamp(0.42 + 0.78 * shade, 0.38, 1.18);
                R *= shade;
                G *= shade;
                B *= shade;
            }
        }

        return (
            (byte)Math.Clamp(B, 0, 255),
            (byte)Math.Clamp(G, 0, 255),
            (byte)Math.Clamp(R, 0, 255),
            (byte)255);
    }

    private static double SampleDisplayZ(NavCellDefinition c)
    {
        if (c.Composition == SurfaceComposition.Water || (!c.Walkable && c.FluidDepth > 0.01))
            return c.WaterSurfaceZ > 0 ? c.WaterSurfaceZ : c.BedElevationZ;
        return c.ElevationZ;
    }

    private static void BaseRgbFromComposition(SurfaceComposition comp, out double R, out double G, out double B)
    {
        switch (comp)
        {
            case SurfaceComposition.Rock:
                R = 98;
                G = 102;
                B = 108;
                break;
            case SurfaceComposition.Sand:
                R = 210;
                G = 195;
                B = 155;
                break;
            case SurfaceComposition.Mud:
                R = 95;
                G = 85;
                B = 72;
                break;
            case SurfaceComposition.Grass:
                R = 88;
                G = 135;
                B = 78;
                break;
            case SurfaceComposition.ForestFloor:
                R = 68;
                G = 118;
                B = 72;
                break;
            case SurfaceComposition.Ice:
                R = 230;
                G = 245;
                B = 252;
                break;
            case SurfaceComposition.Pavement:
                R = 120;
                G = 118;
                B = 115;
                break;
            default:
                R = 92;
                G = 128;
                B = 84;
                break;
        }
    }

    private static void CommunityTint(VegetationCommunityKind k, out double R, out double G, out double B)
    {
        switch (k)
        {
            case VegetationCommunityKind.AlpineSparse:
                R = 115;
                G = 145;
                B = 105;
                break;
            case VegetationCommunityKind.PlainsHerbaceous:
                R = 72;
                G = 168;
                B = 88;
                break;
            case VegetationCommunityKind.RiparianWoodland:
                R = 48;
                G = 132;
                B = 72;
                break;
            case VegetationCommunityKind.LowlandForest:
                R = 42;
                G = 108;
                B = 58;
                break;
            case VegetationCommunityKind.CoastalSandSparse:
                R = 135;
                G = 175;
                B = 115;
                break;
            case VegetationCommunityKind.TransitionMixed:
                R = 78;
                G = 142;
                B = 82;
                break;
            default:
                R = 88;
                G = 128;
                B = 86;
                break;
        }
    }
}
