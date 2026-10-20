using System.Buffers.Binary;
using System.IO.Compression;
using System.IO;
using System.Text;

namespace Bildbetrachter;

/// <summary>Liest Text-Chunks (tEXt, zTXt, iTXt) aus PNGs - dort legen z. B. Stable-Diffusion-Tools den Prompt ab.</summary>
public static class PngMetadata
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    const int MaxChunk = 8 << 20;

    /// <summary>Liefert den formatierten Text oder null, wenn keine nennenswerten Metadaten vorhanden sind.</summary>
    public static string? Read(string path)
    {
        var entries = new List<(string Key, string Value)>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8 || !sig.AsSpan().SequenceEqual(Signature)) return null;

            var head = new byte[8];
            while (fs.Position + 12 <= fs.Length)
            {
                fs.ReadExactly(head);
                uint len = BinaryPrimitives.ReadUInt32BigEndian(head);
                string type = Encoding.ASCII.GetString(head, 4, 4);
                if (type == "IEND") break;

                if (type is "tEXt" or "zTXt" or "iTXt" && len <= MaxChunk)
                {
                    var data = new byte[len];
                    fs.ReadExactly(data);
                    fs.Seek(4, SeekOrigin.Current); // CRC
                    var e = Parse(type, data);
                    if (e != null) entries.Add(e.Value);
                }
                else
                {
                    fs.Seek((long)len + 4, SeekOrigin.Current);
                }
            }
        }
        catch { /* beschaedigtes PNG -> keine Metadaten */ }

        entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.Value));
        if (!entries.Any(e => e.Value.Length >= 20)) return null;
        return string.Join("\n\n", entries.Select(e => $"[{e.Key}]\n{e.Value}"));
    }

    static (string, string)? Parse(string type, byte[] d)
    {
        int z = Array.IndexOf(d, (byte)0);
        if (z <= 0) return null;
        string key = Encoding.Latin1.GetString(d, 0, z);

        switch (type)
        {
            case "tEXt":
                return (key, Decode(d, z + 1, d.Length - z - 1));

            case "zTXt":
                if (z + 2 > d.Length) return null;
                return (key, Decode(Inflate(d, z + 2), 0, -1));

            default: // iTXt
                if (z + 3 > d.Length) return null;
                bool compressed = d[z + 1] == 1;
                int p = z + 3;
                int z2 = Array.IndexOf(d, (byte)0, p);            // Sprach-Tag
                if (z2 < 0) return null;
                int z3 = Array.IndexOf(d, (byte)0, z2 + 1);       // uebersetztes Schluesselwort
                if (z3 < 0) return null;
                int start = z3 + 1;
                if (compressed) return (key, Decode(Inflate(d, start), 0, -1));
                return (key, Decode(d, start, d.Length - start));
        }
    }

    static byte[] Inflate(byte[] d, int offset)
    {
        using var input = new MemoryStream(d, offset, d.Length - offset);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    static string Decode(byte[] d, int offset, int count)
    {
        if (count < 0) count = d.Length - offset;
        try { return new UTF8Encoding(false, true).GetString(d, offset, count); }
        catch { return Encoding.Latin1.GetString(d, offset, count); }
    }
}
