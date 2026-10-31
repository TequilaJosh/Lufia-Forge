namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II maps (LEdit's L2Map / L2MapHeader / L2MapBlockSet, US addresses). Map n (0-0xF1; 0 and 1 are the
/// world and sea-floor maps) is packed file 0x3F + n on the US ROM (LEdit's 0x40 + n is the German numbering; checked
/// in the game: Tia's shop, map 04, is file 0x43): a 6-byte head, then two layers of 16x16 blocks (u16 head,
/// u8 width, u8 height, then width x height cells of block-low, flags). Per-map lists at $CF:F72E... pick its block
/// set, tile set and palettes; the header table at $87:8000 names it.
/// </summary>
public static class L2Maps
{
    public const int Count = 0xF2, MapFileBase = 0x3F;
    public const int HeaderTable = 0x38000, BlockSetList = 0x27F72E, TileSetList = 0x27F81E, AnimIndexList = 0x27F90E,
                     AnimData = 0x27F9FE, PaletteSetList = 0x27FA08, PaletteSetPointers = 0x27FAFA, InfoPointers = 0x27FCBC;
    public const int BlockSetFileBase = 0x130, TileSetFileBase = 0x14B;

    public sealed class Layer
    {
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>Block number (10 bits) per cell, row-major.</summary>
        public int[] Blocks { get; init; } = Array.Empty<int>();
        /// <summary>The cell's flag byte (bits 0-1 are the block's high bits; 04 wall above, 08 wall left, 10 room
        /// border, 20 doorway, 40 upper level, 80 obstacle).</summary>
        public byte[] Flags { get; init; } = Array.Empty<byte>();
    }

    public sealed class Map
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public Layer[] Layers { get; init; } = Array.Empty<Layer>();
        public int BlockSet { get; init; }
        public int TileSet { get; init; }
        /// <summary>The palette set to draw it with (-1: the one the ROM gives the map).</summary>
        public int PaletteSet { get; init; } = -1;
    }

    /// <summary>A map's name: zero-terminated at the header table + u16. It can use the text dictionary (05 / 06 /
    /// 80-FF, as dialogue does) and 0A xx yy = the first (yyxx &gt;&gt; 12) + 2 letters of another name, at the header
    /// table + (yyxx &amp; 0FFF) (e.g. "Elcid" for the houses of Elcid, "Cave" + " to Sundletan B" + "2").</summary>
    public static string Name(RomBuffer rom, int id) => NameAt(rom, HeaderTable + rom.ReadUInt16Le(HeaderTable + 0x10 + id * 8 + 6), 0);

    private static string NameAt(RomBuffer rom, int at, int depth, bool keepSpaces = false)
    {
        var words = L2Text.Words(rom);
        var sb = new System.Text.StringBuilder();
        for (int p = at; p < at + 48 && depth < 4; )
        {
            byte b = rom.ReadByte(p++);
            if (b == 0) break;
            if (b == 0x0A)
            {
                int w = rom.ReadUInt16Le(p); p += 2;
                string part = NameAt(rom, HeaderTable + (w & 0x0FFF), depth + 1, keepSpaces: true);
                int n = (w >> 12) + 2;
                sb.Append(part.Length > n ? part[..n] : part);
            }
            else if (b == 5) sb.Append(words[rom.ReadByte(p++)]);
            else if (b == 6) sb.Append(words[0x100 + rom.ReadByte(p++)]);
            else if (b >= 0x80) sb.Append(words[0x200 + (b & 0x7F)]);
            else sb.Append(L2Charset.ToChar(rom, b) ?? '~');
        }
        return keepSpaces ? sb.ToString() : sb.ToString().Trim();
    }

    public static Map Read(RomBuffer rom, int id)
    {
        byte[] f = L2Rom.ReadFile(rom, MapFileBase + id);
        var layers = new List<Layer>();
        int p = 6;
        for (int l = 0; l < 2 && p + 4 <= f.Length; l++)
        {
            int w = f[p + 2], h = f[p + 3];
            p += 4;
            var blocks = new int[w * h];
            var flags = new byte[w * h];
            for (int i = 0; i < w * h && p + 1 < f.Length; i++, p += 2)
            {
                flags[i] = f[p + 1];
                blocks[i] = f[p] | (f[p + 1] & 3) << 8;
            }
            layers.Add(new Layer { Width = w, Height = h, Blocks = blocks, Flags = flags });
        }
        return new Map
        {
            Id = id, Name = Name(rom, id), Layers = layers.ToArray(),
            BlockSet = BlockSetFileBase + rom.ReadByte(BlockSetList + id), TileSet = TileSetFileBase + rom.ReadByte(TileSetList + id),
        };
    }

    /// <summary>Writes the map's two layers back (block numbers and cell flags): its packed file is rebuilt and put back
    /// (<see cref="L2Rom.WriteFile"/>). A map whose size changed gets its file laid out again (the 6-byte head and each
    /// layer's u16 head kept, the new width and height, the cells).</summary>
    public static void Write(RomBuffer rom, Map map)
    {
        byte[] f = L2Rom.ReadFile(rom, MapFileBase + map.Id);
        var outp = new List<byte>(f.Take(6));
        int p = 6;
        for (int l = 0; l < 2 && l < map.Layers.Length; l++)
        {
            var L = map.Layers[l];
            int ow = f[p + 2], oh = f[p + 3];
            outp.Add(f[p]); outp.Add(f[p + 1]); outp.Add((byte)L.Width); outp.Add((byte)L.Height);
            p += 4 + ow * oh * 2;
            for (int i = 0; i < L.Width * L.Height; i++)
            {
                outp.Add((byte)L.Blocks[i]);
                outp.Add((byte)(L.Flags[i] & 0xFC | L.Blocks[i] >> 8 & 3));
            }
        }
        if (p < f.Length) outp.AddRange(f.Skip(p));
        L2Rom.WriteFile(rom, MapFileBase + map.Id, outp.ToArray());
    }

    /// <summary>How many blocks the map's block set has.</summary>
    public static int BlockCount(RomBuffer rom, int id)
    {
        byte[] bs = L2Rom.ReadFile(rom, BlockSetFileBase + rom.ReadByte(BlockSetList + id));
        return (short)(bs[2] | bs[3] << 8);
    }

    /// <summary>Block <paramref name="block"/> of the map's block set as 16x16 ARGB pixels (for a block picker).</summary>
    public static uint[] BlockPixels(RomBuffer rom, int id, int block)
    {
        var map = Read(rom, id);
        byte[] blockSet = L2Rom.ReadFile(rom, map.BlockSet), tiles = L2Rom.ReadFile(rom, map.TileSet);
        var pals = Palettes(rom, id);
        var px = new uint[256];
        int rec = 0x10 + block * 8;
        if (rec + 8 > blockSet.Length) return px;
        for (int q = 0; q < 4; q++)
            DrawTile(px, 16, q >= 2 ? 8 : 0, (q & 1) * 8, blockSet[rec + q * 2] | blockSet[rec + q * 2 + 1] << 8, tiles, pals);
        return px;
    }

    /// <summary>Every block of the map's block set (as the map is drawn: its block set, tile set and palette set), 16x16
    /// ARGB pixels each (0 = see-through).</summary>
    public static uint[][] AllBlocks(RomBuffer rom, Map map)
    {
        byte[] blockSet = L2Rom.ReadFile(rom, map.BlockSet), tiles = L2Rom.ReadFile(rom, map.TileSet);
        var pals = Palettes(rom, map.Id, map.PaletteSet);
        int count = blockSet.Length >= 4 ? Math.Max(0, (int)(short)(blockSet[2] | blockSet[3] << 8)) : 0;
        var all = new uint[count][];
        for (int b = 0; b < count; b++)
        {
            var px = new uint[256];
            int rec = 0x10 + b * 8;
            if (rec + 8 <= blockSet.Length)
                for (int q = 0; q < 4; q++)
                    DrawTile(px, 16, q >= 2 ? 8 : 0, (q & 1) * 8, blockSet[rec + q * 2] | blockSet[rec + q * 2 + 1] << 8, tiles, pals);
            all[b] = px;
        }
        return all;
    }

    /// <summary>The map's 6 background palettes (CGRAM rows 2-7), 16 ARGB colours each.</summary>
    public static uint[][] Palettes(RomBuffer rom, int id, int paletteSet = -1)
    {
        int set = paletteSet >= 0 ? paletteSet : rom.ReadByte(PaletteSetList + id);
        int anim = rom.ReadByte(AnimIndexList + id);
        int v29 = rom.ReadByte(AnimData + anim * 2 + 1);
        int palOff = (v29 >> 4) != 0 ? 0x20 : 0x10;
        int ptr = rom.ReadUInt16Le(PaletteSetPointers + Math.Max(0, set - 1) * 2);
        int at = L2Rom.SnesToFile(0x9B0000 | ptr) + palOff;
        var pals = new uint[6][];
        for (int r = 0; r < 6; r++)
        {
            pals[r] = new uint[16];
            for (int c = 0; c < 16; c++)
            {
                int v = rom.ReadUInt16Le(at + r * 32 + c * 2);
                pals[r][c] = 0xFF000000u | (uint)((v & 31) * 255 / 31) << 16 | (uint)((v >> 5 & 31) * 255 / 31) << 8 | (uint)((v >> 10 & 31) * 255 / 31);
            }
        }
        return pals;
    }

    /// <summary>Draws the map as ARGB pixels: the first layer (the ground) first, then the second (walls, furniture,
    /// roofs; its flags hold the collision) over it; block 0 is left out. (Checked against the game's own tile maps
    /// in Tia's shop.)</summary>
    public static (int Width, int Height, uint[] Pixels) Render(RomBuffer rom, int id, bool layer0 = true, bool layer1 = true) =>
        Render(rom, Read(rom, id), layer0, layer1);

    /// <summary>Draws a map as given (an edited copy before it is written).</summary>
    public static (int Width, int Height, uint[] Pixels) Render(RomBuffer rom, Map map, bool layer0 = true, bool layer1 = true)
    {
        int id = map.Id;
        byte[] blockSet = L2Rom.ReadFile(rom, map.BlockSet);
        byte[] tiles = L2Rom.ReadFile(rom, map.TileSet);
        var pals = Palettes(rom, id, map.PaletteSet);
        int w = map.Layers.Max(l => l.Width) * 16, h = map.Layers.Max(l => l.Height) * 16;
        var px = new uint[w * h];
        int blockCount = blockSet.Length >= 4 ? (short)(blockSet[2] | blockSet[3] << 8) : 0;
        foreach (int li in new[] { 0, 1 })
        {
            if (li >= map.Layers.Length) continue;
            if (li == 0 && !layer0 || li == 1 && !layer1) continue;
            var L = map.Layers[li];
            for (int cy = 0; cy < L.Height; cy++)
                for (int cx = 0; cx < L.Width; cx++)
                {
                    int b = L.Blocks[cy * L.Width + cx];
                    if (b == 0 || b >= blockCount) continue;
                    int rec = 0x10 + b * 8;
                    for (int q = 0; q < 4; q++)
                    {
                        int word = blockSet[rec + q * 2] | blockSet[rec + q * 2 + 1] << 8;
                        int qx = q >= 2 ? 8 : 0, qy = (q & 1) * 8;   // TL, BL, TR, BR
                        DrawTile(px, w, cx * 16 + qx, cy * 16 + qy, word, tiles, pals);
                    }
                }
        }
        return (w, h, px);
    }

    private static void DrawTile(uint[] px, int w, int x0, int y0, int word, byte[] tiles, uint[][] pals)
    {
        int tile = word & 0x3FF, pal = word >> 10 & 7;
        bool hf = (word & 0x4000) != 0, vf = (word & 0x8000) != 0;
        int row = pal >= 2 ? pal - 2 : 5;
        int at = tile * 32;
        if (at + 32 > tiles.Length) return;
        for (int r = 0; r < 8; r++)
        {
            int sr = vf ? 7 - r : r;
            byte p0 = tiles[at + sr * 2], p1 = tiles[at + sr * 2 + 1], p2 = tiles[at + 0x10 + sr * 2], p3 = tiles[at + 0x11 + sr * 2];
            for (int c = 0; c < 8; c++)
            {
                int bit = hf ? c : 7 - c;
                int ci = (p0 >> bit & 1) | (p1 >> bit & 1) << 1 | (p2 >> bit & 1) << 2 | (p3 >> bit & 1) << 3;
                if (ci == 0) continue;
                int x = x0 + c, y = y0 + r;
                if (x < w && y * w + x < px.Length) px[y * w + x] = pals[row][ci];
            }
        }
    }
}
