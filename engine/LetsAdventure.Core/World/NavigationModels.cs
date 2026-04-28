using System.Text.Json.Serialization;
using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

public sealed class NavigationGraphDefinition
{
    public List<NavigationNodeDefinition> Nodes { get; set; } = [];
    public List<NavigationEdgeDefinition> Edges { get; set; } = [];
}

public sealed class NavigationNodeDefinition
{
    public string Id { get; set; } = "";
    public Vec3 Position { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed class NavigationEdgeDefinition
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public double TraverseCost { get; set; } = 1;
    public bool Bidirectional { get; set; } = true;
}

public sealed class NavCellDefinition
{
    public bool Walkable { get; set; } = true;

    /// <summary>Open-air ground or submerged bed (terrain under water column).</summary>
    public double ElevationZ { get; set; }

    /// <summary>Same as <see cref="ElevationZ"/> on dry land; underwater, the bottom of the water column.</summary>
    public double BedElevationZ { get; set; }

    /// <summary>Water surface Z when fluid is present; 0 on dry land.</summary>
    public double WaterSurfaceZ { get; set; }

    public double MovementCostMultiplier { get; set; } = 1;
    public SurfaceComposition Composition { get; set; } = SurfaceComposition.Soil;

    /// <summary>Water column height: <see cref="WaterSurfaceZ"/> − <see cref="BedElevationZ"/> when wet.</summary>
    public double FluidDepth { get; set; }

    /// <summary>Plant cover 0…1 (herb layer to canopy); 0 on open water.</summary>
    public double VegetationDensity01 { get; set; }

    /// <summary>Inferred dominant community from baseline / procedural overlay rules.</summary>
    public VegetationCommunityKind VegetationCommunity { get; set; }

    /// <summary>Which strata contribute measurable biomass (grass, shrub, tree, forbs, etc.).</summary>
    public VegetationStratum VegetationStrata { get; set; }
}

public sealed class TerrainNavGridDefinition
{
    public double OriginX { get; set; }
    public double OriginY { get; set; }

    /// <summary>Nav sample spacing along X and Y in meters (world XY uses 1 unit = 1 m).</summary>
    public double CellSize { get; set; } = 4;
    public int Columns { get; set; }
    public int Rows { get; set; }

    /// <summary>Row-major: index = row * Columns + col.</summary>
    public List<NavCellDefinition>? Cells { get; set; }

    /// <summary>
    /// Directory name (relative to the physical world JSON file) containing <c>manifest.json</c> and chunk binaries.
    /// When set, <see cref="Cells"/> may be omitted from JSON for large worlds.
    /// </summary>
    public string? NavGridChunkStoreRelativePath { get; set; }

    /// <summary>Editor-only absolute path for unsaved baseline chunk spill (not serialized).</summary>
    [JsonIgnore]
    public string? NavGridChunkSessionDirectoryAbsolute { get; set; }
}

/// <summary>Runtime grid for pathfinding queries.</summary>
public sealed class TerrainNavGrid
{
    public double OriginX { get; }
    public double OriginY { get; }
    public double CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    private readonly NavCellDefinition[] _cells;

    public TerrainNavGrid(TerrainNavGridDefinition def)
    {
        OriginX = def.OriginX;
        OriginY = def.OriginY;
        CellSize = def.CellSize <= 0 ? 1 : def.CellSize;
        Columns = Math.Max(1, def.Columns);
        Rows = Math.Max(1, def.Rows);
        var n = Columns * Rows;
        _cells = new NavCellDefinition[n];
        var src = def.Cells;
        for (var i = 0; i < n; i++)
        {
            _cells[i] = i < src?.Count ? src[i] : new NavCellDefinition();
        }
    }

    public NavCellDefinition Cell(int col, int row) => _cells[row * Columns + col];

    public bool InBounds(int col, int row) => (uint)col < (uint)Columns && (uint)row < (uint)Rows;

    public bool TryWorldToCell(double wx, double wy, out int col, out int row)
    {
        col = (int)Math.Floor((wx - OriginX) / CellSize);
        row = (int)Math.Floor((wy - OriginY) / CellSize);
        return InBounds(col, row);
    }

    public Vec3 CellCenter(int col, int row)
    {
        var c = Cell(col, row);
        var cx = OriginX + (col + 0.5) * CellSize;
        var cy = OriginY + (row + 0.5) * CellSize;
        return new Vec3 { X = cx, Y = cy, Z = c.ElevationZ };
    }
}
