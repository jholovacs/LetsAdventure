using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor.Services;

/// <summary>Raw BGRA nav-patch raster built off the UI thread; use <see cref="WorldNavGridPreviewRenderer.CreateNavGridPatchImage"/> on the dispatcher.</summary>
public readonly record struct NavGridPatchBitmapData(int PixelWidth, int PixelHeight, byte[] BgraPixels);

/// <summary>High-detail 2D preview: one pixel per nav cell with composition, vegetation, and optional hillshade.</summary>
public static class WorldNavGridPreviewRenderer
{
    /// <summary>Downsampled PNG for map preview when the full grid lives in chunked files.</summary>
    public static bool TryWriteOverviewPng(TerrainNavGridDefinition grid, string path, int maxSide = 2048,
        bool hillshade = true)
    {
        if (grid.Cells is null || grid.Columns < 1 || grid.Rows < 1)
            return false;
        maxSide = Math.Clamp(maxSide, 256, 8192);
        var cols = grid.Columns;
        var rows = grid.Rows;
        var stepC = Math.Max(1, (cols + maxSide - 1) / maxSide);
        var stepR = Math.Max(1, (rows + maxSide - 1) / maxSide);
        var outC = (cols + stepC - 1) / stepC;
        var outR = (rows + stepR - 1) / stepR;

        var cells = grid.Cells;
        var (zMin, zMax) = NavGridMapPreviewRaster.ElevRange(cells, cols, rows);
        var zSpan = Math.Max(1e-3, zMax - zMin);
        var stride = outC * 4;
        var buffer = new byte[stride * outR];
        for (var or = 0; or < outR; or++)
        {
            var r = Math.Min(rows - 1, or * stepR);
            for (var oc = 0; oc < outC; oc++)
            {
                var c = Math.Min(cols - 1, oc * stepC);
                var cell = cells[r * cols + c];
                var (b, g, r8, a) = NavGridMapPreviewRaster.PixelBgra(cell, c, r, cols, rows, cells, zMin, zSpan,
                    hillshade);
                var o = or * stride + oc * 4;
                buffer[o] = b;
                buffer[o + 1] = g;
                buffer[o + 2] = r8;
                buffer[o + 3] = a;
            }
        }

        var bmp = new WriteableBitmap(outC, outR, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, outC, outR), buffer, stride, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path))
            enc.Save(fs);
        return true;
    }

    /// <summary>
    /// Horizontal / vertical preview pixels per world meter so vector overlays align with the nav raster
    /// (<paramref name="rasterCellPixels"/> per raster “cell pixel” slider; full grid, chunked manifest, or overview PNG).
    /// </summary>
    public static bool TryGetMapPreviewPixelsPerWorldMeter(
        PhysicalWorldDefinition world,
        double rasterCellPixels,
        string? chunkStoreDirectory,
        out double pixelsPerWorldMeterX,
        out double pixelsPerWorldMeterY)
    {
        pixelsPerWorldMeterX = 0;
        pixelsPerWorldMeterY = 0;
        var grid = world.Navigation.Grid;
        if (grid is null || grid.CellSize <= 1e-9 || grid.Columns < 1 || grid.Rows < 1)
            return false;

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cs = grid.CellSize;
        rasterCellPixels = Math.Max(1e-6, rasterCellPixels);

        if (grid.Cells is not null && grid.Cells.Count >= (long)cols * rows)
        {
            var s = rasterCellPixels / cs;
            pixelsPerWorldMeterX = s;
            pixelsPerWorldMeterY = s;
            return true;
        }

        // Chunked world in this session — scale from nav cell size (patch rasters do not need preview.png).
        if (world.NavGridCellSource is not null)
        {
            var s = rasterCellPixels / cs;
            pixelsPerWorldMeterX = s;
            pixelsPerWorldMeterY = s;
            return true;
        }

        if (string.IsNullOrEmpty(chunkStoreDirectory))
            return false;
        if (!NavGridChunkIO.TryLoadManifest(chunkStoreDirectory, out var manifest) || manifest is null)
            return false;

        var pngPath = Path.Combine(chunkStoreDirectory, manifest.PreviewPngFile);
        if (File.Exists(pngPath))
        {
            int iw, ih;
            try
            {
                using var stream = File.OpenRead(pngPath);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                iw = frame.PixelWidth;
                ih = frame.PixelHeight;
            }
            catch
            {
                return false;
            }

            if (iw < 1 || ih < 1)
                return false;
            pixelsPerWorldMeterX = iw * rasterCellPixels / (cols * cs);
            pixelsPerWorldMeterY = ih * rasterCellPixels / (rows * cs);
            return pixelsPerWorldMeterX > 0 && pixelsPerWorldMeterY > 0;
        }

        // Manifest on disk but overview PNG not written yet (e.g. fresh baseline) — same geometry as in-memory grid.
        {
            var s = rasterCellPixels / cs;
            pixelsPerWorldMeterX = s;
            pixelsPerWorldMeterY = s;
            return true;
        }
    }

    /// <summary>Loads <paramref name="chunkStoreDirectory"/>/preview.png (or manifest name) when cells are external.</summary>
    public static Image? TryBuildNavGridImage(
        PhysicalWorldDefinition world,
        double cellPixels,
        bool hillshade,
        double marginOx,
        double marginOy,
        string? chunkStoreDirectory = null) =>
        TryBuildNavGridImage(world, cellPixels, hillshade, marginOx, marginOy, chunkStoreDirectory,
            fullGridOnly: true);

    /// <summary>
    /// When <paramref name="fullGridOnly"/> is true, always builds the full nav raster (legacy).
    /// When false and <paramref name="patch"/> is set, renders only that cell range at <paramref name="superSample"/>×
    /// bitmap pixels per nav cell (for zoomed map preview).
    /// </summary>
    public static Image? TryBuildNavGridImage(
        PhysicalWorldDefinition world,
        double cellPixels,
        bool hillshade,
        double marginOx,
        double marginOy,
        string? chunkStoreDirectory,
        bool fullGridOnly,
        int patchC0 = 0,
        int patchC1 = 0,
        int patchR0 = 0,
        int patchR1 = 0,
        int superSample = 1)
    {
        var grid = world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1)
            return null;

        if (grid.Cells is null || grid.Cells.Count < grid.Columns * grid.Rows)
        {
            if (!fullGridOnly && world.NavGridCellSource is INavGridCellSource nkSrc
                && patchC0 <= patchC1 && patchR0 <= patchR1 && superSample >= 1)
            {
                var pc = patchC1 - patchC0 + 1;
                var pr = patchR1 - patchR0 + 1;
                if (pc > 0 && pr > 0 && (superSample > 1 || pc < grid.Columns || pr < grid.Rows))
                {
                    if (!string.IsNullOrEmpty(chunkStoreDirectory) &&
                        NavGridChunkIO.TryLoadManifest(chunkStoreDirectory, out var mtiles) && mtiles is not null &&
                        mtiles.ChunkPreviewPixelsPerNavCell > 0)
                    {
                        var fromTiles = TryComposePatchFromChunkPreviewPngs(chunkStoreDirectory, mtiles, patchC0,
                            patchC1, patchR0, patchR1, superSample, hillshade, progress: null,
                            CancellationToken.None);
                        if (fromTiles is not null)
                            return CreateNavGridPatchImage(fromTiles.Value, cellPixels, marginOx, marginOy, patchC0,
                                patchR0, pc, pr);
                    }

                    var fromChunks = TryBuildNavGridPatchFromChunkSource(grid, nkSrc, patchC0, patchC1, patchR0, patchR1,
                        cellPixels, superSample, hillshade, marginOx, marginOy);
                    if (fromChunks is not null)
                        return fromChunks;
                }
            }

            if (string.IsNullOrEmpty(chunkStoreDirectory))
                return null;
            if (!NavGridChunkIO.TryLoadManifest(chunkStoreDirectory, out var manifest) || manifest is null)
                return null;
            var png = Path.Combine(chunkStoreDirectory, manifest.PreviewPngFile);
            if (!File.Exists(png))
                return null;
            BitmapImage bi;
            try
            {
                bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(Path.GetFullPath(png));
                bi.EndInit();
                bi.Freeze();
            }
            catch
            {
                return null;
            }

            var overviewImage = new Image
            {
                Source = bi,
                Width = bi.PixelWidth * cellPixels,
                Height = bi.PixelHeight * cellPixels,
            };
            Canvas.SetLeft(overviewImage, marginOx);
            Canvas.SetTop(overviewImage, marginOy);
            RenderOptions.SetBitmapScalingMode(overviewImage, BitmapScalingMode.HighQuality);
            return overviewImage;
        }

        var cols = grid.Columns;
        var rows = grid.Rows;
        var cells = grid.Cells!;
        superSample = Math.Clamp(superSample, 1, 8);

        var isFullExtents = patchC0 == 0 && patchC1 == cols - 1 && patchR0 == 0 && patchR1 == rows - 1;
        var pcPatch = patchC1 - patchC0 + 1;
        var prPatch = patchR1 - patchR0 + 1;
        var patchBitmapPixels = (long)pcPatch * prPatch * superSample * superSample;
        const long maxPatchPixels = 14_000_000L;
        // Strict subset, or full grid at >1× when the supersampled bitmap stays within the pixel budget.
        var usePatch = !fullGridOnly && patchC0 <= patchC1 && patchR0 <= patchR1
                       && (!isFullExtents || (superSample > 1 && patchBitmapPixels <= maxPatchPixels));
        if (usePatch)
        {
            var patchImage = TryBuildNavGridImageFromCellPatchInMemory(
                cells, cols, rows, patchC0, patchC1, patchR0, patchR1, cellPixels, superSample, hillshade, marginOx,
                marginOy);
            if (patchImage is not null)
                return patchImage;
        }

        var bmp = new WriteableBitmap(cols, rows, 96, 96, PixelFormats.Bgra32, null);
        var stride = cols * 4;
        var buffer = new byte[stride * rows];

        var (zMin, zMax) = NavGridMapPreviewRaster.ElevRange(cells, cols, rows);
        var zSpan = Math.Max(1e-3, zMax - zMin);

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var cell = cells[r * cols + c];
                var (b, g, r8, a) = NavGridMapPreviewRaster.PixelBgra(cell, c, r, cols, rows, cells, zMin, zSpan,
                    hillshade);
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

    /// <summary>Builds BGRA patch pixels for chunked or in-memory nav grids (for background encoding).</summary>
    public static bool TryEncodeNavGridPatchForMapPreview(
        PhysicalWorldDefinition world,
        bool hillshade,
        string? chunkStoreDirectory,
        bool fullGridOnly,
        int patchC0,
        int patchC1,
        int patchR0,
        int patchR1,
        int superSample,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        out NavGridPatchBitmapData? data)
    {
        data = null;
        var grid = world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1)
            return false;

        superSample = Math.Clamp(superSample, 1, 8);
        var cols = grid.Columns;
        var rows = grid.Rows;
        var isFullExtents = patchC0 == 0 && patchC1 == cols - 1 && patchR0 == 0 && patchR1 == rows - 1;
        var pcPatch = patchC1 - patchC0 + 1;
        var prPatch = patchR1 - patchR0 + 1;
        var patchBitmapPixels = (long)pcPatch * prPatch * superSample * superSample;
        const long maxPatchPixels = 14_000_000L;
        var usePatch = !fullGridOnly && patchC0 <= patchC1 && patchR0 <= patchR1
                       && (!isFullExtents || (superSample > 1 && patchBitmapPixels <= maxPatchPixels));
        if (!usePatch)
            return false;

        if (grid.Cells is null || grid.Cells.Count < grid.Columns * grid.Rows)
        {
            if (world.NavGridCellSource is not INavGridCellSource nkSrc)
                return false;
            var pc = patchC1 - patchC0 + 1;
            var pr = patchR1 - patchR0 + 1;
            if (pc < 1 || pr < 1 || !(superSample > 1 || pc < cols || pr < rows))
                return false;

            var storeDir = chunkStoreDirectory;
            if (string.IsNullOrEmpty(storeDir) && world.NavGridCellSource is NavGridChunkCellSource ncs)
                storeDir = ncs.Directory;
            if (!string.IsNullOrEmpty(storeDir) &&
                NavGridChunkIO.TryLoadManifest(storeDir, out var manT) && manT is not null &&
                manT.ChunkPreviewPixelsPerNavCell > 0)
            {
                var fromTiles = TryComposePatchFromChunkPreviewPngs(storeDir, manT, patchC0, patchC1, patchR0, patchR1,
                    superSample, hillshade, progress, cancellationToken);
                if (fromTiles is not null)
                {
                    data = fromTiles;
                    return true;
                }
            }

            data = TryFillNavGridPatchBufferFromChunkSource(grid, nkSrc, patchC0, patchC1, patchR0, patchR1, superSample,
                hillshade, progress, cancellationToken);
            return data is not null;
        }

        var cells = grid.Cells!;
        data = TryFillNavGridPatchBufferInMemory(cells, cols, rows, patchC0, patchC1, patchR0, patchR1, superSample,
            hillshade, progress, cancellationToken);
        return data is not null;
    }

    /// <summary>Creates a WPF <see cref="Image"/> from encoded patch bytes (dispatcher thread).</summary>
    public static Image? CreateNavGridPatchImage(
        NavGridPatchBitmapData data,
        double cellPixels,
        double marginOx,
        double marginOy,
        int patchC0,
        int patchR0,
        int patchCols,
        int patchRows)
    {
        if (data.PixelWidth < 1 || data.PixelHeight < 1 || data.BgraPixels.Length < (long)data.PixelWidth * data.PixelHeight * 4)
            return null;
        var wBmp = new WriteableBitmap(data.PixelWidth, data.PixelHeight, 96, 96, PixelFormats.Bgra32, null);
        wBmp.WritePixels(new Int32Rect(0, 0, data.PixelWidth, data.PixelHeight), data.BgraPixels, data.PixelWidth * 4, 0);
        var img = new Image
        {
            Source = wBmp,
            Width = patchCols * cellPixels,
            Height = patchRows * cellPixels,
            Stretch = Stretch.Fill,
        };
        Canvas.SetLeft(img, marginOx + patchC0 * cellPixels);
        Canvas.SetTop(img, marginOy + patchR0 * cellPixels);
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
        return img;
    }

    private static NavGridPatchBitmapData? TryFillNavGridPatchBufferInMemory(
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
        CancellationToken cancellationToken)
    {
        var pc = patchC1 - patchC0 + 1;
        var pr = patchR1 - patchR0 + 1;
        if (pc < 1 || pr < 1)
            return null;
        var bw = pc * superSample;
        var bh = pr * superSample;
        if (bw < 1 || bh < 1 || bw > 20000 || bh > 20000)
            return null;

        var bytes = NavGridMapPreviewRaster.TryEncodePatchBgra(cells, cols, rows, patchC0, patchC1, patchR0, patchR1,
            superSample, hillshade, progress, cancellationToken);
        if (bytes is null)
            return null;
        return new NavGridPatchBitmapData(bw, bh, bytes);
    }

    private static NavGridPatchBitmapData? TryFillNavGridPatchBufferFromChunkSource(
        TerrainNavGridDefinition grid,
        INavGridCellSource source,
        int patchC0,
        int patchC1,
        int patchR0,
        int patchR1,
        int superSample,
        bool hillshade,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var cols = grid.Columns;
        var rows = grid.Rows;
        var pc = patchC1 - patchC0 + 1;
        var pr = patchR1 - patchR0 + 1;
        if (pc < 1 || pr < 1)
            return null;

        var cLo = Math.Max(0, patchC0 - 1);
        var cHi = Math.Min(cols - 1, patchC1 + 1);
        var rLo = Math.Max(0, patchR0 - 1);
        var rHi = Math.Min(rows - 1, patchR1 + 1);
        var ew = cHi - cLo + 1;
        var eh = rHi - rLo + 1;
        var ext = new NavCellDefinition[ew * eh];
        var loadTotal = (long)ew * eh;
        long loadP = 0;
        for (var j = 0; j < eh; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < ew; i++)
            {
                var gc = cLo + i;
                var gr = rLo + j;
                if (!source.TryGetCell(gc, gr, out var cell))
                    cell = new NavCellDefinition { ElevationZ = 0, Walkable = true, Composition = SurfaceComposition.Soil };
                ext[j * ew + i] = cell;
                loadP++;
                if ((loadP & 2047) == 0 && loadTotal > 0)
                    progress?.Report((int)Math.Clamp(loadP * 50 / loadTotal, 0, 49));
            }
        }

        var (zMin, zMax) = NavGridMapPreviewRaster.ElevRange(ext, ew, eh);
        var zSpan = Math.Max(1e-3, zMax - zMin);
        var bw = pc * superSample;
        var bh = pr * superSample;
        if (bw > 20000 || bh > 20000)
            return null;

        var stride = bw * 4;
        var buffer = new byte[stride * bh];
        var pixTotal = (long)bw * bh;
        long pixP = 0;
        for (var jr = 0; jr < bh; jr++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var ic = 0; ic < bw; ic++)
            {
                var nc = patchC0 + ic / superSample;
                var nr = patchR0 + jr / superSample;
                nc = Math.Clamp(nc, patchC0, patchC1);
                nr = Math.Clamp(nr, patchR0, patchR1);
                var lc = nc - cLo;
                var lr = nr - rLo;
                var cell = ext[lr * ew + lc];
                var (b, g, r8, a) = NavGridMapPreviewRaster.PixelBgra(cell, lc, lr, ew, eh, ext, zMin, zSpan,
                    hillshade);
                var o = jr * stride + ic * 4;
                buffer[o] = b;
                buffer[o + 1] = g;
                buffer[o + 2] = r8;
                buffer[o + 3] = a;
                pixP++;
                if ((pixP &  4095) == 0 && pixTotal > 0)
                    progress?.Report((int)Math.Clamp(50 + pixP * 50 / pixTotal, 50, 99));
            }
        }

        progress?.Report(100);
        return new NavGridPatchBitmapData(bw, bh, buffer);
    }

    private static Image? TryBuildNavGridImageFromCellPatchInMemory(
        IReadOnlyList<NavCellDefinition> cells,
        int cols,
        int rows,
        int patchC0,
        int patchC1,
        int patchR0,
        int patchR1,
        double cellPixels,
        int superSample,
        bool hillshade,
        double marginOx,
        double marginOy)
    {
        var filled = TryFillNavGridPatchBufferInMemory(cells, cols, rows, patchC0, patchC1, patchR0, patchR1, superSample,
            hillshade, progress: null, CancellationToken.None);
        if (filled is null)
            return null;
        var pc = patchC1 - patchC0 + 1;
        var pr = patchR1 - patchR0 + 1;
        return CreateNavGridPatchImage(filled.Value, cellPixels, marginOx, marginOy, patchC0, patchR0, pc, pr);
    }

    private static Image? TryBuildNavGridPatchFromChunkSource(
        TerrainNavGridDefinition grid,
        INavGridCellSource source,
        int patchC0,
        int patchC1,
        int patchR0,
        int patchR1,
        double cellPixels,
        int superSample,
        bool hillshade,
        double marginOx,
        double marginOy)
    {
        var filled = TryFillNavGridPatchBufferFromChunkSource(grid, source, patchC0, patchC1, patchR0, patchR1, superSample,
            hillshade, progress: null, CancellationToken.None);
        if (filled is null)
            return null;
        var pc = patchC1 - patchC0 + 1;
        var pr = patchR1 - patchR0 + 1;
        return CreateNavGridPatchImage(filled.Value, cellPixels, marginOx, marginOy, patchC0, patchR0, pc, pr);
    }

    private static bool TryDecodePngBgra(string path, out int w, out int h, out byte[] bgra)
    {
        w = 0;
        h = 0;
        bgra = null!;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource src = frame;
            if (!Equals(frame.Format, PixelFormats.Bgra32))
            {
                src = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                src.Freeze();
            }

            w = src.PixelWidth;
            h = src.PixelHeight;
            bgra = new byte[(long)w * h * 4];
            src.CopyPixels(bgra, w * 4, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fast path: composite visible patch from pre-baked per-chunk PNGs (see <see cref="NavGridChunkPreviewGenerator"/>).</summary>
    private static NavGridPatchBitmapData? TryComposePatchFromChunkPreviewPngs(
        string chunkStoreDirectory,
        NavGridChunkManifest manifest,
        int patchC0,
        int patchC1,
        int patchR0,
        int patchR1,
        int superSample,
        bool hillshade,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var tpn = manifest.ChunkPreviewPixelsPerNavCell;
        if (tpn < 1)
            return null;
        if (manifest.ChunkPreviewHillshade is { } hs && hs != hillshade)
            return null;

        var sub = manifest.ChunkPreviewPngSubfolder;
        if (string.IsNullOrWhiteSpace(sub))
            sub = "preview_chunks";

        var cols = manifest.Columns;
        var rows = manifest.Rows;
        var cw = manifest.ChunkWidthCells;
        var ch = manifest.ChunkHeightCells;
        if (cw < 1 || ch < 1 || cols < 1 || rows < 1)
            return null;

        var pc = patchC1 - patchC0 + 1;
        var pr = patchR1 - patchR0 + 1;
        superSample = Math.Clamp(superSample, 1, 8);
        var bw = pc * superSample;
        var bh = pr * superSample;
        if (bw > 20000 || bh > 20000)
            return null;

        var stride = bw * 4;
        var buffer = new byte[stride * bh];
        var cache = new Dictionary<(int cx, int cy), (int tw, int th, byte[] pix)>();

        bool TryGetChunkPng(int cx, int cy, out int tw, out int th, out byte[] pix)
        {
            if (cache.TryGetValue((cx, cy), out var e))
            {
                tw = e.tw;
                th = e.th;
                pix = e.pix;
                return true;
            }

            var col0 = cx * cw;
            var row0 = cy * ch;
            var lw = Math.Min(cw, cols - col0);
            var lh = Math.Min(ch, rows - row0);
            if (lw < 1 || lh < 1)
            {
                tw = th = 0;
                pix = null!;
                return false;
            }

            tw = lw * tpn;
            th = lh * tpn;
            var path = Path.Combine(chunkStoreDirectory, sub, NavGridChunkIO.ChunkPreviewPngFileName(cx, cy));
            if (!File.Exists(path) || !TryDecodePngBgra(path, out var iw, out var ih, out var bgra))
            {
                pix = null!;
                return false;
            }

            if (iw != tw || ih != th)
            {
                pix = null!;
                return false;
            }

            cache[(cx, cy)] = (tw, th, bgra);
            pix = bgra;
            return true;
        }

        var total = (long)bw * bh;
        long done = 0;
        for (var jr = 0; jr < bh; jr++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var ic = 0; ic < bw; ic++)
            {
                var nc = patchC0 + ic / superSample;
                var nr = patchR0 + jr / superSample;
                nc = Math.Clamp(nc, 0, cols - 1);
                nr = Math.Clamp(nr, 0, rows - 1);
                var cx = nc / cw;
                var cy = nr / ch;
                if (!TryGetChunkPng(cx, cy, out var tw, out var th, out var pix))
                    return null;

                var col0 = cx * cw;
                var row0 = cy * ch;
                var lc = nc - col0;
                var lr = nr - row0;
                var sp = Math.Clamp(tpn / 2, 0, Math.Max(0, tpn - 1));
                var sx = lc * tpn + sp;
                var sy = lr * tpn + sp;
                sx = Math.Clamp(sx, 0, tw - 1);
                sy = Math.Clamp(sy, 0, th - 1);
                var si = (sy * tw + sx) * 4;
                var o = jr * stride + ic * 4;
                buffer[o] = pix[si];
                buffer[o + 1] = pix[si + 1];
                buffer[o + 2] = pix[si + 2];
                buffer[o + 3] = pix[si + 3];

                done++;
                if ((done & 4095) == 0 && total > 0)
                    progress?.Report((int)Math.Clamp(done * 100 / total, 0, 99));
            }
        }

        progress?.Report(100);
        return new NavGridPatchBitmapData(bw, bh, buffer);
    }
}
