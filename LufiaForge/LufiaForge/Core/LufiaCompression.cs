namespace LufiaForge.Core;

/// <summary>
/// Lufia 1's resource compression (map data, tilesets, battle graphics, music).
/// Ported from the unpacker at $00:A47D; verified byte-for-byte against WRAM after a map load.
///
/// Resources are listed in a table of 3-byte SNES pointers at file 0x60000 (index = resource id).
/// Stream format:
///   u16 unpacked length, then groups of: one control byte + up to 8 flagged items.
///   - A byte below $80 is a literal and does not use a control bit.
///   - A byte $80+ uses the next control bit (MSB first): 0 = literal,
///     1 = back-reference starting at that byte (b1):
///       b1 b2 with (b2 &amp; $0F) != 0 : copy (b2 &amp; $0F) + 2 bytes, distance from the 12-bit field  (2 KB window)
///       b1 b2 b3 with (b2 &amp; $0F) == 0: copy (b3 &amp; $3F) + 3 bytes, distance from 14 bits      (8 KB window)
/// </summary>
public static class LufiaCompression
{
    public const int ResourceTable = 0x60000;

    /// <summary>File offset of resource <paramref name="id"/>'s compressed stream.</summary>
    public static int ResourceOffset(RomBuffer rom, int id)
    {
        int p = ResourceTable + id * 3;
        int addr = rom.ReadByte(p) | (rom.ReadByte(p + 1) << 8);
        int bank = rom.ReadByte(p + 2) & 0x7F;
        return bank * 0x8000 + (addr & 0x7FFF);
    }

    /// <summary>Unpack the resource with the given id.</summary>
    public static byte[] DecompressResource(RomBuffer rom, int id, out int compressedSize) =>
        Decompress(rom, ResourceOffset(rom, id), out compressedSize);

    /// <summary>Unpack a stream starting at a file offset.</summary>
    public static byte[] Decompress(RomBuffer rom, int offset, out int compressedSize)
    {
        int pos = offset;
        int length = rom.ReadByte(pos) | (rom.ReadByte(pos + 1) << 8);
        pos += 2;
        var output = new byte[length];
        int outPos = 0;

        while (outPos < length)
        {
            int control = rom.ReadByte(pos++);
            int bits = 8;
            while (bits > 0 && outPos < length)
            {
                byte b = rom.ReadByte(pos);
                if (b < 0x80)
                {
                    output[outPos++] = b;
                    pos++;
                    continue;
                }

                bool isCopy = (control & 0x80) != 0;
                control = (control << 1) & 0xFF;
                bits--;

                if (!isCopy)
                {
                    output[outPos++] = b;
                    pos++;
                    continue;
                }

                byte b2 = rom.ReadByte(pos + 1);
                int count, distance;
                int field = ((b << 8) | b2) >> 4;           // 12 bits, top bit always set
                if ((b2 & 0x0F) != 0)
                {
                    count    = (b2 & 0x0F) + 2;
                    distance = ((field | 0xF000) & 0xFFFF) - 0x10000;
                    pos += 2;
                }
                else
                {
                    byte b3  = rom.ReadByte(pos + 2);
                    count    = (b3 & 0x3F) + 3;
                    distance = (((((field | 0xF000) << 2) & 0xFFFF) | (b3 >> 6)) - 0x10000);
                    pos += 3;
                }

                int src = outPos + distance;
                for (int i = 0; i < count && outPos < length; i++, src++)
                    output[outPos++] = src >= 0 ? output[src] : (byte)0;
            }
        }

        compressedSize = pos - offset;
        return output;
    }

    // ── Compression ────────────────────────────────────────────────────────

    private const int ShortMaxDistance = 2048, ShortMinLen = 3, ShortMaxLen = 17;
    private const int LongMaxDistance  = 8192, LongMinLen  = 3, LongMaxLen  = 66;
    private const int MaxChain = 512;

    /// <summary>
    /// Compress <paramref name="data"/> into a stream the game's unpacker reads back exactly.
    /// Uses optimal parsing (shortest path over literal / short copy / long copy choices), so output is
    /// usually as small as or smaller than the original game data.
    /// </summary>
    public static byte[] Compress(byte[] data)
    {
        int n = data.Length;
        if (n == 0 || n > 0xFFFF) throw new ArgumentException("Data must be 1..65535 bytes.");

        // Candidate matches via hash chains on 3-byte prefixes.
        var head = new int[1 << 16];
        Array.Fill(head, -1);
        var prev = new int[n];
        int Hash(int i) => ((data[i] << 8) ^ (data[i + 1] << 4) ^ data[i + 2]) & 0xFFFF;

        // Best (longest) match per position, within each distance window.
        var shortLen = new int[n]; var shortDist = new int[n];
        var longLen  = new int[n]; var longDist  = new int[n];

        for (int i = 0; i < n; i++)
        {
            if (i + 2 < n)
            {
                int chain = 0;
                for (int j = head[Hash(i)]; j >= 0 && chain < MaxChain; j = prev[j], chain++)
                {
                    int dist = i - j;
                    if (dist > LongMaxDistance) break;
                    int max = Math.Min(LongMaxLen, n - i);
                    int len = 0;
                    while (len < max && data[j + len] == data[i + len]) len++;
                    if (len < 3) continue;
                    if (len > longLen[i]) { longLen[i] = len; longDist[i] = dist; }
                    if (dist <= ShortMaxDistance)
                    {
                        int sl = Math.Min(len, ShortMaxLen);
                        if (sl > shortLen[i]) { shortLen[i] = sl; shortDist[i] = dist; }
                    }
                    if (len == max) break;
                }
                int h = Hash(i);
                prev[i] = head[h];
                head[h] = i;
            }
        }

        // Shortest path (cost in bits; flagged items also cost one control bit).
        var cost = new int[n + 1];
        var choiceLen = new int[n + 1];      // 1 = literal, otherwise copy length
        var choiceLong = new bool[n + 1];
        Array.Fill(cost, int.MaxValue);
        cost[n] = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            int best = (data[i] < 0x80 ? 8 : 9) + cost[i + 1];
            int bestLen = 1; bool bestLong = false;
            for (int len = ShortMinLen; len <= shortLen[i]; len++)
            {
                int c = 17 + cost[i + len];
                if (c < best) { best = c; bestLen = len; bestLong = false; }
            }
            for (int len = LongMinLen; len <= longLen[i]; len++)
            {
                int c = 25 + cost[i + len];
                if (c < best) { best = c; bestLen = len; bestLong = true; }
            }
            cost[i] = best; choiceLen[i] = bestLen; choiceLong[i] = bestLong;
        }

        // Emit: u16 length, then items; a control byte precedes each group of 8 flagged items.
        var output = new List<byte>(n) { (byte)n, (byte)(n >> 8) };
        int ctrlPos = output.Count; output.Add(0);
        int bitsUsed = 0;
        int pos = 0;
        while (pos < n)
        {
            int len = choiceLen[pos];
            bool flagged;
            if (len == 1)
            {
                byte b = data[pos];
                output.Add(b);
                flagged = b >= 0x80;          // 0 bit: literal
                pos++;
            }
            else
            {
                output[ctrlPos] |= (byte)(0x80 >> bitsUsed);
                if (!choiceLong[pos])
                {
                    int v = 0x1000 - shortDist[pos];                       // 0x800..0xFFF
                    output.Add((byte)(v >> 4));
                    output.Add((byte)(((v & 0x0F) << 4) | (len - 2)));    // low nibble 1..15
                }
                else
                {
                    int x = 0x10000 - longDist[pos];                       // 0xE000..0xFFFF
                    int v = (x >> 2) & 0xFFF;
                    output.Add((byte)(v >> 4));
                    output.Add((byte)((v & 0x0F) << 4));                   // low nibble 0 = long form
                    output.Add((byte)(((x & 3) << 6) | (len - 3)));
                }
                flagged = true;
                pos += len;
            }
            if (flagged && ++bitsUsed == 8 && pos < n)
            {
                ctrlPos = output.Count; output.Add(0);
                bitsUsed = 0;
            }
        }
        return output.ToArray();
    }

    /// <summary>Unpack a compressed stream held in memory (used to verify <see cref="Compress"/>).</summary>
    public static byte[] Decompress(byte[] stream)
    {
        var tmp = new RomBuffer(stream.Concat(new byte[8]).ToArray(), "");
        return Decompress(tmp, 0, out _);
    }
}
