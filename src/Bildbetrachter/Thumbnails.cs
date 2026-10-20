using System.Windows.Media.Imaging;

namespace Bildbetrachter;

/// <summary>
/// Lädt Miniaturen für die Listenansicht im Hintergrund.
/// Neueste Anfrage zuerst (LIFO): beim schnellen Scrollen werden zuerst die gerade sichtbaren Zeilen bedient.
/// Die Warteschlange ist begrenzt; verworfene Zeilen fragen erneut an, sobald sie wieder sichtbar werden.
/// </summary>
public static class ThumbnailLoader
{
    const int DecodeSize = 100;   // 50 x 50 WPF-Einheiten, auch noch scharf bei 200 % Skalierung
    const int MaxQueue = 300;
    const int Workers = 2;

    static readonly ImageCache Cache = new(96L << 20);
    static readonly LinkedList<ImageItem> Stack = new();
    static readonly object Lock = new();
    static int _running;

    static string Key(ImageItem i) => $"{i.FullPath}|{i.Modified.Ticks}|{i.Size}";

    public static bool TryGetCached(ImageItem item, out BitmapSource? bmp)
    {
        if (Cache.TryGet(Key(item), out var li) && li != null) { bmp = li.Bitmap; return true; }
        bmp = null;
        return false;
    }

    public static void Request(ImageItem item)
    {
        lock (Lock)
        {
            Stack.AddFirst(item);
            while (Stack.Count > MaxQueue)
            {
                var old = Stack.Last!.Value;
                Stack.RemoveLast();
                old.ResetThumbnailRequest();
            }
            if (_running >= Workers) return;
            _running++;
        }
        Task.Run(Worker);
    }

    static void Worker()
    {
        while (true)
        {
            ImageItem item;
            lock (Lock)
            {
                if (Stack.Count == 0) { _running--; return; }
                item = Stack.First!.Value;
                Stack.RemoveFirst();
            }

            try
            {
                var li = ImageLoader.Load(item.FullPath, DecodeSize);
                Cache.Add(Key(item), li);
                item.SetThumbnail(li.Bitmap);
            }
            catch
            {
                item.SetThumbnail(null);   // defektes Bild: keine Miniatur, kein Endlos-Retry
            }
        }
    }
}
