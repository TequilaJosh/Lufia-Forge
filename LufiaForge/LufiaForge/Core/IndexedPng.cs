using System.IO;
using System.IO.Compression;

namespace LufiaForge.Core;

// indexed (palette) png files written and read byte for byte. wpf's encoder merges palette entries with the same
// colour and renumbers the pixels, which breaks colour numbers (a rom palette often has a colour twice), so exports go
// through here: 8-bit colour type 3, one palette entry per colour number, entry 0 transparent if asked (tRNS).
// reading takes 1/2/4/8-bit palette pngs (not interlaced) and gives back the stored numbers
public static class IndexedPng
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // palette = ARGB colours (alpha ignored), one per colour number
    public static void Save(string path, int width, int height, byte[] indices, IReadOnlyList<uint> palette, bool firstTransparent)
    {
        using var file = File.Create(path);
        file.Write(Signature);
        var ihdr = new byte[13];
        Be32(ihdr, 0, width); Be32(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 3; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(file, "IHDR", ihdr);
        var plte = new byte[palette.Count * 3];
        for (int i = 0; i < palette.Count; i++) { plte[i * 3] = (byte)(palette[i] >> 16); plte[i * 3 + 1] = (byte)(palette[i] >> 8); plte[i * 3 + 2] = (byte)palette[i]; }
        Chunk(file, "PLTE", plte);
        if (firstTransparent) Chunk(file, "tRNS", new byte[] { 0 });
        var raw = new byte[height * (width + 1)];
        for (int y = 0; y < height; y++) Array.Copy(indices, y * width, raw, y * (width + 1) + 1, width);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    // the colour numbers and palette of a palette png, or null if the file isn't one (use a normal decoder then)
    public static (int Width, int Height, byte[] Indices, uint[] Palette)? TryLoad(string path)
    {
        byte[] fileBytes;
        try { fileBytes = File.ReadAllBytes(path); } catch { return null; }
        if (fileBytes.Length < 8 || !fileBytes.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int width = 0, height = 0, depth = 0, type = -1, interlace = 0;
        var palette = Array.Empty<uint>();
        using var idat = new MemoryStream();
        for (int chunkStart = 8; chunkStart + 8 <= fileBytes.Length;)
        {
            int chunkLength = fileBytes[chunkStart] << 24 | fileBytes[chunkStart + 1] << 16 | fileBytes[chunkStart + 2] << 8 | fileBytes[chunkStart + 3];
            string name = System.Text.Encoding.ASCII.GetString(fileBytes, chunkStart + 4, 4);
            int dataStart = chunkStart + 8;
            if (chunkLength < 0 || dataStart + chunkLength > fileBytes.Length) return null;
            switch (name)
            {
                case "IHDR":
                    width = fileBytes[dataStart] << 24 | fileBytes[dataStart + 1] << 16 | fileBytes[dataStart + 2] << 8 | fileBytes[dataStart + 3];
                    height = fileBytes[dataStart + 4] << 24 | fileBytes[dataStart + 5] << 16 | fileBytes[dataStart + 6] << 8 | fileBytes[dataStart + 7];
                    depth = fileBytes[dataStart + 8]; type = fileBytes[dataStart + 9]; interlace = fileBytes[dataStart + 12];
                    break;
                case "PLTE":
                    palette = new uint[chunkLength / 3];
                    for (int i = 0; i < palette.Length; i++) palette[i] = 0xFF000000u | (uint)fileBytes[dataStart + i * 3] << 16 | (uint)fileBytes[dataStart + i * 3 + 1] << 8 | fileBytes[dataStart + i * 3 + 2];
                    break;
                case "IDAT": idat.Write(fileBytes, dataStart, chunkLength); break;
            }
            if (name == "IEND") break;
            chunkStart = dataStart + chunkLength + 4;
        }
        if (type != 3 || interlace != 0 || depth is not (1 or 2 or 4 or 8) || width <= 0 || height <= 0) return null;
        byte[] raw;
        using (var zlib = new ZLibStream(new MemoryStream(idat.ToArray()), CompressionMode.Decompress))
        using (var output = new MemoryStream()) { zlib.CopyTo(output); raw = output.ToArray(); }
        int stride = (width * depth + 7) / 8;
        if (raw.Length < height * (stride + 1)) return null;
        var rows = new byte[height * stride];
        var previousRow = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            var row = new byte[stride];
            Array.Copy(raw, y * (stride + 1) + 1, row, 0, stride);
            int bpp = 1;   // bytes per pixel for filtering (1 for palette images)
            for (int i = 0; i < stride; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0, up = previousRow[i], upLeft = i >= bpp ? previousRow[i - bpp] : 0;
                row[i] = filter switch
                {
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + (left + up) / 2),
                    4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                    _ => row[i],
                };
            }
            Array.Copy(row, 0, rows, y * stride, stride);
            previousRow = row;
        }
        var indices = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int bit = x * depth;
                int colorNumber = rows[y * stride + bit / 8] >> (8 - depth - bit % 8) & ((1 << depth) - 1);
                indices[y * width + x] = (byte)colorNumber;
            }
        return (width, height, indices, palette);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Be32(byte[] buffer, int offset, int value) { buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16); buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value; }

    private static void Chunk(Stream stream, string name, byte[] data)
    {
        var length = new byte[4]; Be32(length, 0, data.Length);
        stream.Write(length);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(body, 0);
        data.CopyTo(body, 4);
        stream.Write(body);
        var crc = new byte[4]; Be32(crc, 0, (int)Crc(body));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint crc = (uint)n;
        for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
