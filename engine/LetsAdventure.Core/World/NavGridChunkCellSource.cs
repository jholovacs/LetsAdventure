namespace LetsAdventure.Core.World;

/// <summary>Lazy chunk loader with small in-memory LRU for <see cref="INavGridCellSource"/>.</summary>
public sealed class NavGridChunkCellSource : INavGridCellSource, IDisposable
{
    private readonly string _directory;
    private readonly NavGridChunkManifest _manifest;
    private readonly Dictionary<(int cx, int cy), ChunkCacheEntry> _cache = new();
    private readonly LinkedList<(int cx, int cy)> _lru = new();
    private readonly int _maxCachedChunks;
    private readonly object _gate = new();

    private sealed class ChunkCacheEntry
    {
        public required int LocalW;
        public required int LocalH;
        public required NavCellDefinition[] Cells;
        public required LinkedListNode<(int cx, int cy)> LruNode;
    }

    private NavGridChunkCellSource(string directory, NavGridChunkManifest manifest, int maxCachedChunks = 48)
    {
        _directory = directory;
        _manifest = manifest;
        _maxCachedChunks = Math.Max(4, maxCachedChunks);
    }

    public string Directory => _directory;
    public NavGridChunkManifest Manifest => _manifest;

    public static NavGridChunkCellSource? TryOpen(string directory, int maxCachedChunks = 48)
    {
        if (!NavGridChunkIO.TryLoadManifest(directory, out var m) || m is null)
            return null;
        return new NavGridChunkCellSource(Path.GetFullPath(directory), m, maxCachedChunks);
    }

    public bool TryGetCell(int col, int row, out NavCellDefinition cell)
    {
        cell = default!;
        if ((uint)col >= (uint)_manifest.Columns || (uint)row >= (uint)_manifest.Rows)
            return false;

        var cw = _manifest.ChunkWidthCells;
        var ch = _manifest.ChunkHeightCells;
        var cx = col / cw;
        var cy = row / ch;
        var col0 = cx * cw;
        var row0 = cy * ch;

        var payload = GetOrLoadChunk(cx, cy);
        if (payload is null)
            return false;

        var lx = col - col0;
        var ly = row - row0;
        if ((uint)lx >= (uint)payload.LocalW || (uint)ly >= (uint)payload.LocalH)
            return false;

        cell = payload.Cells[ly * payload.LocalW + lx];
        return true;
    }

    /// <summary>Best-effort load of one chunk file into the LRU (no-op if out of bounds).</summary>
    public void PreloadChunk(int cx, int cy)
    {
        if ((uint)cx >= (uint)_manifest.ChunksX || (uint)cy >= (uint)_manifest.ChunksY)
            return;
        var cw = _manifest.ChunkWidthCells;
        var ch = _manifest.ChunkHeightCells;
        TryGetCell(cx * cw, cy * ch, out _);
    }

    /// <summary>Preload every chunk whose index is within [−radius, +radius] of the chunk containing <paramref name="col"/>, <paramref name="row"/>.</summary>
    public void PreloadChunksAroundCell(int col, int row, int radiusChunks)
    {
        if (radiusChunks < 1 || _manifest.ChunksX < 1 || _manifest.ChunksY < 1)
            return;
        var cw = _manifest.ChunkWidthCells;
        var ch = _manifest.ChunkHeightCells;
        if (cw < 1 || ch < 1)
            return;
        var cx = col / cw;
        var cy = row / ch;
        var r = Math.Clamp(radiusChunks, 1, Math.Max(_manifest.ChunksX, _manifest.ChunksY) + 4);
        var cx0 = Math.Max(0, cx - r);
        var cx1 = Math.Min(_manifest.ChunksX - 1, cx + r);
        var cy0 = Math.Max(0, cy - r);
        var cy1 = Math.Min(_manifest.ChunksY - 1, cy + r);
        for (var ccy = cy0; ccy <= cy1; ccy++)
        {
            for (var ccx = cx0; ccx <= cx1; ccx++)
                PreloadChunk(ccx, ccy);
        }
    }

    /// <summary>Preload around the grid cell containing world (<paramref name="worldX"/>, <paramref name="worldY"/>).</summary>
    public void PreloadChunksAroundWorldXY(double worldX, double worldY, int radiusChunks)
    {
        var cs = _manifest.CellSize <= 0 ? 1 : _manifest.CellSize;
        var col = (int)Math.Floor((worldX - _manifest.OriginX) / cs);
        var row = (int)Math.Floor((worldY - _manifest.OriginY) / cs);
        col = Math.Clamp(col, 0, Math.Max(0, _manifest.Columns - 1));
        row = Math.Clamp(row, 0, Math.Max(0, _manifest.Rows - 1));
        PreloadChunksAroundCell(col, row, radiusChunks);
    }

    /// <summary>
    /// Drop chunks whose XY bounds do not intersect the axis-aligned square
    /// [<paramref name="centerWorldX"/> ± <paramref name="halfExtentWorldMeters"/>],
    /// [<paramref name="centerWorldY"/> ± <paramref name="halfExtentWorldMeters"/>].
    /// </summary>
    public void RetainChunksIntersectingWorldSquare(double centerWorldX, double centerWorldY,
        double halfExtentWorldMeters)
    {
        if (halfExtentWorldMeters <= 0 || _cache.Count == 0)
            return;
        var minWx = centerWorldX - halfExtentWorldMeters;
        var maxWx = centerWorldX + halfExtentWorldMeters;
        var minWy = centerWorldY - halfExtentWorldMeters;
        var maxWy = centerWorldY + halfExtentWorldMeters;
        List<(int cx, int cy)>? toRemove = null;
        lock (_gate)
        {
            foreach (var kv in _cache)
            {
                ChunkWorldBounds2D(_manifest, kv.Key.cx, kv.Key.cy, out var ax0, out var ax1, out var ay0, out var ay1);
                var intersects = ax1 >= minWx && ax0 <= maxWx && ay1 >= minWy && ay0 <= maxWy;
                if (!intersects)
                    (toRemove ??= new List<(int cx, int cy)>()).Add(kv.Key);
            }

            if (toRemove is null)
                return;
            foreach (var key in toRemove)
            {
                if (!_cache.TryGetValue(key, out var ent))
                    continue;
                _lru.Remove(ent.LruNode);
                _cache.Remove(key);
            }
        }
    }

    private static void ChunkWorldBounds2D(NavGridChunkManifest m, int cx, int cy,
        out double minX, out double maxX, out double minY, out double maxY)
    {
        var cs = m.CellSize <= 0 ? 1 : m.CellSize;
        var cw = Math.Max(1, m.ChunkWidthCells);
        var ch = Math.Max(1, m.ChunkHeightCells);
        var col0 = cx * cw;
        var row0 = cy * ch;
        minX = m.OriginX + col0 * cs;
        maxX = m.OriginX + (col0 + cw) * cs;
        minY = m.OriginY + row0 * cs;
        maxY = m.OriginY + (row0 + ch) * cs;
    }

    private ChunkCacheEntry? GetOrLoadChunk(int cx, int cy)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((cx, cy), out var hit))
            {
                _lru.Remove(hit.LruNode);
                _lru.AddFirst(hit.LruNode);
                return hit;
            }
        }

        var path = Path.Combine(_directory, NavGridChunkIO.ChunkFileName(cx, cy));
        if (!File.Exists(path))
            return null;

        var cells = NavGridChunkIO.ReadChunkFile(path, out var fcx, out var fcy, out var w, out var h);
        if (fcx != cx || fcy != cy)
            return null;

        var entry = new ChunkCacheEntry
        {
            LocalW = w,
            LocalH = h,
            Cells = cells,
            LruNode = null!,
        };

        lock (_gate)
        {
            if (_cache.TryGetValue((cx, cy), out var raced))
            {
                _lru.Remove(raced.LruNode);
                _lru.AddFirst(raced.LruNode);
                return raced;
            }

            var node = _lru.AddFirst((cx, cy));
            entry.LruNode = node;
            _cache[(cx, cy)] = entry;
            while (_cache.Count > _maxCachedChunks)
            {
                var last = _lru.Last;
                if (last is null)
                    break;
                _lru.RemoveLast();
                _cache.Remove(last.Value);
            }
        }

        return entry;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }
}
