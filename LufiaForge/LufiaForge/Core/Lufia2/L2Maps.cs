namespace LufiaForge.Core.Lufia2;

// lufia 2 maps (LEdit's L2Map / L2MapHeader / L2MapBlockSet, US addresses). map n (0-0xF1, plus F2 up if made with
// New map, 0 and 1 are the world and sea floor maps) is packed file 0x3F + n on the US rom (new maps:
// L2NewMaps.FileOf). LEdit's 0x40 + n is the German numbering, checked in the game: Tia's shop, map 04, is file 0x43.
// the file is a 6-byte head, then two layers of 16x16 blocks (u16 head, u8 width, u8 height, then width x height
// cells of block-low, flags). per-map lists at $CF:F72E... pick its block set, tile set and palettes, and the header
// table at $87:8000 names it
public static class L2Maps
{
    public const int MapFileBase = 0x3F;
    public const int HeaderTable = 0x38000, AnimData = 0x27F9FE, PaletteSetPointers = 0x27FAFA;
    public const int BlockSetFileBase = 0x130, TileSetFileBase = 0x14B;

    // how many maps the rom has: the game's 242 (00-F1) plus the ones made with New map (L2NewMaps)
    public static int Count(RomBuffer rom) => L2NewMaps.Count(rom);

    // file offsets of the map's bytes in the per-map graphics lists ($CF:F72E block set, $CF:F81E tile set,
    // $CF:F90E animation, $CF:FA08 palette set, $CF:FBA6 colour animation, New map moves them)
    public static int BlockSetAt(RomBuffer rom, int id) => L2NewMaps.Entry(rom, L2MapTable.BlockSet, id);
    public static int TileSetAt(RomBuffer rom, int id) => L2NewMaps.Entry(rom, L2MapTable.TileSet, id);
    public static int AnimIndexAt(RomBuffer rom, int id) => L2NewMaps.Entry(rom, L2MapTable.AnimIndex, id);
    public static int PaletteSetAt(RomBuffer rom, int id) => L2NewMaps.Entry(rom, L2MapTable.PaletteSet, id);
    public static int ColourAnimAt(RomBuffer rom, int id) => L2NewMaps.Entry(rom, L2MapTable.ColourAnim, id);

    public sealed class Layer
    {
        public int Width { get; init; }
        public int Height { get; init; }
        // block number (10 bits) per cell, row-major
        public int[] Blocks { get; init; } = Array.Empty<int>();
        // the cell's flag byte: bits 0-1 are the block's high bits, 04 wall above, 08 wall left, 10 room border,
        // 20 doorway, 40 upper level, 80 obstacle
        public byte[] Flags { get; init; } = Array.Empty<byte>();
    }

    public sealed class Map
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public Layer[] Layers { get; init; } = Array.Empty<Layer>();
        public int BlockSet { get; init; }
        public int TileSet { get; init; }
        // palette set to draw it with (-1 = the one the rom gives the map)
        public int PaletteSet { get; init; } = -1;
        // tile animation to draw it with (it picks where the palettes start, -1 = the map's own)
        public int AnimIndex { get; init; } = -1;
    }

    // a map's name, zero-terminated at the header table + u16. it can use the text dictionary (05 / 06 / 80-FF, like
    // dialogue does) and 0A xx yy = the first (yyxx >> 12) + 2 letters of another name at the header table +
    // (yyxx & 0FFF). like "Elcid" for the houses of Elcid, or "Cave" + " to Sundletan B" + "2"
    public static string Name(RomBuffer rom, int id) => NameAt(rom, L2MapNames.NameAt(rom, id), 0);

    private static string NameAt(RomBuffer rom, int at, int depth, bool keepSpaces = false)
    {
        var words = L2Text.Words(rom);
        var sb = new System.Text.StringBuilder();
        for (int offset = at; offset < at + 48 && depth < 4; )
        {
            byte nameByte = rom.ReadByte(offset++);
            if (nameByte == 0) break;
            if (nameByte == 0x0A)
            {
                int pieceWord = rom.ReadUInt16Le(offset); offset += 2;
                string part = NameAt(rom, L2MapNames.Base(rom) + (pieceWord & 0x0FFF), depth + 1, keepSpaces: true);
                int pieceLength = (pieceWord >> 12) + 2;
                sb.Append(part.Length > pieceLength ? part[..pieceLength] : part);
            }
            else if (nameByte == 5) sb.Append(words[rom.ReadByte(offset++)]);
            else if (nameByte == 6) sb.Append(words[0x100 + rom.ReadByte(offset++)]);
            else if (nameByte >= 0x80) sb.Append(words[0x200 + (nameByte & 0x7F)]);
            else sb.Append(L2Charset.ToChar(rom, nameByte) ?? '~');
        }
        return keepSpaces ? sb.ToString() : sb.ToString().Trim();
    }

    public static Map Read(RomBuffer rom, int id)
    {
        byte[] file = L2Rom.ReadFile(rom, L2NewMaps.FileOf(id));
        var layers = new List<Layer>();
        int offset = 6;
        for (int l = 0; l < 2 && offset + 4 <= file.Length; l++)
        {
            int width = file[offset + 2], height = file[offset + 3];
            offset += 4;
            var blocks = new int[width * height];
            var flags = new byte[width * height];
            for (int i = 0; i < width * height && offset + 1 < file.Length; i++, offset += 2)
            {
                flags[i] = file[offset + 1];
                blocks[i] = file[offset] | (file[offset + 1] & 3) << 8;
            }
            layers.Add(new Layer { Width = width, Height = height, Blocks = blocks, Flags = flags });
        }
        return new Map
        {
            Id = id, Name = Name(rom, id), Layers = layers.ToArray(),
            BlockSet = BlockSetFileBase + rom.ReadByte(BlockSetAt(rom, id)), TileSet = TileSetFileBase + rom.ReadByte(TileSetAt(rom, id)),
        };
    }

    // the word at the head of that layer in the map's file. low byte is the scroll mode (0 = scrolls with the map,
    // the game's mountains, towers and Shrine of Daos floors give their ground layer 02 or 03, a parallax background
    // far below, table $8E:BE6E), high byte is its parallax speeds
    public static int LayerMode(RomBuffer rom, int id, int layer)
    {
        byte[] file = L2Rom.ReadFile(rom, L2NewMaps.FileOf(id));
        int offset = 6;
        for (int l = 0; l < layer && offset + 4 <= file.Length; l++) offset += 4 + file[offset + 2] * file[offset + 3] * 2;
        return offset + 1 < file.Length ? file[offset] | file[offset + 1] << 8 : 0;
    }

    // the ground layer scroll mode most of the game's mountains and towers have (mode 02, speeds 11)
    public const int ParallaxMode = 0x1102;

    // writes the map's two layers back (block numbers and cell flags): its packed file is rebuilt and put back
    // (L2Rom.WriteFile). if the map's size changed its file gets laid out again (the 6-byte head and each layer's
    // u16 head kept, then the new width and height, then the cells)
    public static void Write(RomBuffer rom, Map map, int groundMode = -1)
    {
        byte[] file = L2Rom.ReadFile(rom, L2NewMaps.FileOf(map.Id));
        var output = new List<byte>(file.Take(6));
        int offset = 6;
        for (int l = 0; l < 2 && l < map.Layers.Length; l++)
        {
            var layer = map.Layers[l];
            int oldWidth = file[offset + 2], oldHeight = file[offset + 3];
            if (l == 0 && groundMode >= 0) { output.Add((byte)groundMode); output.Add((byte)(groundMode >> 8)); }
            else { output.Add(file[offset]); output.Add(file[offset + 1]); }
            output.Add((byte)layer.Width); output.Add((byte)layer.Height);
            offset += 4 + oldWidth * oldHeight * 2;
            for (int i = 0; i < layer.Width * layer.Height; i++)
            {
                output.Add((byte)layer.Blocks[i]);
                output.Add((byte)(layer.Flags[i] & 0xFC | layer.Blocks[i] >> 8 & 3));
            }
        }
        if (offset < file.Length) output.AddRange(file.Skip(offset));
        L2Rom.WriteFile(rom, L2NewMaps.FileOf(map.Id), output.ToArray());
    }

    // how many blocks the map's block set has
    public static int BlockCount(RomBuffer rom, int id)
    {
        byte[] blockSet = L2Rom.ReadFile(rom, BlockSetFileBase + rom.ReadByte(BlockSetAt(rom, id)));
        return (short)(blockSet[2] | blockSet[3] << 8);
    }

    // one block of the map's block set as 16x16 ARGB pixels (for a block picker)
    public static uint[] BlockPixels(RomBuffer rom, int id, int block)
    {
        var map = Read(rom, id);
        byte[] blockSet = L2Rom.ReadFile(rom, map.BlockSet), tiles = L2Rom.ReadFile(rom, map.TileSet);
        var palettes = Palettes(rom, id);
        var pixels = new uint[256];
        int blockRecord = 0x10 + block * 8;
        if (blockRecord + 8 > blockSet.Length) return pixels;
        for (int quarter = 0; quarter < 4; quarter++)
            DrawTile(pixels, 16, quarter >= 2 ? 8 : 0, (quarter & 1) * 8, blockSet[blockRecord + quarter * 2] | blockSet[blockRecord + quarter * 2 + 1] << 8, tiles, palettes);
        return pixels;
    }

    // every block of the map's block set the way the map is drawn (its block set, tile set and palette set),
    // 16x16 ARGB pixels each (0 = see-through)
    public static uint[][] AllBlocks(RomBuffer rom, Map map)
    {
        byte[] blockSet = L2Rom.ReadFile(rom, map.BlockSet), tiles = L2Rom.ReadFile(rom, map.TileSet);
        var palettes = Palettes(rom, map.Id, map.PaletteSet, map.AnimIndex);
        int count = blockSet.Length >= 4 ? Math.Max(0, (int)(short)(blockSet[2] | blockSet[3] << 8)) : 0;
        var all = new uint[count][];
        for (int block = 0; block < count; block++)
        {
            var pixels = new uint[256];
            int blockRecord = 0x10 + block * 8;
            if (blockRecord + 8 <= blockSet.Length)
                for (int quarter = 0; quarter < 4; quarter++)
                    DrawTile(pixels, 16, quarter >= 2 ? 8 : 0, (quarter & 1) * 8, blockSet[blockRecord + quarter * 2] | blockSet[blockRecord + quarter * 2 + 1] << 8, tiles, palettes);
            all[block] = pixels;
        }
        return all;
    }

    // the map's 6 background palettes (CGRAM rows 2-7), 16 ARGB colours each
    public static uint[][] Palettes(RomBuffer rom, int id, int paletteSet = -1, int animIndex = -1)
    {
        int set = paletteSet >= 0 ? paletteSet : rom.ReadByte(PaletteSetAt(rom, id));
        int anim = animIndex >= 0 ? animIndex : rom.ReadByte(AnimIndexAt(rom, id));
        int animByte = rom.ReadByte(AnimData + anim * 2 + 1);
        int paletteOffset = (animByte >> 4) != 0 ? 0x20 : 0x10;
        int pointer = rom.ReadUInt16Le(PaletteSetPointers + Math.Max(0, set - 1) * 2);
        int paletteAt = L2Rom.SnesToFile(0x9B0000 | pointer) + paletteOffset;
        var palettes = new uint[6][];
        for (int row = 0; row < 6; row++)
        {
            palettes[row] = new uint[16];
            for (int c = 0; c < 16; c++)
            {
                int colour = rom.ReadUInt16Le(paletteAt + row * 32 + c * 2);
                palettes[row][c] = 0xFF000000u | (uint)((colour & 31) * 255 / 31) << 16 | (uint)((colour >> 5 & 31) * 255 / 31) << 8 | (uint)((colour >> 10 & 31) * 255 / 31);
            }
        }
        return palettes;
    }

    // draws the map as ARGB pixels: the first layer (the ground) first, then the second (walls, furniture, roofs,
    // its flags hold the collision) over it. block 0 is left out. checked against the game's own tile maps in Tia's shop
    public static (int Width, int Height, uint[] Pixels) Render(RomBuffer rom, int id, bool layer0 = true, bool layer1 = true) =>
        Render(rom, Read(rom, id), layer0, layer1);

    // draws a map as given (an edited copy before it's written)
    public static (int Width, int Height, uint[] Pixels) Render(RomBuffer rom, Map map, bool layer0 = true, bool layer1 = true)
    {
        int id = map.Id;
        byte[] blockSet = L2Rom.ReadFile(rom, map.BlockSet);
        byte[] tiles = L2Rom.ReadFile(rom, map.TileSet);
        var palettes = Palettes(rom, id, map.PaletteSet, map.AnimIndex);
        int w = map.Layers.Max(l => l.Width) * 16, h = map.Layers.Max(l => l.Height) * 16;
        var pixels = new uint[w * h];
        int blockCount = blockSet.Length >= 4 ? (short)(blockSet[2] | blockSet[3] << 8) : 0;
        foreach (int layerIndex in new[] { 0, 1 })
        {
            if (layerIndex >= map.Layers.Length) continue;
            if (layerIndex == 0 && !layer0 || layerIndex == 1 && !layer1) continue;
            var layer = map.Layers[layerIndex];
            for (int cy = 0; cy < layer.Height; cy++)
                for (int cx = 0; cx < layer.Width; cx++)
                {
                    int block = layer.Blocks[cy * layer.Width + cx];
                    if (block == 0 || block >= blockCount) continue;
                    int blockRecord = 0x10 + block * 8;
                    for (int quarter = 0; quarter < 4; quarter++)
                    {
                        int word = blockSet[blockRecord + quarter * 2] | blockSet[blockRecord + quarter * 2 + 1] << 8;
                        int qx = quarter >= 2 ? 8 : 0, qy = (quarter & 1) * 8;   // TL, BL, TR, BR
                        DrawTile(pixels, w, cx * 16 + qx, cy * 16 + qy, word, tiles, palettes);
                    }
                }
        }
        return (w, h, pixels);
    }

    private static void DrawTile(uint[] pixels, int w, int x0, int y0, int word, byte[] tiles, uint[][] palettes)
    {
        int tile = word & 0x3FF, palette = word >> 10 & 7;
        bool flipX = (word & 0x4000) != 0, flipY = (word & 0x8000) != 0;
        int row = palette >= 2 ? palette - 2 : 5;
        int tileAt = tile * 32;
        if (tileAt + 32 > tiles.Length) return;
        for (int r = 0; r < 8; r++)
        {
            int sourceRow = flipY ? 7 - r : r;
            byte plane0 = tiles[tileAt + sourceRow * 2], plane1 = tiles[tileAt + sourceRow * 2 + 1], plane2 = tiles[tileAt + 0x10 + sourceRow * 2], plane3 = tiles[tileAt + 0x11 + sourceRow * 2];
            for (int c = 0; c < 8; c++)
            {
                int bit = flipX ? c : 7 - c;
                int colourIndex = (plane0 >> bit & 1) | (plane1 >> bit & 1) << 1 | (plane2 >> bit & 1) << 2 | (plane3 >> bit & 1) << 3;
                if (colourIndex == 0) continue;
                int x = x0 + c, y = y0 + r;
                if (x < w && y * w + x < pixels.Length) pixels[y * w + x] = palettes[row][colourIndex];
            }
        }
    }
}
