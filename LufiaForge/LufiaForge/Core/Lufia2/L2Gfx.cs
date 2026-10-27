namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II pictures for the Graphics page: read as colour numbers + colours, and written back from a picture.
/// <list type="bullet">
/// <item>Battle backgrounds: 24 records at $97:FD40 (4 bytes: tilemap, tiles, palette, effect; read by $81:B9C7).
/// Tiles = packed file 0x16C + n (4bpp, the file's tile 0 is BG tile 0x100), tilemap = packed file 0x179 + n (32x32,
/// delta coded: the first word as is, then each word = the previous one minus the stored one), palette = 32 colours
/// at $97:CD58 + 0x40 n (CGRAM rows 2 and 3). Several backgrounds share tiles, tilemaps or colours.</item>
/// <item>Capsule monster sprites: packed files 0x194-0x1B6 (capsule * 5 + form), 4bpp, nine 16x16 blocks (3 x 3) of
/// 4 tiles (top left, top right, bottom left, bottom right); colours at $97:D258 + 0x20 n.</item>
/// <item>Monster battle sprites: packed file 0x1B6 + sprite (and Lufia Forge's new ones), their palettes inside the
/// file (<see cref="L2MonsterSprites"/>).</item>
/// <item>Hero battle sprites: 7 sheets of 4bpp tiles through the table at $8E:E5C2 (u24 each), copied in 0x200-byte
/// pieces of two rows of 8 tiles ($81:BB75, $86:8F6F). Their colours aren't traced: shown with the map sprite
/// colours.</item>
/// <item>Title screen: tiles 0x283 (4bpp, BG1 and BG2), 0x284 (2bpp, BG3), maps 0x286/0x287 (u16 width, u16 height,
/// entries) and 0x288 (64 x 32), sparkles 0x285; colours at $A6:B000 (256). Logo: 0x28D (OBJ tiles), colours at
/// $A6:EA20 (OBJ palette 6).</item>
/// <item>Fonts: dialogue $9A:F970 (1bpp 8x16, 96 letters), menu $9F:8500 and $9F:BA80 (2bpp), credits packed file
/// 0x040 (2bpp, a letter is tile n over tile n + 16). Menu graphics: 0x281, 0x282 (4bpp), 0x289, 0x28A (2bpp).</item>
/// </list>
/// </summary>
public static class L2Gfx
{
    public enum Kind { BattleBackground, Capsule, Title, PackedSheet, RomSheet, Font8x16, MapSprite, Palette, MonsterSprite }

    /// <summary>A set of colours a picture can be shown with: <paramref name="Offset"/> = file offset of its first
    /// colour (BGR555), -1 = grey levels (graphics with no colours of their own).</summary>
    public sealed record PaletteChoice(string Name, int Offset);

    /// <summary>A row of a palette set (<see cref="Kind.Palette"/>): <paramref name="Count"/> colours (at most 16) at
    /// file offset <paramref name="Offset"/>.</summary>
    public sealed record PaletteRow(string Name, int Offset, int Count);

    public sealed class Asset
    {
        public string Group { get; init; } = "";
        public string Name { get; init; } = "";
        public Kind Kind { get; init; }
        /// <summary>Background id, capsule sprite number, packed file or file offset (by kind).</summary>
        public int Id { get; init; }
        public int Bpp { get; init; } = 4;
        /// <summary>ROM sheets: bytes.</summary>
        public int Size { get; init; }
        /// <summary>Sheets: tiles across.</summary>
        public int Columns { get; init; } = 16;
        /// <summary>Title layers: tiles file, map file, the other map using the same tiles (or -1).</summary>
        public int TileFile { get; init; }
        public int MapFile { get; init; }
        public int OtherMap { get; init; } = -1;
        public IReadOnlyList<PaletteChoice> Palettes { get; init; } = Array.Empty<PaletteChoice>();
        /// <summary>Palette sets: the rows shown.</summary>
        public IReadOnlyList<PaletteRow> PaletteRows { get; init; } = Array.Empty<PaletteRow>();
        public int DefaultPalette { get; init; }
        public string Note { get; init; } = "";
        public override string ToString() => Name;
    }

    /// <summary>A picture: colour numbers into <see cref="Colours"/>. A square of 8x8 uses one row of
    /// <see cref="RowSize"/> colours (tilemap screens can mix rows square by square).</summary>
    public sealed class Picture
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] Pixels { get; init; } = Array.Empty<byte>();
        public uint[] Colours { get; init; } = Array.Empty<uint>();
        public int RowSize { get; init; }
        public bool Transparent0 { get; init; }
        /// <summary>The colours are the ROM's (so a picture's own colours can be written back).</summary>
        public bool RomColours { get; init; }
        /// <summary>File offset of each colour (BGR555), -1 for one that isn't in the ROM; null when none are.</summary>
        public int[]? ColourOffsets { get; init; }
        /// <summary>Palette sets: the name of each row.</summary>
        public string[]? RowNames { get; init; }
        /// <summary>The colours are stored with the graphics (a monster sprite's palettes, inside its packed data):
        /// <see cref="L2MonsterSprites.SetColour"/> changes them.</summary>
        public bool FileColours { get; init; }
        public int Rows => Colours.Length / RowSize;

        public uint[] Argb()
        {
            var o = new uint[Pixels.Length];
            for (int i = 0; i < o.Length; i++) o[i] = Transparent0 && Pixels[i] % RowSize == 0 ? 0u : Colours[Pixels[i]];
            return o;
        }
    }

    // ── addresses ───────────────────────────────────────────────────────────

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

    /// <summary>The 24 battle backgrounds as they look.</summary>
    public static readonly string[] BgNames =
    {
        "Desert", "Dark cave with plants", "Water stones", "Ship deck at sea", "Meadow", "White stone temple", "Mossy rock cave",
        "Mountain path", "Tower with windows", "Brick catacomb", "Green hall with window", "Cave, red floor", "Cave, brown floor",
        "Cave with water", "Cave, purple floor", "Cave with waterfall", "Cave, olive floor", "Cave, olive, vines", "Lava cave",
        "Lava cave, rock band", "Dark cave, vines", "Brick tower inside", "Pillared hall", "Green hall",
    };

    /// <summary>Hero sheet sizes (the table holds no length: each runs to the next known data).</summary>
    private static readonly int[] HeroSizes = { 0x1800, 0x1A00, 0x1400, 0x1800, 0x1800, 0x1A00, 0x1A00 };

    // ── the list ────────────────────────────────────────────────────────────

    private static List<PaletteChoice> Rows(string name, int offset, int rows, int rowBytes, int first = 0) =>
        Enumerable.Range(0, rows).Select(r => new PaletteChoice($"{name} {first + r}", offset + r * rowBytes)).ToList();

    private static readonly PaletteChoice Grey = new("Grey levels", -1);

    public static List<Asset> Assets(RomBuffer rom)
    {
        var list = new List<Asset>();
        for (int k = 0; k < BgCount; k++)
            list.Add(new Asset { Group = "Battle backgrounds", Name = $"{k:X2} {BgNames[k]}", Kind = Kind.BattleBackground, Id = k, Note = BgNote(rom, k) });
        var caps = L2Capsules.All(rom);
        for (int n = 0; n < CapsuleCount; n++)
            list.Add(new Asset
            {
                Group = "Capsule monsters", Name = $"{L2Capsules.MonsterName(rom, n / 5)} {n % 5 + 1}: {caps[n].Name}", Kind = Kind.Capsule, Id = n,
                Note = $"48 x 48, packed file {CapsuleFile + n:X3}, colours at $97:{0xD258 + n * 0x20:X4}. Colour 0 is see-through.",
            });
        list.AddRange(MonsterSpriteAssets(rom));
        var spritePals = Rows("Map sprite colours", MapSpritePalette, 8, 0x20);
        for (int h = 0; h < 7; h++)
        {
            int at = L2Rom.SnesToFile((int)rom.ReadUInt24Le(HeroTable + h * 3));
            list.Add(new Asset
            {
                Group = "Hero battle sprites", Name = L2Rom.Heroes[h], Kind = Kind.RomSheet, Id = at, Size = HeroSizes[h], Columns = 8, Bpp = 4,
                Palettes = spritePals, DefaultPalette = 1,
                Note = $"4bpp tiles at {at:X6} (${L2Rom.FileToSnes(at):X6}), 0x{HeroSizes[h]:X} bytes, 8 tiles across as the game copies them. Which colours the game uses isn't traced yet: pick a set below.",
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
        list.Add(new Asset { Group = "Fonts", Name = "Dialogue font (8 x 16)", Kind = Kind.Font8x16, Id = DialogueFont, Size = 0x600, Bpp = 1,
            Note = "96 letters from space (ASCII order), 1 bit per pixel, at $9A:F970." });
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
            var m = MapSpriteOf(rom, n);
            int w = m.FrameWidth * 8, h = m.FrameHeight * 8, p = L2Sprites.PaletteOf(rom, n);
            bool added = L2Sprites.IsNew(n);
            list.Add(new Asset
            {
                Group = "Map sprites", Name = $"{L2Sprites.Name(n)} ({w} x {h}, {m.Frames} frame{(m.Frames == 1 ? "" : "s")})", Kind = Kind.MapSprite, Id = n,
                Palettes = mapPals, DefaultPalette = p > 0 ? 7 + p : m.Row,
                Note = $"Sprite {L2Sprites.Name(n)}{(added ? " (added by Lufia Forge; on a map it is FB, FC or FD)" : "")}: {m.Frames} frame{(m.Frames == 1 ? "" : "s")} of {w} x {h} side by side, {m.FrameWidth * m.FrameHeight * m.Frames} tiles at {m.Tiles:X6} (${L2Rom.FileToSnes(m.Tiles):X6}), " +
                       (p > 0 ? $"colours: new palette {p}" : $"colours: map sprite row {m.Row}") + ". Colour 0 is see-through. Import and painting write the tiles back in place (same size).",
            });
        }
        list.AddRange(PaletteSets(rom));
        return list;
    }

    /// <summary>The monster battle sprites (1 up), named by the monsters that use them.</summary>
    public static List<Asset> MonsterSpriteAssets(RomBuffer rom)
    {
        var list = new List<Asset>();
        var users = L2Battle.Monsters(rom).GroupBy(m => (int)m.Sprite).ToDictionary(g => g.Key, g => g.ToList());
        for (int n = 1; n <= L2MonsterSprites.Count(rom); n++)
        {
            int pals;
            try { pals = L2MonsterSprites.PaletteCount(rom, n); } catch { continue; }
            var (tx, ty, _) = L2MonsterSprites.Size(rom, n);
            var who = users.TryGetValue(n, out var l) ? l : new List<L2Battle.Monster>();
            var names = who.Select(m => m.Name).Distinct().ToList();
            string label = names.Count == 0 ? (L2MonsterSprites.IsNew(n) ? "new" : "no monster") : string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", ..." : "");
            var choices = Enumerable.Range(0, Math.Max(1, pals)).Select(p =>
            {
                var on = who.Where(m => m.Palette == p).Select(m => m.Name).Distinct().ToList();
                return new PaletteChoice($"Palette {p}" + (on.Count > 0 ? ": " + string.Join(", ", on.Take(3)) + (on.Count > 3 ? ", ..." : "") : ""), -1);
            }).ToList();
            var (sx, sy) = L2MonsterSprites.Squares(tx, ty);
            list.Add(new Asset
            {
                Group = "Monster battle sprites", Name = $"{n:X2} {label}", Kind = Kind.MonsterSprite, Id = n, Palettes = choices,
                Note = $"Sprite {n:X2} ({n}), {tx * 8} x {ty * 8}, {sx * sy} squares of 16 x 16, {pals} palette{(pals == 1 ? "" : "s")}, {L2MonsterSprites.Where(rom, n)}. " +
                       (who.Count > 0 ? "Used by " + string.Join(", ", who.Select(m => $"{m.Index:X2} {m.Name} (palette {m.Palette})")) + ". " : "No monster uses it yet: pick it on the Monsters page. ") +
                       "Colour 0 is see-through.",
            });
        }
        return list;
    }

    /// <summary>A background's record: tilemap, tiles, palette.</summary>
    public static (int Map, int Tiles, int Palette) BgRecord(RomBuffer rom, int k) =>
        (rom.ReadByte(BgRecords + k * 4), rom.ReadByte(BgRecords + k * 4 + 1), rom.ReadByte(BgRecords + k * 4 + 2));

    /// <summary>What a background shares with others (an import changes them too).</summary>
    public static string BgNote(RomBuffer rom, int k)
    {
        var (m, t, p) = BgRecord(rom, k);
        var parts = new List<string>();
        string Ids(Func<int, bool> f) => string.Join(", ", Enumerable.Range(0, BgCount).Where(j => j != k && f(j)).Select(j => j.ToString("X2")));
        string sm = Ids(j => BgRecord(rom, j).Map == m), st = Ids(j => BgRecord(rom, j).Tiles == t), sp = Ids(j => BgRecord(rom, j).Palette == p);
        if (sm.Length > 0) parts.Add($"its layout (tilemap) with {sm}");
        if (sp.Length > 0) parts.Add($"its colours with {sp}");
        if (st.Length > 0) parts.Add($"its tiles with {st} (their squares are kept)");
        return $"Tilemap file {BgMapFile + m:X3}, tiles file {BgTileFile + t:X3}, colours {p:X2} at $97:{0xCD58 + p * 0x40:X4}." +
               (parts.Count > 0 ? " Shares " + string.Join("; ", parts) + "." : "");
    }

    // ── tiles and colours ───────────────────────────────────────────────────

    public static uint Argb(int v) => 0xFF000000u | (uint)((v & 31) * 255 / 31) << 16 | (uint)((v >> 5 & 31) * 255 / 31) << 8 | (uint)((v >> 10 & 31) * 255 / 31);

    public static ushort Bgr555(uint argb)
    {
        int r = (int)(argb >> 16 & 255), g = (int)(argb >> 8 & 255), b = (int)(argb & 255);
        return (ushort)((r * 31 + 127) / 255 | (g * 31 + 127) / 255 << 5 | (b * 31 + 127) / 255 << 10);
    }

    private static uint[] Colours(RomBuffer rom, int offset, int n)
    {
        if (offset < 0) return Enumerable.Range(0, n).Select(i => 0xFF000000u | (uint)(i * 255 / Math.Max(1, n - 1)) * 0x010101u).ToArray();
        return Enumerable.Range(0, n).Select(i => Argb(rom.ReadUInt16Le(offset + i * 2))).ToArray();
    }

    private static int TileBytes(int bpp) => bpp * 8;

    private static int[]? Offsets(int offset, int n) => offset < 0 ? null : Enumerable.Range(0, n).Select(i => offset + i * 2).ToArray();

    /// <summary>One 8x8 SNES planar tile (1bpp: 8 bytes, 2bpp: 16, 4bpp: 32) to 64 colour numbers.</summary>
    public static byte[] DecodeTile(byte[] d, int o, int bpp)
    {
        var px = new byte[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int s = 7 - x, v = 0;
                if (bpp == 1) v = d[o + y] >> s & 1;
                else
                    for (int pl = 0; pl < bpp / 2; pl++)
                        v |= (d[o + pl * 16 + y * 2] >> s & 1) << (2 * pl) | (d[o + pl * 16 + y * 2 + 1] >> s & 1) << (2 * pl + 1);
                px[y * 8 + x] = (byte)v;
            }
        return px;
    }

    public static byte[] EncodeTile(byte[] px, int bpp)
    {
        var d = new byte[TileBytes(bpp)];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int v = px[y * 8 + x], s = 7 - x;
                if (bpp == 1) { d[y] |= (byte)((v & 1) << s); continue; }
                for (int pl = 0; pl < bpp / 2; pl++)
                {
                    d[pl * 16 + y * 2] |= (byte)((v >> (2 * pl) & 1) << s);
                    d[pl * 16 + y * 2 + 1] |= (byte)((v >> (2 * pl + 1) & 1) << s);
                }
            }
        return d;
    }

    private static byte[] Flip(byte[] p, bool h, bool v)
    {
        if (!h && !v) return p;
        var q = new byte[64];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) q[y * 8 + x] = p[(v ? 7 - y : y) * 8 + (h ? 7 - x : x)];
        return q;
    }

    // ── tilemap screens (battle backgrounds, title layers) ──────────────────

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

    private static ushort[] BgMap(RomBuffer rom, int m)
    {
        var d = L2Rom.ReadFile(rom, BgMapFile + m);
        var v = new ushort[0x400];
        for (int i = 0; i < v.Length; i++)
        {
            int raw = 2 * i + 1 < d.Length ? d[2 * i] | d[2 * i + 1] << 8 : 0;
            v[i] = i == 0 ? (ushort)raw : (ushort)(v[i - 1] - raw);
        }
        return v;
    }

    /// <summary>The delta coding of a battle background tilemap (the first word as is, then previous minus current).</summary>
    public static byte[] BgMapBytes(ushort[] v)
    {
        var d = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++)
        {
            int raw = i == 0 ? v[0] : (ushort)(v[i - 1] - v[i]);
            d[2 * i] = (byte)raw; d[2 * i + 1] = (byte)(raw >> 8);
        }
        return d;
    }

    private static Screen ScreenOf(RomBuffer rom, Asset a)
    {
        if (a.Kind == Kind.BattleBackground)
        {
            var (m, t, p) = BgRecord(rom, a.Id);
            return new Screen
            {
                Bpp = 4, W = 32, H = 32, TileBase = 0x100, MaxTiles = BgMaxTiles, RowBase = 2, Rows = 2, PaletteOffset = BgPalettes + p * 0x40,
                ReadTiles = () => L2Rom.ReadFile(rom, BgTileFile + t),
                WriteTiles = d => L2Rom.WriteFile(rom, BgTileFile + t, d),
                ReadMap = () => BgMap(rom, m),
                WriteMap = v => L2Rom.WriteFile(rom, BgMapFile + m, BgMapBytes(v)),
                Others = () => Enumerable.Range(0, BgCount).Select(j => BgRecord(rom, j)).Where(r => r.Tiles == t && r.Map != m)
                    .Select(r => r.Map).Distinct().Select(mm => BgMap(rom, mm)).ToList(),
            };
        }
        // title layers: u16 width, u16 height, then the entries
        ushort[] Map(int file, out int w, out int h)
        {
            var d = L2Rom.ReadFile(rom, file);
            w = d[0] | d[1] << 8; h = d[2] | d[3] << 8;
            var v = new ushort[w * h];
            for (int i = 0; i < v.Length; i++) v[i] = (ushort)(d[4 + 2 * i] | d[5 + 2 * i] << 8);
            return v;
        }
        Map(a.MapFile, out int mw, out int mh);
        int tileCount = L2Rom.ReadFile(rom, a.TileFile).Length / TileBytes(a.Bpp);
        return new Screen
        {
            Bpp = a.Bpp, W = mw, H = mh, TileBase = 0, MaxTiles = tileCount, RowBase = 0, Rows = 8, PaletteOffset = TitlePalette,
            ReadTiles = () => L2Rom.ReadFile(rom, a.TileFile),
            WriteTiles = d => L2Rom.WriteFile(rom, a.TileFile, d),
            ReadMap = () => Map(a.MapFile, out _, out _),
            WriteMap = v =>
            {
                var d = L2Rom.ReadFile(rom, a.MapFile);
                for (int i = 0; i < v.Length; i++) { d[4 + 2 * i] = (byte)v[i]; d[5 + 2 * i] = (byte)(v[i] >> 8); }
                L2Rom.WriteFile(rom, a.MapFile, d);
            },
            Others = () => a.OtherMap >= 0 ? new List<ushort[]> { Map(a.OtherMap, out _, out _) } : new(),
        };
    }

    private static Picture DecodeScreen(RomBuffer rom, Screen s)
    {
        var tiles = s.ReadTiles();
        var map = s.ReadMap();
        int rs = s.RowSize, bpt = TileBytes(s.Bpp), n = tiles.Length / bpt;
        int w = s.W * 8, h = s.H * 8;
        var px = new byte[w * h];
        for (int cy = 0; cy < s.H; cy++)
            for (int cx = 0; cx < s.W; cx++)
            {
                var cell = CellOf(map[cy * s.W + cx], tiles, n, s);
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[(cy * 8 + y) * w + cx * 8 + x] = cell[y * 8 + x];
            }
        return new Picture { Width = w, Height = h, Pixels = px, Colours = Colours(rom, s.PaletteOffset, s.Rows * rs), RowSize = rs, RomColours = true,
                             ColourOffsets = Offsets(s.PaletteOffset, s.Rows * rs) };
    }

    /// <summary>The 64 colour numbers a map entry shows.</summary>
    private static byte[] CellOf(ushort e, byte[] tiles, int n, Screen s)
    {
        int t = (e & 0x3FF) - s.TileBase, row = (((e >> 10) & 7) - s.RowBase) % s.Rows;
        if (row < 0) row += s.Rows;
        var p = t >= 0 && t < n ? Flip(DecodeTile(tiles, t * TileBytes(s.Bpp), s.Bpp), (e & 0x4000) != 0, (e & 0x8000) != 0) : new byte[64];
        var o = new byte[64];
        for (int i = 0; i < 64; i++) o[i] = (byte)(row * s.RowSize + p[i]);
        return o;
    }

    private static string Key(byte[] p) => Convert.ToBase64String(p);

    /// <summary>Puts a picture into a tilemap screen: squares that still look the same keep their entry; the others
    /// reuse an identical tile (flipped too) or get a free tile (ones only this map used first, then unused ones, then
    /// new ones at the end). Returns the rows the picture uses and the tile count.</summary>
    private static (HashSet<int> Rows, int Tiles, int NewTiles) EncodeScreen(Screen s, byte[] px)
    {
        var tiles = s.ReadTiles();
        var map = s.ReadMap();
        int rs = s.RowSize, bpt = TileBytes(s.Bpp), n = tiles.Length / bpt, w = s.W * 8;
        var content = new List<byte[]>();
        for (int t = 0; t < n; t++) content.Add(DecodeTile(tiles, t * bpt, s.Bpp));
        var locked = new HashSet<int>();
        foreach (var other in s.Others()) foreach (var e in other) { int t = (e & 0x3FF) - s.TileBase; if (t >= 0 && t < n) locked.Add(t); }
        var mine = new HashSet<int>(map.Select(e => (e & 0x3FF) - s.TileBase).Where(t => t >= 0 && t < n));

        // the squares and their rows
        int cells = s.W * s.H;
        var want = new byte[cells][];
        var rowOf = new int[cells];
        var rowsUsed = new HashSet<int>();
        for (int c = 0; c < cells; c++)
        {
            int cx = c % s.W, cy = c / s.W, row = -1;
            var full = new byte[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int v = px[(cy * 8 + y) * w + cx * 8 + x];
                    if (v >= s.Rows * rs) throw new InvalidOperationException($"The square at ({cx * 8}, {cy * 8}) uses colour {v}; there are {s.Rows * rs}.");
                    full[y * 8 + x] = (byte)v;
                    if (v % rs == 0) continue;
                    if (row >= 0 && row != v / rs)
                        throw new InvalidOperationException($"The 8x8 square at ({cx * 8}, {cy * 8}) mixes colours of rows {row} and {v / rs}; a square can use one row of {rs} colours.");
                    row = v / rs;
                }
            if (row < 0) row = full[0] / rs;
            rowOf[c] = row;
            if (full.Any(v => v % rs != 0)) rowsUsed.Add(row);
            want[c] = full;
        }

        // 1. squares that look the same keep their entry
        var newMap = new ushort[cells];
        var done = new bool[cells];
        var used = new HashSet<int>(locked);
        for (int c = 0; c < cells; c++)
        {
            if (!CellOf(map[c], tiles, n, s).AsSpan().SequenceEqual(want[c])) continue;
            newMap[c] = map[c]; done[c] = true;
            int t = (map[c] & 0x3FF) - s.TileBase;
            if (t >= 0 && t < n) used.Add(t);
        }
        // 2. the rest: an identical tile, else a free one
        var index = new Dictionary<string, int>();
        foreach (int t in used.OrderBy(t => t)) index.TryAdd(Key(content[t]), t);
        var free = new Queue<int>(mine.Where(t => !used.Contains(t)).OrderBy(t => t)
            .Concat(Enumerable.Range(0, n).Where(t => !used.Contains(t) && !mine.Contains(t))));
        int newTiles = 0;
        for (int c = 0; c < cells; c++)
        {
            if (done[c]) continue;
            var p = want[c].Select(v => (byte)(v % rs)).ToArray();
            int tile = -1, flips = 0;
            for (int f = 0; f < 4 && tile < 0; f++)
                if (index.TryGetValue(Key(Flip(p, (f & 1) != 0, (f & 2) != 0)), out int t)) { tile = t; flips = f; }
            if (tile < 0)
            {
                if (free.Count > 0) tile = free.Dequeue();
                else
                {
                    if (content.Count >= s.MaxTiles)
                        throw new InvalidOperationException($"The picture needs more than {s.MaxTiles} different 8x8 squares (flips count as the same square).");
                    tile = content.Count; content.Add(new byte[64]); newTiles++;
                }
                content[tile] = p;
                index[Key(p)] = tile;
                used.Add(tile);
            }
            int prio = map[c] & 0x2000;
            newMap[c] = (ushort)((flips & 2) << 14 | (flips & 1) << 14 | prio | (rowOf[c] + s.RowBase) << 10 | (tile + s.TileBase));
        }
        // write only what changed
        // (a part tile at the end of the file is kept as it was)
        var tileBytes = new byte[Math.Max(content.Count * bpt, tiles.Length)];
        Array.Copy(tiles, tileBytes, tiles.Length);
        for (int t = 0; t < content.Count; t++) EncodeTile(content[t], s.Bpp).CopyTo(tileBytes, t * bpt);
        if (!tileBytes.AsSpan().SequenceEqual(tiles)) s.WriteTiles(tileBytes);
        if (!newMap.AsSpan().SequenceEqual(map)) s.WriteMap(newMap);
        return (rowsUsed, used.Count, newTiles);
    }

    // ── sheets ───────────────────────────────────────────────────────────────

    private static byte[] SheetBytes(RomBuffer rom, Asset a) => a.Kind switch
    {
        Kind.PackedSheet => L2Rom.ReadFile(rom, a.Id),
        _ => rom.ReadBytes(a.Id, a.Size),
    };

    private static (int W, int H, int Tiles) SheetSize(Asset a, int bytes)
    {
        if (a.Kind == Kind.Font8x16) { int g = bytes / 16; return (16 * 8, (g + 15) / 16 * 16, g); }
        int tiles = bytes / TileBytes(a.Bpp), rows = Math.Max(1, (tiles + a.Columns - 1) / a.Columns);
        return (a.Columns * 8, rows * 8, tiles);
    }

    /// <summary>Pixel position of sheet tile <paramref name="t"/> (fonts: glyph t, 8 x 16).</summary>
    private static (int X, int Y) SheetPos(Asset a, int t) =>
        a.Kind == Kind.Font8x16 ? (t % 16 * 8, t / 16 * 16) : (t % a.Columns * 8, t / a.Columns * 8);

    // ── map sprites ─────────────────────────────────────────────────────────

    /// <summary>A map sprite's entry (<see cref="L2Graphics.MapSpriteTable"/>): flags, palette row, the file offset of
    /// its tiles and its frame size in tiles.</summary>
    public sealed record MapSpriteEntry(int Flags, int Row, int Tiles, int FrameWidth, int FrameHeight, int Frames);

    public static MapSpriteEntry MapSpriteOf(RomBuffer rom, int sprite)
    {
        int e = L2Sprites.EntryAt(rom, sprite);
        int flags = rom.ReadByte(e), row = rom.ReadByte(e + 1) & 7;
        int gfx = L2Rom.SnesToFile((int)rom.ReadUInt24Le(e + 2));
        int frames = (flags & 8) != 0 ? 2 : (flags & 0x10) != 0 ? 6 : 1;
        return new MapSpriteEntry(flags, row, gfx, (flags & 2) != 0 ? 4 : 2, (flags & 1) != 0 ? 4 : 2, frames);
    }

    /// <summary>The colours a map sprite can use: the 8 sprite rows, then the new palettes (<see cref="L2Sprites"/>).</summary>
    public static List<PaletteChoice> MapSpritePalettes(RomBuffer rom) =>
        Rows("Map sprite colours", MapSpritePalette, 8, 0x20)
            .Concat(Enumerable.Range(1, L2Sprites.PaletteCount(rom)).Select(p => new PaletteChoice($"New palette {p}", L2Sprites.PaletteAt(rom, p)))).ToList();

    /// <summary>The tile number (from the sprite's first) of tile (x, y) of frame f: each frame's tiles are the upper
    /// halves of its 16-pixel rows, then the lower halves (LEdit's L2MapSprite).</summary>
    private static int MapSpriteTile(MapSpriteEntry m, int f, int x, int y)
    {
        int n = m.FrameWidth * m.FrameHeight;
        return f * n + (y & 1) * (n / 2) + (y >> 1) * m.FrameWidth + x;
    }

    // ── palettes ────────────────────────────────────────────────────────────

    /// <summary>The size of a colour square in a palette set's picture.</summary>
    public const int PaletteCell = 8;

    /// <summary>Writes one colour (BGR555) at <paramref name="offset"/>, keeping the unused top bit as it was. True
    /// when it changed.</summary>
    public static bool SetColour(RomBuffer rom, int offset, int bgr555)
    {
        int old = rom.ReadUInt16Le(offset);
        ushort v = (ushort)(old & 0x8000 | bgr555 & 0x7FFF);
        if (v == old) return false;
        rom.WriteUInt16Le(offset, v);
        return true;
    }

    private static List<PaletteRow> RowsOf(string name, int offset, int rows, int count = 16, int first = 0) =>
        Enumerable.Range(0, rows).Select(r => new PaletteRow($"{name} {first + r}", offset + r * count * 2, count)).ToList();

    /// <summary>The palette sets Lufia Forge knows, each a <see cref="Kind.Palette"/> asset.</summary>
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

        // map palette sets: the map's set byte n (1 up) picks pointer n - 1; a map reads 6 rows (CGRAM rows 2-7) from
        // 0x10 or 0x20 bytes into the set (L2Maps.Palettes)
        const int sets = 0x57;
        var ptrs = Enumerable.Range(0, sets).Select(i => (int)rom.ReadUInt16Le(L2Maps.PaletteSetPointers + i * 2)).ToArray();
        var sorted = ptrs.Distinct().OrderBy(p => p).ToList();
        for (int i = 0; i < sets; i++)
        {
            var maps = Enumerable.Range(0, L2Maps.Count).Where(m => rom.ReadByte(L2Maps.PaletteSetList + m) == i + 1).ToList();
            int k = sorted.IndexOf(ptrs[i]);
            int size = Math.Min(0x100, k + 1 < sorted.Count ? sorted[k + 1] - ptrs[i] : 0x10000 - ptrs[i]);
            int head = maps.Count > 0 && rom.ReadByte(L2Maps.AnimData + rom.ReadByte(L2Maps.AnimIndexList + maps[0]) * 2 + 1) >> 4 != 0 ? 0x20 : 0x10;
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

    // ── decode / import ─────────────────────────────────────────────────────

    /// <summary>The picture of an asset, with colours <paramref name="palette"/> of its choices.</summary>
    public static Picture Decode(RomBuffer rom, Asset a, int palette = -1)
    {
        switch (a.Kind)
        {
            case Kind.BattleBackground:
            case Kind.Title:
                return DecodeScreen(rom, ScreenOf(rom, a));
            case Kind.Capsule:
            {
                var d = L2Rom.ReadFile(rom, CapsuleFile + a.Id);
                var px = new byte[48 * 48];
                for (int b = 0; b < 9; b++)
                    for (int t = 0; t < 4; t++)
                    {
                        if (b * 0x80 + t * 0x20 + 0x20 > d.Length) continue;
                        var p = DecodeTile(d, b * 0x80 + t * 0x20, 4);
                        int ox = b % 3 * 16 + (t & 1) * 8, oy = b / 3 * 16 + (t >> 1) * 8;
                        for (int i = 0; i < 64; i++) px[(oy + i / 8) * 48 + ox + i % 8] = p[i];
                    }
                return new Picture { Width = 48, Height = 48, Pixels = px, Colours = Colours(rom, CapsulePalettes + a.Id * 0x20, 16), RowSize = 16, Transparent0 = true, RomColours = true,
                                     ColourOffsets = Offsets(CapsulePalettes + a.Id * 0x20, 16) };
            }
            case Kind.MonsterSprite:
            {
                var data = L2MonsterSprites.Read(rom, a.Id);
                var (w, h, px) = L2MonsterSprites.Pixels(rom, a.Id, data);
                int pal = palette < 0 || palette >= a.Palettes.Count ? a.DefaultPalette : palette;
                var colours = L2MonsterSprites.Palette(data, pal).Select(Argb).ToArray();
                return new Picture { Width = w, Height = h, Pixels = px, Colours = colours, RowSize = 16, Transparent0 = true, RomColours = true, FileColours = data[0] > 0 };
            }
            case Kind.MapSprite:
            {
                var m = MapSpriteOf(rom, a.Id);
                int w = m.FrameWidth * 8 * m.Frames, h = m.FrameHeight * 8;
                var px = new byte[w * h];
                for (int f = 0; f < m.Frames; f++)
                    for (int y = 0; y < m.FrameHeight; y++)
                        for (int x = 0; x < m.FrameWidth; x++)
                        {
                            int at = m.Tiles + MapSpriteTile(m, f, x, y) * 32;
                            if (at < 0 || at + 32 > rom.Length) continue;
                            var p = DecodeTile(rom.ReadBytes(at, 32), 0, 4);
                            int ox = (f * m.FrameWidth + x) * 8, oy = y * 8;
                            for (int i = 0; i < 64; i++) px[(oy + i / 8) * w + ox + i % 8] = p[i];
                        }
                int off = PaletteOf(a, palette)?.Offset ?? MapSpritePalette + m.Row * 0x20;
                return new Picture { Width = w, Height = h, Pixels = px, Colours = Colours(rom, off, 16), RowSize = 16, Transparent0 = true, RomColours = true,
                                     ColourOffsets = Offsets(off, 16) };
            }
            case Kind.Palette:
            {
                var rows = a.PaletteRows;
                int nr = Math.Max(1, rows.Count), w = 16 * PaletteCell, h = nr * PaletteCell;
                var px = new byte[w * h];
                for (int i = 0; i < px.Length; i++) px[i] = (byte)(i / w / PaletteCell * 16 + i % w / PaletteCell);
                var colours = new uint[nr * 16];
                var offs = new int[colours.Length];
                for (int i = 0; i < offs.Length; i++) { offs[i] = -1; colours[i] = 0xFF101010u; }
                for (int r = 0; r < rows.Count; r++)
                    for (int c = 0; c < Math.Min(16, rows[r].Count); c++)
                    {
                        offs[r * 16 + c] = rows[r].Offset + c * 2;
                        colours[r * 16 + c] = Argb(rom.ReadUInt16Le(rows[r].Offset + c * 2));
                    }
                return new Picture { Width = w, Height = h, Pixels = px, Colours = colours, RowSize = 16, RomColours = true, ColourOffsets = offs,
                                     RowNames = rows.Select(r => r.Name).ToArray() };
            }
            default:
            {
                var d = SheetBytes(rom, a);
                var (w, h, tiles) = SheetSize(a, d.Length);
                var px = new byte[w * h];
                for (int t = 0; t < tiles; t++)
                {
                    var (ox, oy) = SheetPos(a, t);
                    if (a.Kind == Kind.Font8x16)
                    {
                        for (int y = 0; y < 16; y++) for (int x = 0; x < 8; x++) px[(oy + y) * w + ox + x] = (byte)(d[t * 16 + y] >> (7 - x) & 1);
                        continue;
                    }
                    var p = DecodeTile(d, t * TileBytes(a.Bpp), a.Bpp);
                    for (int i = 0; i < 64; i++) px[(oy + i / 8) * w + ox + i % 8] = p[i];
                }
                int n = 1 << a.Bpp;
                var choice = PaletteOf(a, palette);
                uint[] colours = a.Kind == Kind.Font8x16 ? new[] { 0xFF000000u, 0xFFFFFFFFu } : Colours(rom, choice?.Offset ?? -1, n);
                return new Picture { Width = w, Height = h, Pixels = px, Colours = colours, RowSize = n, RomColours = choice is { Offset: >= 0 },
                                     ColourOffsets = a.Kind == Kind.Font8x16 ? null : Offsets(choice?.Offset ?? -1, n) };
            }
        }
    }

    private static PaletteChoice? PaletteOf(Asset a, int palette)
    {
        if (a.Palettes.Count == 0) return null;
        if (palette < 0 || palette >= a.Palettes.Count) palette = a.DefaultPalette;
        return a.Palettes[Math.Clamp(palette, 0, a.Palettes.Count - 1)];
    }

    /// <summary>The colour offset a picture's colours are written to, or -1.</summary>
    private static int ColourOffset(RomBuffer rom, Asset a, int palette) => a.Kind switch
    {
        Kind.BattleBackground => BgPalettes + BgRecord(rom, a.Id).Palette * 0x40,
        Kind.Title => TitlePalette,
        Kind.Capsule => CapsulePalettes + a.Id * 0x20,
        Kind.MapSprite => PaletteOf(a, palette)?.Offset ?? MapSpritePalette + MapSpriteOf(rom, a.Id).Row * 0x20,
        Kind.Font8x16 => -1,
        _ => PaletteOf(a, palette)?.Offset ?? -1,
    };

    /// <summary>
    /// Writes a picture (colour numbers as <see cref="Decode"/> gives them, same size) into the ROM. With
    /// <paramref name="colours"/> the rows of colours the picture uses are written too (colour 0 of each row, the
    /// see-through one, is left as is). Throws InvalidOperationException with a plain message when it can't be done.
    /// Returns what was done.
    /// </summary>
    public static string Import(RomBuffer rom, Asset a, int palette, byte[] pixels, uint[]? colours)
    {
        var before = Decode(rom, a, palette);
        if (pixels.Length != before.Pixels.Length) throw new InvalidOperationException($"The picture must be {before.Width} x {before.Height} pixels.");
        int max = before.Colours.Length;
        int bad = Array.FindIndex(pixels, v => v >= max);
        if (bad >= 0) throw new InvalidOperationException($"The pixel at ({bad % before.Width}, {bad / before.Width}) uses colour {pixels[bad]}; there are {max} colours.");
        HashSet<int> rows;
        string what;
        switch (a.Kind)
        {
            case Kind.BattleBackground:
            case Kind.Title:
            {
                var (r, used, added) = EncodeScreen(ScreenOf(rom, a), pixels);
                rows = r;
                what = $"{used} tiles in use" + (added > 0 ? $", {added} added" : "");
                break;
            }
            case Kind.Capsule:
            {
                var d = new byte[9 * 0x80];
                for (int b = 0; b < 9; b++)
                    for (int t = 0; t < 4; t++)
                    {
                        int ox = b % 3 * 16 + (t & 1) * 8, oy = b / 3 * 16 + (t >> 1) * 8;
                        var p = new byte[64];
                        for (int i = 0; i < 64; i++) p[i] = pixels[(oy + i / 8) * 48 + ox + i % 8];
                        EncodeTile(p, 4).CopyTo(d, b * 0x80 + t * 0x20);
                    }
                if (!L2Rom.ReadFile(rom, CapsuleFile + a.Id).AsSpan().SequenceEqual(d)) L2Rom.WriteFile(rom, CapsuleFile + a.Id, d);
                rows = new HashSet<int> { 0 };
                what = $"packed file {CapsuleFile + a.Id:X3}";
                break;
            }
            case Kind.MapSprite:
            {
                var m = MapSpriteOf(rom, a.Id);
                int n = m.FrameWidth * m.FrameHeight * m.Frames, w = before.Width;
                if (m.Tiles < 0 || m.Tiles + n * 32 > rom.Length) throw new InvalidOperationException("This sprite's tiles are outside the ROM.");
                var old = rom.ReadBytes(m.Tiles, n * 32);
                var d = (byte[])old.Clone();
                for (int f = 0; f < m.Frames; f++)
                    for (int y = 0; y < m.FrameHeight; y++)
                        for (int x = 0; x < m.FrameWidth; x++)
                        {
                            int ox = (f * m.FrameWidth + x) * 8, oy = y * 8;
                            var p = new byte[64];
                            for (int i = 0; i < 64; i++) p[i] = pixels[(oy + i / 8) * w + ox + i % 8];
                            EncodeTile(p, 4).CopyTo(d, MapSpriteTile(m, f, x, y) * 32);
                        }
                if (!d.AsSpan().SequenceEqual(old)) rom.WriteBytes(m.Tiles, d);
                rows = new HashSet<int> { 0 };
                what = $"{n} tiles at {m.Tiles:X6}";
                break;
            }
            case Kind.MonsterSprite:
            {
                int pal = palette < 0 || palette >= a.Palettes.Count ? a.DefaultPalette : palette;
                int changed = L2MonsterSprites.WritePicture(rom, a.Id, pixels, pal, colours?.Select(c => (int)Bgr555(c)).ToArray());
                return L2MonsterSprites.Where(rom, a.Id) + (colours == null ? "" : $"; {changed} colour{(changed == 1 ? "" : "s")} of palette {pal} changed");
            }
            case Kind.Palette:
            {
                if (colours == null) throw new InvalidOperationException("A palette is only colours: turn on \"Take the colours from the picture\" to bring them in.");
                var offs = before.ColourOffsets!;
                int changed = 0;
                for (int i = 0; i < offs.Length && i < colours.Length; i++)
                    if (offs[i] >= 0 && SetColour(rom, offs[i], Bgr555(colours[i]))) changed++;
                return $"{changed} colour{(changed == 1 ? "" : "s")} changed";
            }
            default:
            {
                var old = SheetBytes(rom, a);
                var d = (byte[])old.Clone();
                var (w, _, tiles) = SheetSize(a, d.Length);
                for (int t = 0; t < tiles; t++)
                {
                    var (ox, oy) = SheetPos(a, t);
                    if (a.Kind == Kind.Font8x16)
                    {
                        for (int y = 0; y < 16; y++)
                        {
                            int b = 0;
                            for (int x = 0; x < 8; x++) b |= (pixels[(oy + y) * w + ox + x] & 1) << (7 - x);
                            d[t * 16 + y] = (byte)b;
                        }
                        continue;
                    }
                    var p = new byte[64];
                    for (int i = 0; i < 64; i++) p[i] = pixels[(oy + i / 8) * w + ox + i % 8];
                    EncodeTile(p, a.Bpp).CopyTo(d, t * TileBytes(a.Bpp));
                }
                if (!d.AsSpan().SequenceEqual(old))
                {
                    if (a.Kind == Kind.PackedSheet) L2Rom.WriteFile(rom, a.Id, d);
                    else rom.WriteBytes(a.Id, d);
                }
                rows = new HashSet<int> { 0 };
                what = a.Kind == Kind.PackedSheet ? $"packed file {a.Id:X3}" : $"{d.Length} bytes at {a.Id:X6}";
                break;
            }
        }
        if (colours == null) return what;
        int at = ColourOffset(rom, a, palette);
        if (at < 0) return what + "; these graphics have no colours of their own in the ROM, so only the colour numbers were taken";
        int rs = before.RowSize, written = 0;
        foreach (int r in rows.OrderBy(r => r))
            for (int c = 1; c < rs; c++)
            {
                int i = r * rs + c;
                if (i >= colours.Length || i >= max) continue;
                ushort v = Bgr555(colours[i]);
                if (rom.ReadUInt16Le(at + i * 2) != v) { rom.WriteUInt16Le(at + i * 2, v); written++; }
            }
        return what + $"; {written} colour{(written == 1 ? "" : "s")} changed";
    }

    /// <summary>Colour numbers for a full-colour picture: each 8x8 square takes the row of colours that fits it best,
    /// each pixel the nearest colour of that row (mostly see-through pixels become colour 0).</summary>
    public static byte[] MatchColours(uint[] argb, Picture pic)
    {
        int w = pic.Width, rs = pic.RowSize, rows = pic.Rows;
        var o = new byte[argb.Length];
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
                            uint p = argb[y * w + x];
                            if (p >> 24 < 128) continue;
                            long best = long.MaxValue;
                            for (int c = pic.Transparent0 ? 1 : 0; c < rs; c++) best = Math.Min(best, Dist(p, pic.Colours[r * rs + c]));
                            err += best;
                        }
                    if (err < bestErr) { bestErr = err; bestRow = r; }
                }
                for (int y = cy; y < Math.Min(cy + cellH, pic.Height); y++)
                    for (int x = cx; x < Math.Min(cx + 8, w); x++)
                    {
                        uint p = argb[y * w + x];
                        if (p >> 24 < 128) { o[y * w + x] = (byte)(bestRow * rs); continue; }
                        long best = long.MaxValue;
                        for (int c = pic.Transparent0 ? 1 : 0; c < rs; c++)
                        {
                            long e = Dist(p, pic.Colours[bestRow * rs + c]);
                            if (e < best) { best = e; o[y * w + x] = (byte)(bestRow * rs + c); }
                        }
                    }
            }
        return o;
    }

    /// <summary>Capsule monster sprite <paramref name="index"/> (0-34: capsule * 5 + form) as ARGB, colour 0 see-through.</summary>
    public static (int Width, int Height, uint[] Pixels) CapsuleSprite(RomBuffer rom, int index)
    {
        var pic = Decode(rom, new Asset { Kind = Kind.Capsule, Id = Math.Clamp(index, 0, CapsuleCount - 1) });
        return (pic.Width, pic.Height, pic.Argb());
    }
}
