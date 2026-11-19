namespace LufiaForge.Core;

// lufia 1's resource compression (map data, tilesets, battle graphics, music).
// ported from the unpacker at $00:A47D, checked byte for byte against wram after a map load.
//
// resources are listed in a table of 3-byte snes pointers (index = resource id): the game's at file 0x60000, or a
// bigger copy in the added space (ResourceIds).
// stream format:
//   u16 unpacked length, then groups of one control byte + up to 8 flagged items
//   - a byte below $80 is a literal and doesn't use a control bit
//   - a byte $80+ uses the next control bit (MSB first): 0 = literal,
//     1 = back-reference starting at that byte (b1):
//       b1 b2 with (b2 & $0F) != 0 : copy (b2 & $0F) + 2 bytes, distance from the 12-bit field  (2 KB window)
//       b1 b2 b3 with (b2 & $0F) == 0: copy (b3 & $3F) + 3 bytes, distance from 14 bits      (8 KB window)
public static class LufiaCompression
{
    public const int ResourceTable = 0x60000;

    // file offset of resource id's compressed stream
    public static int ResourceOffset(RomBuffer rom, int id)
    {
        int entryOffset = ResourceIds.Entry(rom, id);
        int address = rom.ReadByte(entryOffset) | (rom.ReadByte(entryOffset + 1) << 8);
        int bank = rom.ReadByte(entryOffset + 2) & 0x7F;
        return bank * 0x8000 + (address & 0x7FFF);
    }

    // unpacks the resource with this id
    public static byte[] DecompressResource(RomBuffer rom, int id, out int compressedSize) =>
        Decompress(rom, ResourceOffset(rom, id), out compressedSize);

    // unpacks a stream starting at a file offset
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

                int copyFrom = outPos + distance;
                for (int i = 0; i < count && outPos < length; i++, copyFrom++)
                    output[outPos++] = copyFrom >= 0 ? output[copyFrom] : (byte)0;
            }
        }

        compressedSize = pos - offset;
        return output;
    }

    // compression

    private const int ShortMaxDistance = 2048, ShortMinLen = 3, ShortMaxLen = 17;
    private const int LongMaxDistance  = 8192, LongMinLen  = 3, LongMaxLen  = 66;
    private const int MaxChain = 512;

    // packs data into a stream the game's unpacker reads back exactly.
    // optimal parsing (shortest path over the literal / short copy / long copy choices), so the output usually comes
    // out as small as the original game data or smaller
    public static byte[] Compress(byte[] data)
    {
        int size = data.Length;
        if (size == 0 || size > 0xFFFF) throw new ArgumentException("Data must be 1..65535 bytes.");

        // candidate matches from hash chains on 3-byte prefixes
        var head = new int[1 << 16];
        Array.Fill(head, -1);
        var prev = new int[size];
        int Hash(int i) => ((data[i] << 8) ^ (data[i + 1] << 4) ^ data[i + 2]) & 0xFFFF;

        // the best (longest) match at each position, in each distance window
        var shortLen = new int[size]; var shortDist = new int[size];
        var longLen  = new int[size]; var longDist  = new int[size];

        for (int i = 0; i < size; i++)
        {
            if (i + 2 < size)
            {
                int chain = 0;
                for (int j = head[Hash(i)]; j >= 0 && chain < MaxChain; j = prev[j], chain++)
                {
                    int dist = i - j;
                    if (dist > LongMaxDistance) break;
                    int max = Math.Min(LongMaxLen, size - i);
                    int len = 0;
                    while (len < max && data[j + len] == data[i + len]) len++;
                    if (len < 3) continue;
                    if (len > longLen[i]) { longLen[i] = len; longDist[i] = dist; }
                    if (dist <= ShortMaxDistance)
                    {
                        int shortCopyLen = Math.Min(len, ShortMaxLen);
                        if (shortCopyLen > shortLen[i]) { shortLen[i] = shortCopyLen; shortDist[i] = dist; }
                    }
                    if (len == max) break;
                }
                int hash = Hash(i);
                prev[i] = head[hash];
                head[hash] = i;
            }
        }

        // shortest path (cost in bits, flagged items cost one control bit on top)
        var cost = new int[size + 1];
        var choiceLen = new int[size + 1];      // 1 = literal, otherwise copy length
        var choiceLong = new bool[size + 1];
        Array.Fill(cost, int.MaxValue);
        cost[size] = 0;
        for (int i = size - 1; i >= 0; i--)
        {
            int best = (data[i] < 0x80 ? 8 : 9) + cost[i + 1];
            int bestLen = 1; bool bestLong = false;
            for (int len = ShortMinLen; len <= shortLen[i]; len++)
            {
                int copyCost = 17 + cost[i + len];
                if (copyCost < best) { best = copyCost; bestLen = len; bestLong = false; }
            }
            for (int len = LongMinLen; len <= longLen[i]; len++)
            {
                int copyCost = 25 + cost[i + len];
                if (copyCost < best) { best = copyCost; bestLen = len; bestLong = true; }
            }
            cost[i] = best; choiceLen[i] = bestLen; choiceLong[i] = bestLong;
        }

        // write it out: u16 length, then the items, with a control byte in front of each group of 8 flagged items
        var output = new List<byte>(size) { (byte)size, (byte)(size >> 8) };
        int ctrlPos = output.Count; output.Add(0);
        int bitsUsed = 0;
        int pos = 0;
        while (pos < size)
        {
            int len = choiceLen[pos];
            bool flagged;
            if (len == 1)
            {
                byte b = data[pos];
                output.Add(b);
                flagged = b >= 0x80;          // 0 bit = literal
                pos++;
            }
            else
            {
                output[ctrlPos] |= (byte)(0x80 >> bitsUsed);
                if (!choiceLong[pos])
                {
                    int field = 0x1000 - shortDist[pos];                   // 0x800..0xFFF
                    output.Add((byte)(field >> 4));
                    output.Add((byte)(((field & 0x0F) << 4) | (len - 2)));    // low nibble 1..15
                }
                else
                {
                    int backDistance = 0x10000 - longDist[pos];            // 0xE000..0xFFFF
                    int field = (backDistance >> 2) & 0xFFF;
                    output.Add((byte)(field >> 4));
                    output.Add((byte)((field & 0x0F) << 4));               // low nibble 0 = long form
                    output.Add((byte)(((backDistance & 3) << 6) | (len - 3)));
                }
                flagged = true;
                pos += len;
            }
            if (flagged && ++bitsUsed == 8 && pos < size)
            {
                ctrlPos = output.Count; output.Add(0);
                bitsUsed = 0;
            }
        }
        return output.ToArray();
    }

    // unpacks a compressed stream that's in memory (used to check Compress)
    public static byte[] Decompress(byte[] stream)
    {
        var buffer = new RomBuffer(stream.Concat(new byte[8]).ToArray(), "");
        return Decompress(buffer, 0, out _);
    }
}
