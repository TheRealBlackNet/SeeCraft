using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Bildbetrachter;

public sealed class ImageItem : INotifyPropertyChanged
{
    public ImageItem(string fullPath, string name, DateTime modified, DateTime created, long size)
    {
        FullPath = fullPath; Name = name; Modified = modified; Created = created; Size = size;
    }

    public string FullPath { get; }
    public string Name { get; }
    public DateTime Modified { get; }
    public DateTime Created { get; }
    public long Size { get; }

    public string ModifiedText => Modified.ToString("g");
    public string SizeText => Size < 1024 ? $"{Size} B"
                            : Size < 1 << 20 ? $"{Size / 1024.0:0.#} KB"
                            : $"{Size / 1048576.0:0.##} MB";

    // ---- Miniatur: wird erst geladen, wenn die Zeile tatsächlich sichtbar ist (Binding liest die Property) ----
    BitmapSource? _thumb;
    bool _requested;

    public BitmapSource? Thumbnail
    {
        get
        {
            if (_thumb == null && !_requested)
            {
                _requested = true;
                if (ThumbnailLoader.TryGetCached(this, out var cached)) _thumb = cached;
                else ThumbnailLoader.Request(this);
            }
            return _thumb;
        }
    }

    internal void ResetThumbnailRequest() => _requested = false;

    internal void SetThumbnail(BitmapSource? bmp)
    {
        _thumb = bmp;
        Application.Current?.Dispatcher.InvokeAsync(
            () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class ImageFiles
{
    /// <summary>Formate, die Windows (WIC) selbst dekodiert - der schnellste Weg.</summary>
    public static readonly HashSet<string> WicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".jxr", ".wdp"
    };

    /// <summary>Formate, die ueber ImageMagick (Magick.NET) geladen werden.</summary>
    public static readonly HashSet<string> MagickExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp", ".heic", ".heif", ".avif", ".jxl", ".tga", ".psd", ".jp2", ".dds", ".pcx"
    };

    public static bool IsImage(string extension) =>
        WicExtensions.Contains(extension) || MagickExtensions.Contains(extension);

    public static List<ImageItem> Enumerate(string folder)
    {
        var list = new List<ImageItem>();
        foreach (var fi in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (IsImage(fi.Extension))
                list.Add(new ImageItem(fi.FullName, fi.Name, fi.LastWriteTime, fi.CreationTime, fi.Length));
        }
        return list;
    }

    public static void Sort(List<ImageItem> list, SortField field, bool descending)
    {
        int Cmp(ImageItem a, ImageItem b)
        {
            int r = field switch
            {
                SortField.Modified => a.Modified.CompareTo(b.Modified),
                SortField.Created => a.Created.CompareTo(b.Created),
                _ => Native.StrCmpLogicalW(a.Name, b.Name)
            };
            if (r == 0) r = Native.StrCmpLogicalW(a.Name, b.Name);
            return descending ? -r : r;
        }
        list.Sort(Cmp);
    }
}
