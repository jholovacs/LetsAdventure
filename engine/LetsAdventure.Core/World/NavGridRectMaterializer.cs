namespace LetsAdventure.Core.World;

/// <summary>Copies a nav-cell axis-aligned rectangle from chunk binaries into a dense row-major buffer.</summary>
public static class NavGridRectMaterializer
{
    /// <summary>Fills <paramref name="dest"/> row-major [0..w) × [0..h) with cells [c0,c0+w) × [r0,r0+h).</summary>
    public static void CopyRect(
        string chunkDirectory,
        NavGridChunkManifest manifest,
        int c0,
        int r0,
        int w,
        int h,
        NavCellDefinition[] dest)
    {
        ArgumentNullException.ThrowIfNull(dest);
        if (string.IsNullOrWhiteSpace(chunkDirectory))
            throw new ArgumentException("Chunk directory is required.", nameof(chunkDirectory));
        if (w < 1 || h < 1)
            throw new ArgumentOutOfRangeException(nameof(w));
        if (dest.Length < (long)w * h)
            throw new ArgumentException("dest too small.", nameof(dest));

        var cols = manifest.Columns;
        var rows = manifest.Rows;
        var cw = manifest.ChunkWidthCells;
        var ch = manifest.ChunkHeightCells;
        if (cw < 1 || ch < 1)
            throw new InvalidOperationException("Invalid chunk dimensions in manifest.");

        var c1 = Math.Min(c0 + w, cols) - 1;
        var r1 = Math.Min(r0 + h, rows) - 1;
        if (c0 > c1 || r0 > r1 || c0 < 0 || r0 < 0)
            throw new ArgumentOutOfRangeException(nameof(c0), "Rectangle outside grid.");

        var cx0 = c0 / cw;
        var cx1 = c1 / cw;
        var cy0 = r0 / ch;
        var cy1 = r1 / ch;

        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var path = Path.Combine(chunkDirectory, NavGridChunkIO.ChunkFileName(cx, cy));
                var cells = NavGridChunkIO.ReadChunkFile(path, out var fcx, out var fcy, out var lw, out var lh);
                if (fcx != cx || fcy != cy)
                    throw new InvalidDataException($"Chunk coord mismatch for c_{cx:0000}_{cy:0000}.bin");

                var colBase = cx * cw;
                var rowBase = cy * ch;

                var gx0 = Math.Max(c0, colBase);
                var gy0 = Math.Max(r0, rowBase);
                var gx1 = Math.Min(c0 + w - 1, colBase + lw - 1);
                var gy1 = Math.Min(r0 + h - 1, rowBase + lh - 1);

                for (var gr = gy0; gr <= gy1; gr++)
                {
                    for (var gc = gx0; gc <= gx1; gc++)
                    {
                        var lx = gc - colBase;
                        var ly = gr - rowBase;
                        if ((uint)lx >= (uint)lw || (uint)ly >= (uint)lh)
                            continue;
                        var cell = cells[ly * lw + lx];
                        var di = (gr - r0) * w + (gc - c0);
                        dest[di] = cell;
                    }
                }
            }
        }
    }
}
