using System.Drawing.Imaging;

namespace TarkovOverlay;

/// <summary>
/// LRU cache of decoded map tiles with a background loader.
///
/// This is the whole reason the app stays small: a 12241x8380 map would be
/// ~410 MB decoded, but only the ~20 tiles actually on screen are ever held,
/// and they are evicted as you pan away.
/// </summary>
public sealed class TileCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, LinkedListNode<Entry>> _index = new();
    private readonly LinkedList<Entry> _lru = new();      // head = most recently used
    private readonly List<long> _pending = new();          // used as a LIFO stack
    private readonly HashSet<long> _pendingSet = new();
    private readonly AutoResetEvent _work = new(false);
    private readonly Thread _worker;

    private MapMeta? _map;
    private int _capacity;
    private volatile bool _disposed;

    public event Action? TileLoaded;

    private sealed class Entry
    {
        public long Key;
        public Bitmap? Bitmap;   // null = known-missing tile, don't retry
    }

    public TileCache(int capacity)
    {
        _capacity = Math.Max(12, capacity);
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "tile-loader",
            // Loading must never compete with the game for CPU.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    private static long Key(int z, int x, int y) => ((long)z << 48) | ((long)x << 24) | (uint)y;

    public void SetMap(MapMeta? map)
    {
        lock (_gate)
        {
            _map = map;
            foreach (var e in _lru) e.Bitmap?.Dispose();
            _lru.Clear();
            _index.Clear();
            _pending.Clear();
            _pendingSet.Clear();
        }
    }

    public void SetCapacity(int capacity)
    {
        lock (_gate)
        {
            _capacity = Math.Max(12, capacity);
            Evict();
        }
    }

    /// <summary>Returns a cached tile, or null. Never touches disk.</summary>
    public Bitmap? TryGet(int z, int x, int y)
    {
        var k = Key(z, x, y);
        lock (_gate)
        {
            if (!_index.TryGetValue(k, out var node)) return null;
            if (node != _lru.First)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            return node.Value.Bitmap;
        }
    }

    /// <summary>Queues a tile for background decode if it isn't cached already.</summary>
    public void Request(int z, int x, int y)
    {
        var k = Key(z, x, y);
        lock (_gate)
        {
            if (_index.ContainsKey(k) || _pendingSet.Contains(k)) return;
            _pendingSet.Add(k);
            _pending.Add(k);            // newest request is popped first
        }
        _work.Set();
    }

    /// <summary>Drops queued work that scrolled out of view before it was loaded.</summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            _pending.Clear();
            _pendingSet.Clear();
        }
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            long k;
            MapMeta? map;
            lock (_gate)
            {
                map = _map;
                if (_pending.Count == 0 || map is null)
                {
                    k = -1;
                }
                else
                {
                    var last = _pending.Count - 1;
                    k = _pending[last];
                    _pending.RemoveAt(last);
                    _pendingSet.Remove(k);
                }
            }

            if (k < 0)
            {
                _work.WaitOne(250);
                continue;
            }

            int z = (int)(k >> 48);
            int x = (int)((k >> 24) & 0xFFFFFF);
            int y = (int)(k & 0xFFFFFF);

            Bitmap? bmp = null;
            try
            {
                var path = Path.Combine(map!.TilesDir, z.ToString(), $"{x}_{y}.png");
                if (File.Exists(path))
                {
                    // Decode from a byte[] so GDI+ never holds the file handle,
                    // then copy so the bitmap is detached from the source stream.
                    var bytes = File.ReadAllBytes(path);
                    using var ms = new MemoryStream(bytes, writable: false);
                    using var decoded = new Bitmap(ms);
                    bmp = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format24bppRgb);
                    using var g = Graphics.FromImage(bmp);
                    g.DrawImageUnscaled(decoded, 0, 0);
                }
            }
            catch
            {
                bmp = null;   // treat as a missing tile
            }

            bool stillWanted;
            lock (_gate)
            {
                stillWanted = _map == map;
                if (stillWanted && !_index.ContainsKey(k))
                {
                    var node = _lru.AddFirst(new Entry { Key = k, Bitmap = bmp });
                    _index[k] = node;
                    Evict();
                }
                else
                {
                    bmp?.Dispose();
                }
            }

            if (stillWanted) TileLoaded?.Invoke();
        }
    }

    /// <summary>Caller must hold _gate.</summary>
    private void Evict()
    {
        while (_lru.Count > _capacity)
        {
            var node = _lru.Last!;
            _lru.RemoveLast();
            _index.Remove(node.Value.Key);
            node.Value.Bitmap?.Dispose();
        }
    }

    /// <summary>Frees every decoded tile. Called when the overlay is hidden.</summary>
    public void Purge()
    {
        lock (_gate)
        {
            foreach (var e in _lru) e.Bitmap?.Dispose();
            _lru.Clear();
            _index.Clear();
            _pending.Clear();
            _pendingSet.Clear();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _work.Set();
        Purge();
        _work.Dispose();
    }
}
