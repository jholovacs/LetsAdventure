using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

/// <summary>Large-scale baseline worlds: 2^31 × 2^31 XY, Z ∈ [0, 12288], ground reference 0, BSP lore regions, territories, and extra features.</summary>
public static class BaselinePhysicalWorldGenerator
{
    /// <summary>2^31 — single-world XY extent (WPF/doubles handle this range; nav uses a coarse grid).</summary>
    public const double DefaultExtentXy = 2147483648.0;

    /// <summary>Vertical bound after elevation normalize; large enough that mountain relief is not flattened by clamping.</summary>
    public const double DefaultMaxZ = 12288;

    public const double GroundLevelZ = 0;

    /// <summary>Nav grid resolution (pathfinding / authoring samples over the huge XY extent).</summary>
    public const int BaselineGridDimension = 1024;

    public static PhysicalWorldGenerationResult Generate(int? seed = null, IProgress<string>? progress = null)
    {
        progress?.Report("Starting baseline world generation…");
        var masterSeed = seed ?? Random.Shared.Next();
        progress?.Report($"Master seed: {masterSeed}");
        var rng = new Random(masterSeed);
        var procSeed = rng.Next();

        var span = DefaultExtentXy;
        var cell = span / BaselineGridDimension;

        var proc = new ProceduralWorldSpec
        {
            MinX = 0,
            MinY = 0,
            MaxX = DefaultExtentXy,
            MaxY = DefaultExtentXy,
            MinZBound = GroundLevelZ,
            MaxZBound = DefaultMaxZ,
            CellSize = cell,
            Seed = procSeed,
            MaxLandStepOrthogonal = Math.Max(80, cell * 0.038),
            TerrainAmplitude = 1520,
            NoiseOctaves = 6,
            LakeRadiusWorld = span * 0.015,
            RiverChannelHalfWidthWorld = span * 0.0029,
            LakeDepth = 92,
            FlowDirectionDownstream = new GeoVec2 { X = 1, Y = 0.08 },
            UphillPathPenalty = 34,
            SlopeRelaxationIterations = 56,
            RiverBedCarve = 44,
            GeneratedRegionId = "region.baseline_placeholder",
            UsePerimeterOcean = true,
            ContinentalDomeAmplitude = 880,
            OceanBandMinWorld = span * 0.056,
            OceanBandVariationWorld = span * 0.05,
            OceanDepth = 95,
            TerrainRidgeWeight = 0.18,
            MountainPeakCount = 8,
            MountainLiftScale = 0.86,
            TributaryStreamCount = 14,
            HydrologyMeanderNoise = 0.17,
            InteriorLakeCount = 4,
            MajorRiverCountMin = 3,
            MajorRiverCountMax = 7,
            CoastalShelfCells = 12,
        };

        progress?.Report(
            "Running procedural synthesis (continental terrain, perimeter ocean, lakes, rivers to sea, nav grid). This may take a little while…");
        var result = ProceduralPhysicalWorldGenerator.Generate(proc, progress);
        var world = result.World;
        var report = result.Report;
        progress?.Report("Procedural synthesis complete.");

        var cells = world.Navigation.Grid?.Cells;
        if (cells is { Count: > 0 })
        {
            progress?.Report("Normalizing elevation so minimum land height is Z = 0…");
            var lo = double.MaxValue;
            var hi = double.MinValue;
            foreach (var c in cells)
            {
                lo = Math.Min(lo, c.ElevationZ);
                lo = Math.Min(lo, c.BedElevationZ);
                hi = Math.Max(hi, c.ElevationZ);
                hi = Math.Max(hi, c.BedElevationZ);
            }

            // Shift so minimum ground is 0; preserve vertical deltas (keeps slope limits from procedural pass).
            double Map(double z) => Math.Clamp(z - lo, GroundLevelZ, DefaultMaxZ - 1);

            foreach (var c in cells)
            {
                c.ElevationZ = Map(c.ElevationZ);
                c.BedElevationZ = Map(c.BedElevationZ);
                if (!c.Walkable && c.FluidDepth > 0.01)
                    c.WaterSurfaceZ = Map(c.WaterSurfaceZ);
                else
                    c.WaterSurfaceZ = 0;
                c.FluidDepth = !c.Walkable && c.FluidDepth > 0.01
                    ? Math.Max(0, c.WaterSurfaceZ - c.BedElevationZ)
                    : 0;
            }

            foreach (var f in world.Features)
                AffineMapFeatureZ(f, Map);

            progress?.Report("Vegetation overlay (density, community, strata from terrain & hydrology)…");
            NavGridVegetationOverlay.Apply(world.Navigation.Grid, rng);
        }

        var regionCount = rng.Next(4, 13);
        world.RegionBoundaries.Clear();
        progress?.Report($"Building Voronoi lore regions ({regionCount} seeds, water as barriers) — this step scans the full nav grid…");
        BuildVoronoiRegionsFromTerrain(world, regionCount, rng);
        progress?.Report($"Lore regions: {world.RegionBoundaries.Count}.");

        world.Territories.Clear();
        var factions = new[]
        {
            "faction.baseline.wild",
            "faction.baseline.trade",
            "faction.baseline.temple",
            "faction.baseline.clan",
            "faction.baseline.mining",
        };
        progress?.Report("Placing optional territory claims…");
        for (var i = 0; i < world.RegionBoundaries.Count; i++)
        {
            if (rng.NextDouble() > 0.82)
                continue;
            var reg = world.RegionBoundaries[i];
            world.Territories.Add(new TerritoryClaim
            {
                Id = $"terr.baseline_{i:000}",
                ClaimantFactionId = factions[rng.Next(factions.Length)],
                LoreRegionId = reg.LoreRegionId,
                Priority = rng.Next(1, 6),
                Boundary = CloneBoundary(reg.Boundary),
            });
        }

        progress?.Report("Adding baseline features (paths, vegetation, ridge overlays, plateaus)…");
        AddBaselineFeatures(world, rng, progress);

        report.Messages.Insert(0,
            $"Baseline world seed={masterSeed}, regions={world.RegionBoundaries.Count}, territories={world.Territories.Count}, features={world.Features.Count}, cell={cell:F0}; nav cells carry vegetation density/community/strata overlay.");
        progress?.Report("Baseline world build finished.");

        return new PhysicalWorldGenerationResult { World = world, Report = report };
    }

    private static void AffineMapFeatureZ(PhysicalTerrainFeature f, Func<double, double> map)
    {
        switch (f)
        {
            case FlowingWaterFeature fw:
                fw.WaterSurfaceZ = Math.Clamp(map(fw.WaterSurfaceZ), GroundLevelZ, DefaultMaxZ - 1);
                break;
            case StandingWaterFeature sw:
                sw.WaterSurfaceZ = Math.Clamp(map(sw.WaterSurfaceZ), GroundLevelZ, DefaultMaxZ - 1);
                break;
            case GroundPlateauFeature g:
                g.ElevationZ = Math.Clamp(map(g.ElevationZ), GroundLevelZ, DefaultMaxZ - 1);
                break;
            case BuildingFootprintFeature b:
                b.BaseZ = Math.Clamp(map(b.BaseZ), GroundLevelZ, DefaultMaxZ - 1);
                b.RoofZ = Math.Clamp(map(b.RoofZ), b.BaseZ, DefaultMaxZ);
                break;
            case MountainRidgeFeature m:
                m.BaseElevationZ = Math.Clamp(map(m.BaseElevationZ), GroundLevelZ, DefaultMaxZ - 1);
                m.PeakElevationZ = Math.Clamp(map(m.PeakElevationZ), m.BaseElevationZ, DefaultMaxZ);
                break;
            case SolidVolumeFeature s:
                s.Bounds.Min = new Vec3
                {
                    X = s.Bounds.Min.X,
                    Y = s.Bounds.Min.Y,
                    Z = Math.Clamp(map(s.Bounds.Min.Z), GroundLevelZ, DefaultMaxZ),
                };
                s.Bounds.Max = new Vec3
                {
                    X = s.Bounds.Max.X,
                    Y = s.Bounds.Max.Y,
                    Z = Math.Clamp(map(s.Bounds.Max.Z), GroundLevelZ, DefaultMaxZ),
                };
                break;
            case VegetationVolumeFeature v:
                v.ZMin = Math.Clamp(map(v.ZMin), GroundLevelZ, DefaultMaxZ - 1);
                v.ZMax = Math.Clamp(map(v.ZMax), v.ZMin, DefaultMaxZ);
                break;
        }
    }

    private static PolygonColumnBounds CloneBoundary(PolygonColumnBounds b) =>
        new()
        {
            ZMin = b.ZMin,
            ZMax = b.ZMax,
            Vertices = b.Vertices.Select(v => new GeoVec2 { X = v.X, Y = v.Y }).ToList(),
        };

    /// <summary>
    /// Lore regions with organic boundaries (Voronoi on the nav grid). Water is treated as a barrier id so
    /// shorelines and map edges can bound regions; seeds are placed on walkable land.
    /// </summary>
    private static void BuildVoronoiRegionsFromTerrain(PhysicalWorldDefinition world, int regionCount, Random rng)
    {
        var grid = world.Navigation.Grid;
        if (grid is null || grid.Columns < 4 || grid.Rows < 4 || grid.Cells is null)
            return;

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cs = grid.CellSize <= 0 ? 1 : grid.CellSize;
        var ox = grid.OriginX;
        var oy = grid.OriginY;
        var cells = grid.Cells;

        var seeds = PickLandVoronoiSeeds(grid, regionCount, rng);
        if (seeds.Count == 0)
            return;

        const int barrierId = -999;
        var owner = new int[rows * cols];
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < cols; i++)
            {
                var cell = cells[j * cols + i];
                if (!cell.Walkable || cell.Composition == SurfaceComposition.Water)
                    owner[j * cols + i] = barrierId;
                else
                {
                    var wx = ox + (i + 0.5) * cs;
                    var wy = oy + (j + 0.5) * cs;
                    var best = 0;
                    var bestD = double.PositiveInfinity;
                    for (var s = 0; s < seeds.Count; s++)
                    {
                        var dx = wx - seeds[s].X;
                        var dy = wy - seeds[s].Y;
                        var d = dx * dx + dy * dy;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = s;
                        }
                    }

                    owner[j * cols + i] = best;
                }
            }
        }

        for (var rid = 0; rid < seeds.Count; rid++)
        {
            var poly = ExtractVoronoiRegionPolygon(rid, owner, cols, rows, ox, oy, cs, barrierId);
            if (poly.Count < 3)
                continue;
            world.RegionBoundaries.Add(new RegionBoundaryEntry
            {
                LoreRegionId = $"region.baseline_{rid:000}",
                Boundary = new PolygonColumnBounds
                {
                    ZMin = GroundLevelZ,
                    ZMax = DefaultMaxZ,
                    Vertices = poly,
                },
            });
        }
    }

    private static List<GeoVec2> PickLandVoronoiSeeds(TerrainNavGridDefinition grid, int k, Random rng)
    {
        var cols = grid.Columns;
        var rows = grid.Rows;
        var cs = grid.CellSize <= 0 ? 1 : grid.CellSize;
        var ox = grid.OriginX;
        var oy = grid.OriginY;
        var cells = grid.Cells;
        if (cells is null)
            return [];

        var candidates = new List<(int c, int r)>();
        for (var r = 1; r < rows - 1; r++)
        {
            for (var c = 1; c < cols - 1; c++)
            {
                var cell = cells[r * cols + c];
                if (cell.Walkable && cell.Composition != SurfaceComposition.Water)
                    candidates.Add((c, r));
            }
        }

        if (candidates.Count == 0)
            return [];

        var area = cols * cs * rows * cs;
        var minDist = Math.Sqrt(area / (k * Math.PI)) * 0.42;
        var minDistSq = minDist * minDist;
        var seeds = new List<GeoVec2>();
        var tries = 0;
        while (seeds.Count < k && tries++ < k * 400)
        {
            var (c, r) = candidates[rng.Next(candidates.Count)];
            var wx = ox + (c + 0.5) * cs;
            var wy = oy + (r + 0.5) * cs;
            if (seeds.Count == 0 || seeds.All(s =>
                {
                    var dx = s.X - wx;
                    var dy = s.Y - wy;
                    return dx * dx + dy * dy >= minDistSq;
                }))
            {
                seeds.Add(new GeoVec2 { X = wx, Y = wy });
            }
        }

        while (seeds.Count < k)
        {
            var (c, r) = candidates[rng.Next(candidates.Count)];
            seeds.Add(new GeoVec2 { X = ox + (c + 0.5) * cs, Y = oy + (r + 0.5) * cs });
        }

        return seeds;
    }

    private static List<GeoVec2> ExtractVoronoiRegionPolygon(
        int regionId,
        int[] owner,
        int cols,
        int rows,
        double ox,
        double oy,
        double cs,
        int barrierId)
    {
        double cx = 0, cy = 0;
        var n = 0;
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < cols; i++)
            {
                if (owner[j * cols + i] != regionId)
                    continue;
                cx += ox + (i + 0.5) * cs;
                cy += oy + (j + 0.5) * cs;
                n++;
            }
        }

        if (n == 0)
            return [];

        cx /= n;
        cy /= n;

        int IdAt(int ci, int cr)
        {
            if (ci < 0 || cr < 0 || ci >= cols || cr >= rows)
                return barrierId;
            return owner[cr * cols + ci];
        }

        var verts = new List<(double x, double y)>();
        for (var vi = 0; vi <= cols; vi++)
        {
            for (var vj = 0; vj <= rows; vj++)
            {
                var set = new HashSet<int>
                {
                    IdAt(vi - 1, vj - 1),
                    IdAt(vi, vj - 1),
                    IdAt(vi - 1, vj),
                    IdAt(vi, vj),
                };
                if (!set.Contains(regionId))
                    continue;
                if (!set.Any(x => x != regionId))
                    continue;

                verts.Add((ox + vi * cs, oy + vj * cs));
            }
        }

        if (verts.Count < 3)
            return [];

        verts.Sort((a, b) =>
        {
            var aa = Math.Atan2(a.y - cy, a.x - cx);
            var ab = Math.Atan2(b.y - cy, b.x - cx);
            return aa.CompareTo(ab);
        });

        var poly = new List<GeoVec2>();
        var minSepSq = cs * cs * 0.02;
        foreach (var (x, y) in verts)
        {
            if (poly.Count > 0)
            {
                var last = poly[^1];
                var dx = x - last.X;
                var dy = y - last.Y;
                if (dx * dx + dy * dy < minSepSq)
                    continue;
            }

            poly.Add(new GeoVec2 { X = x, Y = y });
        }

        if (poly.Count >= 3)
        {
            var f = poly[0];
            var l = poly[^1];
            var dx = f.X - l.X;
            var dy = f.Y - l.Y;
            if (dx * dx + dy * dy < minSepSq)
                poly.RemoveAt(poly.Count - 1);
        }

        return poly.Count >= 3 ? poly : [];
    }

    private static void AddBaselineFeatures(PhysicalWorldDefinition world, Random rng, IProgress<string>? progress = null)
    {
        var grid = world.Navigation.Grid;
        if (grid is null || grid.Columns < 4 || grid.Rows < 4 || grid.Cells is null)
            return;

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cs = grid.CellSize;
        var ox = grid.OriginX;
        var oy = grid.OriginY;
        var cells = grid.Cells;
        var maxStep = Math.Max(200, cs * 0.06);

        var pathCount = rng.Next(4, 11);
        progress?.Report($"  • Path corridors: generating up to {pathCount} gradual walks on the grid…");
        for (var p = 0; p < pathCount; p++)
        {
            var line = RandomGradualWalk(cols, rows, cells, ox, oy, cs, rng, rng.Next(18, 55), maxStep);
            if (line.Count < 2)
                continue;
            world.Features.Add(new PathCorridorFeature
            {
                Id = $"feat.baseline.path_{p:000}",
                LayerPriority = 5,
                Centerline = line,
                HalfWidth = cs * 0.12,
                Surface = SurfaceComposition.Pavement,
                MovementCostMultiplier = 0.88,
            });
        }

        var vegCount = rng.Next(3, 9);
        progress?.Report($"  • Vegetation volumes: placing {vegCount} organic regions…");
        for (var v = 0; v < vegCount; v++)
        {
            var cx = rng.NextDouble() * DefaultExtentXy;
            var cy = rng.NextDouble() * DefaultExtentXy;
            var radius = 2500 + rng.NextDouble() * 9000;
            var z = SampleCellElevation(grid, cx, cy);
            if (z is null)
                continue;
            var n = 6 + rng.Next(0, 4);
            var poly = new List<GeoVec2>();
            for (var k = 0; k < n; k++)
            {
                var ang = 2 * Math.PI * k / n + rng.NextDouble() * 0.4;
                poly.Add(new GeoVec2
                {
                    X = cx + radius * Math.Cos(ang),
                    Y = cy + radius * Math.Sin(ang),
                });
            }

            var zBase = z.Value;
            var zHi = Math.Min(DefaultMaxZ - 1, zBase + 45 + rng.NextDouble() * 80);
            var zLo = Math.Max(GroundLevelZ, zBase - 15);
            if (zHi <= zLo)
                zHi = Math.Min(DefaultMaxZ - 1, zLo + 25);
            world.Features.Add(new VegetationVolumeFeature
            {
                Id = $"feat.baseline.veg_{v:000}",
                LayerPriority = 4,
                Boundary = poly,
                ZMin = zLo,
                ZMax = zHi,
                Density01 = 0.35 + rng.NextDouble() * 0.45,
                MovementCostMultiplier = 1.15 + rng.NextDouble() * 0.2,
            });
        }

        var ridgeCount = rng.Next(2, 5);
        progress?.Report($"  • Authoring mountain ridge overlays: {ridgeCount} polylines on high ground…");
        for (var mr = 0; mr < ridgeCount; mr++)
        {
            var line = RidgeWalkHighGround(cols, rows, cells, ox, oy, cs, rng, 14, 48);
            if (line.Count < 2)
                continue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var p in line)
            {
                var z = SampleCellElevation(grid, p.X, p.Y);
                if (z is null)
                    continue;
                minZ = Math.Min(minZ, z.Value);
                maxZ = Math.Max(maxZ, z.Value);
            }

            if (minZ > maxZ)
                continue;
            var peak = Math.Min(DefaultMaxZ - 1, maxZ + 140 + rng.NextDouble() * 320);
            world.Features.Add(new MountainRidgeFeature
            {
                Id = $"feat.baseline.ridge_{mr:000}",
                LayerPriority = 2,
                RidgeLine = line,
                CorridorHalfWidth = Math.Max(cs * 1.1, 2200),
                BaseElevationZ = minZ,
                PeakElevationZ = peak,
                SurfaceComposition = SurfaceComposition.Rock,
            });
        }

        var plateauCount = rng.Next(1, 4);
        progress?.Report($"  • Ground plateaus: {plateauCount} rectangular lifts…");
        for (var pl = 0; pl < plateauCount; pl++)
        {
            var cx = rng.NextDouble() * DefaultExtentXy;
            var cy = rng.NextDouble() * DefaultExtentXy;
            var half = 1800 + rng.NextDouble() * 4000;
            var z0 = SampleCellElevation(grid, cx, cy);
            if (z0 is null)
                continue;
            var bump = 8 + rng.NextDouble() * 35;
            world.Features.Add(new GroundPlateauFeature
            {
                Id = $"feat.baseline.plateau_{pl:000}",
                LayerPriority = 1,
                Boundary =
                [
                    new GeoVec2 { X = cx - half, Y = cy - half },
                    new GeoVec2 { X = cx + half, Y = cy - half },
                    new GeoVec2 { X = cx + half, Y = cy + half },
                    new GeoVec2 { X = cx - half, Y = cy + half },
                ],
                ElevationZ = Math.Min(z0.Value + bump, DefaultMaxZ - 1),
                Composition = SurfaceComposition.Grass,
            });
        }
    }

    private static double? SampleCellElevation(TerrainNavGridDefinition grid, double wx, double wy)
    {
        var cs = grid.CellSize <= 0 ? 1 : grid.CellSize;
        var c = (int)Math.Floor((wx - grid.OriginX) / cs);
        var r = (int)Math.Floor((wy - grid.OriginY) / cs);
        if ((uint)c >= (uint)grid.Columns || (uint)r >= (uint)grid.Rows)
            return null;
        var cells = grid.Cells;
        if (cells is null || r * grid.Columns + c >= cells.Count)
            return null;
        return cells[r * grid.Columns + c].ElevationZ;
    }

    /// <summary>Biased random walk that tends to climb, producing ridge polylines for visualization.</summary>
    private static List<GeoVec2> RidgeWalkHighGround(
        int cols,
        int rows,
        List<NavCellDefinition> cells,
        double ox,
        double oy,
        double cs,
        Random rng,
        int stepsMin,
        int stepsMax)
    {
        var line = new List<GeoVec2>();
        int c = rng.Next(1, cols - 1);
        int r = rng.Next(1, rows - 1);
        if (!cells[r * cols + c].Walkable)
        {
            if (!TryFindWalkableStart(cols, rows, cells, rng, out c, out r))
                return line;
        }

        void AddPt() =>
            line.Add(new GeoVec2 { X = ox + (c + 0.5) * cs, Y = oy + (r + 0.5) * cs });

        AddPt();
        var dirs = new (int dc, int dr)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        var steps = rng.Next(stepsMin, stepsMax);
        for (var s = 0; s < steps; s++)
        {
            var order = dirs.OrderBy(_ => rng.Next()).ToArray();
            (int nc, int nr) choice = (-1, -1);
            if (rng.NextDouble() < 0.72)
            {
                var bestH = double.NegativeInfinity;
                foreach (var (dc, dr) in order)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                        continue;
                    if (!cells[nr * cols + nc].Walkable)
                        continue;
                    var ez = cells[nr * cols + nc].ElevationZ;
                    if (ez > bestH)
                    {
                        bestH = ez;
                        choice = (nc, nr);
                    }
                }
            }
            else
            {
                foreach (var (dc, dr) in order)
                {
                    var nc = c + dc;
                    var nr = r + dr;
                    if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                        continue;
                    if (!cells[nr * cols + nc].Walkable)
                        continue;
                    choice = (nc, nr);
                    break;
                }
            }

            if (choice.nc < 0)
                break;
            if (choice.nc == c && choice.nr == r)
                break;
            c = choice.nc;
            r = choice.nr;
            AddPt();
        }

        return line;
    }

    private static List<GeoVec2> RandomGradualWalk(
        int cols,
        int rows,
        List<NavCellDefinition> cells,
        double ox,
        double oy,
        double cs,
        Random rng,
        int steps,
        double maxStep)
    {
        var line = new List<GeoVec2>();
        int c = rng.Next(1, cols - 1);
        int r = rng.Next(1, rows - 1);
        if (!cells[r * cols + c].Walkable)
        {
            if (!TryFindWalkableStart(cols, rows, cells, rng, out c, out r))
                return line;
        }

        void AddPt()
        {
            line.Add(new GeoVec2 { X = ox + (c + 0.5) * cs, Y = oy + (r + 0.5) * cs });
        }

        AddPt();
        var dirs = new (int dc, int dr)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var s = 0; s < steps; s++)
        {
            var order = dirs.OrderBy(_ => rng.Next()).ToArray();
            var moved = false;
            foreach (var (dc, dr) in order)
            {
                var nc = c + dc;
                var nr = r + dr;
                if ((uint)nc >= (uint)cols || (uint)nr >= (uint)rows)
                    continue;
                var a = cells[r * cols + c];
                var b = cells[nr * cols + nc];
                if (!b.Walkable)
                    continue;
                if (Math.Abs(a.ElevationZ - b.ElevationZ) > maxStep)
                    continue;
                c = nc;
                r = nr;
                AddPt();
                moved = true;
                break;
            }

            if (!moved)
                break;
        }

        return line;
    }

    private static bool TryFindWalkableStart(int cols, int rows, List<NavCellDefinition> cells, Random rng, out int c,
        out int r)
    {
        for (var t = 0; t < 80; t++)
        {
            c = rng.Next(cols);
            r = rng.Next(rows);
            if (cells[r * cols + c].Walkable)
                return true;
        }

        c = 0;
        r = 0;
        return false;
    }
}
