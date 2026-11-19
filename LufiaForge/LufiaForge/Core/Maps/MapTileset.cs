using LufiaForge.Modules.TileViewer;

namespace LufiaForge.Core.Maps;

// the graphics we need to draw a map: 4bpp tile graphics, 16x16 metatile definitions and the palette.
// metatile record (9 bytes): four snes tilemap words (top-left, bottom-left, top-right, bottom-right) then one
// attribute byte (probably collision). tile numbers are relative to the tile resource, which the game loads at the
// BG character base (VRAM $1000 words)
public sealed class MapTileset
{
    public const int MetatileSize = 9;

    public int Tileset { get; }
    public int PaletteIndex { get; }
    public int TilesResource { get; }
    public int BlocksResource { get; }
    public byte[] Tiles { get; }
    public byte[] Blocks { get; }
    // 128 BGRA colors (palette rows 0-7)
    public uint[] Palette { get; }
    // the same 128 colours the way the console stores them (BGR555)
    public ushort[] Palette555 { get; } = new ushort[128];
    public int MetatileCount => Blocks.Length / MetatileSize;
    // first composite metatile (see LufiaMap.CompositeThreshold), or -1
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
        TilesResource  = Tilesets.TilesResource(rom, tileset);
        BlocksResource = Tilesets.BlocksResource(rom, tileset);
        Tiles  = LufiaCompression.DecompressResource(rom, TilesResource, out _);
        Blocks = LufiaCompression.DecompressResource(rom, BlocksResource, out _);

        Palette = new uint[128];
        for (int i = 0; i < 32; i++)
            Palette555[i] = rom.ReadUInt16Le(LufiaMap.SharedPaletteRows + i * 2);
        int mapRows = LufiaMap.MapPaletteTable + (paletteIndex + 1) * 0xC0;
        for (int i = 0; i < 96; i++)
            Palette555[32 + i] = rom.ReadUInt16Le(mapRows + i * 2);
        for (int i = 0; i < 128; i++) Palette[i] = SnesPalette.Bgr555ToArgb32(Palette555[i]);

        _compositeTable = Tilesets.CompositePairs(rom, tileset);
        CompositeThreshold = compositeThreshold;
    }

    // world map graphics: fixed tile resource and palette, block records from the map data
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
        int mapRows = LufiaMap.MapPaletteTable + (paletteIndex + 1) * 0xC0;
        for (int i = 0; i < 96; i++)
            Palette[32 + i] = SnesPalette.Bgr555ToArgb32(rom.ReadUInt16Le(mapRows + i * 2));
        CompositeThreshold = -1;
    }

    // the graphics for a map (town/dungeon tileset, or the world map's own blocks).
    // paletteIndex = palette to draw with instead of the map's own (the intro shows map 4F in palette 18)
    public static MapTileset ForMap(RomBuffer rom, LufiaMap map, int? paletteIndex = null) =>
        map.IsWorld
            ? new MapTileset(rom, LufiaMap.WorldTilesResource, map.WorldBlocks, map.PaletteIndex)
            : new MapTileset(rom, map.Tileset, paletteIndex ?? map.PaletteIndex, map.CompositeThreshold);

    // the ground and overlay metatiles the game shows for a composite metatile, or null if the metatile
    // is drawn as it is
    public (int Ground, int Overlay)? CompositeParts(int metatile)
    {
        metatile &= 0x3FF;
        if (CompositeThreshold < 0 || metatile < CompositeThreshold) return null;
        int pairEntry = _compositeTable + (metatile - CompositeThreshold) * 4;
        if (pairEntry + 4 > _rom.Length) return null;
        return (_rom.ReadUInt16Le(pairEntry) & 0x3FF, _rom.ReadUInt16Le(pairEntry + 2) & 0x3FF);
    }

    public byte Attribute(int metatile) =>
        metatile * MetatileSize + 8 < Blocks.Length ? Blocks[metatile * MetatileSize + 8] : (byte)0;

    private byte[] TilePixels(int tile)
    {
        if (!_tileCache.TryGetValue(tile, out var pixels))
        {
            pixels = SnesTileDecoder.DecodeTile(Tiles, tile * 32, BitDepth.Bpp4);
            _tileCache[tile] = pixels;
        }
        return pixels;
    }

    // 16x16 BGRA pixels of a metatile the way it looks in game, color 0 is drawn with the backdrop color.
    // composite metatiles are drawn as their ground block with the overlay block on top
    public uint[] MetatilePixels(int metatile)
    {
        metatile &= 0x3FF;
        if (_metatileCache.TryGetValue(metatile, out var cached)) return cached;

        var pixels = new uint[256];
        Array.Fill(pixels, Palette[0]);
        if (CompositeParts(metatile) is var (ground, overlay))
        {
            DrawRecord(pixels, ground);
            DrawRecord(pixels, overlay);
        }
        else
        {
            DrawRecord(pixels, metatile);
        }
        _metatileCache[metatile] = pixels;
        return pixels;
    }

    private readonly Dictionary<int, byte[]> _indexCache = new();

    // 16x16 colour indices of a metatile (palette row * 16 + colour, 0 = transparent)
    public byte[] MetatileIndices(int metatile)
    {
        metatile &= 0x3FF;
        if (_indexCache.TryGetValue(metatile, out var cached)) return cached;
        var indices = new byte[256];
        if (CompositeParts(metatile) is var (ground, overlay)) { IndexRecord(indices, ground); IndexRecord(indices, overlay); }
        else IndexRecord(indices, metatile);
        _indexCache[metatile] = indices;
        return indices;
    }

    private void IndexRecord(byte[] indices, int metatile)
    {
        int record = metatile * MetatileSize;
        if (record + 8 > Blocks.Length) return;
        for (int q = 0; q < 4; q++)
        {
            int entry = Blocks[record + q * 2] | (Blocks[record + q * 2 + 1] << 8);
            int qx = q >= 2 ? 8 : 0, qy = (q & 1) == 1 ? 8 : 0;
            var tile = TilePixels(entry & 0x3FF);
            int paletteRow = (entry >> 10) & 7;
            bool flipH = (entry & 0x4000) != 0, flipV = (entry & 0x8000) != 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int colour = tile[(flipV ? 7 - y : y) * 8 + (flipH ? 7 - x : x)];
                    if (colour != 0) indices[(qy + y) * 16 + qx + x] = (byte)(paletteRow * 16 + colour);
                }
        }
    }

    // draws a raw metatile record's non-transparent pixels over a 16x16 buffer
    private void DrawRecord(uint[] pixels, int metatile)
    {
        int record = metatile * MetatileSize;
        if (record + 8 > Blocks.Length) return;
        for (int q = 0; q < 4; q++)
        {
            int entry = Blocks[record + q * 2] | (Blocks[record + q * 2 + 1] << 8);
            int qx = q >= 2 ? 8 : 0;          // order: TL, BL, TR, BR
            int qy = (q & 1) == 1 ? 8 : 0;
            var tile = TilePixels(entry & 0x3FF);
            int paletteRow = (entry >> 10) & 7;
            bool flipH = (entry & 0x4000) != 0, flipV = (entry & 0x8000) != 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int colour = tile[(flipV ? 7 - y : y) * 8 + (flipH ? 7 - x : x)];
                    if (colour != 0) pixels[(qy + y) * 16 + qx + x] = Palette[paletteRow * 16 + colour];
                }
        }
    }

    // draws one metatile into a BGRA buffer of the given width
    public void DrawMetatile(uint[] dest, int destWidth, int px, int py, int metatile)
    {
        var source = MetatilePixels(metatile);
        for (int y = 0; y < 16; y++)
            Array.Copy(source, y * 16, dest, (py + y) * destWidth + px, 16);
    }

    // renders a whole map into a BGRA buffer (Width*16 x Height*16)
    public uint[] RenderMap(LufiaMap map)
    {
        int pixelWidth = map.Width * 16;
        var buffer = new uint[pixelWidth * map.Height * 16];
        for (int my = 0; my < map.Height; my++)
            for (int mx = 0; mx < map.Width; mx++)
                DrawMetatile(buffer, pixelWidth, mx * 16, my * 16, map.GetTile(mx, my));
        return buffer;
    }
}
