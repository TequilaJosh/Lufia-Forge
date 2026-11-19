namespace LufiaForge.Core.Lufia2;

// lufia 2 pictures for the Graphics page: read as colour numbers + colours, and written back from a picture
// - battle backgrounds: 24 records at $97:FD40 (4 bytes: tilemap, tiles, palette, effect, read by $81:B9C7). tiles =
//   packed file 0x16C + n (4bpp, the file's tile 0 is BG tile 0x100), tilemap = packed file 0x179 + n (32x32, delta
//   coded: the first word as is, then each word = the previous one minus the stored one), palette = 32 colours at
//   $97:CD58 + 0x40 n (CGRAM rows 2 and 3). several backgrounds share tiles, tilemaps or colours
// - capsule monster sprites: packed files 0x194-0x1B6 (capsule * 5 + form), 4bpp, nine 16x16 blocks (3 x 3) of 4
//   tiles (top left, top right, bottom left, bottom right), colours at $97:D258 + 0x20 n
// - monster battle sprites: packed file 0x1B6 + sprite (and our new ones), their palettes inside the file
//   (L2MonsterSprites)
// - hero battle sprites: 7 sheets of 4bpp tiles through the table at $8E:E5C2 (u24 each), copied in 0x200-byte
//   pieces of two rows of 8 tiles ($81:BB75, $86:8F6F). their colours aren't traced, so we show them with the map
//   sprite colours
// - title screen: tiles 0x283 (4bpp, BG1 and BG2), 0x284 (2bpp, BG3), maps 0x286/0x287 (u16 width, u16 height,
//   entries) and 0x288 (64 x 32), sparkles 0x285, colours at $A6:B000 (256). logo: 0x28D (OBJ tiles), colours at
//   $A6:EA20 (OBJ palette 6)
// - fonts: dialogue $9A:F970 (1bpp 8x16, 96 letters), menu $9F:8500 and $9F:BA80 (2bpp), credits packed file 0x040
//   (2bpp, a letter is tile n over tile n + 16). menu graphics: 0x281, 0x282 (4bpp), 0x289, 0x28A (2bpp)
public static class L2Gfx
{
    public enum Kind { BattleBackground, Capsule, Title, PackedSheet, RomSheet, Font8x16, MapSprite, Palette, MonsterSprite }

    // a set of colours a picture can be shown with: Offset = file offset of its first colour (BGR555), -1 = grey
    // levels (graphics with no colours of their own)
    public sealed record PaletteChoice(string Name, int Offset);

    // a row of a palette set (Kind.Palette): Count colours (at most 16) at file offset Offset
    public sealed record PaletteRow(string Name, int Offset, int Count);

    public sealed class Asset
    {
        public string Group { get; init; } = "";
        public string Name { get; init; } = "";
        public Kind Kind { get; init; }
        // background id, capsule sprite number, packed file or file offset (by kind)
        public int Id { get; init; }
        public int Bpp { get; init; } = 4;
        // rom sheets: bytes
        public int Size { get; init; }
        // sheets: tiles across
        public int Columns { get; init; } = 16;
        // title layers: tiles file, map file, the other map using the same tiles (or -1)
        public int TileFile { get; init; }
        public int MapFile { get; init; }
        public int OtherMap { get; init; } = -1;
        public IReadOnlyList<PaletteChoice> Palettes { get; init; } = Array.Empty<PaletteChoice>();
        // palette sets: the rows shown
        public IReadOnlyList<PaletteRow> PaletteRows { get; init; } = Array.Empty<PaletteRow>();
        public int DefaultPalette { get; init; }
        public string Note { get; init; } = "";
        public override string ToString() => Name;
    }

    // a picture: colour numbers into Colours. an 8x8 square uses one row of RowSize colours (tilemap screens can mix
    // rows square by square)
    public sealed class Picture
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Pixels { get; init; } = Array.Empty<byte>();
        public uint[] Colours { get; init; } = Array.Empty<uint>();
        public int RowSize { get; init; }
        public bool Transparent0 { get; init; }
        // the colours are the rom's (so a picture's own colours can be written back)
        public bool RomColours { get; init; }
        // file offset of each colour (BGR555), -1 for one that isn't in the rom, null if none are
        public int[]? ColourOffsets { get; init; }
        // palette sets: the name of each row
        public string[]? RowNames { get; init; }
        // the colours are stored with the graphics (a monster sprite's palettes, inside its packed data):
        // L2MonsterSprites.SetColour changes them
        public bool FileColours { get; init; }
        public int Rows => Colours.Length / RowSize;

        public uint[] Argb()
        {
            var argb = new uint[Pixels.Length];
            for (int i = 0; i < argb.Length; i++) argb[i] = Transparent0 && Pixels[i] % RowSize == 0 ? 0u : Colours[Pixels[i]];
            return argb;
        }
    }

    // addresses

    public const int BgRecords = 0xBFD40, BgCount = 24, BgTileFile = 0x16C, BgMapFile = 0x179, BgMaxTiles = 256;
    public static readonly int BgPalettes = L2Rom.SnesToFile(0x97CD58);
    public const int CapsuleFile = 0x194, CapsuleCount = 35;
    public static readonly int CapsulePalettes = L2Rom.SnesToFile(0x97D258);
    public static readonly int HeroTable = L2Rom.SnesToFile(0x8EE5C2);
    public static readonly int TitlePalette = L2Rom.SnesToFile(0xA6B000), LogoPalette = L2Rom.SnesToFile(0xA6EA20);
    public static readonly int MapSpritePalette = L2Rom.SnesToFile(0xA6BF00);
    public static readonly int MenuPalette2bpp = L2Rom.SnesToFile(0x9F8200), MenuPalette = L2Rom.SnesToFile(0x9F8040), MenuObjPalette = L2Rom.SnesToFile(0x9F8100);
    public static readonly int BattlePalette = L2Rom.SnesToFile(0x97FC40);
    public static readonly int DialogueFont = L2Rom.SnesToFile(0x9AF970), MenuFont = L2Rom.SnesToFile(0x9F8500), SecondFont = L2Rom.SnesToFile(0x9FBA80);

    // the 24 battle backgrounds as they look
    public static readonly string[] BgNames =
    {
        "Desert", "Dark cave with plants", "Water stones", "Ship deck at sea", "Meadow", "White stone temple", "Mossy rock cave",
        "Mountain path", "Tower with windows", "Brick catacomb", "Green hall with window", "Cave, red floor", "Cave, brown floor",
        "Cave with water", "Cave, purple floor", "Cave with waterfall", "Cave, olive floor", "Cave, olive, vines", "Lava cave",
        "Lava cave, rock band", "Dark cave, vines", "Brick tower inside", "Pillared hall", "Green hall",
    };

    // hero sheet sizes (the table holds no length, each one runs to the next known data)
    private static readonly int[] HeroSizes = { 0x1800, 0x1A00, 0x1400, 0x1800, 0x1800, 0x1A00, 0x1A00 };

    // the list

    private static List<PaletteChoice> Rows(string name, int offset, int rows, int rowBytes, int first = 0) =>
        Enumerable.Range(0, rows).Select(r => new PaletteChoice($"{name} {first + r}", offset + r * rowBytes)).ToList();

    private static readonly PaletteChoice Grey = new("Grey levels", -1);

    public static List<Asset> Assets(RomBuffer rom)
    {
        var list = new List<Asset>();
        for (int k = 0; k < BgCount; k++)
            list.Add(new Asset { Group = "Battle backgrounds", Name = $"{k:X2} {BgNames[k]}", Kind = Kind.BattleBackground, Id = k, Note = BgNote(rom, k) });
        var capsules = L2Capsules.All(rom);
        for (int n = 0; n < CapsuleCount; n++)
            list.Add(new Asset
            {
                Group = "Capsule monsters", Name = $"{L2Capsules.MonsterName(rom, n / 5)} {n % 5 + 1}: {capsules[n].Name}", Kind = Kind.Capsule, Id = n,
                Note = $"48 x 48, packed file {CapsuleFile + n:X3}, colours at $97:{0xD258 + n * 0x20:X4}. Colour 0 is see-through.",
            });
        list.AddRange(MonsterSpriteAssets(rom));
        var spritePals = Rows("Map sprite colours", MapSpritePalette, 8, 0x20);
        for (int hero = 0; hero < 7; hero++)
        {
            int sheetAt = L2Rom.SnesToFile((int)rom.ReadUInt24Le(HeroTable + hero * 3));
            list.Add(new Asset
            {
                Group = "Hero battle sprites", Name = L2Rom.Heroes[hero], Kind = Kind.RomSheet, Id = sheetAt, Size = HeroSizes[hero], Columns = 8, Bpp = 4,
                Palettes = spritePals, DefaultPalette = 1,
                Note = $"4bpp tiles at {sheetAt:X6} (${L2Rom.FileToSnes(sheetAt):X6}), 0x{HeroSizes[hero]:X} bytes, 8 tiles across as the game copies them. Which colours the game uses isn't traced yet: pick a set below.",
            });
        }
        var titleObj = Rows("Title sprite colours", TitlePalette + 0x100, 8, 0x20);
        list.Add(new Asset { Group = "Title screen", Name = "Sea and island (BG1)", Kind = Kind.Title, TileFile = 0x283, MapFile = 0x286, OtherMap = 0x287, Bpp = 4,
            Note = "Map 286 on tiles 283 (shared with BG2), colours at $A6:B000." });
        list.Add(new Asset { Group = "Title screen", Name = "Sky (BG2)", Kind = Kind.Title, TileFile = 0x283, MapFile = 0x287, OtherMap = 0x286, Bpp = 4,
            Note = "Map 287 on tiles 283 (shared with BG1), colours at $A6:B000." });
        list.Add(new Asset { Group = "Title screen", Name = "Trees (BG3)", Kind = Kind.Title, TileFile = 0x284, MapFile = 0x288, Bpp = 2,
            Note = "Map 288 (64 x 32) on 2bpp tiles 284, colours at $A6:B000." });
        list.Add(new Asset { Group = "Title screen", Name = "Sparkles (sprites)", Kind = Kind.PackedSheet, Id = 0x285, Bpp = 4, Palettes = titleObj, Note = "Packed file 285, sprite tiles." });
        list.Add(new Asset { Group = "Title screen", Name = "Lufia II logo (sprites)", Kind = Kind.PackedSheet, Id = 0x28D, Bpp = 4,
            Palettes = Rows("Logo colours", LogoPalette, 8, 0x20), DefaultPalette = 6, Note = "Packed file 28D, sprite tiles; the logo uses sprite colours 6 at $A6:EA20." });
        var menu2 = new List<PaletteChoice> { Grey }.Concat(Rows("Menu text colours", MenuPalette2bpp, 8, 8)).ToList();
        // the Dutch font (L2Dutch.Import) sits somewhere else and starts at character 10, so read where the glyph code points
        var (fontOp, from10) = L2Dutch.FontSite(rom);
        int fontAt = fontOp >= 0 ? L2Rom.SnesToFile((int)rom.ReadUInt24Le(fontOp)) : DialogueFont;
        list.Add(new Asset { Group = "Fonts", Name = "Dialogue font (8 x 16)", Kind = Kind.Font8x16, Id = fontAt, Size = from10 ? 0x700 : 0x600, Bpp = 1,
            Note = from10
                ? $"The Dutch font: 112 letters from character 10 (10-1F accented letters, then ASCII order), 1 bit per pixel, at ${L2Rom.FileToSnes(fontAt):X6}."
                : $"96 letters from space (ASCII order), 1 bit per pixel, at ${L2Rom.FileToSnes(fontAt):X6}." });
        list.Add(new Asset { Group = "Fonts", Name = "Menu font and icons", Kind = Kind.RomSheet, Id = MenuFont, Size = 0x1000, Bpp = 2, Palettes = menu2,
            Note = "256 tiles at $9F:8500, tile = letter code; item icons from row 6." });
        list.Add(new Asset { Group = "Fonts", Name = "Second font", Kind = Kind.RomSheet, Id = SecondFont, Size = 0x1000, Bpp = 2, Palettes = menu2,
            Note = "256 tiles at $9F:BA80." });
        list.Add(new Asset { Group = "Fonts", Name = "Credits font", Kind = Kind.PackedSheet, Id = 0x040, Bpp = 2, Palettes = menu2,
            Note = "Packed file 040: a letter is a tile over the tile 16 after it." });
        var menuBg = new List<PaletteChoice> { Grey }.Concat(Rows("Menu colours", MenuPalette, 6, 0x20, 2)).ToList();
        var menuObj = new List<PaletteChoice> { Grey }.Concat(Rows("Menu sprite colours", MenuObjPalette, 4, 0x20)).ToList();
        list.Add(new Asset { Group = "Menu graphics", Name = "Menu sprites", Kind = Kind.PackedSheet, Id = 0x281, Bpp = 4, Palettes = menuObj, DefaultPalette = 1,
            Note = "Packed file 281: digits, cursor hands, gems, stat letters." });
        list.Add(new Asset { Group = "Menu graphics", Name = "Menu windows", Kind = Kind.PackedSheet, Id = 0x282, Bpp = 4, Palettes = menuBg, DefaultPalette = 1,
            Note = "Packed file 282: window borders, banners, buttons." });
        list.Add(new Asset { Group = "Menu graphics", Name = "Menu labels and numbers", Kind = Kind.PackedSheet, Id = 0x289, Bpp = 2, Palettes = menu2, DefaultPalette = 1,
            Note = "Packed file 289: HP MP, stat names, digits, button hints." });
        list.Add(new Asset { Group = "Menu graphics", Name = "Menu small tiles", Kind = Kind.PackedSheet, Id = 0x28A, Bpp = 2, Palettes = menu2, DefaultPalette = 1,
            Note = "Packed file 28A." });
        var battle = new List<PaletteChoice> { Grey }.Concat(Rows("Battle colours", BattlePalette, 8, 0x20)).ToList();
        list.Add(new Asset { Group = "Battle graphics", Name = "Status bar", Kind = Kind.PackedSheet, Id = 0x18D, Bpp = 4, Palettes = battle, Note = "Packed file 18D: HP, MP and IP gauges, digits." });
        list.Add(new Asset { Group = "Battle graphics", Name = "Status bar (second)", Kind = Kind.PackedSheet, Id = 0x18E, Bpp = 4, Palettes = battle, Note = "Packed file 18E." });
        list.Add(new Asset { Group = "Battle graphics", Name = "Command icons", Kind = Kind.PackedSheet, Id = 0x18F, Bpp = 4, Palettes = battle, Note = "Packed file 18F: attack, spell, item, defend..." });
        list.Add(new Asset { Group = "Battle graphics", Name = "Status balloons", Kind = Kind.PackedSheet, Id = 0x190, Bpp = 4, Palettes = battle, Note = "Packed file 190: sleep, ?, !, skull..." });
        var mapPals = MapSpritePalettes(rom);
        for (int n = 0; n < L2Sprites.Count(rom); n++)
        {
            var entry = MapSpriteOf(rom, n);
            int width = entry.FrameWidth * 8, height = entry.FrameHeight * 8, palette = L2Sprites.PaletteOf(rom, n);
            bool added = L2Sprites.IsNew(n);
            list.Add(new Asset
            {
                Group = "Map sprites", Name = $"{L2Sprites.Name(n)} ({width} x {height}, {entry.Frames} frame{(entry.Frames == 1 ? "" : "s")})", Kind = Kind.MapSprite, Id = n,
                Palettes = mapPals, DefaultPalette = palette > 0 ? 7 + palette : entry.Row,
                Note = $"Sprite {L2Sprites.Name(n)}{(added ? " (added by Lufia Forge; on a map it is FB, FC or FD)" : "")}: {entry.Frames} frame{(entry.Frames == 1 ? "" : "s")} of {width} x {height} side by side, {entry.FrameWidth * entry.FrameHeight * entry.Frames} tiles at {entry.Tiles:X6} (${L2Rom.FileToSnes(entry.Tiles):X6}), " +
                       (palette > 0 ? $"colours: new palette {palette}" : $"colours: map sprite row {entry.Row}") + ". Colour 0 is see-through. Import and painting write the tiles back in place (same size).",
            });
        }
        list.AddRange(PaletteSets(rom));
        return list;
    }

    // the monster battle sprites (1 up), named by the monsters that use them
    public static List<Asset> MonsterSpriteAssets(RomBuffer rom)
    {
        var list = new List<Asset>();
        var users = L2Battle.Monsters(rom).GroupBy(m => (int)m.Sprite).ToDictionary(g => g.Key, g => g.ToList());
        for (int n = 1; n <= L2MonsterSprites.Count(rom); n++)
        {
            int paletteCount;
            try { paletteCount = L2MonsterSprites.PaletteCount(rom, n); } catch { continue; }
            var (tilesAcross, tilesDown, _) = L2MonsterSprites.Size(rom, n);
            var who = users.TryGetValue(n, out var monsters) ? monsters : new List<L2Battle.Monster>();
            var names = who.Select(m => m.Name).Distinct().ToList();
            string label = names.Count == 0 ? (L2MonsterSprites.IsNew(n) ? "new" : "no monster") : string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", ..." : "");
            var choices = Enumerable.Range(0, Math.Max(1, paletteCount)).Select(p =>
            {
                var on = who.Where(m => m.Palette == p).Select(m => m.Name).Distinct().ToList();
                return new PaletteChoice($"Palette {p}" + (on.Count > 0 ? ": " + string.Join(", ", on.Take(3)) + (on.Count > 3 ? ", ..." : "") : ""), -1);
            }).ToList();
            var (squaresAcross, squaresDown) = L2MonsterSprites.Squares(tilesAcross, tilesDown);
            list.Add(new Asset
            {
                Group = "Monster battle sprites", Name = $"{n:X2} {label}", Kind = Kind.MonsterSprite, Id = n, Palettes = choices,
                Note = $"Sprite {n:X2} ({n}), {tilesAcross * 8} x {tilesDown * 8}, {squaresAcross * squaresDown} squares of 16 x 16, {paletteCount} palette{(paletteCount == 1 ? "" : "s")}, {L2MonsterSprites.Where(rom, n)}. " +
                       (who.Count > 0 ? "Used by " + string.Join(", ", who.Select(m => $"{m.Index:X2} {m.Name} (palette {m.Palette})")) + ". " : "No monster uses it yet: pick it on the Monsters page. ") +
                       "Colour 0 is see-through.",
            });
        }
        return list;
    }

    // a background's record: tilemap, tiles, palette
    public static (int Map, int Tiles, int Palette) BgRecord(RomBuffer rom, int k) =>
        (rom.ReadByte(BgRecords + k * 4), rom.ReadByte(BgRecords + k * 4 + 1), rom.ReadByte(BgRecords + k * 4 + 2));

    // what a background shares with others (an import changes them too)
    public static string BgNote(RomBuffer rom, int k)
    {
        var (map, tiles, palette) = BgRecord(rom, k);
        var parts = new List<string>();
        string Ids(Func<int, bool> matches) => string.Join(", ", Enumerable.Range(0, BgCount).Where(j => j != k && matches(j)).Select(j => j.ToString("X2")));
        string sameMap = Ids(j => BgRecord(rom, j).Map == map), sameTiles = Ids(j => BgRecord(rom, j).Tiles == tiles), samePalette = Ids(j => BgRecord(rom, j).Palette == palette);
        if (sameMap.Length > 0) parts.Add($"its layout (tilemap) with {sameMap}");
        if (samePalette.Length > 0) parts.Add($"its colours with {samePalette}");
        if (sameTiles.Length > 0) parts.Add($"its tiles with {sameTiles} (their squares are kept)");
        return $"Tilemap file {BgMapFile + map:X3}, tiles file {BgTileFile + tiles:X3}, colours {palette:X2} at $97:{0xCD58 + palette * 0x40:X4}." +
               (parts.Count > 0 ? " Shares " + string.Join("; ", parts) + "." : "");
    }

    // tiles and colours

    public static uint Argb(int bgr) => 0xFF000000u | (uint)((bgr & 31) * 255 / 31) << 16 | (uint)((bgr >> 5 & 31) * 255 / 31) << 8 | (uint)((bgr >> 10 & 31) * 255 / 31);

    public static ushort Bgr555(uint argb)
    {
        int r = (int)(argb >> 16 & 255), g = (int)(argb >> 8 & 255), b = (int)(argb & 255);
        return (ushort)((r * 31 + 127) / 255 | (g * 31 + 127) / 255 << 5 | (b * 31 + 127) / 255 << 10);
    }

    private static uint[] Colours(RomBuffer rom, int offset, int count)
    {
        if (offset < 0) return Enumerable.Range(0, count).Select(i => 0xFF000000u | (uint)(i * 255 / Math.Max(1, count - 1)) * 0x010101u).ToArray();
        return Enumerable.Range(0, count).Select(i => Argb(rom.ReadUInt16Le(offset + i * 2))).ToArray();
    }

    private static int TileBytes(int bpp) => bpp * 8;

    private static int[]? Offsets(int offset, int count) => offset < 0 ? null : Enumerable.Range(0, count).Select(i => offset + i * 2).ToArray();

    // one 8x8 SNES planar tile (1bpp: 8 bytes, 2bpp: 16, 4bpp: 32) to 64 colour numbers
    public static byte[] DecodeTile(byte[] data, int offset, int bpp)
    {
        var pixels = new byte[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int shift = 7 - x, colour = 0;
                if (bpp == 1) colour = data[offset + y] >> shift & 1;
                else
                    for (int plane = 0; plane < bpp / 2; plane++)
                        colour |= (data[offset + plane * 16 + y * 2] >> shift & 1) << (2 * plane) | (data[offset + plane * 16 + y * 2 + 1] >> shift & 1) << (2 * plane + 1);
                pixels[y * 8 + x] = (byte)colour;
            }
        return pixels;
    }

    public static byte[] EncodeTile(byte[] pixels, int bpp)
    {
        var data = new byte[TileBytes(bpp)];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int colour = pixels[y * 8 + x], shift = 7 - x;
                if (bpp == 1) { data[y] |= (byte)((colour & 1) << shift); continue; }
                for (int plane = 0; plane < bpp / 2; plane++)
                {
                    data[plane * 16 + y * 2] |= (byte)((colour >> (2 * plane) & 1) << shift);
                    data[plane * 16 + y * 2 + 1] |= (byte)((colour >> (2 * plane + 1) & 1) << shift);
                }
            }
        return data;
    }

    private static byte[] Flip(byte[] pixels, bool flipX, bool flipY)
    {
        if (!flipX && !flipY) return pixels;
        var flipped = new byte[64];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) flipped[y * 8 + x] = pixels[(flipY ? 7 - y : y) * 8 + (flipX ? 7 - x : x)];
        return flipped;
    }

    // tilemap screens (battle backgrounds, title layers)

    private sealed class Screen
    {
        public int Bpp, W, H, TileBase, MaxTiles, RowBase, Rows, PaletteOffset;
        public Func<byte[]> ReadTiles = null!;
        public Action<byte[]> WriteTiles = null!;
        public Func<ushort[]> ReadMap = null!;
        public Action<ushort[]> WriteMap = null!;
        public Func<List<ushort[]>> Others = () => new();
        public int RowSize => 1 << Bpp;
    }

    private static ushort[] BgMap(RomBuffer rom, int map)
    {
        var data = L2Rom.ReadFile(rom, BgMapFile + map);
        var words = new ushort[0x400];
        for (int i = 0; i < words.Length; i++)
        {
            int raw = 2 * i + 1 < data.Length ? data[2 * i] | data[2 * i + 1] << 8 : 0;
            words[i] = i == 0 ? (ushort)raw : (ushort)(words[i - 1] - raw);
        }
        return words;
    }

    // the delta coding of a battle background tilemap (the first word as is, then previous minus current)
    public static byte[] BgMapBytes(ushort[] words)
    {
        var data = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
        {
            int raw = i == 0 ? words[0] : (ushort)(words[i - 1] - words[i]);
            data[2 * i] = (byte)raw; data[2 * i + 1] = (byte)(raw >> 8);
        }
        return data;
    }

    private static Screen ScreenOf(RomBuffer rom, Asset asset)
    {
        if (asset.Kind == Kind.BattleBackground)
        {
            var (map, tiles, palette) = BgRecord(rom, asset.Id);
            return new Screen
            {
                Bpp = 4, W = 32, H = 32, TileBase = 0x100, MaxTiles = BgMaxTiles, RowBase = 2, Rows = 2, PaletteOffset = BgPalettes + palette * 0x40,
                ReadTiles = () => L2Rom.ReadFile(rom, BgTileFile + tiles),
                WriteTiles = data => L2Rom.WriteFile(rom, BgTileFile + tiles, data),
                ReadMap = () => BgMap(rom, map),
                WriteMap = words => L2Rom.WriteFile(rom, BgMapFile + map, BgMapBytes(words)),
                Others = () => Enumerable.Range(0, BgCount).Select(j => BgRecord(rom, j)).Where(r => r.Tiles == tiles && r.Map != map)
                    .Select(r => r.Map).Distinct().Select(mm => BgMap(rom, mm)).ToList(),
            };
        }
        // title layers: u16 width, u16 height, then the entries
        ushort[] Map(int file, out int width, out int height)
        {
            var data = L2Rom.ReadFile(rom, file);
            width = data[0] | data[1] << 8; height = data[2] | data[3] << 8;
            var words = new ushort[width * height];
            for (int i = 0; i < words.Length; i++) words[i] = (ushort)(data[4 + 2 * i] | data[5 + 2 * i] << 8);
            return words;
        }
        Map(asset.MapFile, out int mapWidth, out int mapHeight);
        int tileCount = L2Rom.ReadFile(rom, asset.TileFile).Length / TileBytes(asset.Bpp);
        return new Screen
        {
            Bpp = asset.Bpp, W = mapWidth, H = mapHeight, TileBase = 0, MaxTiles = tileCount, RowBase = 0, Rows = 8, PaletteOffset = TitlePalette,
            ReadTiles = () => L2Rom.ReadFile(rom, asset.TileFile),
            WriteTiles = data => L2Rom.WriteFile(rom, asset.TileFile, data),
            ReadMap = () => Map(asset.MapFile, out _, out _),
            WriteMap = words =>
            {
                var data = L2Rom.ReadFile(rom, asset.MapFile);
                for (int i = 0; i < words.Length; i++) { data[4 + 2 * i] = (byte)words[i]; data[5 + 2 * i] = (byte)(words[i] >> 8); }
                L2Rom.WriteFile(rom, asset.MapFile, data);
            },
            Others = () => asset.OtherMap >= 0 ? new List<ushort[]> { Map(asset.OtherMap, out _, out _) } : new(),
        };
    }

    private static Picture DecodeScreen(RomBuffer rom, Screen screen)
    {
        var tiles = screen.ReadTiles();
        var map = screen.ReadMap();
        int rowSize = screen.RowSize, bytesPerTile = TileBytes(screen.Bpp), tileCount = tiles.Length / bytesPerTile;
        int w = screen.W * 8, h = screen.H * 8;
        var pixels = new byte[w * h];
        for (int cy = 0; cy < screen.H; cy++)
            for (int cx = 0; cx < screen.W; cx++)
            {
                var cell = CellOf(map[cy * screen.W + cx], tiles, tileCount, screen);
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pixels[(cy * 8 + y) * w + cx * 8 + x] = cell[y * 8 + x];
            }
        return new Picture { Width = w, Height = h, Pixels = pixels, Colours = Colours(rom, screen.PaletteOffset, screen.Rows * rowSize), RowSize = rowSize, RomColours = true,
                             ColourOffsets = Offsets(screen.PaletteOffset, screen.Rows * rowSize) };
    }

    // the 64 colour numbers a map entry shows
    private static byte[] CellOf(ushort entry, byte[] tiles, int tileCount, Screen screen)
    {
        int tile = (entry & 0x3FF) - screen.TileBase, row = (((entry >> 10) & 7) - screen.RowBase) % screen.Rows;
        if (row < 0) row += screen.Rows;
        var pixels = tile >= 0 && tile < tileCount ? Flip(DecodeTile(tiles, tile * TileBytes(screen.Bpp), screen.Bpp), (entry & 0x4000) != 0, (entry & 0x8000) != 0) : new byte[64];
        var cell = new byte[64];
        for (int i = 0; i < 64; i++) cell[i] = (byte)(row * screen.RowSize + pixels[i]);
        return cell;
    }

    private static string Key(byte[] pixels) => Convert.ToBase64String(pixels);

    // puts a picture into a tilemap screen: squares that still look the same keep their entry, the others reuse an
    // identical tile (flipped too) or get a free tile (ones only this map used first, then unused ones, then new ones
    // at the end). returns the rows the picture uses and the tile count
    private static (HashSet<int> Rows, int Tiles, int NewTiles) EncodeScreen(Screen screen, byte[] pixels)
    {
        var tiles = screen.ReadTiles();
        var map = screen.ReadMap();
        int rowSize = screen.RowSize, bytesPerTile = TileBytes(screen.Bpp), tileCount = tiles.Length / bytesPerTile, w = screen.W * 8;
        var content = new List<byte[]>();
        for (int t = 0; t < tileCount; t++) content.Add(DecodeTile(tiles, t * bytesPerTile, screen.Bpp));
        var locked = new HashSet<int>();
        foreach (var other in screen.Others()) foreach (var e in other) { int t = (e & 0x3FF) - screen.TileBase; if (t >= 0 && t < tileCount) locked.Add(t); }
        var mine = new HashSet<int>(map.Select(e => (e & 0x3FF) - screen.TileBase).Where(t => t >= 0 && t < tileCount));

        // the squares and their rows
        int cells = screen.W * screen.H;
        var want = new byte[cells][];
        var rowOf = new int[cells];
        var rowsUsed = new HashSet<int>();
        for (int c = 0; c < cells; c++)
        {
            int cx = c % screen.W, cy = c / screen.W, row = -1;
            var full = new byte[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int v = pixels[(cy * 8 + y) * w + cx * 8 + x];
                    if (v >= screen.Rows * rowSize) throw new InvalidOperationException($"The square at ({cx * 8}, {cy * 8}) uses colour {v}; there are {screen.Rows * rowSize}.");
                    full[y * 8 + x] = (byte)v;
                    if (v % rowSize == 0) continue;
                    if (row >= 0 && row != v / rowSize)
                        throw new InvalidOperationException($"The 8x8 square at ({cx * 8}, {cy * 8}) mixes colours of rows {row} and {v / rowSize}; a square can use one row of {rowSize} colours.");
                    row = v / rowSize;
                }
            if (row < 0) row = full[0] / rowSize;
            rowOf[c] = row;
            if (full.Any(v => v % rowSize != 0)) rowsUsed.Add(row);
            want[c] = full;
        }

        // 1. squares that look the same keep their entry
        var newMap = new ushort[cells];
        var done = new bool[cells];
        var used = new HashSet<int>(locked);
        for (int c = 0; c < cells; c++)
        {
            if (!CellOf(map[c], tiles, tileCount, screen).AsSpan().SequenceEqual(want[c])) continue;
            newMap[c] = map[c]; done[c] = true;
            int t = (map[c] & 0x3FF) - screen.TileBase;
            if (t >= 0 && t < tileCount) used.Add(t);
        }
        // 2. the rest: an identical tile, else a free one
        var index = new Dictionary<string, int>();
        foreach (int t in used.OrderBy(t => t)) index.TryAdd(Key(content[t]), t);
        var free = new Queue<int>(mine.Where(t => !used.Contains(t)).OrderBy(t => t)
            .Concat(Enumerable.Range(0, tileCount).Where(t => !used.Contains(t) && !mine.Contains(t))));
        int newTiles = 0;
        for (int c = 0; c < cells; c++)
        {
            if (done[c]) continue;
            var p = want[c].Select(v => (byte)(v % rowSize)).ToArray();
            int tile = -1, flips = 0;
            for (int f = 0; f < 4 && tile < 0; f++)
                if (index.TryGetValue(Key(Flip(p, (f & 1) != 0, (f & 2) != 0)), out int t)) { tile = t; flips = f; }
            if (tile < 0)
            {
                if (free.Count > 0) tile = free.Dequeue();
                else
                {
                    if (content.Count >= screen.MaxTiles)
                        throw new InvalidOperationException($"The picture needs more than {screen.MaxTiles} different 8x8 squares (flips count as the same square).");
                    tile = content.Count; content.Add(new byte[64]); newTiles++;
                }
                content[tile] = p;
                index[Key(p)] = tile;
                used.Add(tile);
            }
            int prio = map[c] & 0x2000;
            newMap[c] = (ushort)((flips & 2) << 14 | (flips & 1) << 14 | prio | (rowOf[c] + screen.RowBase) << 10 | (tile + screen.TileBase));
        }
        // write only what changed
        // (a part tile at the end of the file is kept as it was)
        var tileBytes = new byte[Math.Max(content.Count * bytesPerTile, tiles.Length)];
        Array.Copy(tiles, tileBytes, tiles.Length);
        for (int t = 0; t < content.Count; t++) EncodeTile(content[t], screen.Bpp).CopyTo(tileBytes, t * bytesPerTile);
        if (!tileBytes.AsSpan().SequenceEqual(tiles)) screen.WriteTiles(tileBytes);
        if (!newMap.AsSpan().SequenceEqual(map)) screen.WriteMap(newMap);
        return (rowsUsed, used.Count, newTiles);
    }

    // sheets

    private static byte[] SheetBytes(RomBuffer rom, Asset asset) => asset.Kind switch
    {
        Kind.PackedSheet => L2Rom.ReadFile(rom, asset.Id),
        _ => rom.ReadBytes(asset.Id, asset.Size),
    };

    private static (int W, int H, int Tiles) SheetSize(Asset asset, int bytes)
    {
        if (asset.Kind == Kind.Font8x16) { int glyphs = bytes / 16; return (16 * 8, (glyphs + 15) / 16 * 16, glyphs); }
        int tiles = bytes / TileBytes(asset.Bpp), rows = Math.Max(1, (tiles + asset.Columns - 1) / asset.Columns);
        return (asset.Columns * 8, rows * 8, tiles);
    }

    // pixel position of sheet tile tile (fonts: glyph tile, 8 x 16)
    private static (int X, int Y) SheetPos(Asset asset, int tile) =>
        asset.Kind == Kind.Font8x16 ? (tile % 16 * 8, tile / 16 * 16) : (tile % asset.Columns * 8, tile / asset.Columns * 8);

    // map sprites

    // a map sprite's entry (L2Graphics.MapSpriteTable): flags, palette row, the file offset of its tiles and its frame
    // size in tiles
    public sealed record MapSpriteEntry(int Flags, int Row, int Tiles, int FrameWidth, int FrameHeight, int Frames);

    public static MapSpriteEntry MapSpriteOf(RomBuffer rom, int sprite)
    {
        int entry = L2Sprites.EntryAt(rom, sprite);
        int flags = rom.ReadByte(entry), row = rom.ReadByte(entry + 1) & 7;
        int gfx = L2Rom.SnesToFile((int)rom.ReadUInt24Le(entry + 2));
        int frames = (flags & 8) != 0 ? 2 : (flags & 0x10) != 0 ? 6 : 1;
        return new MapSpriteEntry(flags, row, gfx, (flags & 2) != 0 ? 4 : 2, (flags & 1) != 0 ? 4 : 2, frames);
    }

    // the colours a map sprite can use: the 8 sprite rows, then the new palettes (L2Sprites)
    public static List<PaletteChoice> MapSpritePalettes(RomBuffer rom) =>
        Rows("Map sprite colours", MapSpritePalette, 8, 0x20)
            .Concat(Enumerable.Range(1, L2Sprites.PaletteCount(rom)).Select(p => new PaletteChoice($"New palette {p}", L2Sprites.PaletteAt(rom, p)))).ToList();

    // the tile number (from the sprite's first) of tile (x, y) of frame frame: each frame's tiles are the upper halves
    // of its 16-pixel rows, then the lower halves (LEdit's L2MapSprite)
    private static int MapSpriteTile(MapSpriteEntry sprite, int frame, int x, int y)
    {
        int frameTiles = sprite.FrameWidth * sprite.FrameHeight;
        return frame * frameTiles + (y & 1) * (frameTiles / 2) + (y >> 1) * sprite.FrameWidth + x;
    }

    // palettes

    // the size of a colour square in a palette set's picture
    public const int PaletteCell = 8;

    // writes one colour (BGR555) at offset, keeping the unused top bit as it was. true if it changed
    public static bool SetColour(RomBuffer rom, int offset, int bgr555)
    {
        int old = rom.ReadUInt16Le(offset);
        ushort value = (ushort)(old & 0x8000 | bgr555 & 0x7FFF);
        if (value == old) return false;
        rom.WriteUInt16Le(offset, value);
        return true;
    }

    private static List<PaletteRow> RowsOf(string name, int offset, int rows, int count = 16, int first = 0) =>
        Enumerable.Range(0, rows).Select(r => new PaletteRow($"{name} {first + r}", offset + r * count * 2, count)).ToList();

    // the palette sets we know, each a Kind.Palette asset
    public static List<Asset> PaletteSets(RomBuffer rom)
    {
        var list = new List<Asset>();
        const string G = "Palettes";
        string Snes(int file) => $"${L2Rom.FileToSnes(file) >> 16:X2}:{L2Rom.FileToSnes(file) & 0xFFFF:X4}";
        for (int p = 0; p < 20; p++)
        {
            var users = Enumerable.Range(0, BgCount).Where(k => BgRecord(rom, k).Palette == p).Select(k => $"{k:X2} {BgNames[k]}").ToList();
            list.Add(new Asset { Group = G, Name = $"Battle background colours {p:X2}", Kind = Kind.Palette, PaletteRows = RowsOf("Background row", BgPalettes + p * 0x40, 2, 16, 2),
                Note = $"At $97:{0xCD58 + p * 0x40:X4}, 32 colours (rows 2 and 3 of the screen). " + (users.Count > 0 ? "Used by " + string.Join(", ", users) + "." : "No battle background uses it.") });
        }
        for (int c = 0; c < 7; c++)
            list.Add(new Asset { Group = G, Name = $"Capsule monster colours: {L2Capsules.MonsterName(rom, c)}", Kind = Kind.Palette,
                PaletteRows = Enumerable.Range(0, 5).Select(f => new PaletteRow($"Form {f + 1}", CapsulePalettes + (c * 5 + f) * 0x20, 16)).ToList(),
                Note = $"A row of 16 per form, at $97:{0xD258 + c * 5 * 0x20:X4}. Colour 0 is see-through." });
        list.Add(new Asset { Group = G, Name = "Map sprite colours", Kind = Kind.Palette, PaletteRows = RowsOf("Sprite row", MapSpritePalette, 8),
            Note = "The 8 rows people, monsters and objects on maps use (each sprite names its row), at $A6:BF00. Colour 0 is see-through." });
        int newPals = L2Sprites.PaletteCount(rom);
        if (newPals > 0)
            list.Add(new Asset { Group = G, Name = "New map sprite palettes", Kind = Kind.Palette,
                PaletteRows = Enumerable.Range(1, newPals).Select(p => new PaletteRow($"New palette {p}", L2Sprites.PaletteAt(rom, p), 16)).ToList(),
                Note = "Palettes added by Lufia Forge for map sprites. On a map the game puts one into a sprite row no other character there is using. Colour 0 is see-through." });
        list.Add(new Asset { Group = G, Name = "Title screen colours", Kind = Kind.Palette, PaletteRows = RowsOf("Row", TitlePalette, 16),
            Note = "256 colours at $A6:B000: rows 0-7 the sea, sky and trees, rows 8-15 the sprites." });
        list.Add(new Asset { Group = G, Name = "Title logo colours", Kind = Kind.Palette, PaletteRows = RowsOf("Sprite row", LogoPalette, 8),
            Note = "Sprite colours at $A6:EA20; the logo uses row 6." });
        int worldB = L2Rom.SnesToFile(0xA6B200), worldA = L2Rom.SnesToFile(0xA6B400), seaFloor = L2Rom.SnesToFile(0xA6BA00);
        list.Add(new Asset { Group = G, Name = "World map colours", Kind = Kind.Palette, PaletteRows = RowsOf("Row", worldA, 8),
            Note = "At $A6:B400 (the World map tab draws with these)." });
        list.Add(new Asset { Group = G, Name = "World map colours (second set)", Kind = Kind.Palette, PaletteRows = RowsOf("Row", worldB, 8),
            Note = "At $A6:B200, the world map's other set." });
        list.Add(new Asset { Group = G, Name = "Sea floor map colours", Kind = Kind.Palette, PaletteRows = RowsOf("Row", seaFloor, 8),
            Note = "At $A6:BA00, the map under the sea." });
        list.Add(new Asset { Group = G, Name = "Menu colours", Kind = Kind.Palette, PaletteRows = RowsOf("Menu row", MenuPalette, 6, 16, 2),
            Note = "Background rows 2-7 of the menus, at $9F:8040." });
        list.Add(new Asset { Group = G, Name = "Menu sprite colours", Kind = Kind.Palette, PaletteRows = RowsOf("Sprite row", MenuObjPalette, 4),
            Note = "At $9F:8100: cursor hands, digits, gems." });
        list.Add(new Asset { Group = G, Name = "Menu text colours", Kind = Kind.Palette, PaletteRows = RowsOf("Text row", MenuPalette2bpp, 8, 4),
            Note = "4 colours a row for the 2 bits per pixel font and labels, at $9F:8200." });
        list.Add(new Asset { Group = G, Name = "Battle colours", Kind = Kind.Palette, PaletteRows = RowsOf("Row", BattlePalette, 8),
            Note = "At $97:FC40: the status bar, command icons and balloons." });

        // map palette sets: the map's set byte n (1 up) picks pointer n - 1. a map reads 6 rows (CGRAM rows 2-7) from
        // 0x10 or 0x20 bytes into the set (L2Maps.Palettes)
        const int sets = 0x57;
        var ptrs = Enumerable.Range(0, sets).Select(i => (int)rom.ReadUInt16Le(L2Maps.PaletteSetPointers + i * 2)).ToArray();
        var sorted = ptrs.Distinct().OrderBy(p => p).ToList();
        for (int i = 0; i < sets; i++)
        {
            var maps = Enumerable.Range(0, L2Maps.Count(rom)).Where(m => rom.ReadByte(L2Maps.PaletteSetAt(rom, m)) == i + 1).ToList();
            int k = sorted.IndexOf(ptrs[i]);
            int size = Math.Min(0x100, k + 1 < sorted.Count ? sorted[k + 1] - ptrs[i] : 0x10000 - ptrs[i]);
            int head = maps.Count > 0 && rom.ReadByte(L2Maps.AnimData + rom.ReadByte(L2Maps.AnimIndexAt(rom, maps[0])) * 2 + 1) >> 4 != 0 ? 0x20 : 0x10;
            int at = L2Rom.SnesToFile(0x9B0000 | ptrs[i]);
            var rows = new List<PaletteRow> { new("Before the map rows", at, head / 2) };
            for (int r = 0; head + r * 0x20 < size && r < 6; r++)
                rows.Add(new PaletteRow($"Map row {r + 2}", at + head + r * 0x20, Math.Min(16, (size - head - r * 0x20) / 2)));
            string first = maps.Count > 0 ? $"{maps[0]:X2} {L2Maps.Name(rom, maps[0])}" : "no map";
            list.Add(new Asset { Group = "Map palettes", Name = $"{i + 1:X2} {first}" + (maps.Count > 1 ? $" (+{maps.Count - 1})" : ""), Kind = Kind.Palette, PaletteRows = rows,
                Note = $"Map palette set {i + 1:X2} at {Snes(at)}. " +
                       (maps.Count > 0 ? $"Used by map{(maps.Count == 1 ? "" : "s")} " + string.Join(", ", maps.Select(m => m.ToString("X2"))) + ". " : "") +
                       $"A map shows 6 rows (rows 2-7 of the screen) from {head} bytes in; this set holds {rows.Count - 1}." });
        }
        return list;
    }

    // decode / import

    // the picture of an asset, with colours palette of its choices
    public static Picture Decode(RomBuffer rom, Asset asset, int palette = -1)
    {
        switch (asset.Kind)
        {
            case Kind.BattleBackground:
            case Kind.Title:
                return DecodeScreen(rom, ScreenOf(rom, asset));
            case Kind.Capsule:
            {
                var data = L2Rom.ReadFile(rom, CapsuleFile + asset.Id);
                var pixels = new byte[48 * 48];
                for (int square = 0; square < 9; square++)
                    for (int t = 0; t < 4; t++)
                    {
                        if (square * 0x80 + t * 0x20 + 0x20 > data.Length) continue;
                        var tilePixels = DecodeTile(data, square * 0x80 + t * 0x20, 4);
                        int ox = square % 3 * 16 + (t & 1) * 8, oy = square / 3 * 16 + (t >> 1) * 8;
                        for (int i = 0; i < 64; i++) pixels[(oy + i / 8) * 48 + ox + i % 8] = tilePixels[i];
                    }
                return new Picture { Width = 48, Height = 48, Pixels = pixels, Colours = Colours(rom, CapsulePalettes + asset.Id * 0x20, 16), RowSize = 16, Transparent0 = true, RomColours = true,
                                     ColourOffsets = Offsets(CapsulePalettes + asset.Id * 0x20, 16) };
            }
            case Kind.MonsterSprite:
            {
                var data = L2MonsterSprites.Read(rom, asset.Id);
                var (w, h, pixels) = L2MonsterSprites.Pixels(rom, asset.Id, data);
                int paletteIndex = palette < 0 || palette >= asset.Palettes.Count ? asset.DefaultPalette : palette;
                var colours = L2MonsterSprites.Palette(data, paletteIndex).Select(Argb).ToArray();
                return new Picture { Width = w, Height = h, Pixels = pixels, Colours = colours, RowSize = 16, Transparent0 = true, RomColours = true, FileColours = data[0] > 0 };
            }
            case Kind.MapSprite:
            {
                var sprite = MapSpriteOf(rom, asset.Id);
                int w = sprite.FrameWidth * 8 * sprite.Frames, h = sprite.FrameHeight * 8;
                var pixels = new byte[w * h];
                for (int f = 0; f < sprite.Frames; f++)
                    for (int y = 0; y < sprite.FrameHeight; y++)
                        for (int x = 0; x < sprite.FrameWidth; x++)
                        {
                            int at = sprite.Tiles + MapSpriteTile(sprite, f, x, y) * 32;
                            if (at < 0 || at + 32 > rom.Length) continue;
                            var tilePixels = DecodeTile(rom.ReadBytes(at, 32), 0, 4);
                            int ox = (f * sprite.FrameWidth + x) * 8, oy = y * 8;
                            for (int i = 0; i < 64; i++) pixels[(oy + i / 8) * w + ox + i % 8] = tilePixels[i];
                        }
                int colourAt = PaletteOf(asset, palette)?.Offset ?? MapSpritePalette + sprite.Row * 0x20;
                return new Picture { Width = w, Height = h, Pixels = pixels, Colours = Colours(rom, colourAt, 16), RowSize = 16, Transparent0 = true, RomColours = true,
                                     ColourOffsets = Offsets(colourAt, 16) };
            }
            case Kind.Palette:
            {
                var rows = asset.PaletteRows;
                int rowCount = Math.Max(1, rows.Count), w = 16 * PaletteCell, h = rowCount * PaletteCell;
                var pixels = new byte[w * h];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i / w / PaletteCell * 16 + i % w / PaletteCell);
                var colours = new uint[rowCount * 16];
                var offsets = new int[colours.Length];
                for (int i = 0; i < offsets.Length; i++) { offsets[i] = -1; colours[i] = 0xFF101010u; }
                for (int r = 0; r < rows.Count; r++)
                    for (int c = 0; c < Math.Min(16, rows[r].Count); c++)
                    {
                        offsets[r * 16 + c] = rows[r].Offset + c * 2;
                        colours[r * 16 + c] = Argb(rom.ReadUInt16Le(rows[r].Offset + c * 2));
                    }
                return new Picture { Width = w, Height = h, Pixels = pixels, Colours = colours, RowSize = 16, RomColours = true, ColourOffsets = offsets,
                                     RowNames = rows.Select(r => r.Name).ToArray() };
            }
            default:
            {
                var data = SheetBytes(rom, asset);
                var (w, h, tiles) = SheetSize(asset, data.Length);
                var pixels = new byte[w * h];
                for (int t = 0; t < tiles; t++)
                {
                    var (ox, oy) = SheetPos(asset, t);
                    if (asset.Kind == Kind.Font8x16)
                    {
                        for (int y = 0; y < 16; y++) for (int x = 0; x < 8; x++) pixels[(oy + y) * w + ox + x] = (byte)(data[t * 16 + y] >> (7 - x) & 1);
                        continue;
                    }
                    var tilePixels = DecodeTile(data, t * TileBytes(asset.Bpp), asset.Bpp);
                    for (int i = 0; i < 64; i++) pixels[(oy + i / 8) * w + ox + i % 8] = tilePixels[i];
                }
                int colourCount = 1 << asset.Bpp;
                var choice = PaletteOf(asset, palette);
                uint[] colours = asset.Kind == Kind.Font8x16 ? new[] { 0xFF000000u, 0xFFFFFFFFu } : Colours(rom, choice?.Offset ?? -1, colourCount);
                return new Picture { Width = w, Height = h, Pixels = pixels, Colours = colours, RowSize = colourCount, RomColours = choice is { Offset: >= 0 },
                                     ColourOffsets = asset.Kind == Kind.Font8x16 ? null : Offsets(choice?.Offset ?? -1, colourCount) };
            }
        }
    }

    private static PaletteChoice? PaletteOf(Asset asset, int palette)
    {
        if (asset.Palettes.Count == 0) return null;
        if (palette < 0 || palette >= asset.Palettes.Count) palette = asset.DefaultPalette;
        return asset.Palettes[Math.Clamp(palette, 0, asset.Palettes.Count - 1)];
    }

    // the colour offset a picture's colours get written to, or -1
    private static int ColourOffset(RomBuffer rom, Asset asset, int palette) => asset.Kind switch
    {
        Kind.BattleBackground => BgPalettes + BgRecord(rom, asset.Id).Palette * 0x40,
        Kind.Title => TitlePalette,
        Kind.Capsule => CapsulePalettes + asset.Id * 0x20,
        Kind.MapSprite => PaletteOf(asset, palette)?.Offset ?? MapSpritePalette + MapSpriteOf(rom, asset.Id).Row * 0x20,
        Kind.Font8x16 => -1,
        _ => PaletteOf(asset, palette)?.Offset ?? -1,
    };

    // writes a picture (colour numbers the way Decode gives them, same size) into the rom. with colours, the rows of
    // colours the picture uses get written too (colour 0 of each row, the see-through one, is left alone). throws
    // InvalidOperationException with a plain message if it can't be done. returns what was done
    public static string Import(RomBuffer rom, Asset asset, int palette, byte[] pixels, uint[]? colours)
    {
        var before = Decode(rom, asset, palette);
        if (pixels.Length != before.Pixels.Length) throw new InvalidOperationException($"The picture must be {before.Width} x {before.Height} pixels.");
        int max = before.Colours.Length;
        int bad = Array.FindIndex(pixels, v => v >= max);
        if (bad >= 0) throw new InvalidOperationException($"The pixel at ({bad % before.Width}, {bad / before.Width}) uses colour {pixels[bad]}; there are {max} colours.");
        HashSet<int> rows;
        string what;
        switch (asset.Kind)
        {
            case Kind.BattleBackground:
            case Kind.Title:
            {
                var (screenRows, used, added) = EncodeScreen(ScreenOf(rom, asset), pixels);
                rows = screenRows;
                what = $"{used} tiles in use" + (added > 0 ? $", {added} added" : "");
                break;
            }
            case Kind.Capsule:
            {
                var data = new byte[9 * 0x80];
                for (int square = 0; square < 9; square++)
                    for (int t = 0; t < 4; t++)
                    {
                        int ox = square % 3 * 16 + (t & 1) * 8, oy = square / 3 * 16 + (t >> 1) * 8;
                        var tilePixels = new byte[64];
                        for (int i = 0; i < 64; i++) tilePixels[i] = pixels[(oy + i / 8) * 48 + ox + i % 8];
                        EncodeTile(tilePixels, 4).CopyTo(data, square * 0x80 + t * 0x20);
                    }
                if (!L2Rom.ReadFile(rom, CapsuleFile + asset.Id).AsSpan().SequenceEqual(data)) L2Rom.WriteFile(rom, CapsuleFile + asset.Id, data);
                rows = new HashSet<int> { 0 };
                what = $"packed file {CapsuleFile + asset.Id:X3}";
                break;
            }
            case Kind.MapSprite:
            {
                var sprite = MapSpriteOf(rom, asset.Id);
                int tileCount = sprite.FrameWidth * sprite.FrameHeight * sprite.Frames, w = before.Width;
                if (sprite.Tiles < 0 || sprite.Tiles + tileCount * 32 > rom.Length) throw new InvalidOperationException("This sprite's tiles are outside the ROM.");
                var old = rom.ReadBytes(sprite.Tiles, tileCount * 32);
                var data = (byte[])old.Clone();
                for (int f = 0; f < sprite.Frames; f++)
                    for (int y = 0; y < sprite.FrameHeight; y++)
                        for (int x = 0; x < sprite.FrameWidth; x++)
                        {
                            int ox = (f * sprite.FrameWidth + x) * 8, oy = y * 8;
                            var tilePixels = new byte[64];
                            for (int i = 0; i < 64; i++) tilePixels[i] = pixels[(oy + i / 8) * w + ox + i % 8];
                            EncodeTile(tilePixels, 4).CopyTo(data, MapSpriteTile(sprite, f, x, y) * 32);
                        }
                if (!data.AsSpan().SequenceEqual(old)) rom.WriteBytes(sprite.Tiles, data);
                rows = new HashSet<int> { 0 };
                what = $"{tileCount} tiles at {sprite.Tiles:X6}";
                break;
            }
            case Kind.MonsterSprite:
            {
                int paletteIndex = palette < 0 || palette >= asset.Palettes.Count ? asset.DefaultPalette : palette;
                int changed = L2MonsterSprites.WritePicture(rom, asset.Id, pixels, paletteIndex, colours?.Select(c => (int)Bgr555(c)).ToArray());
                return L2MonsterSprites.Where(rom, asset.Id) + (colours == null ? "" : $"; {changed} colour{(changed == 1 ? "" : "s")} of palette {paletteIndex} changed");
            }
            case Kind.Palette:
            {
                if (colours == null) throw new InvalidOperationException("A palette is only colours: turn on \"Take the colours from the picture\" to bring them in.");
                var offsets = before.ColourOffsets!;
                int changed = 0;
                for (int i = 0; i < offsets.Length && i < colours.Length; i++)
                    if (offsets[i] >= 0 && SetColour(rom, offsets[i], Bgr555(colours[i]))) changed++;
                return $"{changed} colour{(changed == 1 ? "" : "s")} changed";
            }
            default:
            {
                var old = SheetBytes(rom, asset);
                var data = (byte[])old.Clone();
                var (w, _, tiles) = SheetSize(asset, data.Length);
                for (int t = 0; t < tiles; t++)
                {
                    var (ox, oy) = SheetPos(asset, t);
                    if (asset.Kind == Kind.Font8x16)
                    {
                        for (int y = 0; y < 16; y++)
                        {
                            int bits = 0;
                            for (int x = 0; x < 8; x++) bits |= (pixels[(oy + y) * w + ox + x] & 1) << (7 - x);
                            data[t * 16 + y] = (byte)bits;
                        }
                        continue;
                    }
                    var tilePixels = new byte[64];
                    for (int i = 0; i < 64; i++) tilePixels[i] = pixels[(oy + i / 8) * w + ox + i % 8];
                    EncodeTile(tilePixels, asset.Bpp).CopyTo(data, t * TileBytes(asset.Bpp));
                }
                if (!data.AsSpan().SequenceEqual(old))
                {
                    if (asset.Kind == Kind.PackedSheet) L2Rom.WriteFile(rom, asset.Id, data);
                    else rom.WriteBytes(asset.Id, data);
                }
                rows = new HashSet<int> { 0 };
                what = asset.Kind == Kind.PackedSheet ? $"packed file {asset.Id:X3}" : $"{data.Length} bytes at {asset.Id:X6}";
                break;
            }
        }
        if (colours == null) return what;
        int colourAt = ColourOffset(rom, asset, palette);
        if (colourAt < 0) return what + "; these graphics have no colours of their own in the ROM, so only the colour numbers were taken";
        int rowSize = before.RowSize, written = 0;
        foreach (int r in rows.OrderBy(r => r))
            for (int c = 1; c < rowSize; c++)
            {
                int i = r * rowSize + c;
                if (i >= colours.Length || i >= max) continue;
                ushort bgr = Bgr555(colours[i]);
                if (rom.ReadUInt16Le(colourAt + i * 2) != bgr) { rom.WriteUInt16Le(colourAt + i * 2, bgr); written++; }
            }
        return what + $"; {written} colour{(written == 1 ? "" : "s")} changed";
    }

    // colour numbers for a full-colour picture: each 8x8 square takes the row of colours that fits it best, each pixel
    // the nearest colour of that row (mostly see-through pixels become colour 0)
    public static byte[] MatchColours(uint[] argb, Picture pic)
    {
        int w = pic.Width, rowSize = pic.RowSize, rows = pic.Rows;
        var output = new byte[argb.Length];
        static long Dist(uint a, uint b)
        {
            long dr = (long)(a >> 16 & 255) - (b >> 16 & 255), dg = (long)(a >> 8 & 255) - (b >> 8 & 255), db = (long)(a & 255) - (b & 255);
            return dr * dr * 3 + dg * dg * 4 + db * db * 2;
        }
        int cellH = 8;
        for (int cy = 0; cy < pic.Height; cy += cellH)
            for (int cx = 0; cx < w; cx += 8)
            {
                long bestErr = long.MaxValue; int bestRow = 0;
                for (int r = 0; r < rows; r++)
                {
                    long err = 0;
                    for (int y = cy; y < Math.Min(cy + cellH, pic.Height); y++)
                        for (int x = cx; x < Math.Min(cx + 8, w); x++)
                        {
                            uint pixel = argb[y * w + x];
                            if (pixel >> 24 < 128) continue;
                            long best = long.MaxValue;
                            for (int c = pic.Transparent0 ? 1 : 0; c < rowSize; c++) best = Math.Min(best, Dist(pixel, pic.Colours[r * rowSize + c]));
                            err += best;
                        }
                    if (err < bestErr) { bestErr = err; bestRow = r; }
                }
                for (int y = cy; y < Math.Min(cy + cellH, pic.Height); y++)
                    for (int x = cx; x < Math.Min(cx + 8, w); x++)
                    {
                        uint pixel = argb[y * w + x];
                        if (pixel >> 24 < 128) { output[y * w + x] = (byte)(bestRow * rowSize); continue; }
                        long best = long.MaxValue;
                        for (int c = pic.Transparent0 ? 1 : 0; c < rowSize; c++)
                        {
                            long distance = Dist(pixel, pic.Colours[bestRow * rowSize + c]);
                            if (distance < best) { best = distance; output[y * w + x] = (byte)(bestRow * rowSize + c); }
                        }
                    }
            }
        return output;
    }

    // capsule monster sprite index (0-34: capsule * 5 + form) as ARGB, colour 0 see-through
    public static (int Width, int Height, uint[] Pixels) CapsuleSprite(RomBuffer rom, int index)
    {
        var pic = Decode(rom, new Asset { Kind = Kind.Capsule, Id = Math.Clamp(index, 0, CapsuleCount - 1) });
        return (pic.Width, pic.Height, pic.Argb());
    }
}
