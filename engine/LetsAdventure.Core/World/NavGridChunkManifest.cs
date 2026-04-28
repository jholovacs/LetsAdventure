namespace LetsAdventure.Core.World;

/// <summary>Index for chunked nav grid binaries on disk (one manifest per store directory).</summary>
public sealed class NavGridChunkManifest
{
    public int SchemaVersion { get; set; } = 1;

    public int Columns { get; set; }
    public int Rows { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double CellSize { get; set; }

    public int ChunkWidthCells { get; set; }
    public int ChunkHeightCells { get; set; }
    public int ChunksX { get; set; }
    public int ChunksY { get; set; }

    /// <summary>Full-grid elevation range (for coloring coarse terrain).</summary>
    public double GlobalZMin { get; set; }
    public double GlobalZMax { get; set; }

    /// <summary>Optional 2D overview image written beside chunks (e.g. preview.png).</summary>
    public string PreviewPngFile { get; set; } = "preview.png";

    public List<NavGridChunkLodEntry> Chunks { get; set; } = [];
}

/// <summary>Per-chunk coarse stats for editor LOD mesh without loading every cell.</summary>
public sealed class NavGridChunkLodEntry
{
    public int Cx { get; set; }
    public int Cy { get; set; }
    public double AvgZ { get; set; }
    public double MinZ { get; set; }
    public double MaxZ { get; set; }

    /// <summary>Fraction of cells in chunk that are water / non-walkable fluid.</summary>
    public double WaterFraction01 { get; set; }
}
