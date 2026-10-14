using System.Windows.Media.Imaging;

namespace PdfEdit.Services;

/// <summary>
/// Bounded cache in front of <see cref="PdfiumEngine.RenderPage"/>.
///
/// Pages are re-rendered whenever the zoom changes, and scrolling revisits pages constantly,
/// so without a cache a modest document would re-rasterise on every scroll. Entries are keyed
/// on a rounded scale to stop tiny zoom deltas from each minting their own bitmap.
/// </summary>
public sealed class RenderService : IDisposable
{
    private readonly record struct Key(int Page, int ScaleMilli, int Rotation, bool Annots);

    private readonly PdfiumEngine _engine;
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _lru = new();

    private const int MaxEntries = 48;

    /// <summary>Keeps at most this many bytes of bitmaps alive before evicting.</summary>
    private const long MaxBytes = 512L * 1024 * 1024;

    private long _bytes;

    private sealed class Entry
    {
        public required Key Key { get; init; }
        public required BitmapSource Bitmap { get; init; }
        public long Size { get; init; }
    }

    public RenderService(PdfiumEngine engine) => _engine = engine;

    public BitmapSource Get(int pageIndex, double scale, int extraRotation, bool includeAnnotations)
    {
        // Round to 3 decimals: imperceptible visually, but collapses the long tail of
        // near-identical zoom values into shared cache entries.
        var key = new Key(pageIndex, (int)Math.Round(scale * 1000), extraRotation, includeAnnotations);

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        // Rasterise outside the lock: PDFium serialises internally, and holding our own lock
        // across a slow render would block every other page request behind it.
        var bitmap = _engine.RenderPage(pageIndex, scale, extraRotation, includeAnnotations);
        var size = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Bitmap;
            }

            var entry = new Entry { Key = key, Bitmap = bitmap, Size = size };
            var added = _lru.AddFirst(entry);
            _map[key] = added;
            _bytes += size;

            Trim();
            return bitmap;
        }
    }

    /// <summary>Evicts least-recently-used entries until both budgets are satisfied.</summary>
    private void Trim()
    {
        while ((_lru.Count > MaxEntries || _bytes > MaxBytes) && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            _bytes -= last.Value.Size;
        }
    }

    /// <summary>
    /// Drops every cached bitmap for one page. Needed after a content edit: the cache key
    /// covers page, zoom and rotation but not the page's contents, which have just changed.
    /// </summary>
    public void InvalidatePage(int pageIndex)
    {
        lock (_gate)
        {
            var stale = _map.Where(kv => kv.Key.Page == pageIndex)
                            .Select(kv => kv.Value)
                            .ToList();

            foreach (var node in stale)
            {
                _lru.Remove(node);
                _map.Remove(node.Value.Key);
                _bytes -= node.Value.Size;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    public void Dispose() => Clear();
}
