namespace Bildbetrachter;

public sealed record ImageItem(string FullPath, string Name, DateTime Modified, DateTime Created, long Size)
{
    public string ModifiedText => Modified.ToString("g");
    public string SizeText => Size < 1024 ? $"{Size} B"
                            : Size < 1 << 20 ? $"{Size / 1024.0:0.#} KB"
                            : $"{Size / 1048576.0:0.##} MB";
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
