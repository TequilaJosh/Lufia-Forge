using LufiaForge.Modules.TileViewer;

namespace LufiaForge.Core.Maps;

/// <summary>
/// Graphics needed to draw a map: 4bpp tile graphics, 16x16 metatile definitions and the palette.
/// Metatile record (9 bytes): four SNES tilemap words (top-left, bottom-left, top-right, bottom-right)
/// followed by one attribute byte (probably collision). Tile numbers are relative to the tile resource,
/// which the game loads at the BG character base (VRAM $1000 words).
/// </summary>
public sealed class MapTileset
{
    public const int MetatileSize = 9;

    public int Tileset { get; }
    public int PaletteIndex { get; }
    public int TilesResource { get; }
    public int BlocksResource { get; }
    public byte[] Tiles { get; }
    public byte[] Blocks { get; }
    /// <summary>128 BGRA colors (palette rows 0-7).</summary>
    public uint[] Palette { get; }
    /// <summary>The same 128 colours as the console stores them (BGR555).</summary>
    public ushort[] Palette555 { get; } = new ushort[128];
    public int MetatileCount => Blocks.Length / MetatileSize;
    /// <summary>First composite metatile (see <see cref="LufiaMap.CompositeThreshold"/>), or -1.</summary>
    public int CompositeThreshold { get; }

    private readonly RomBuffer _rom;
    private readonly int _compositeTable;   // file offset of this tileset's (ground, overlay) pairs

    private readonly Dictionary<int, uint[]> _metatileCache = new();
    private readonly Dictionary<int, byte[]> _tileCache = new();

    public MapTileset(RomBuffer rom, int tileset, int paletteIndex, int compositeThreshold = -1)
    {
        _rom = rom;
        Tileset = tileset;
        PaletteIndex = paletteIndex;
        TilesResource  = 0xB1 + rom.ReadByte(LufiaMap.TilesSelectTable + tileset);
        BlocksResource = 0xB1 + rom.ReadByte(LufiaMap.BlocksSelectTable + tileset);
        Tiles  = LufiaCompression.DecompressResource(rom, TilesResource, out _);
        Blocks = LufiaCompression.DecompressResource(rom, BlocksResource, out _);

        Palette = new uint[128];
        for (int i = 0; i < 32; i++)
            Palette555[i] = rom.ReadUInt16Le(LufiaMap.SharedPaletteRows + i * 2);
        int rows = LufiaMap.MapPaletteTable + (paletteIndex + 1) * 0xC0;
        for (int i = 0; i < 96; i++)
            Palette555[32 + i] = rom.ReadUInt16Le(rows + i * 2);
        for (int i = 0; i < 128; i++) Palette[i] = SnesPalette.Bgr555ToArgb32(Palette555[i]);

        int ptr = LufiaMap.CompositeTable + tileset * 3;
        int snes = rom.ReadByte(ptr) | (rom.ReadByte(ptr + 1) << 8) | (rom.ReadByte(ptr + 2) << 16);
        _compositeTable = ((snes >> 16) & 0x7F) * 0x8000 + (snes & 0x7FFF);
        CompositeThreshold = compositeThreshold;
    }

    /// <summary>World map graphics: fixed tile resource and palette, block records from the map data.</summary>
    private MapTileset(RomBuffer rom, int tilesResource, byte[] blocks, int paletteIndex)
    {
        _rom = rom;
        Tileset = -1;
        PaletteIndex = paletteIndex;
        TilesResource = tilesResource;
        BlocksResource = -1;
        Tiles = LufiaCompression.DecompressResource(rom, tilesResource, out _);
        Blocks = blocks;
        Palette = new uint[128];
        for (int i = 0; i < 32; i++)
            Palette[i] = SnesPalette.Bgr555ToArgb32(rom.ReadUInt16Le(LufiaMap.SharedPaletteRows + i * 2));
        int rows = LufiaMap.MapPaletteTable + (paletteIndex + 1) * 0xC0;
        for (int i = 0; i < 96; i++)
            Palette[32 + i] = SnesPalette.Bgr555ToArgb32(rom.ReadUInt16Le(rows + i * 2));
        CompositeThreshold = -1;
    }

    /// <summary>The graphics for a map (town/dungeon tileset, or the world map's own blocks).</summary>
    /// <param name="paletteIndex">Palette to draw with instead of the map's own (the intro shows map 4F in palette 18).</param>
    public static MapTileset ForMap(RomBuffer rom, LufiaMap map, int? paletteIndex = null) =>
        map.IsWorld
            ? new MapTileset(rom, LufiaMap.WorldTilesResource, map.WorldBlocks, map.PaletteIndex)
            : new MapTileset(rom, map.Tileset, paletteIndex ?? map.PaletteIndex, map.CompositeThreshold);

    /// <summary>
    /// The ground and overlay metatiles the game shows for a composite metatile, or null if the
    /// metatile is drawn as-is.
    /// </summary>
    public (int Ground, int Overlay)? CompositeParts(int metatile)
    {
        metatile &= 0x3FF;
        if (CompositeThreshold < 0 || metatile < CompositeThreshold) return null;
        int e = _compositeTable + (metatile - CompositeThreshold) * 4;
        if (e + 4 > _rom.Length) return null;
        return (_rom.ReadUInt16Le(e) & 0x3FF, _rom.ReadUInt16Le(e + 2) & 0x3FF);
    }

    public byte Attribute(int metatile) =>
        metatile * MetatileSize + 8 < Blocks.Length ? Blocks[metatile * MetatileSize + 8] : (byte)0;

    private byte[] TilePixels(int tile)
    {
        if (!_tileCache.TryGetValue(tile, out var px))
        {
            px = SnesTileDecoder.DecodeTile(Tiles, tile * 32, BitDepth.Bpp4);
            _tileCache[tile] = px;
        }
        return px;
    }

    /// <summary>
    /// 16x16 BGRA pixels of a metatile as it appears in game; color 0 is drawn with the backdrop
    /// color. Composite metatiles are drawn as their ground block with the overlay block on top.
    /// </summary>
    public uint[] MetatilePixels(int metatile)
    {
        metatile &= 0x3FF;
        if (_metatileCache.TryGetValue(metatile, out var cached)) return cached;

        var px = new uint[256];
        Array.Fill(px, Palette[0]);
        if (CompositeParts(metatile) is var (ground, overlay))
        {
            DrawRecord(px, ground);
            DrawRecord(px, overlay);
        }
        else
        {
            DrawRecord(px, metatile);
        }
        _metatileCache[metatile] = px;
        return px;
    }

    private readonly Dictionary<int, byte[]> _indexCache = new();

    /// <summary>16x16 colour indices of a metatile (palette row * 16 + colour; 0 = transparent).</summary>
    public byte[] MetatileIndices(int metatile)
    {
        metatile &= 0x3FF;
        if (_indexCache.TryGetValue(metatile, out var cached)) return cached;
        var px = new byte[256];
        if (CompositeParts(metatile) is var (ground, overlay)) { IndexRecord(px, ground); IndexRecord(px, overlay); }
        else IndexRecord(px, metatile);
        _indexCache[metatile] = px;
        return px;
    }

    private void IndexRecord(byte[] px, int metatile)
    {
        int rec = metatile * MetatileSize;
        if (rec + 8 > Blocks.Length) return;
        for (int q = 0; q < 4; q++)
        {
            int entry = Blocks[rec + q * 2] | (Blocks[rec + q * 2 + 1] << 8);
            int qx = q >= 2 ? 8 : 0, qy = (q & 1) == 1 ? 8 : 0;
            var tile = TilePixels(entry & 0x3FF);
            int pal = (entry >> 10) & 7;
            bool hf = (entry & 0x4000) != 0, vf = (entry & 0x8000) != 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int c = tile[(vf ? 7 - y : y) * 8 + (hf ? 7 - x : x)];
                    if (c != 0) px[(qy + y) * 16 + qx + x] = (byte)(pal * 16 + c);
                }
        }
    }

    /// <summary>Draw a raw metatile record's non-transparent pixels over a 16x16 buffer.</summary>
    private void DrawRecord(uint[] px, int metatile)
    {
        int rec = metatile * MetatileSize;
        if (rec + 8 > Blocks.Length) return;
        for (int q = 0; q < 4; q++)
        {
            int entry = Blocks[rec + q * 2] | (Blocks[rec + q * 2 + 1] << 8);
            int qx = q >= 2 ? 8 : 0;          // order: TL, BL, TR, BR
            int qy = (q & 1) == 1 ? 8 : 0;
            var tile = TilePixels(entry & 0x3FF);
            int pal = (entry >> 10) & 7;
            bool hf = (entry & 0x4000) != 0, vf = (entry & 0x8000) != 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int c = tile[(vf ? 7 - y : y) * 8 + (hf ? 7 - x : x)];
                    if (c != 0) px[(qy + y) * 16 + qx + x] = Palette[pal * 16 + c];
                }
        }
    }

    /// <summary>Draw one metatile into a BGRA buffer of the given width.</summary>
    public void DrawMetatile(uint[] dest, int destWidth, int px, int py, int metatile)
    {
        var src = MetatilePixels(metatile);
        for (int y = 0; y < 16; y++)
            Array.Copy(src, y * 16, dest, (py + y) * destWidth + px, 16);
    }

    /// <summary>Render a whole map into a BGRA buffer (Width*16 x Height*16).</summary>
    public uint[] RenderMap(LufiaMap map)
    {
        int w = map.Width * 16;
        var buf = new uint[w * map.Height * 16];
        for (int my = 0; my < map.Height; my++)
            for (int mx = 0; mx < map.Width; mx++)
                DrawMetatile(buf, w, mx * 16, my * 16, map.GetTile(mx, my));
        return buf;
    }
}
