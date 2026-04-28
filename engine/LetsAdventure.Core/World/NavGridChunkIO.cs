using System.Text.Json;
using LetsAdventure.Core.Json;

namespace LetsAdventure.Core.World;

/// <summary>Writes / reads chunked nav grid cell binaries + manifest.json for scalable editor loading.</summary>
public static class NavGridChunkIO
{
    /// <summary>Above this cell count, the world editor spills the nav grid to a sidecar chunk directory on save.</summary>
    public const int AutoExportCellThreshold = 262_144;

    public const uint FileMagic = 0x3143474e; // 'N','G','C','1' little-endian
    public const ushort FormatVersion = 1;
    public const int DefaultChunkWidth = 128;
    public const int DefaultChunkHeight = 128;

    public const string ManifestFileName = "manifest.json";

    public static string ChunkFileName(int cx, int cy) => $"c_{cx:0000}_{cy:0000}.bin";

    /// <summary>Export full in-memory grid to <paramref name="directory"/> (created if missing).</summary>
    public static NavGridChunkManifest ExportToDirectory(
        TerrainNavGridDefinition grid,
        string directory,
        int chunkWidth = DefaultChunkWidth,
        int chunkHeight = DefaultChunkHeight,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        var cells = grid.Cells;
        if (cells is null || cells.Count < grid.Columns * grid.Rows)
            throw new InvalidOperationException("Nav grid must have a full Cells list to export chunks.");

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cs = grid.CellSize <= 0 ? 1 : grid.CellSize;
        chunkWidth = Math.Clamp(chunkWidth, 8, 1024);
        chunkHeight = Math.Clamp(chunkHeight, 8, 1024);

        Directory.CreateDirectory(directory);

        Report(progress, $"[navgrid chunks] Exporting {cols}×{rows} cells to {directory} ({chunkWidth}×{chunkHeight} cells/chunk)…");

        var chunksX = (cols + chunkWidth - 1) / chunkWidth;
        var chunksY = (rows + chunkHeight - 1) / chunkHeight;

        double gMin = double.MaxValue, gMax = double.MinValue;
        foreach (var c in cells)
        {
            gMin = Math.Min(gMin, c.ElevationZ);
            gMin = Math.Min(gMin, c.BedElevationZ);
            gMax = Math.Max(gMax, c.ElevationZ);
            gMax = Math.Max(gMax, c.BedElevationZ);
        }

        var manifest = new NavGridChunkManifest
        {
            Columns = cols,
            Rows = rows,
            OriginX = grid.OriginX,
            OriginY = grid.OriginY,
            CellSize = cs,
            ChunkWidthCells = chunkWidth,
            ChunkHeightCells = chunkHeight,
            ChunksX = chunksX,
            ChunksY = chunksY,
            GlobalZMin = gMin,
            GlobalZMax = gMax,
        };

        var done = 0;
        var total = chunksX * chunksY;
        for (var cy = 0; cy < chunksY; cy++)
        {
            for (var cx = 0; cx < chunksX; cx++)
            {
                var col0 = cx * chunkWidth;
                var row0 = cy * chunkHeight;
                var w = Math.Min(chunkWidth, cols - col0);
                var h = Math.Min(chunkHeight, rows - row0);
                if (w <= 0 || h <= 0)
                    continue;

                var buffer = new NavCellDefinition[w * h];
                var lod = new NavGridChunkLodEntry { Cx = cx, Cy = cy };
                double sZ = 0;
                var nZ = 0;
                lod.MinZ = double.MaxValue;
                lod.MaxZ = double.MinValue;
                var water = 0;
                var land = 0;

                for (var ly = 0; ly < h; ly++)
                {
                    for (var lx = 0; lx < w; lx++)
                    {
                        var c = cells[(row0 + ly) * cols + (col0 + lx)];
                        buffer[ly * w + lx] = c;
                        var z = c.ElevationZ;
                        sZ += z;
                        nZ++;
                        lod.MinZ = Math.Min(lod.MinZ, z);
                        lod.MaxZ = Math.Max(lod.MaxZ, z);
                        if (!c.Walkable && (c.FluidDepth > 0.01 || c.Composition == SurfaceComposition.Water))
                        {
                            water++;
                            lod.MinZ = Math.Min(lod.MinZ, c.WaterSurfaceZ);
                            lod.MaxZ = Math.Max(lod.MaxZ, c.WaterSurfaceZ);
                        }
                        else
                            land++;
                    }
                }

                lod.AvgZ = nZ > 0 ? sZ / nZ : 0;
                lod.WaterFraction01 = water + land > 0 ? water / (double)(water + land) : 0;

                var path = Path.Combine(directory, ChunkFileName(cx, cy));
                WriteChunkFile(path, cx, cy, w, h, buffer);
                manifest.Chunks.Add(lod);

                done++;
                if (progress != null && (done == 1 || done == total || done % Math.Max(1, total / 16) == 0))
                    Report(progress, $"[navgrid chunks] Wrote {done}/{total} chunk files…");
            }
        }

        var manifestPath = Path.Combine(directory, ManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, GameJson.Options));
        Report(progress, "[navgrid chunks] Manifest written.");
        return manifest;
    }

    public static bool TryLoadManifest(string directory, out NavGridChunkManifest? manifest)
    {
        manifest = null;
        var path = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(path))
            return false;
        try
        {
            manifest = JsonSerializer.Deserialize<NavGridChunkManifest>(File.ReadAllText(path), GameJson.Options);
            return manifest is { Columns: > 0, Rows: > 0 };
        }
        catch
        {
            manifest = null;
            return false;
        }
    }

    public static void WriteChunkFile(string path, int cx, int cy, int localW, int localH,
        ReadOnlySpan<NavCellDefinition> cellsRowMajor)
    {
        if (cellsRowMajor.Length < localW * localH)
            throw new ArgumentException("Cell buffer too small for localW×localH.");

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(FileMagic);
        bw.Write(FormatVersion);
        bw.Write((ushort)cx);
        bw.Write((ushort)cy);
        bw.Write((ushort)localW);
        bw.Write((ushort)localH);
        for (var i = 0; i < localW * localH; i++)
            WriteCell(bw, cellsRowMajor[i]);
    }

    public static NavCellDefinition[] ReadChunkFile(string path, out int cx, out int cy, out int localW, out int localH)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var magic = br.ReadUInt32();
        if (magic != FileMagic)
            throw new InvalidDataException("Invalid nav grid chunk magic.");
        var ver = br.ReadUInt16();
        if (ver != FormatVersion)
            throw new InvalidDataException($"Unsupported chunk format version {ver}.");
        cx = br.ReadUInt16();
        cy = br.ReadUInt16();
        localW = br.ReadUInt16();
        localH = br.ReadUInt16();
        var n = localW * localH;
        var cells = new NavCellDefinition[n];
        for (var i = 0; i < n; i++)
            cells[i] = ReadCell(br);
        return cells;
    }

    private static void WriteCell(BinaryWriter w, NavCellDefinition c)
    {
        w.Write(c.Walkable ? (byte)1 : (byte)0);
        w.Write((int)c.Composition);
        w.Write((int)c.VegetationCommunity);
        w.Write((int)c.VegetationStrata);
        w.Write(c.ElevationZ);
        w.Write(c.BedElevationZ);
        w.Write(c.WaterSurfaceZ);
        w.Write(c.FluidDepth);
        w.Write(c.MovementCostMultiplier);
        w.Write(c.VegetationDensity01);
    }

    private static NavCellDefinition ReadCell(BinaryReader r) =>
        new()
        {
            Walkable = r.ReadByte() != 0,
            Composition = (SurfaceComposition)r.ReadInt32(),
            VegetationCommunity = (VegetationCommunityKind)r.ReadInt32(),
            VegetationStrata = (VegetationStratum)r.ReadInt32(),
            ElevationZ = r.ReadDouble(),
            BedElevationZ = r.ReadDouble(),
            WaterSurfaceZ = r.ReadDouble(),
            FluidDepth = r.ReadDouble(),
            MovementCostMultiplier = r.ReadDouble(),
            VegetationDensity01 = r.ReadDouble(),
        };

    private static void Report(IProgress<string>? progress, string msg) => progress?.Report(msg);
}
