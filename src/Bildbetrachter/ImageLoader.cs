using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bildbetrachter;

public sealed class LoadedImage
{
    public required BitmapSource Bitmap { get; init; }
    public int OriginalWidth { get; init; }
    public int OriginalHeight { get; init; }
    public int BitsPerPixel { get; init; }
    public bool IsAnimatedGif { get; init; }
    public string Format { get; init; } = "";
    public long EstimatedBytes => (long)Bitmap.PixelWidth * Bitmap.PixelHeight * 4;
}

/// <summary>
/// Laedt Bilder thread-sicher (Aufruf aus Hintergrund-Threads). Ergebnisse sind eingefroren (Frozen),
/// damit sie ohne Kopie im UI-Thread angezeigt werden koennen.
/// </summary>
public static class ImageLoader
{
    /// <param name="maxSide">0 = volle Aufloesung; sonst wird beim Dekodieren auf diese Kantenlaenge verkleinert.</param>
    public static LoadedImage Load(string path, int maxSide = 0)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageFiles.WicExtensions.Contains(ext))
        {
            try { return LoadWic(path, maxSide, ext); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* Fallback: ImageMagick */ }
        }
        return LoadMagick(path, maxSide);
    }

    static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.None);

    static LoadedImage LoadWic(string path, int maxSide, string ext)
    {
        int w, h, bpp, orientation = 1;
        bool animated;

        // 1) Nur Header lesen: Groesse, Farbtiefe, Ausrichtung, Animation
        using (var fs = OpenRead(path))
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = dec.Frames[0];
            w = frame.PixelWidth;
            h = frame.PixelHeight;
            bpp = frame.Format.BitsPerPixel;
            animated = dec is GifBitmapDecoder && dec.Frames.Count > 1;
            if (ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".tif" or ".tiff")
                orientation = ReadOrientation(frame);
        }

        string format = ext.TrimStart('.').ToUpperInvariant();

        // 2) Animierte GIFs: per URI laden, die Animation uebernimmt WpfAnimatedGif
        if (animated)
        {
            var gif = new BitmapImage();
            gif.BeginInit();
            gif.UriSource = new Uri(path, UriKind.Absolute);
            gif.CacheOption = BitmapCacheOption.OnLoad;
            gif.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            gif.EndInit();
            gif.Freeze();
            return new LoadedImage { Bitmap = gif, OriginalWidth = w, OriginalHeight = h, BitsPerPixel = bpp, IsAnimatedGif = true, Format = format };
        }

        // 3) Normal laden (ggf. schon beim Dekodieren verkleinern -> schnell und speicherschonend)
        BitmapSource bmp;
        using (var fs = OpenRead(path))
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = fs;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (maxSide > 0 && Math.Max(w, h) > maxSide)
            {
                if (w >= h) bi.DecodePixelWidth = maxSide; else bi.DecodePixelHeight = maxSide;
            }
            bi.EndInit();
            bi.Freeze();
            bmp = bi;
        }

        if (orientation > 1)
        {
            bmp = ApplyOrientation(bmp, orientation);
            if (orientation >= 5) (w, h) = (h, w);
        }

        return new LoadedImage { Bitmap = bmp, OriginalWidth = w, OriginalHeight = h, BitsPerPixel = bpp, Format = format };
    }

    static LoadedImage LoadMagick(string path, int maxSide)
    {
        var r = MagickLoader.Load(path, maxSide);
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgra32, null, r.Bgra, r.Width * 4);
        bmp.Freeze();
        return new LoadedImage
        {
            Bitmap = bmp,
            OriginalWidth = r.OrigWidth,
            OriginalHeight = r.OrigHeight,
            BitsPerPixel = r.Alpha ? 32 : 24,
            Format = r.Format
        };
    }

    static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata m && m.ContainsQuery("System.Photo.Orientation")
                && m.GetQuery("System.Photo.Orientation") is ushort o && o is >= 1 and <= 8)
                return o;
        }
        catch { /* keine EXIF-Daten */ }
        return 1;
    }

    static BitmapSource ApplyOrientation(BitmapSource src, int o)
    {
        static BitmapSource Rot(BitmapSource s, double angle) => new TransformedBitmap(s, new RotateTransform(angle));
        static BitmapSource Flip(BitmapSource s) => new TransformedBitmap(s, new ScaleTransform(-1, 1));

        BitmapSource t = o switch
        {
            2 => Flip(src),
            3 => Rot(src, 180),
            4 => Rot(Flip(src), 180),
            5 => Rot(Flip(src), 270),
            6 => Rot(src, 90),
            7 => Rot(Flip(src), 90),
            8 => Rot(src, 270),
            _ => src
        };

        // Sofort (im Hintergrund-Thread) berechnen, nicht erst beim ersten Zeichnen im UI-Thread
        var cached = new CachedBitmap(t, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }
}
