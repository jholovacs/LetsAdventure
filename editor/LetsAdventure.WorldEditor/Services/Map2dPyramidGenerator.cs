using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using LetsAdventure.Core.Json;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor.Services;

/// <param name="PercentComplete">0–100 while running; 100 when finished.</param>
public readonly record struct Map2dPyramidProgressReport(string Message, int PercentComplete);

/// <summary>Builds <c>map2d/</c> PNG pyramid next to a nav chunk store (level 0 from nav cells; coarser from 2×2 averages).</summary>
public static class Map2dPyramidGenerator
{
    public const string SubfolderName = "map2d";
    public const string ManifestFileName = "manifest.json";

    public static string ResolvePyramidRoot(string navChunkDirectoryAbsolute) =>
        Path.Combine(Path.GetFullPath(navChunkDirectoryAbsolute), SubfolderName);

    public static bool TryLoadManifest(string navChunkDirectoryAbsolute, out Map2dPyramidManifest? manifest) =>
        TryLoadManifestFromRoot(ResolvePyramidRoot(navChunkDirectoryAbsolute), out manifest);

    public static bool TryLoadManifestFromRoot(string pyramidRoot, out Map2dPyramidManifest? manifest)
    {
        manifest = null;
        var p = Path.Combine(pyramidRoot, ManifestFileName);
        if (!File.Exists(p))
            return false;
        try
        {
            manifest = JsonSerializer.Deserialize<Map2dPyramidManifest>(File.ReadAllText(p), GameJson.Options);
            return manifest is { Columns: > 0, Rows: > 0, TilePixels: > 0 };
        }
        catch
        {
            manifest = null;
            return false;
        }
    }

    public static void Generate(
        string navChunkDirectoryAbsolute,
        TerrainNavGridDefinition? inMemoryGrid,
        bool hillshade,
        IProgress<Map2dPyramidProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var chunkDir = Path.GetFullPath(navChunkDirectoryAbsolute);
        if (!Directory.Exists(chunkDir) || !NavGridChunkIO.TryLoadManifest(chunkDir, out var nm) || nm is null)
            throw new InvalidOperationException("Nav chunk directory with manifest.json is required.");

        var cols = nm.Columns;
        var rows = nm.Rows;
        var tilePx = Map2dPyramidTileMath.DefaultTilePixels;
        var shardSpan = 50;
        var maxL = Map2dPyramidTileMath.ComputeMaxLevel(cols, rows, tilePx);
        var pyramidRoot = ResolvePyramidRoot(chunkDir);
        Directory.CreateDirectory(pyramidRoot);

        var gMin = nm.GlobalZMin;
        var gMax = nm.GlobalZMax;
        if (gMax <= gMin + 1e-9)
        {
            gMin = 0;
            gMax = 1;
        }

        var inMemory = inMemoryGrid?.Cells is { Count: var ic } && ic >= (long)cols * rows;

        long totalTilesLong = 0;
        for (var L = 0; L <= maxL; L++)
            totalTilesLong += Map2dPyramidTileMath.TileCountXLong(cols, L, tilePx) *
                              Map2dPyramidTileMath.TileCountYLong(rows, L, tilePx);
        if (totalTilesLong < 1)
            throw new InvalidOperationException("Nav manifest implies zero pyramid tiles (check Columns/Rows).");
        var doneTilesLong = 0L;

        void Report(string message)
        {
            var pct = totalTilesLong <= 0
                ? 0
                : Math.Clamp((int)(doneTilesLong * 100L / totalTilesLong), 0, 99);
            progress?.Report(new Map2dPyramidProgressReport(message, pct));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var txN0 = Map2dPyramidTileMath.TileCountX(cols, 0, tilePx);
        var tyN0 = Map2dPyramidTileMath.TileCountY(rows, 0, tilePx);
        var l0Files = Map2dPyramidTileMath.TileCountXLong(cols, 0, tilePx) *
                        Map2dPyramidTileMath.TileCountYLong(rows, 0, tilePx);
        Report(string.Format(CultureInfo.InvariantCulture,
            "[map2d] Nav grid {0}×{1} cells (from chunk manifest) → ~{2:N0} L0 tiles ({3}×{4}), {5} levels",
            cols, rows, l0Files, txN0, tyN0, maxL + 1));
        for (var ty = 0; ty < tyN0; ty++)
        {
            for (var tx = 0; tx < txN0; tx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EncodeLevel0Tile(chunkDir, nm, inMemory, inMemoryGrid, cols, rows, tilePx, tx, ty, hillshade,
                    gMin, gMax, pyramidRoot, shardSpan, cancellationToken);
                doneTilesLong++;
                if (doneTilesLong == 1 || doneTilesLong == totalTilesLong ||
                    doneTilesLong % Math.Max(1L, totalTilesLong / 32) == 0)
                    Report($"[map2d] Tiles {doneTilesLong}/{totalTilesLong} (level 0, {tx},{ty})");
            }
        }

        if (maxL == 0)
        {
            var relO = Map2dPyramidTileMath.RelativeTilePath(0, 0, 0, shardSpan);
            var pathO = Path.Combine(pyramidRoot, relO);
            var rawOv = Map2dPyramidCodec.TryLoadBgraPng(pathO, out var oww, out var ohh);
            if (rawOv is null || oww < 1 || ohh < 1)
                throw new InvalidOperationException("Level-0 overview tile missing after encode.");
            if (oww != tilePx || ohh != tilePx)
            {
                var sized = Map2dPyramidCodec.ResizeBgraAreaAverage(rawOv, oww, ohh, tilePx, tilePx);
                Map2dPyramidCodec.SaveBgraPng(pathO, tilePx, tilePx, sized);
            }
        }

        for (var L = 1; L <= maxL; L++)
        {
            var txN = Map2dPyramidTileMath.TileCountX(cols, L, tilePx);
            var tyN = Map2dPyramidTileMath.TileCountY(rows, L, tilePx);
            var txPrev = Map2dPyramidTileMath.TileCountX(cols, L - 1, tilePx);
            var tyPrev = Map2dPyramidTileMath.TileCountY(rows, L - 1, tilePx);
            var strideL = 1L << L;
            var cellSpanL = (long)tilePx * strideL;

            for (var ty = 0; ty < tyN; ty++)
            {
                for (var tx = 0; tx < txN; tx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int ClampChildTx(int cx) => txPrev <= 0 ? 0 : Math.Clamp(cx, 0, txPrev - 1);
                    int ClampChildTy(int cy) => tyPrev <= 0 ? 0 : Math.Clamp(cy, 0, tyPrev - 1);
                    var cxt0 = ClampChildTx(2 * tx);
                    var cxt1 = ClampChildTx(2 * tx + 1);
                    var cyt0 = ClampChildTy(2 * ty);
                    var cyt1 = ClampChildTy(2 * ty + 1);

                    LoadPaddedChild(pyramidRoot, L - 1, cxt0, cyt0, shardSpan, tilePx, out var b00, out var w00,
                        out var h00);
                    LoadPaddedChild(pyramidRoot, L - 1, cxt1, cyt0, shardSpan, tilePx, out var b01, out var w01,
                        out var h01);
                    LoadPaddedChild(pyramidRoot, L - 1, cxt0, cyt1, shardSpan, tilePx, out var b10, out var w10,
                        out var h10);
                    LoadPaddedChild(pyramidRoot, L - 1, cxt1, cyt1, shardSpan, tilePx, out var b11, out var w11,
                        out var h11);

                    if (b00 is null || b01 is null || b10 is null || b11 is null)
                        throw new InvalidOperationException(
                            $"Missing child PNGs for level {L} tile t_{tx}_{ty}.");

                    var merged = Map2dPyramidCodec.QuarterFromFourChildren(
                        b00, w00, h00, b01, w01, h01, b10, w10, h10, b11, w11, h11,
                        tilePx, tilePx, tilePx, tilePx);

                    var c0 = (long)tx * cellSpanL;
                    var r0 = (long)ty * cellSpanL;
                    var wCells = (int)Math.Min(cellSpanL, (long)cols - c0);
                    var hCells = (int)Math.Min(cellSpanL, (long)rows - r0);
                    var outW = (int)Math.Ceiling(wCells / (double)strideL);
                    var outH = (int)Math.Ceiling(hCells / (double)strideL);
                    outW = Math.Clamp(outW, 1, tilePx);
                    outH = Math.Clamp(outH, 1, tilePx);
                    var finalBgra = CropBgraTopLeft(merged, tilePx, tilePx, outW, outH);
                    if (L == maxL)
                        finalBgra = Map2dPyramidCodec.ResizeBgraAreaAverage(finalBgra, outW, outH, tilePx, tilePx);

                    var rel = Map2dPyramidTileMath.RelativeTilePath(L, tx, ty, shardSpan);
                    Map2dPyramidCodec.SaveBgraPng(Path.Combine(pyramidRoot, rel), tilePx, tilePx, finalBgra);

                    doneTilesLong++;
                    if (doneTilesLong == 1 || doneTilesLong == totalTilesLong ||
                        doneTilesLong % Math.Max(1L, totalTilesLong / 32) == 0)
                        Report($"[map2d] Tiles {doneTilesLong}/{totalTilesLong} (level {L}, {tx},{ty})");
                }
            }
        }

        var manifest = new Map2dPyramidManifest
        {
            Columns = cols,
            Rows = rows,
            OriginX = nm.OriginX,
            OriginY = nm.OriginY,
            CellSize = nm.CellSize,
            TilePixels = tilePx,
            MaxLevel = maxL,
            Hillshade = hillshade,
            ShardSpan = shardSpan,
            GeneratedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        File.WriteAllText(Path.Combine(pyramidRoot, ManifestFileName),
            JsonSerializer.Serialize(manifest, GameJson.Options));
        progress?.Report(new Map2dPyramidProgressReport("[map2d] Done.", 100));
    }

    private static byte[] CropBgraTopLeft(byte[] src, int srcW, int srcH, int cropW, int cropH)
    {
        if (cropW >= srcW && cropH >= srcH)
            return src;
        var stride = cropW * 4;
        var dst = new byte[stride * cropH];
        var srcStride = srcW * 4;
        for (var y = 0; y < cropH; y++)
        {
            Buffer.BlockCopy(src, y * srcStride, dst, y * stride, cropW * 4);
        }

        return dst;
    }

    private static void LoadPaddedChild(string pyramidRoot, int level, int tx, int ty, int shardSpan, int tilePx,
        out byte[]? bgra, out int w, out int h)
    {
        var rp = Path.Combine(pyramidRoot, Map2dPyramidTileMath.RelativeTilePath(level, tx, ty, shardSpan));
        var raw = Map2dPyramidCodec.TryLoadBgraPng(rp, out w, out h);
        if (raw is null)
        {
            bgra = null;
            w = 0;
            h = 0;
            return;
        }

        bgra = w != tilePx || h != tilePx ? Map2dPyramidCodec.PadBgraToSize(raw, w, h, tilePx, tilePx) : raw;
        w = tilePx;
        h = tilePx;
    }

    private static void EncodeLevel0Tile(
        string chunkDir,
        NavGridChunkManifest nm,
        bool inMemory,
        TerrainNavGridDefinition? grid,
        int cols,
        int rows,
        int tilePx,
        int tx,
        int ty,
        bool hillshade,
        double gMin,
        double gMax,
        string pyramidRoot,
        int shardSpan,
        CancellationToken cancellationToken)
    {
        var c0 = (long)tx * tilePx;
        var r0 = (long)ty * tilePx;
        var w = (int)Math.Min(tilePx, (long)cols - c0);
        var h = (int)Math.Min(tilePx, (long)rows - r0);
        if (w < 1 || h < 1)
            return;

        var padL = c0 > 0 ? 1 : 0;
        var padT = r0 > 0 ? 1 : 0;
        var padR = c0 + w < cols ? 1 : 0;
        var padB = r0 + h < rows ? 1 : 0;
        var pc0 = (int)(c0 - padL);
        var pr0 = (int)(r0 - padT);
        var pw = w + padL + padR;
        var ph = h + padT + padB;
        var patchBuf = new NavCellDefinition[pw * ph];

        if (inMemory)
            CopyRectFromFullGrid(grid!, cols, rows, pc0, pr0, pw, ph, patchBuf);
        else
            NavGridRectMaterializer.CopyRect(chunkDir, nm, pc0, pr0, pw, ph, patchBuf);

        var bgra = NavGridMapPreviewRaster.TryEncodePatchBgra(
            patchBuf, pw, ph, padL, padL + w - 1, padT, padT + h - 1,
            superSample: 1, hillshade, progress: null, cancellationToken, gMin, gMax);
        if (bgra is null)
            throw new InvalidOperationException($"Encode failed for tile t_{tx}_{ty} level 0.");

        var rel = Map2dPyramidTileMath.RelativeTilePath(0, tx, ty, shardSpan);
        Map2dPyramidCodec.SaveBgraPng(Path.Combine(pyramidRoot, rel), w, h, bgra);
    }

    private static void CopyRectFromFullGrid(
        TerrainNavGridDefinition grid,
        int cols,
        int rows,
        int c0,
        int r0,
        int w,
        int h,
        NavCellDefinition[] dest)
    {
        var cells = grid.Cells!;
        for (var r = 0; r < h; r++)
        {
            for (var c = 0; c < w; c++)
            {
                var gc = c0 + c;
                var gr = r0 + r;
                dest[r * w + c] = cells[gr * cols + gc];
            }
        }
    }
}
