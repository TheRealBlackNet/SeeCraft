namespace Bildbetrachter;
using System.IO;

/// <summary>LRU-Cache mit Byte-Budget: haelt nur so viel Bilddaten, wie das Budget erlaubt.</summary>
public sealed class ImageCache
{
    sealed record Entry(string Key, LoadedImage Image, long Bytes);

    readonly long _budget;
    long _used;
    readonly object _lock = new();
    readonly LinkedList<Entry> _lru = new();
    readonly Dictionary<string, LinkedListNode<Entry>> _map = new();

    public ImageCache(long budgetBytes) => _budget = budgetBytes;

    public bool TryGet(string key, out LoadedImage? image)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null;
        return false;
    }

    public void Add(string key, LoadedImage image)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old))
            {
                _used -= old.Value.Bytes;
                _lru.Remove(old);
                _map.Remove(key);
            }
            var node = _lru.AddFirst(new Entry(key, image, image.EstimatedBytes));
            _map[key] = node;
            _used += node.Value.Bytes;

            while (_used > _budget && _lru.Count > 1)
            {
                var last = _lru.Last!;
                _used -= last.Value.Bytes;
                _map.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_lock) { _lru.Clear(); _map.Clear(); _used = 0; }
    }
}

/// <summary>
/// Zentraler Bild-Lader: Cache + hoechstens 2 gleichzeitige Dekodierungen.
/// Wer beim schnellen Blaettern abgebrochen wird (CancellationToken), wartet noch gar nicht
/// im Decoder - veraltete Bilder werden also uebersprungen statt berechnet.
/// </summary>
public sealed class ImageService
{
    readonly ImageCache _full = new(600L << 20);     // Vollbilder fuer die Bildansicht
    readonly ImageCache _preview = new(150L << 20);  // verkleinerte Vorschauen
    readonly SemaphoreSlim _gate = new(2);

    static string Key(string path, int cap)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{path}|{cap}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        }
        catch { return $"{path}|{cap}"; }
    }

    public bool TryGetCached(string path, int cap, out LoadedImage? image) =>
        (cap == 0 ? _full : _preview).TryGet(Key(path, cap), out image);

    public async Task<LoadedImage> GetAsync(string path, int cap, CancellationToken ct)
    {
        var cache = cap == 0 ? _full : _preview;
        string key = Key(path, cap);
        if (cache.TryGet(key, out var hit) && hit != null) return hit;

        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (cache.TryGet(key, out hit) && hit != null) return hit;

            var img = await Task.Run(() => ImageLoader.Load(path, cap), CancellationToken.None);
            cache.Add(key, img);
            return img;
        }
        finally { _gate.Release(); }
    }
}
