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
