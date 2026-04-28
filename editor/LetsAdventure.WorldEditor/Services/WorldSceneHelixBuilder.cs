using System.Windows.Media;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using Color4 = SharpDX.Color4;
using HelixMesh = HelixToolkit.Wpf.SharpDX.MeshGeometry3D;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.World;
using MediaColor = System.Windows.Media.Color;

namespace LetsAdventure.WorldEditor.Services;

/// <summary>Builds a batched Direct3D11 scene for <see cref="Viewport3DX"/> (GPU-friendly draw calls).</summary>
public static class WorldSceneHelixBuilder
{
    /// <summary>
    /// World Z (up) is mapped to Helix Y; with strong XY shrink, multiply elevation scale so hills read in the editor.
    /// </summary>
    private const float TerrainVerticalExaggeration = 12f;

    /// <summary>Unlit terrain so vertex colors read clearly at any lighting angle.</summary>
    private static readonly DiffuseMaterial TerrainVertexMaterial = new()
    {
        DiffuseColor = Color4.White,
        EnableUnLit = true,
        VertexColorBlendingFactor = 1f,
    };

    private static readonly PhongMaterial VertexColorPhong = new()
    {
        DiffuseColor = Color4.White,
        AmbientColor = new Color4(0.14f, 0.15f, 0.16f, 1f),
        SpecularColor = new Color4(0.08f, 0.08f, 0.08f, 1f),
        SpecularShininess = 6f,
        VertexColorBlendingFactor = 1f,
    };

    /// <summary>Ground reference at nav grid center (world X,Y,Z).</summary>
    public static bool TryGetSceneOriginGround(PhysicalWorldDefinition world, out Vec3 originWorld)
    {
        originWorld = default;
        var g = world.Navigation.Grid;
        if (g is null || g.Columns < 1 || g.Rows < 1)
            return false;
        var cols = g.Columns;
        var rows = g.Rows;
        var cs = g.CellSize <= 0 ? 1 : g.CellSize;
        var cx = g.OriginX + cols * cs * 0.5;
        var cy = g.OriginY + rows * cs * 0.5;
        var ci = cols / 2;
        var rj = rows / 2;
        double gz;
        if (g.Cells is { Count: var n } && n >= cols * rows)
            gz = g.Cells[rj * cols + ci].ElevationZ;
        else if (world.NavGridCellSource?.TryGetCell(ci, rj, out var cell) == true)
            gz = cell.ElevationZ;
        else
            return false;
        originWorld = new Vec3 { X = cx, Y = cy, Z = gz };
        return true;
    }

    /// <summary>
    /// Uniform scale for editor view: maps the larger XY extent to ~3000 view units so the GPU sees a compact scene
    /// (avoids far-plane clip and depth precision loss at 10⁶ world coordinates).
    /// </summary>
    public static bool TryGetViewMapping(PhysicalWorldDefinition world, out Vec3 originGroundWorld,
        out float uniformScale, out float heightScale)
    {
        originGroundWorld = default;
        uniformScale = 1f;
        heightScale = 1f;
        if (world.Navigation.Grid is not { } navGrid)
            return false;
        if (!TryGetSceneOriginGround(world, out originGroundWorld))
            return false;
        var extent = Math.Max(navGrid.Columns * Math.Max(navGrid.CellSize, 1e-6),
            navGrid.Rows * Math.Max(navGrid.CellSize, 1e-6));
        uniformScale = extent > 1e-6 ? (float)(3000.0 / extent) : 1f;
        heightScale = uniformScale * TerrainVerticalExaggeration;
        return true;
    }

    /// <summary>Horizontal scale only (orbit distance), matching terrain X/Z in view space.</summary>
    public static bool TryGetViewMapping(PhysicalWorldDefinition world, out Vec3 originGroundWorld,
        out float uniformScale) =>
        TryGetViewMapping(world, out originGroundWorld, out uniformScale, out _);

    /// <summary>Camera / orbit: same transform as mesh (Helix Y = world Z with exaggeration).</summary>
    public static System.Windows.Media.Media3D.Point3D ScaledFocusFromWorld(Vec3 sceneOriginGround, Vec3 focusWorld,
        float uniformScale, float heightScale)
    {
        var o = WorldToHelix((float)sceneOriginGround.X, (float)sceneOriginGround.Y, (float)sceneOriginGround.Z);
        var f = WorldToHelix((float)focusWorld.X, (float)focusWorld.Y, (float)focusWorld.Z);
        var w = f - o;
        return new System.Windows.Media.Media3D.Point3D(w.X * uniformScale, w.Y * heightScale, w.Z * uniformScale);
    }

    public static GroupModel3D Build(PhysicalWorldDefinition world, WorldScene3DOptions opt)
    {
        var root = new GroupModel3D();
        if (!TryCreateSceneTransform(world, out var originHelix, out var scale, out var hScale))
        {
            originHelix = SharpDX.Vector3.Zero;
            scale = 1f;
            hScale = 1f;
        }

        if (opt.Terrain)
        {
            var terrain = BuildTerrainLayers(world, opt, originHelix, scale, hScale);
            if (terrain is not null && terrain.Children.Count > 0)
                root.Children.Add(terrain);
        }

        if (opt.Regions)
        {
            foreach (var reg in world.RegionBoundaries)
                AddBoundaryLines(root, reg.Boundary.Vertices, (reg.Boundary.ZMin + reg.Boundary.ZMax) * 0.5,
                    MediaColor.FromRgb(65, 145, 255), originHelix, scale, hScale);
        }

        if (opt.Territories)
        {
            foreach (var t in world.Territories)
                AddBoundaryLines(root, t.Boundary.Vertices, (t.Boundary.ZMin + t.Boundary.ZMax) * 0.5,
                    MediaColor.FromRgb(255, 120, 60), originHelix, scale, hScale);
        }

        if (opt.Features)
        {
            foreach (var f in world.Features)
                AddFeatureMeshes(root, f, world, originHelix, scale, hScale);
        }

        if (opt.NavGraph && world.Navigation.Graph is { } g)
            AddNavGraphMeshes(root, g, originHelix, scale, hScale);

        return root;
    }

    private static bool TryCreateSceneTransform(PhysicalWorldDefinition world, out SharpDX.Vector3 originHelix,
        out float scale, out float heightScale)
    {
        originHelix = SharpDX.Vector3.Zero;
        scale = 1f;
        heightScale = 1f;
        if (!TryGetViewMapping(world, out var ow, out scale, out heightScale))
            return false;
        originHelix = WorldToHelix((float)ow.X, (float)ow.Y, (float)ow.Z);
        return true;
    }

    private static SharpDX.Vector3 ToHelixRel(float worldX, float worldY, float worldZ, SharpDX.Vector3 originHelix,
        float xyScale, float heightScale)
    {
        var w = WorldToHelix(worldX, worldY, worldZ) - originHelix;
        return new SharpDX.Vector3(w.X * xyScale, w.Y * heightScale, w.Z * xyScale);
    }

    private static float BilinearScalar(float z00, float z10, float z01, float z11, float u, float v)
    {
        var a = z00 + (z10 - z00) * u;
        var b = z01 + (z11 - z01) * u;
        return a + (b - a) * v;
    }

    private static Color4 LerpColor4(Color4 a, Color4 b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color4(
            a.Red + (b.Red - a.Red) * t,
            a.Green + (b.Green - a.Green) * t,
            a.Blue + (b.Blue - a.Blue) * t,
            a.Alpha + (b.Alpha - a.Alpha) * t);
    }

    private static Color4 BilinearColor4(Color4 c00, Color4 c10, Color4 c01, Color4 c11, float u, float v)
    {
        var x = LerpColor4(c00, c10, u);
        var y = LerpColor4(c01, c11, u);
        return LerpColor4(x, y, v);
    }

    /// <summary>Smoothstep aerial fade: near terrain keeps color, far shifts toward horizon sky.</summary>
    private static Color4 ApplyAerialPerspective(Color4 ground, float wx, float wy, double ax, double ay, double nearW,
        double farW)
    {
        var dx = wx - (float)ax;
        var dy = wy - (float)ay;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var t = (dist - nearW) / Math.Max(farW - nearW, 1.0);
        t = Math.Clamp(t, 0, 1);
        t = t * t * (3 - 2 * t);
        var sky = new Color4(0.53f, 0.74f, 0.94f, 1f);
        return LerpColor4(ground, sky, (float)(t * 0.72));
    }

    private readonly struct ChunkIndexRanges(int cxMin, int cxMax, int cyMin, int cyMax)
    {
        public int CxMin { get; } = cxMin;
        public int CxMax { get; } = cxMax;
        public int CyMin { get; } = cyMin;
        public int CyMax { get; } = cyMax;
    }

    private static GroupModel3D? BuildTerrainLayers(PhysicalWorldDefinition world, WorldScene3DOptions opt,
        SharpDX.Vector3 originHelix, float scale, float heightScale)
    {
        var grid = world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1)
        {
            var fb = BuildBoundsFallbackMesh(world, originHelix, scale, heightScale);
            return fb is null ? null : new GroupModel3D { Children = { fb } };
        }

        var cols = grid.Columns;
        var rows = grid.Rows;
        var expected = cols * rows;
        var cells = grid.Cells;
        if (cells is null || cells.Count < expected)
        {
            if (world.NavGridCellSource is NavGridChunkCellSource chunked)
                return BuildChunkedTerrainLayers(world, opt, chunked, originHelix, scale, heightScale);
            var fb = BuildBoundsFallbackMesh(world, originHelix, scale, heightScale);
            return fb is null ? null : new GroupModel3D { Children = { fb } };
        }

        var mesh = BuildBatchedTerrain(world, opt, originHelix, scale, heightScale);
        return mesh is null ? null : new GroupModel3D { Children = { mesh } };
    }

    private static GroupModel3D? BuildChunkedTerrainLayers(PhysicalWorldDefinition world, WorldScene3DOptions opt,
        NavGridChunkCellSource chunked, SharpDX.Vector3 originHelix, float scale, float heightScale)
    {
        var manifest = chunked.Manifest;
        var cols = manifest.Columns;
        var rows = manifest.Rows;
        var cs = manifest.CellSize <= 0 ? 1 : manifest.CellSize;
        var ox = manifest.OriginX;
        var oy = manifest.OriginY;
        var cw = manifest.ChunkWidthCells;
        var ch = manifest.ChunkHeightCells;
        var zMin = manifest.GlobalZMin;
        var zMax = manifest.GlobalZMax;

        ChunkIndexRanges? omitChunks = null;
        MeshGeometryModel3D? detail = null;
        var halfExt = Math.Max(0, opt.TerrainDetailCellHalfExtent);
        if (halfExt > 0)
        {
            Vec3? anchor = opt.TerrainDetailAnchorWorld;
            if (anchor is null && TryGetSceneOriginGround(world, out var og))
                anchor = og;
            anchor ??= new Vec3 { X = ox + cols * cs * 0.5, Y = oy + rows * cs * 0.5, Z = 0 };

            var axw = anchor.Value.X;
            var ayw = anchor.Value.Y;
            var cCenter = (int)Math.Floor((axw - ox) / cs);
            var rCenter = (int)Math.Floor((ayw - oy) / cs);
            cCenter = Math.Clamp(cCenter, 0, cols - 1);
            rCenter = Math.Clamp(rCenter, 0, rows - 1);

            var maxSpan = Math.Clamp(opt.TerrainDetailMaxCellsPerAxis, 48, 512);
            var cMin = Math.Max(0, cCenter - halfExt);
            var cMax = Math.Min(cols - 1, cCenter + halfExt);
            var rMin = Math.Max(0, rCenter - halfExt);
            var rMax = Math.Min(rows - 1, rCenter + halfExt);

            var spanC = cMax - cMin + 1;
            if (spanC > maxSpan)
            {
                var excess = spanC - maxSpan;
                cMin += excess / 2;
                cMax = cMin + maxSpan - 1;
                if (cMax > cols - 1)
                {
                    cMax = cols - 1;
                    cMin = Math.Max(0, cMax - maxSpan + 1);
                }
            }

            var spanR = rMax - rMin + 1;
            if (spanR > maxSpan)
            {
                var excess = spanR - maxSpan;
                rMin += excess / 2;
                rMax = rMin + maxSpan - 1;
                if (rMax > rows - 1)
                {
                    rMax = rows - 1;
                    rMin = Math.Max(0, rMax - maxSpan + 1);
                }
            }

            var cxMin = cMin / cw;
            var cxMax = cMax / cw;
            var cyMin = rMin / ch;
            var cyMax = rMax / ch;

            var col0Fine = cxMin * cw;
            var col1Fine = Math.Min(cols - 1, (cxMax + 1) * cw - 1);
            var row0Fine = cyMin * ch;
            var row1Fine = Math.Min(rows - 1, (cyMax + 1) * ch - 1);

            detail = BuildChunkNavDetailMesh(opt, manifest, chunked, originHelix, scale, heightScale, col0Fine,
                col1Fine, row0Fine, row1Fine, zMin, zMax);
            if (detail is not null)
                omitChunks = new ChunkIndexRanges(cxMin, cxMax, cyMin, cyMax);
        }

        var coarse = BuildCoarseChunkLodTerrain(world, opt, manifest, originHelix, scale, heightScale, omitChunks);
        var g = new GroupModel3D();
        if (coarse is not null)
            g.Children.Add(coarse);
        if (detail is not null)
            g.Children.Add(detail);
        return g.Children.Count == 0 ? null : g;
    }

    /// <summary>One flat shaded quad per on-disk chunk; optional <paramref name="omitChunks"/> skips chunks replaced by the detail mesh.</summary>
    private static MeshGeometryModel3D? BuildCoarseChunkLodTerrain(PhysicalWorldDefinition world, WorldScene3DOptions opt,
        NavGridChunkManifest manifest, SharpDX.Vector3 originHelix, float scale, float heightScale,
        ChunkIndexRanges? omitChunks)
    {
        var cols = manifest.Columns;
        var rows = manifest.Rows;
        var cs = manifest.CellSize <= 0 ? 1 : manifest.CellSize;
        var ox = manifest.OriginX;
        var oy = manifest.OriginY;
        var cw = manifest.ChunkWidthCells;
        var ch = manifest.ChunkHeightCells;
        var zMin = manifest.GlobalZMin;
        var zMax = manifest.GlobalZMax;

        var mapSpan = Math.Max(cols * cs, rows * cs);
        var hazeNear = Math.Max(cs * 35, mapSpan * 0.018);
        var hazeFar = Math.Max(mapSpan * 0.14, hazeNear * 4);
        double ax, ay;
        if (opt.TerrainPerspectiveAnchorWorld is { } aw)
        {
            ax = aw.X;
            ay = aw.Y;
        }
        else
        {
            ax = ox + cols * cs * 0.5;
            ay = oy + rows * cs * 0.5;
        }

        var useHaze = opt.TerrainAerialPerspective;
        var positions = new Vector3Collection(manifest.Chunks.Count * 4);
        var colors = new Color4Collection(manifest.Chunks.Count * 4);
        var indices = new IntCollection(manifest.Chunks.Count * 6);

        var vi = 0;
        foreach (var e in manifest.Chunks)
        {
            if (omitChunks is { } om && e.Cx >= om.CxMin && e.Cx <= om.CxMax && e.Cy >= om.CyMin && e.Cy <= om.CyMax)
                continue;

            var col0 = e.Cx * cw;
            var row0 = e.Cy * ch;
            var wCells = Math.Min(cw, cols - col0);
            var hCells = Math.Min(ch, rows - row0);
            if (wCells <= 0 || hCells <= 0)
                continue;

            var x0 = (float)(ox + col0 * cs);
            var y0 = (float)(oy + row0 * cs);
            var x1 = (float)(ox + (col0 + wCells) * cs);
            var y1 = (float)(oy + (row0 + hCells) * cs);
            var zh = (float)e.AvgZ;

            var syn = SyntheticCellForChunkLod(e);
            var baseCol = CellToColor4(syn, zMin, zMax);

            void AddCorner(float wx, float wy)
            {
                positions.Add(ToHelixRel(wx, wy, zh, originHelix, scale, heightScale));
                var col = baseCol;
                if (useHaze)
                    col = ApplyAerialPerspective(col, wx, wy, ax, ay, hazeNear, hazeFar);
                colors.Add(col);
            }

            AddCorner(x0, y0);
            AddCorner(x1, y0);
            AddCorner(x1, y1);
            AddCorner(x0, y1);

            indices.Add(vi);
            indices.Add(vi + 1);
            indices.Add(vi + 2);
            indices.Add(vi);
            indices.Add(vi + 2);
            indices.Add(vi + 3);
            vi += 4;
        }

        if (positions.Count == 0)
            return omitChunks is not null ? null : BuildBoundsFallbackMesh(world, originHelix, scale, heightScale);

        var geom = new HelixMesh
        {
            Positions = positions,
            TriangleIndices = indices,
            Colors = colors,
        };
        geom.Normals = MeshGeometryHelper.CalculateNormals(geom);
        return new MeshGeometryModel3D
        {
            Geometry = geom,
            Material = TerrainVertexMaterial,
            CullMode = SharpDX.Direct3D11.CullMode.Back,
        };
    }

    private static MeshGeometryModel3D? BuildChunkNavDetailMesh(
        WorldScene3DOptions opt,
        NavGridChunkManifest manifest,
        NavGridChunkCellSource source,
        SharpDX.Vector3 originHelix,
        float scale,
        float heightScale,
        int col0,
        int col1,
        int row0,
        int row1,
        double zMin,
        double zMax)
    {
        var patchCols = col1 - col0 + 1;
        var patchRows = row1 - row0 + 1;
        if (patchCols < 1 || patchRows < 1)
            return null;

        var cs = manifest.CellSize <= 0 ? 1 : manifest.CellSize;
        var ox = manifest.OriginX + col0 * cs;
        var oy = manifest.OriginY + row0 * cs;

        var patchCells = new NavCellDefinition[patchCols * patchRows];
        var defaultCell = new NavCellDefinition { ElevationZ = (zMin + zMax) * 0.5, Walkable = true };
        for (var j = 0; j < patchRows; j++)
        {
            for (var i = 0; i < patchCols; i++)
            {
                var gc = col0 + i;
                var gr = row0 + j;
                patchCells[j * patchCols + i] = source.TryGetCell(gc, gr, out var cell) ? cell : defaultCell;
            }
        }

        var sub = Math.Clamp(opt.TerrainSubdivisionsPerCell, 1, 8);
        var smoothPasses = Math.Clamp(6 + sub * 2, 8, 18);
        var smoothAlpha = Math.Clamp(0.48f + sub * 0.028f, 0.48f, 0.62f);
        var cornerZ = BuildSmoothedCornerHeights(patchCells, patchCols, patchRows, smoothPasses, smoothAlpha);

        var cols = manifest.Columns;
        var rows = manifest.Rows;
        var mapSpan = Math.Max(cols * cs, rows * cs);
        var hazeNear = Math.Max(cs * 35, mapSpan * 0.018);
        var hazeFar = Math.Max(mapSpan * 0.14, hazeNear * 4);
        double ax, ay;
        if (opt.TerrainPerspectiveAnchorWorld is { } aw)
        {
            ax = aw.X;
            ay = aw.Y;
        }
        else
        {
            ax = manifest.OriginX + cols * cs * 0.5;
            ay = manifest.OriginY + rows * cs * 0.5;
        }

        var useHaze = opt.TerrainAerialPerspective;
        var vx = patchCols * sub + 1;
        var vy = patchRows * sub + 1;
        var cellColors = new Color4[patchCols * patchRows];
        for (var j = 0; j < patchRows; j++)
        {
            for (var i = 0; i < patchCols; i++)
                cellColors[j * patchCols + i] = CellToColor4(patchCells[j * patchCols + i], zMin, zMax);
        }

        Color4 CellColorAt(int ci, int cj) =>
            cellColors[Math.Clamp(cj, 0, patchRows - 1) * patchCols + Math.Clamp(ci, 0, patchCols - 1)];

        var positions = new Vector3Collection(vx * vy);
        var colors = new Color4Collection(vx * vy);
        var indices = new IntCollection(patchCols * patchRows * sub * sub * 6);

        var dCell = cs / sub;
        for (var gj = 0; gj <= patchRows * sub; gj++)
        {
            for (var gi = 0; gi <= patchCols * sub; gi++)
            {
                var fc = gi / (double)sub;
                var fr = gj / (double)sub;
                var i0 = Math.Clamp((int)Math.Floor(fc + 1e-9), 0, patchCols - 1);
                var j0 = Math.Clamp((int)Math.Floor(fr + 1e-9), 0, patchRows - 1);
                var u = (float)Math.Clamp(fc - i0, 0, 1);
                var v = (float)Math.Clamp(fr - j0, 0, 1);
                var ci1 = Math.Min(i0 + 1, patchCols - 1);
                var cj1 = Math.Min(j0 + 1, patchRows - 1);

                var z00 = cornerZ[i0, j0];
                var z10 = cornerZ[i0 + 1, j0];
                var z01 = cornerZ[i0, j0 + 1];
                var z11 = cornerZ[i0 + 1, j0 + 1];
                var zh = BilinearScalar(z00, z10, z01, z11, u, v);

                var wx = (float)(ox + gi * dCell);
                var wy = (float)(oy + gj * dCell);
                positions.Add(ToHelixRel(wx, wy, zh, originHelix, scale, heightScale));
                var col = BilinearColor4(
                    CellColorAt(i0, j0),
                    CellColorAt(ci1, j0),
                    CellColorAt(i0, cj1),
                    CellColorAt(ci1, cj1),
                    u,
                    v);
                if (useHaze)
                    col = ApplyAerialPerspective(col, wx, wy, ax, ay, hazeNear, hazeFar);
                colors.Add(col);
            }
        }

        int V(int gix, int gjy) => gjy * vx + gix;
        for (var gj = 0; gj < patchRows * sub; gj++)
        {
            for (var gi = 0; gi < patchCols * sub; gi++)
            {
                var i00 = V(gi, gj);
                var i10 = i00 + 1;
                var i01 = i00 + vx;
                var i11 = i01 + 1;
                indices.Add(i00);
                indices.Add(i10);
                indices.Add(i11);
                indices.Add(i00);
                indices.Add(i11);
                indices.Add(i01);
            }
        }

        var geom = new HelixMesh
        {
            Positions = positions,
            TriangleIndices = indices,
            Colors = colors,
        };
        geom.Normals = MeshGeometryHelper.CalculateNormals(geom);

        return new MeshGeometryModel3D
        {
            Geometry = geom,
            Material = TerrainVertexMaterial,
            CullMode = SharpDX.Direct3D11.CullMode.Back,
        };
    }

    private static NavCellDefinition SyntheticCellForChunkLod(NavGridChunkLodEntry e)
    {
        var water = e.WaterFraction01 >= 0.45;
        return new NavCellDefinition
        {
            ElevationZ = e.AvgZ,
            Walkable = !water,
            FluidDepth = water ? 1 : 0,
            Composition = water ? SurfaceComposition.Water : SurfaceComposition.Soil,
        };
    }

    /// <summary>Full in-memory nav grid only (chunked worlds use <see cref="BuildTerrainLayers"/>).</summary>
    private static MeshGeometryModel3D? BuildBatchedTerrain(PhysicalWorldDefinition world, WorldScene3DOptions opt,
        SharpDX.Vector3 originHelix, float scale, float heightScale)
    {
        var grid = world.Navigation.Grid!;
        var cols = grid.Columns;
        var rows = grid.Rows;
        var cs = grid.CellSize <= 0 ? 1 : grid.CellSize;
        var ox = grid.OriginX;
        var oy = grid.OriginY;
        var cells = grid.Cells!;

        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var c in cells)
        {
            zMin = Math.Min(zMin, c.ElevationZ);
            zMax = Math.Max(zMax, c.ElevationZ);
        }

        var sub = Math.Clamp(opt.TerrainSubdivisionsPerCell, 1, 8);
        var smoothPasses = Math.Clamp(6 + sub * 2, 8, 18);
        var smoothAlpha = Math.Clamp(0.48f + sub * 0.028f, 0.48f, 0.62f);
        var cornerZ = BuildSmoothedCornerHeights(cells, cols, rows, smoothPasses, smoothAlpha);

        var mapSpan = Math.Max(cols * cs, rows * cs);
        var hazeNear = Math.Max(cs * 35, mapSpan * 0.018);
        var hazeFar = Math.Max(mapSpan * 0.14, hazeNear * 4);
        double ax, ay;
        if (opt.TerrainPerspectiveAnchorWorld is { } aw)
        {
            ax = aw.X;
            ay = aw.Y;
        }
        else if (TryGetSceneOriginGround(world, out var og))
        {
            ax = og.X;
            ay = og.Y;
        }
        else
        {
            ax = ox + cols * cs * 0.5;
            ay = oy + rows * cs * 0.5;
        }

        var useHaze = opt.TerrainAerialPerspective;
        var vx = cols * sub + 1;
        var vy = rows * sub + 1;
        var cellColors = new Color4[cols * rows];
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < cols; i++)
                cellColors[j * cols + i] = CellToColor4(cells[j * cols + i], zMin, zMax);
        }

        Color4 CellColorAt(int ci, int cj) => cellColors[Math.Clamp(cj, 0, rows - 1) * cols + Math.Clamp(ci, 0, cols - 1)];

        var positions = new Vector3Collection(vx * vy);
        var colors = new Color4Collection(vx * vy);
        var indices = new IntCollection(cols * rows * sub * sub * 6);

        var dCell = cs / sub;
        for (var gj = 0; gj <= rows * sub; gj++)
        {
            for (var gi = 0; gi <= cols * sub; gi++)
            {
                var fc = gi / (double)sub;
                var fr = gj / (double)sub;
                var i0 = Math.Clamp((int)Math.Floor(fc + 1e-9), 0, cols - 1);
                var j0 = Math.Clamp((int)Math.Floor(fr + 1e-9), 0, rows - 1);
                var u = (float)Math.Clamp(fc - i0, 0, 1);
                var v = (float)Math.Clamp(fr - j0, 0, 1);
                var ci1 = Math.Min(i0 + 1, cols - 1);
                var cj1 = Math.Min(j0 + 1, rows - 1);

                var z00 = cornerZ[i0, j0];
                var z10 = cornerZ[i0 + 1, j0];
                var z01 = cornerZ[i0, j0 + 1];
                var z11 = cornerZ[i0 + 1, j0 + 1];
                var zh = BilinearScalar(z00, z10, z01, z11, u, v);

                var wx = (float)(ox + gi * dCell);
                var wy = (float)(oy + gj * dCell);
                positions.Add(ToHelixRel(wx, wy, zh, originHelix, scale, heightScale));
                var col = BilinearColor4(
                    CellColorAt(i0, j0),
                    CellColorAt(ci1, j0),
                    CellColorAt(i0, cj1),
                    CellColorAt(ci1, cj1),
                    u,
                    v);
                if (useHaze)
                    col = ApplyAerialPerspective(col, wx, wy, ax, ay, hazeNear, hazeFar);
                colors.Add(col);
            }
        }

        int V(int gix, int gjy) => gjy * vx + gix;
        for (var gj = 0; gj < rows * sub; gj++)
        {
            for (var gi = 0; gi < cols * sub; gi++)
            {
                var i00 = V(gi, gj);
                var i10 = i00 + 1;
                var i01 = i00 + vx;
                var i11 = i01 + 1;
                indices.Add(i00);
                indices.Add(i10);
                indices.Add(i11);
                indices.Add(i00);
                indices.Add(i11);
                indices.Add(i01);
            }
        }

        var geom = new HelixMesh
        {
            Positions = positions,
            TriangleIndices = indices,
            Colors = colors,
        };
        geom.Normals = MeshGeometryHelper.CalculateNormals(geom);

        return new MeshGeometryModel3D
        {
            Geometry = geom,
            Material = TerrainVertexMaterial,
            CullMode = SharpDX.Direct3D11.CullMode.Back,
        };
    }

    private static float[,] BuildSmoothedCornerHeights(IReadOnlyList<NavCellDefinition> cells, int cols, int rows,
        int passes, float alpha)
    {
        var w = cols + 1;
        var h = rows + 1;
        var z = new float[w, h];
        for (var vj = 0; vj <= rows; vj++)
        {
            for (var vi = 0; vi <= cols; vi++)
                z[vi, vj] = AverageCornerElevation(cells, cols, rows, vi, vj);
        }

        for (var p = 0; p < passes; p++)
        {
            var copy = (float[,])z.Clone();
            for (var vj = 0; vj <= rows; vj++)
            {
                for (var vi = 0; vi <= cols; vi++)
                {
                    var lap = Average4(copy, vi, vj, w, h);
                    z[vi, vj] = (1 - alpha) * copy[vi, vj] + alpha * lap;
                }
            }
        }

        return z;
    }

    private static float Average4(float[,] copy, int vi, int vj, int w, int h)
    {
        double s = 0;
        var n = 0;
        void N(int i, int j)
        {
            if ((uint)i >= (uint)w || (uint)j >= (uint)h)
                return;
            s += copy[i, j];
            n++;
        }

        N(vi - 1, vj);
        N(vi + 1, vj);
        N(vi, vj - 1);
        N(vi, vj + 1);
        return n == 0 ? copy[vi, vj] : (float)(s / n);
    }

    private static MeshGeometryModel3D? BuildBoundsFallbackMesh(PhysicalWorldDefinition world,
        SharpDX.Vector3 originHelix, float scale, float heightScale)
    {
        var min = world.GlobalBounds.Min;
        var max = world.GlobalBounds.Max;
        var z = (float)min.Z;
        var mx = (float)((min.X + max.X) * 0.5);
        var my = (float)((min.Y + max.Y) * 0.5);
        var oz = WorldToHelix(mx, my, z);
        var useOrigin = originHelix.LengthSquared() < 1e-12 ? oz : originHelix;
        var p00 = ToHelixRel((float)min.X, (float)min.Y, z, useOrigin, scale, heightScale);
        var p10 = ToHelixRel((float)max.X, (float)min.Y, z, useOrigin, scale, heightScale);
        var p11 = ToHelixRel((float)max.X, (float)max.Y, z, useOrigin, scale, heightScale);
        var p01 = ToHelixRel((float)min.X, (float)max.Y, z, useOrigin, scale, heightScale);
        var gray = new Color4(0.45f, 0.48f, 0.5f, 1f);
        var geom = new HelixMesh
        {
            Positions = new Vector3Collection { p00, p10, p11, p01 },
            TriangleIndices = new IntCollection { 0, 1, 2, 0, 2, 3 },
            Colors = new Color4Collection { gray, gray, gray, gray },
        };
        geom.Normals = MeshGeometryHelper.CalculateNormals(geom);
        return new MeshGeometryModel3D { Geometry = geom, Material = TerrainVertexMaterial };
    }

    private static SharpDX.Vector3 WorldToHelix(float worldX, float worldY, float worldZ) =>
        new(worldX, worldZ, -worldY);

    /// <summary>Averages adjacent cell elevations so shared grid corners align — continuous hills and valleys.</summary>
    private static float AverageCornerElevation(IReadOnlyList<NavCellDefinition> cells, int cols, int rows, int vi,
        int vj)
    {
        double sum = 0;
        var n = 0;
        void Add(int ci, int cj)
        {
            if ((uint)ci >= (uint)cols || (uint)cj >= (uint)rows)
                return;
            sum += cells[cj * cols + ci].ElevationZ;
            n++;
        }

        Add(vi - 1, vj - 1);
        Add(vi, vj - 1);
        Add(vi - 1, vj);
        Add(vi, vj);
        return n == 0 ? 0f : (float)(sum / n);
    }

    private static Color4 CellToColor4(NavCellDefinition c, double zMin, double zMax)
    {
        if (!c.Walkable && (c.FluidDepth > 0.01 || c.Composition == SurfaceComposition.Water))
            return new Color4(0.05f, 0.38f, 0.88f, 1f);

        var baseCol = c.Composition switch
        {
            SurfaceComposition.Rock => new Color4(0.62f, 0.58f, 0.54f, 1f),
            SurfaceComposition.Sand => new Color4(0.96f, 0.82f, 0.42f, 1f),
            SurfaceComposition.Mud => new Color4(0.48f, 0.33f, 0.22f, 1f),
            SurfaceComposition.ForestFloor => new Color4(0.14f, 0.48f, 0.2f, 1f),
            SurfaceComposition.Soil => new Color4(0.6f, 0.44f, 0.28f, 1f),
            SurfaceComposition.Grass => new Color4(0.22f, 0.78f, 0.2f, 1f),
            SurfaceComposition.Ice => new Color4(0.72f, 0.86f, 0.95f, 1f),
            SurfaceComposition.Pavement => new Color4(0.48f, 0.48f, 0.52f, 1f),
            _ => HeightBlendColor4(c.ElevationZ, zMin, zMax),
        };

        return ModulateColorWithRelief(baseCol, c.ElevationZ, zMin, zMax);
    }

    private static Color4 ModulateColorWithRelief(Color4 baseCol, double z, double zMin, double zMax)
    {
        var span = zMax - zMin;
        var t = span > 1e-6 ? (float)((z - zMin) / span) : 0.5f;
        t = Math.Clamp(t, 0, 1);
        var shade = 0.68f + 0.32f * t;
        return new Color4(
            Math.Min(1f, baseCol.Red * shade * 1.05f),
            Math.Min(1f, baseCol.Green * shade),
            Math.Min(1f, baseCol.Blue * (0.9f + 0.1f * t)),
            1f);
    }

    private static Color4 HeightBlendColor4(double z, double zMin, double zMax)
    {
        var span = zMax - zMin;
        var t = span > 1e-6 ? (z - zMin) / span : 0.5;
        t = Math.Clamp(t, 0, 1);
        var r = (float)(0.28 + 0.4 * t);
        var g = (float)(0.45 + 0.35 * (1 - t));
        var b = (float)(0.2 + 0.2 * (1 - t));
        return new Color4(r, g, b, 1f);
    }

    private static void AddBoundaryLines(GroupModel3D root, IReadOnlyList<GeoVec2> verts, double zWorld,
        MediaColor color, SharpDX.Vector3 originHelix, float xyScale, float heightScale)
    {
        if (verts.Count < 2)
            return;
        var closed = verts.Count > 2;
        var n = closed ? verts.Count : verts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = verts[i];
            var b = closed ? verts[(i + 1) % verts.Count] : verts[i + 1];
            var p0 = ToHelixRel((float)a.X, (float)a.Y, (float)zWorld, originHelix, xyScale, heightScale);
            var p1 = ToHelixRel((float)b.X, (float)b.Y, (float)zWorld, originHelix, xyScale, heightScale);
            var geom = new HelixMesh
            {
                Positions = new Vector3Collection { p0, p1 },
                Indices = new IntCollection { 0, 1 },
            };
            root.Children.Add(new LineGeometryModel3D
            {
                Geometry = geom,
                Color = color,
                Thickness = Math.Max(0.5, 2.5 * xyScale),
            });
        }
    }

    private static void AddFeatureMeshes(GroupModel3D root, PhysicalTerrainFeature f, PhysicalWorldDefinition world,
        SharpDX.Vector3 originHelix, float xyScale, float heightScale)
    {
        switch (f)
        {
            case PathCorridorFeature p:
                AddPolylineTube(root, p.Centerline, 1.2f, MediaColor.FromRgb(85, 130, 75), world, originHelix,
                    xyScale, heightScale);
                break;
            case StandingWaterFeature w:
                AddPolygonFanMesh(root, w.Shoreline, (float)w.WaterSurfaceZ, MediaColor.FromRgb(40, 100, 190), 0.75f,
                    originHelix, xyScale, heightScale);
                break;
            case FlowingWaterFeature fw:
                for (var i = 0; i < fw.ChannelCenterline.Count - 1; i++)
                {
                    var a = fw.ChannelCenterline[i];
                    var b = fw.ChannelCenterline[i + 1];
                    var p0 = ToHelixRel((float)a.X, (float)a.Y, (float)fw.WaterSurfaceZ, originHelix, xyScale,
                        heightScale);
                    var p1 = ToHelixRel((float)b.X, (float)b.Y, (float)fw.WaterSurfaceZ, originHelix, xyScale,
                        heightScale);
                    var geom = new HelixMesh
                    {
                        Positions = new Vector3Collection { p0, p1 },
                        Indices = new IntCollection { 0, 1 },
                    };
                    root.Children.Add(new LineGeometryModel3D
                    {
                        Geometry = geom,
                        Color = MediaColor.FromRgb(55, 130, 220),
                        Thickness = Math.Max(1.5, fw.ChannelHalfWidth * 0.08 * xyScale),
                    });
                }

                break;
            case GroundPlateauFeature g:
                AddPolygonFanMesh(root, g.Boundary, (float)g.ElevationZ, MediaColor.FromRgb(100, 170, 90), 0.9f,
                    originHelix, xyScale, heightScale);
                break;
            case BuildingFootprintFeature b:
                AddExtrudedPolygon(root, b.Footprint, (float)b.BaseZ, (float)b.RoofZ,
                    MediaColor.FromRgb(160, 150, 140), originHelix, xyScale, heightScale);
                break;
            case SolidVolumeFeature s:
                AddAxisAlignedBox(root, s.Bounds, MediaColor.FromRgb(110, 105, 100), originHelix, xyScale,
                    heightScale);
                break;
            case MountainRidgeFeature m:
                for (var i = 0; i < m.RidgeLine.Count; i++)
                {
                    var q = m.RidgeLine[i];
                    var t = m.RidgeLine.Count > 1 ? i / (double)(m.RidgeLine.Count - 1) : 0;
                    var z = (float)(m.BaseElevationZ + (m.PeakElevationZ - m.BaseElevationZ) * t);
                    var ctr = ToHelixRel((float)q.X, (float)q.Y, z, originHelix, xyScale, heightScale);
                    AddSphereApprox(root, ctr, Math.Max(0.35f, 2.5f * xyScale), MediaColor.FromRgb(120, 115, 110));
                }

                break;
            case VegetationVolumeFeature v:
                AddPolygonFanMesh(root, v.Boundary, (float)(v.ZMin * 0.5 + v.ZMax * 0.5),
                    MediaColor.FromRgb(40, 110, 50), 0.55f, originHelix, xyScale, heightScale);
                break;
        }
    }

    private static void AddPolylineTube(GroupModel3D root, IReadOnlyList<GeoVec2> line, float halfWidth,
        MediaColor color, PhysicalWorldDefinition world, SharpDX.Vector3 originHelix, float xyScale, float heightScale)
    {
        for (var i = 0; i < line.Count - 1; i++)
        {
            var z = (float)(WorldScene3DBuilder.SampleSurfaceElevation(world, line[i].X, line[i].Y) + 0.5);
            var z2 = (float)(WorldScene3DBuilder.SampleSurfaceElevation(world, line[i + 1].X, line[i + 1].Y) + 0.5);
            var p0 = ToHelixRel((float)line[i].X, (float)line[i].Y, z, originHelix, xyScale, heightScale);
            var p1 = ToHelixRel((float)line[i + 1].X, (float)line[i + 1].Y, z2, originHelix, xyScale, heightScale);
            var geom = new HelixMesh
            {
                Positions = new Vector3Collection { p0, p1 },
                Indices = new IntCollection { 0, 1 },
            };
            root.Children.Add(new LineGeometryModel3D
            {
                Geometry = geom,
                Color = color,
                Thickness = Math.Max(1.0, halfWidth * 0.35 * xyScale),
            });
        }
    }

    private static void AddPolygonFanMesh(GroupModel3D root, IReadOnlyList<GeoVec2> poly, float z, MediaColor color,
        float alpha, SharpDX.Vector3 originHelix, float xyScale, float heightScale)
    {
        if (poly.Count < 3)
            return;
        double cx = 0, cy = 0;
        foreach (var p in poly)
        {
            cx += p.X;
            cy += p.Y;
        }

        cx /= poly.Count;
        cy /= poly.Count;
        var center = ToHelixRel((float)cx, (float)cy, z, originHelix, xyScale, heightScale);
        var c4 = ToColor4(color, alpha);
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var p1 = ToHelixRel((float)a.X, (float)a.Y, z, originHelix, xyScale, heightScale);
            var p2 = ToHelixRel((float)b.X, (float)b.Y, z, originHelix, xyScale, heightScale);
            var geom = new HelixMesh
            {
                Positions = new Vector3Collection { center, p1, p2 },
                TriangleIndices = new IntCollection { 0, 2, 1 },
                Colors = new Color4Collection { c4, c4, c4 },
            };
            root.Children.Add(new MeshGeometryModel3D
            {
                Geometry = geom,
                Material = VertexColorPhong,
            });
        }
    }

    private static void AddExtrudedPolygon(GroupModel3D root, IReadOnlyList<GeoVec2> poly, float z0, float z1,
        MediaColor color, SharpDX.Vector3 originHelix, float xyScale, float heightScale)
    {
        if (poly.Count < 3)
            return;
        var c4 = ToColor4(color);
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var p00 = ToHelixRel((float)a.X, (float)a.Y, z0, originHelix, xyScale, heightScale);
            var p01 = ToHelixRel((float)a.X, (float)a.Y, z1, originHelix, xyScale, heightScale);
            var p10 = ToHelixRel((float)b.X, (float)b.Y, z0, originHelix, xyScale, heightScale);
            var p11 = ToHelixRel((float)b.X, (float)b.Y, z1, originHelix, xyScale, heightScale);
            var geom = new HelixMesh
            {
                Positions = new Vector3Collection { p00, p10, p11, p01 },
                TriangleIndices = new IntCollection { 0, 2, 1, 0, 3, 2 },
                Colors = new Color4Collection { c4, c4, c4, c4 },
            };
            root.Children.Add(new MeshGeometryModel3D { Geometry = geom, Material = VertexColorPhong });
        }
    }

    private static void AddAxisAlignedBox(GroupModel3D root, AxisAlignedBounds bounds, MediaColor color,
        SharpDX.Vector3 originHelix, float xyScale, float heightScale)
    {
        var min = bounds.Min;
        var max = bounds.Max;
        var c4 = ToColor4(color);
        var corners = new[]
        {
            ToHelixRel((float)min.X, (float)min.Y, (float)min.Z, originHelix, xyScale, heightScale),
            ToHelixRel((float)max.X, (float)min.Y, (float)min.Z, originHelix, xyScale, heightScale),
            ToHelixRel((float)max.X, (float)max.Y, (float)min.Z, originHelix, xyScale, heightScale),
            ToHelixRel((float)min.X, (float)max.Y, (float)min.Z, originHelix, xyScale, heightScale),
            ToHelixRel((float)min.X, (float)min.Y, (float)max.Z, originHelix, xyScale, heightScale),
            ToHelixRel((float)max.X, (float)min.Y, (float)max.Z, originHelix, xyScale, heightScale),
            ToHelixRel((float)max.X, (float)max.Y, (float)max.Z, originHelix, xyScale, heightScale),
            ToHelixRel((float)min.X, (float)max.Y, (float)max.Z, originHelix, xyScale, heightScale),
        };
        int[] faces =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 5, 4, 0, 1, 5, 1, 6, 5, 1, 2, 6, 2, 7, 6, 2, 3, 7, 3, 4, 7, 3, 0, 4,
        ];
        var pos = new Vector3Collection();
        foreach (var p in corners)
            pos.Add(p);
        var idx = new IntCollection();
        var cols = new Color4Collection();
        foreach (var t in faces)
            idx.Add(t);
        for (var i = 0; i < 8; i++)
            cols.Add(c4);
        var geom = new HelixMesh { Positions = pos, TriangleIndices = idx, Colors = cols };
        root.Children.Add(new MeshGeometryModel3D { Geometry = geom, Material = VertexColorPhong });
    }

    private static void AddSphereApprox(GroupModel3D root, SharpDX.Vector3 center, float radius, MediaColor color)
    {
        const int lat = 4;
        const int lon = 5;
        var positions = new Vector3Collection();
        var indices = new IntCollection();
        var c4 = ToColor4(color);
        var colors = new Color4Collection();
        for (var j = 0; j <= lat; j++)
        {
            var v = (float)(Math.PI * j / lat);
            var y = radius * MathF.Cos(v);
            var ringR = radius * MathF.Sin(v);
            for (var i = 0; i <= lon; i++)
            {
                var u = (float)(2 * Math.PI * i / lon);
                var x = ringR * MathF.Cos(u);
                var z = ringR * MathF.Sin(u);
                positions.Add(center + new SharpDX.Vector3(x, y, z));
                colors.Add(c4);
            }
        }

        for (var j = 0; j < lat; j++)
        {
            for (var i = 0; i < lon; i++)
            {
                var a = j * (lon + 1) + i;
                var b = a + 1;
                var c = a + lon + 1;
                var d = c + 1;
                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
                indices.Add(b);
                indices.Add(d);
                indices.Add(c);
            }
        }

        var geom = new HelixMesh { Positions = positions, TriangleIndices = indices, Colors = colors };
        root.Children.Add(new MeshGeometryModel3D { Geometry = geom, Material = VertexColorPhong });
    }

    private static void AddNavGraphMeshes(GroupModel3D root, NavigationGraphDefinition g, SharpDX.Vector3 originHelix,
        float xyScale, float heightScale)
    {
        var posById = g.Nodes.ToDictionary(n => n.Id, n => n.Position, StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            var p = n.Position;
            var ctr = ToHelixRel((float)p.X, (float)p.Y, (float)p.Z, originHelix, xyScale, heightScale);
            AddSphereApprox(root, ctr, Math.Max(0.25f, 2f * xyScale), MediaColor.FromRgb(255, 220, 60));
        }

        foreach (var e in g.Edges)
        {
            if (!posById.TryGetValue(e.FromId, out var a) || !posById.TryGetValue(e.ToId, out var b))
                continue;
            var p0 = ToHelixRel((float)a.X, (float)a.Y, (float)a.Z, originHelix, xyScale, heightScale);
            var p1 = ToHelixRel((float)b.X, (float)b.Y, (float)b.Z, originHelix, xyScale, heightScale);
            var geom = new HelixMesh
            {
                Positions = new Vector3Collection { p0, p1 },
                Indices = new IntCollection { 0, 1 },
            };
            root.Children.Add(new LineGeometryModel3D
            {
                Geometry = geom,
                Color = MediaColor.FromRgb(255, 200, 80),
                Thickness = Math.Max(0.35, 1.2 * xyScale),
            });
        }
    }

    private static Color4 ToColor4(MediaColor c, float alpha = 1f) =>
        new(c.R / 255f, c.G / 255f, c.B / 255f, alpha * (c.A / 255f));
}
