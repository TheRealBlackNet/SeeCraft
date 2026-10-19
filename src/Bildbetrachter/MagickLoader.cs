using ImageMagick;

namespace Bildbetrachter;

/// <summary>Kapselt Magick.NET (eigene Datei, damit sich die ImageMagick-Typen nicht mit WPF-Typen beissen).</summary>
internal static class MagickLoader
{
    public readonly record struct Result(byte[] Bgra, int Width, int Height, int OrigWidth, int OrigHeight, bool Alpha, string Format);

    public static Result Load(string path, int maxSide)
    {
        using var img = new MagickImage();
        img.Read(new FileInfo(path));
        img.AutoOrient();

        int ow = (int)img.Width, oh = (int)img.Height;
        bool alpha = img.HasAlpha;
        string format = img.Format.ToString().ToUpperInvariant();

        if (maxSide > 0 && Math.Max(ow, oh) > maxSide)
            img.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));

        byte[] bgra = img.ToByteArray(MagickFormat.Bgra);
        return new Result(bgra, (int)img.Width, (int)img.Height, ow, oh, alpha, format);
    }
}
