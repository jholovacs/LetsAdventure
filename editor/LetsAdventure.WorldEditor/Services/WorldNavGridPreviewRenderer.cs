using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor.Services;

/// <summary>High-detail 2D preview: one pixel per nav cell with composition, vegetation, and optional hillshade.</summary>
public static class WorldNavGridPreviewRenderer
{
    public static Image? TryBuildNavGridImage(
        PhysicalWorldDefinition world,
        double cellPixels,
        bool hillshade,
        double marginOx,
        double marginOy)
    {
        var grid = world.Navigation.Grid;
        if (grid?.Cells is null || grid.Columns < 1 || grid.Rows < 1)
            return null;

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cells = grid.Cells;
        var bmp = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
        var stride = cols * 4;
        var buffer = new byte[stride * rows];

        var (zMin, zMax) = ElevRange(cells, cols, rows);
        var zSpan = Math.Max(1e-3, zMax - zMin);

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var cell = cells[r * cols + c];
                var (b, g, r8, a) = PixelBgra(cell, c, r, cols, rows, cells, zMin, zSpan, hillshade);
                var o = r * stride + c * 4;
                buffer[o] = b;
                buffer[o + 1] = g;
                buffer[o + 2] = r8;
                buffer[o + 3] = a;
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, cols, rows), buffer, stride, 0);

        var img = new Image
        {
            Source = bmp,
            Width = cols * cellPixels,
            Height = rows * cellPixels,
        };
        Canvas.SetLeft(img, marginOx);
        Canvas.SetTop(img, marginOy);
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
        return img;
    }

    private static (double zMin, double zMax) ElevRange(IReadOnlyList<NavCellDefinition> cells, int cols, int rows)
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

    private static (byte b, byte g, byte r, byte a) PixelBgra(
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
