using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LetsAdventure.Core.Json;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor.Services;

/// <summary>Writes per-chunk PNG tiles next to nav grid binaries for fast 2D map preview compositing.</summary>
public static class NavGridChunkPreviewGenerator
{
    /// <param name="pixelsPerNavCell">BGRA pixels per nav cell along each axis in every tile PNG (1 = low-res).</param>
    public static void Export(string chunkStoreDirectory, int pixelsPerNavCell = 1, bool hillshade = true,
        IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(chunkStoreDirectory))
            throw new ArgumentException("Chunk directory is required.", nameof(chunkStoreDirectory));
        if (!NavGridChunkIO.TryLoadManifest(chunkStoreDirectory, out var manifest) || manifest is null)
            throw new InvalidOperationException("manifest.json not found or invalid in " + chunkStoreDirectory);

        pixelsPerNavCell = Math.Clamp(pixelsPerNavCell, 1, 8);
        var sub = manifest.ChunkPreviewPngSubfolder;
        if (string.IsNullOrWhiteSpace(sub))
            sub = "preview_chunks";
        var outDir = Path.Combine(chunkStoreDirectory, sub);
        Directory.CreateDirectory(outDir);

        var cols = manifest.Columns;
        var rows = manifest.Rows;
        var cw = manifest.ChunkWidthCells;
        var ch = manifest.ChunkHeightCells;
        var cxN = manifest.ChunksX;
        var cyN = manifest.ChunksY;
        var total = Math.Max(1, cxN * cyN);
        var done = 0;

        for (var cy = 0; cy < cyN; cy++)
        {
            for (var cx = 0; cx < cxN; cx++)
            {
                var col0 = cx * cw;
                var row0 = cy * ch;
                var w = Math.Min(cw, cols - col0);
                var h = Math.Min(ch, rows - row0);
                if (w <= 0 || h <= 0)
                    continue;

                var binPath = Path.Combine(chunkStoreDirectory, NavGridChunkIO.ChunkFileName(cx, cy));
                if (!File.Exists(binPath))
                    throw new FileNotFoundException("Missing chunk binary.", binPath);

                var cells = NavGridChunkIO.ReadChunkFile(binPath, out var fcx, out var fcy, out var lw, out var lh);
                if (fcx != cx || fcy != cy || lw != w || lh != h)
                    throw new InvalidDataException($"Chunk c_{cx:0000}_{cy:0000} dimensions do not match manifest.");

                var bgra = NavGridMapPreviewRaster.TryEncodePatchBgra(cells, w, h, 0, w - 1, 0, h - 1, pixelsPerNavCell,
                    hillshade, progress: null, CancellationToken.None);
                if (bgra is null)
                    throw new InvalidOperationException($"Raster failed for chunk c_{cx:0000}_{cy:0000}.");

                var pw = w * pixelsPerNavCell;
                var ph = h * pixelsPerNavCell;
                SaveBgraPng(Path.Combine(outDir, NavGridChunkIO.ChunkPreviewPngFileName(cx, cy)), pw, ph, bgra);

                done++;
                if (progress != null && (done == 1 || done == total || done % Math.Max(1, total / 16) == 0))
                    progress.Report($"[chunk previews] Wrote {done}/{total} PNG tiles…");
            }
        }

        manifest.ChunkPreviewPixelsPerNavCell = pixelsPerNavCell;
        manifest.ChunkPreviewHillshade = hillshade;
        manifest.ChunkPreviewPngSubfolder = sub;
        File.WriteAllText(Path.Combine(chunkStoreDirectory, NavGridChunkIO.ManifestFileName),
            JsonSerializer.Serialize(manifest, GameJson.Options));
        progress?.Report("[chunk previews] manifest.json updated.");
    }

    private static void SaveBgraPng(string path, int w, int h, byte[] bgra)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
