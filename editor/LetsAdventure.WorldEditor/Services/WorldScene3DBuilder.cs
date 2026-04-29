using System.Windows.Media;
using System.Windows.Media.Media3D;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor.Services;

public sealed class WorldScene3DOptions
{
    public bool Terrain { get; set; } = true;
    public bool Regions { get; set; } = true;
    public bool Territories { get; set; } = true;
    public bool Features { get; set; } = true;
    public bool NavGraph { get; set; } = true;

    /// <summary>Micro-segments per nav cell edge in the Helix terrain mesh (1 = coarse, 6 = very dense).</summary>
    public int TerrainSubdivisionsPerCell { get; set; } = 3;

    /// <summary>World XY used for aerial perspective (near = sharp, far = hazy). When null, nav grid center is used.</summary>
    public Vec3? TerrainPerspectiveAnchorWorld { get; set; }

    /// <summary>Blend distant terrain color toward sky for depth (editor preview).</summary>
    public bool TerrainAerialPerspective { get; set; } = true;

    /// <summary>World XY used to center the high-res terrain patch when the nav grid is chunked. Usually orbit focus.</summary>
    public Vec3? TerrainDetailAnchorWorld { get; set; }

    /// <summary>
    /// For chunked nav grids: nav cells on each side of the anchor to build at full subdiv resolution. 0 = distant coarse only.
    /// </summary>
    public int TerrainDetailCellHalfExtent { get; set; } = 72;

    /// <summary>Hard cap on detail patch width/height in cells (after chunk alignment).</summary>
    public int TerrainDetailMaxCellsPerAxis { get; set; } = 288;

    /// <summary>
    /// When &gt; 0, terrain and Helix overlays are limited to a square of this half-extent (meters in world X/Y)
    /// around <see cref="TerrainDetailAnchorWorld"/> (or nav center). 0 = no clip (full world; can be very slow).
    /// </summary>
    public double TerrainClipHalfExtentM { get; set; } = 1000;
}

/// <summary>Builds a WPF 3D model: world X,Y horizontal and Z up map to WPF (X, Z, -Y) so Y is up in the viewport.</summary>
public static class WorldScene3DBuilder
{
    public static Model3DGroup Build(PhysicalWorldDefinition world, WorldScene3DOptions opt)
    {
        var root = new Model3DGroup();

        if (opt.Terrain)
            AddTerrainMeshes(root, world);

        if (opt.Regions)
        {
            foreach (var reg in world.RegionBoundaries)
                AddBoundaryPolyline(root, reg.Boundary.Vertices, (reg.Boundary.ZMin + reg.Boundary.ZMax) * 0.5,
                    Color.FromRgb(65, 145, 255), 1.2);
        }

        if (opt.Territories)
        {
            foreach (var t in world.Territories)
                AddBoundaryPolyline(root, t.Boundary.Vertices, (t.Boundary.ZMin + t.Boundary.ZMax) * 0.5,
                    Color.FromRgb(255, 120, 60), 1.0);
        }

        if (opt.Features)
        {
            foreach (var f in world.Features)
                AddFeature(root, f, world);
        }

        if (opt.NavGraph && world.Navigation.Graph is { } g)
            AddNavGraph(root, g);

        return root;
    }

    /// <summary>World (x,y,z) → WPF 3D point with Y up.</summary>
    public static Point3D WorldToWpf(Vec3 w) => new(w.X, w.Z, -w.Y);

    public static Point3D WorldToWpf(double x, double y, double z) => new(x, z, -y);

    private static void AddTerrainMeshes(Model3DGroup root, PhysicalWorldDefinition world)
    {
        var grid = world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1)
        {
            var fb = BuildBoundsFallback(world);
            if (fb is not null)
                root.Children.Add(fb);
            return;
        }

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cs = grid.CellSize <= 0 ? 1 : grid.CellSize;
        var ox = grid.OriginX;
        var oy = grid.OriginY;
        var cells = grid.Cells;
        var expected = cols * rows;
        if (cells is null || cells.Count < expected)
        {
            cells = new List<NavCellDefinition>(expected);
            for (var i = 0; i < expected; i++)
                cells.Add(new NavCellDefinition());
        }

        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var c in cells)
        {
            zMin = Math.Min(zMin, c.ElevationZ);
            zMax = Math.Max(zMax, c.ElevationZ);
        }

        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < cols; i++)
            {
                var c = cells[j * cols + i];
                var z00 = AverageCornerElevation(cells, cols, rows, i, j);
                var z10 = AverageCornerElevation(cells, cols, rows, i + 1, j);
                var z11 = AverageCornerElevation(cells, cols, rows, i + 1, j + 1);
                var z01 = AverageCornerElevation(cells, cols, rows, i, j + 1);
                var x0 = ox + i * cs;
                var y0 = oy + j * cs;
                var p00 = WorldToWpf(x0, y0, z00);
                var p10 = WorldToWpf(x0 + cs, y0, z10);
                var p11 = WorldToWpf(x0 + cs, y0 + cs, z11);
                var p01 = WorldToWpf(x0, y0 + cs, z01);
                var mesh = new MeshGeometry3D
                {
                    Positions = new Point3DCollection { p00, p10, p11, p01 },
                    TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 },
                };
                mesh.Freeze();
                var color = CellColor(c, zMin, zMax);
                var mat = new DiffuseMaterial(new SolidColorBrush(color));
                root.Children.Add(new GeometryModel3D(mesh, mat)
                {
                    BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(
                        (byte)Math.Min(255, color.R + 25),
                        (byte)Math.Min(255, color.G + 25),
                        (byte)Math.Min(255, color.B + 25)))),
                });
            }
        }
    }

    private static double AverageCornerElevation(IReadOnlyList<NavCellDefinition> cells, int cols, int rows, int vi,
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
        return n == 0 ? 0 : sum / n;
    }

    private static Color CellColor(NavCellDefinition c, double zMin, double zMax)
    {
        if (!c.Walkable && (c.FluidDepth > 0.01 || c.Composition == SurfaceComposition.Water))
            return Color.FromRgb(13, 97, 224);

        var baseCol = c.Composition switch
        {
            SurfaceComposition.Rock => Color.FromRgb(158, 148, 138),
            SurfaceComposition.Sand => Color.FromRgb(245, 209, 107),
            SurfaceComposition.Mud => Color.FromRgb(122, 84, 56),
            SurfaceComposition.ForestFloor => Color.FromRgb(36, 122, 51),
            SurfaceComposition.Soil => Color.FromRgb(153, 112, 71),
            SurfaceComposition.Grass => Color.FromRgb(56, 199, 51),
            SurfaceComposition.Ice => Color.FromRgb(184, 220, 242),
            SurfaceComposition.Pavement => Color.FromRgb(122, 122, 132),
            _ => HeightBlendRgb(c.ElevationZ, zMin, zMax),
        };

        return ModulateRgbWithRelief(baseCol, c.ElevationZ, zMin, zMax);
    }

    private static Color ModulateRgbWithRelief(Color baseCol, double z, double zMin, double zMax)
    {
        var span = zMax - zMin;
        var t = span > 1e-6 ? (z - zMin) / span : 0.5;
        t = Math.Clamp(t, 0, 1);
        var shade = 0.68 + 0.32 * t;
        return Color.FromRgb(
            (byte)Math.Clamp(baseCol.R * shade * 1.05, 0, 255),
            (byte)Math.Clamp(baseCol.G * shade, 0, 255),
            (byte)Math.Clamp(baseCol.B * (0.9 + 0.1 * t), 0, 255));
    }

    private static Color HeightBlendRgb(double z, double zMin, double zMax)
    {
        var span = zMax - zMin;
        var t = span > 1e-6 ? (z - zMin) / span : 0.5;
        t = Math.Clamp(t, 0, 1);
        var r = (byte)(70 + 100 * t);
        var g = (byte)(120 + 80 * (1 - t));
        var b = (byte)(55 + 40 * (1 - t));
        return Color.FromRgb(r, g, b);
    }

    private static GeometryModel3D? BuildBoundsFallback(PhysicalWorldDefinition world)
    {
        var min = world.GlobalBounds.Min;
        var max = world.GlobalBounds.Max;
        var z = min.Z;
        var p00 = WorldToWpf(min.X, min.Y, z);
        var p10 = WorldToWpf(max.X, min.Y, z);
        var p11 = WorldToWpf(max.X, max.Y, z);
        var p01 = WorldToWpf(min.X, max.Y, z);
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { p00, p10, p11, p01 },
            TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 },
        };
        mesh.Freeze();
        return new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(120, 125, 130))));
    }

    private static void AddBoundaryPolyline(Model3DGroup root, IReadOnlyList<GeoVec2> verts, double zWorld,
        Color color, double thickness)
    {
        if (verts.Count < 2)
            return;
        var closed = verts.Count > 2;
        var segCount = closed ? verts.Count : verts.Count - 1;
        for (var i = 0; i < segCount; i++)
        {
            var a = verts[i];
            var b = closed ? verts[(i + 1) % verts.Count] : verts[i + 1];
            AddThickSegment(root, WorldToWpf(a.X, a.Y, zWorld), WorldToWpf(b.X, b.Y, zWorld), thickness, color);
        }
    }

    private static void AddThickSegment(Model3DGroup root, Point3D a, Point3D b, double thickness, Color color)
    {
        var d = b - a;
        var len = d.Length;
        if (len < 1e-6)
            return;
        d.Normalize();
        var up = Math.Abs(d.Y) < 0.95 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
        var u = Vector3D.CrossProduct(up, d);
        u.Normalize();
        var v = Vector3D.CrossProduct(d, u);
        v.Normalize();
        var r = thickness * 0.5;
        var p0 = a + u * r;
        var p1 = a - u * r;
        var p2 = b - u * r;
        var p3 = b + u * r;
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { p0, p1, p2, p3 },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        mesh.Freeze();
        root.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color))));
    }

    private static void AddFeature(Model3DGroup root, PhysicalTerrainFeature f, PhysicalWorldDefinition world)
    {
        switch (f)
        {
            case GroundPlateauFeature g:
                AddPolygonFan(root, g.Boundary, g.ElevationZ, Color.FromRgb(100, 170, 90), 0.85);
                break;
            case PathCorridorFeature p:
                for (var i = 0; i < p.Centerline.Count - 1; i++)
                {
                    var z = SampleGroundZ(world, p.Centerline[i].X, p.Centerline[i].Y) + 0.5;
                    var a = WorldToWpf(p.Centerline[i].X, p.Centerline[i].Y, z);
                    var z2 = SampleGroundZ(world, p.Centerline[i + 1].X, p.Centerline[i + 1].Y) + 0.5;
                    var b = WorldToWpf(p.Centerline[i + 1].X, p.Centerline[i + 1].Y, z2);
                    AddThickSegment(root, a, b, p.HalfWidth * 0.35, Color.FromRgb(85, 130, 75));
                }

                break;
            case StandingWaterFeature w:
                AddPolygonFan(root, w.Shoreline, w.WaterSurfaceZ, Color.FromRgb(40, 100, 190), 0.75);
                break;
            case FlowingWaterFeature fw:
                for (var i = 0; i < fw.ChannelCenterline.Count - 1; i++)
                {
                    var a = fw.ChannelCenterline[i];
                    var b = fw.ChannelCenterline[i + 1];
                    var za = fw.WaterSurfaceZ;
                    var zb = fw.WaterSurfaceZ;
                    AddThickSegment(root, WorldToWpf(a.X, a.Y, za), WorldToWpf(b.X, b.Y, zb),
                        fw.ChannelHalfWidth * 0.25, Color.FromRgb(55, 130, 220));
                }

                break;
            case BuildingFootprintFeature b:
                AddPolygonExtrude(root, b.Footprint, b.BaseZ, b.RoofZ, Color.FromRgb(160, 150, 140));
                break;
            case SolidVolumeFeature s:
                AddAxisBox(root, s.Bounds, s.Composition);
                break;
            case MountainRidgeFeature m:
                for (var i = 0; i < m.RidgeLine.Count; i++)
                {
                    var q = m.RidgeLine[i];
                    var t = m.RidgeLine.Count > 1 ? i / (double)(m.RidgeLine.Count - 1) : 0;
                    var z = m.BaseElevationZ + (m.PeakElevationZ - m.BaseElevationZ) * t;
                    AddSphere(root, WorldToWpf(q.X, q.Y, z), 2.5, Color.FromRgb(120, 115, 110));
                }

                break;
            case VegetationVolumeFeature v:
                AddPolygonFan(root, v.Boundary, v.ZMax * 0.5 + v.ZMin * 0.5, Color.FromRgb(40, 110, 50), 0.5);
                break;
        }
    }

    private static void AddPolygonFan(Model3DGroup root, IReadOnlyList<GeoVec2> poly, double z, Color color, double opacity)
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
        var center = WorldToWpf(cx, cy, z);
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var p0 = center;
            var p1 = WorldToWpf(a.X, a.Y, z);
            var p2 = WorldToWpf(b.X, b.Y, z);
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection { p0, p1, p2 },
                TriangleIndices = new Int32Collection { 0, 1, 2 },
            };
            mesh.Freeze();
            root.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(brush)));
        }
    }

    private static void AddPolygonExtrude(Model3DGroup root, IReadOnlyList<GeoVec2> poly, double z0, double z1,
        Color color)
    {
        if (poly.Count < 3)
            return;
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var p00 = WorldToWpf(a.X, a.Y, z0);
            var p01 = WorldToWpf(a.X, a.Y, z1);
            var p10 = WorldToWpf(b.X, b.Y, z0);
            var p11 = WorldToWpf(b.X, b.Y, z1);
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection { p00, p10, p11, p01 },
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
            };
            mesh.Freeze();
            root.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color))));
        }
    }

    private static void AddAxisBox(Model3DGroup root, AxisAlignedBounds bounds, SurfaceComposition comp)
    {
        var min = bounds.Min;
        var max = bounds.Max;
        var c = comp == SurfaceComposition.Rock ? Color.FromRgb(110, 105, 100) : Color.FromRgb(140, 130, 120);
        var corners = new[]
        {
            WorldToWpf(min.X, min.Y, min.Z), WorldToWpf(max.X, min.Y, min.Z), WorldToWpf(max.X, max.Y, min.Z),
            WorldToWpf(min.X, max.Y, min.Z),
            WorldToWpf(min.X, min.Y, max.Z), WorldToWpf(max.X, min.Y, max.Z), WorldToWpf(max.X, max.Y, max.Z),
            WorldToWpf(min.X, max.Y, max.Z),
        };
        int[] faces =
        [
            0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0,
        ];
        var pos = new Point3DCollection();
        foreach (var p in corners)
            pos.Add(p);
        var idx = new Int32Collection();
        foreach (var t in faces)
            idx.Add(t);
        var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx };
        mesh.Freeze();
        root.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(c))));
    }

    private static void AddNavGraph(Model3DGroup root, NavigationGraphDefinition g)
    {
        var posById = g.Nodes.ToDictionary(n => n.Id, n => n.Position, StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            AddSphere(root, WorldToWpf(n.Position), 2.0, Color.FromRgb(255, 220, 60));
        }

        foreach (var e in g.Edges)
        {
            if (!posById.TryGetValue(e.FromId, out var a) || !posById.TryGetValue(e.ToId, out var b))
                continue;
            AddThickSegment(root, WorldToWpf(a), WorldToWpf(b), 0.35, Color.FromRgb(255, 200, 80));
            if (e.Bidirectional)
                continue;
        }
    }

    private static void AddSphere(Model3DGroup root, Point3D center, double radius, Color color, int lat = 5,
        int lon = 6)
    {
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        for (var j = 0; j <= lat; j++)
        {
            var v = Math.PI * j / lat;
            var y = radius * Math.Cos(v);
            var ringR = radius * Math.Sin(v);
            for (var i = 0; i <= lon; i++)
            {
                var u = 2 * Math.PI * i / lon;
                var x = ringR * Math.Cos(u);
                var z = ringR * Math.Sin(u);
                positions.Add(center + new Vector3D(x, y, z));
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
                indices.Add(c);
                indices.Add(b);
                indices.Add(b);
                indices.Add(c);
                indices.Add(d);
            }
        }

        var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        mesh.Freeze();
        root.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color))));
    }

    public static double SampleSurfaceElevation(PhysicalWorldDefinition world, double wx, double wy) =>
        SampleGroundZ(world, wx, wy);

    private static double SampleGroundZ(PhysicalWorldDefinition world, double wx, double wy)
    {
        var grid = world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1)
            return world.GlobalBounds.Min.Z;
        var cs = grid.CellSize <= 0 ? 1 : grid.CellSize;
        var fx = (wx - grid.OriginX) / cs;
        var fy = (wy - grid.OriginY) / cs;
        var c0 = (int)Math.Floor(fx);
        var r0 = (int)Math.Floor(fy);
        if (c0 < 0 || r0 < 0 || c0 >= grid.Columns || r0 >= grid.Rows)
            return world.GlobalBounds.Min.Z;
        if (world.NavGridCellSource?.TryGetCell(c0, r0, out var chunkCell) == true)
            return chunkCell.ElevationZ;
        var cells = grid.Cells;
        if (cells is null || cells.Count < grid.Columns * grid.Rows)
            return world.GlobalBounds.Min.Z;
        return cells[r0 * grid.Columns + c0].ElevationZ;
    }
}
