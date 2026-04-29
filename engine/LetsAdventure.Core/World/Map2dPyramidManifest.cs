using System.Text.Json.Serialization;

namespace LetsAdventure.Core.World;

/// <summary>Index for pre-rendered 2D map tiles under a nav chunk store (e.g. <c>map2d/manifest.json</c>).</summary>
public sealed class Map2dPyramidManifest
{
    public int SchemaVersion { get; set; } = 1;

    public int Columns { get; set; }
    public int Rows { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double CellSize { get; set; }

    /// <summary>Output pixels along each axis per tile at every level (e.g. 1000).</summary>
    public int TilePixels { get; set; } = 1000;

    /// <summary>Finest level is 0 (one pixel per nav cell). Coarsest is <see cref="MaxLevel"/>.</summary>
    /// <remarks>The coarsest tile (single <c>t_0_0</c> at <see cref="MaxLevel"/>) is always <see cref="TilePixels"/>×<see cref="TilePixels"/> pixels and shows the full nav grid (area-resampled).</remarks>
    public int MaxLevel { get; set; }

    public bool Hillshade { get; set; }

    /// <summary>Folder depth shard: folder holds at most <see cref="ShardSpan"/>² tile files.</summary>
    public int ShardSpan { get; set; } = 50;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long GeneratedUnixSeconds { get; set; }
}

/// <summary>Tile math for the 2D pyramid (cell-index space).</summary>
public static class Map2dPyramidTileMath
{
    public const int DefaultTilePixels = 1000;

    /// <summary>Cells along one edge covered by one tile at <paramref name="level"/> (finest = 0).</summary>
    public static long CellsPerTileEdgeLong(int level, int tilePixels = DefaultTilePixels)
    {
        if (level < 0 || tilePixels < 1)
            return 0;
        return (long)tilePixels * (1L << level);
    }

    /// <summary>Cells along one edge covered by one tile at <paramref name="level"/> (finest = 0).</summary>
    /// <remarks>Throws if <c>tilePixels × 2^level</c> does not fit in <see cref="int"/>; prefer <see cref="CellsPerTileEdgeLong"/>.</remarks>
    public static int CellsPerTileEdge(int level, int tilePixels = DefaultTilePixels)
    {
        if (level < 0 || tilePixels < 1)
            return 0;
        var stride = 1L << level;
        return checked(tilePixels * (int)stride);
    }

    /// <returns>Tile count along X or 0 if inputs invalid.</returns>
    public static long TileCountXLong(long columns, int level, int tilePixels = DefaultTilePixels) =>
        columns < 1 || tilePixels < 1
            ? 0
            : (long)Math.Ceiling(columns / (double)((long)tilePixels * (1L << Math.Max(0, level))));

    public static int TileCountX(int columns, int level, int tilePixels = DefaultTilePixels) =>
        columns < 1 || tilePixels < 1
            ? 0
            : (int)Math.Min(int.MaxValue, TileCountXLong(columns, level, tilePixels));

    public static long TileCountYLong(long rows, int level, int tilePixels = DefaultTilePixels) =>
        rows < 1 || tilePixels < 1
            ? 0
            : (long)Math.Ceiling(rows / (double)((long)tilePixels * (1L << Math.Max(0, level))));

    public static int TileCountY(int rows, int level, int tilePixels = DefaultTilePixels) =>
        rows < 1 || tilePixels < 1
            ? 0
            : (int)Math.Min(int.MaxValue, TileCountYLong(rows, level, tilePixels));

    /// <summary>
    /// Coarsest level: smallest L with <c>2^L ≥ max(⌈columns/tilePixels⌉, ⌈rows/tilePixels⌉)</c> so one tile
    /// covers the full grid in cell space (then resampled to tilePixels² for the overview PNG).
    /// </summary>
    public static int ComputeMaxLevel(int columns, int rows, int tilePixels = DefaultTilePixels)
    {
        if (columns < 1 || rows < 1 || tilePixels < 1)
            return 0;
        var sMin = Math.Max(
            (columns + tilePixels - 1) / tilePixels,
            (rows + tilePixels - 1) / tilePixels);
        sMin = Math.Max(1, sMin);
        var L = 0;
        while ((1L << L) < (long)sMin && L < 62)
            L++;
        return L;
    }

    public static int ShardX(int tileIndex, int shardSpan) =>
        shardSpan < 1 ? 0 : (int)Math.Floor((double)tileIndex / shardSpan);

    /// <summary>Relative path: <c>L{level}/sx_{shardX}/sy_{shardY}/t_{tx}_{ty}.png</c>.</summary>
    public static string RelativeTilePath(int level, int tx, int ty, int shardSpan)
    {
        shardSpan = Math.Max(1, shardSpan);
        var sx = ShardX(tx, shardSpan);
        var sy = ShardX(ty, shardSpan);
        return Path.Combine($"L{level}", $"sx_{sx}", $"sy_{sy}", $"t_{tx}_{ty}.png");
    }
}
