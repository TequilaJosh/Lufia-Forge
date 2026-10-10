using System.IO;
using System.IO.Compression;

namespace LufiaForge.Core;

/// <summary>
/// Indexed (palette) PNG files written and read byte for byte. WPF's encoder merges palette entries that have the
/// same colour and renumbers the pixels, which breaks colour numbers (a ROM palette often has a colour twice), so
/// exports go through here: 8-bit colour type 3, one palette entry per colour number, entry 0 optionally
/// transparent (tRNS). Reading accepts 1/2/4/8-bit palette PNGs (not interlaced) and returns the stored numbers.
/// </summary>
public static class IndexedPng
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <param name="palette">ARGB colours (alpha ignored), one per colour number.</param>
    public static void Save(string path, int width, int height, byte[] indices, IReadOnlyList<uint> palette, bool firstTransparent)
    {
        using var fs = File.Create(path);
        fs.Write(Signature);
        var ihdr = new byte[13];
        Be32(ihdr, 0, width); Be32(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 3; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);
        var plte = new byte[palette.Count * 3];
        for (int i = 0; i < palette.Count; i++) { plte[i * 3] = (byte)(palette[i] >> 16); plte[i * 3 + 1] = (byte)(palette[i] >> 8); plte[i * 3 + 2] = (byte)palette[i]; }
        Chunk(fs, "PLTE", plte);
        if (firstTransparent) Chunk(fs, "tRNS", new byte[] { 0 });
        var raw = new byte[height * (width + 1)];
        for (int y = 0; y < height; y++) Array.Copy(indices, y * width, raw, y * (width + 1) + 1, width);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    /// <summary>The colour numbers and palette of a palette PNG, or null if the file isn't one (then use a normal decoder).</summary>
    public static (int Width, int Height, byte[] Indices, uint[] Palette)? TryLoad(string path)
    {
        byte[] f;
        try { f = File.ReadAllBytes(path); } catch { return null; }
        if (f.Length < 8 || !f.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int w = 0, h = 0, depth = 0, type = -1, interlace = 0;
        var palette = Array.Empty<uint>();
        using var idat = new MemoryStream();
        for (int p = 8; p + 8 <= f.Length;)
        {
            int len = f[p] << 24 | f[p + 1] << 16 | f[p + 2] << 8 | f[p + 3];
            string name = System.Text.Encoding.ASCII.GetString(f, p + 4, 4);
            int d = p + 8;
            if (len < 0 || d + len > f.Length) return null;
            switch (name)
            {
                case "IHDR":
                    w = f[d] << 24 | f[d + 1] << 16 | f[d + 2] << 8 | f[d + 3];
                    h = f[d + 4] << 24 | f[d + 5] << 16 | f[d + 6] << 8 | f[d + 7];
                    depth = f[d + 8]; type = f[d + 9]; interlace = f[d + 12];
                    break;
                case "PLTE":
                    palette = new uint[len / 3];
                    for (int i = 0; i < palette.Length; i++) palette[i] = 0xFF000000u | (uint)f[d + i * 3] << 16 | (uint)f[d + i * 3 + 1] << 8 | f[d + i * 3 + 2];
                    break;
                case "IDAT": idat.Write(f, d, len); break;
            }
            if (name == "IEND") break;
            p = d + len + 4;
        }
        if (type != 3 || interlace != 0 || depth is not (1 or 2 or 4 or 8) || w <= 0 || h <= 0) return null;
        byte[] raw;
        using (var z = new ZLibStream(new MemoryStream(idat.ToArray()), CompressionMode.Decompress))
        using (var outp = new MemoryStream()) { z.CopyTo(outp); raw = outp.ToArray(); }
        int stride = (w * depth + 7) / 8;
        if (raw.Length < h * (stride + 1)) return null;
        var rows = new byte[h * stride];
        var prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * (stride + 1)];
            var cur = new byte[stride];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            int bpp = 1;   // bytes per pixel for filtering (palette images: 1)
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = filter switch
                {
                    1 => (byte)(cur[i] + a),
                    2 => (byte)(cur[i] + b),
                    3 => (byte)(cur[i] + (a + b) / 2),
                    4 => (byte)(cur[i] + Paeth(a, b, c)),
                    _ => cur[i],
                };
            }
            Array.Copy(cur, 0, rows, y * stride, stride);
            prev = cur;
        }
        var idx = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int bit = x * depth;
                int v = rows[y * stride + bit / 8] >> (8 - depth - bit % 8) & ((1 << depth) - 1);
                idx[y * w + x] = (byte)v;
            }
        return (w, h, idx, palette);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Be32(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static void Chunk(Stream s, string name, byte[] data)
    {
        var len = new byte[4]; Be32(len, 0, data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(body, 0);
        data.CopyTo(body, 4);
        s.Write(body);
        var crc = new byte[4]; Be32(crc, 0, (int)Crc(body));
        s.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
