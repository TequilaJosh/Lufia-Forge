namespace LufiaForge.Core.Maps;

/// <summary>An exit/door rectangle (object section A, 12 bytes).</summary>
public sealed class MapExit
{
    public int Index { get; set; }
    /// <summary>Arrival point number in the destination map (FF = unused).</summary>
    public int Arrival { get; set; }
    /// <summary>Raw byte 1 (high nibble = transition type).</summary>
    public int Flags { get; set; }
    /// <summary>Destination map number; 0 = another spot on the same map.</summary>
    public int DestMap { get; set; }
    public int Byte3 { get; set; }
    /// <summary>Rectangle in metatiles; X2/Y2 are exclusive.</summary>
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public bool IsUnused => Arrival == 0xFF || (X2 <= X1 && Y2 <= Y1);
}

/// <summary>An arrival point (object section B, 6 bytes): where the party appears when arriving.</summary>
public sealed class MapArrival
{
    public int Index { get; set; }
    public int Flags { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// An object spot (object section E, 12 bytes: 4 runtime bytes + rectangle). The map's setup script
/// fills in what it is when the map loads: a treasure chest, a hidden item, a door or a trigger
/// (see <see cref="MapSetupScript"/>).
/// </summary>
public sealed class MapSpot
{
    public int Index { get; init; }
    public int X1 { get; init; }
    public int Y1 { get; init; }
    /// <summary>Exclusive.</summary>
    public int X2 { get; init; }
    /// <summary>Exclusive.</summary>
    public int Y2 { get; init; }
}

/// <summary>
/// A movement path (object section G): 6-byte header [flags, step count, start X (u16), start Y (u16)] then 3-byte steps.
/// Run by event commands 14/15 ("character walks path n", 1-based). Decoded from $01:9D62-$9F41.
/// </summary>
public sealed class MovePath
{
    /// <summary>Header flags: bit 0 = the event doesn't wait, bit 1 = repeat forever, bits 2-3 = camera follows,
    /// bit 4 = walk to the start instead of appearing there.</summary>
    public int Flags { get; set; }
    public int StartX { get; set; }
    public int StartY { get; set; }
    public List<MoveStep> Steps { get; } = new();

    public bool NoWait { get => (Flags & 1) != 0; set => Flags = value ? Flags | 1 : Flags & ~1; }
    public bool Repeat { get => (Flags & 2) != 0; set => Flags = value ? Flags | 2 : Flags & ~2; }
    public bool CameraFollows { get => (Flags & 0x0C) != 0; set => Flags = value ? Flags | 0x04 : Flags & ~0x0C; }
    public bool WalkToStart { get => (Flags & 0x10) != 0; set => Flags = value ? Flags | 0x10 : Flags & ~0x10; }

    /// <summary>Tiles visited: the start, then the end of every step.</summary>
    public List<(int X, int Y)> Points()
    {
        var list = new List<(int, int)> { (StartX, StartY) };
        int x = StartX, y = StartY;
        foreach (var st in Steps)
        {
            (int dx, int dy) = st.Direction switch { 0 => (1, 0), 1 => (-1, 0), 2 => (0, 1), _ => (0, -1) };
            x += dx * st.Tiles; y += dy * st.Tiles;
            list.Add((x, y));
        }
        return list;
    }
}

/// <summary>One step of a path: walk <see cref="Tiles"/> tiles in <see cref="Direction"/>.</summary>
public sealed class MoveStep
{
    /// <summary>Byte 0: bit 0 = layer, bits 2-4 = facing (0 = face the way it walks, 1-4 = face right/left/down/up, 5 = don't turn),
    /// bits 5-7 = speed (index into the speed table $01:E237).</summary>
    public int Mode { get; set; }
    /// <summary>Byte 1 (bits 0-1 = direction: 0 right, 1 left, 2 down, 3 up; other bits kept).</summary>
    public int DirByte { get; set; }
    public int Tiles { get; set; }

    public int Direction { get => DirByte & 3; set => DirByte = (DirByte & ~3) | (value & 3); }
    public int Speed { get => (Mode >> 5) & 7; set => Mode = (Mode & 0x1F) | ((value & 7) << 5); }
    public int Facing { get => (Mode >> 2) & 7; set => Mode = (Mode & ~0x1C) | ((value & 7) << 2); }
    public bool UpperLayer { get => (Mode & 1) != 0; set => Mode = value ? Mode | 1 : Mode & ~1; }
}

/// <summary>An NPC (object section C, 14 bytes). NPC n runs event n of the map's script block.</summary>
public sealed class MapNpc
{
    public int Index { get; init; }
    public int Sprite { get; set; }
    public int Flags { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int BoxX1 { get; set; }
    public int BoxY1 { get; set; }
    public int BoxX2 { get; set; }
    public int BoxY2 { get; set; }
    public bool IsUnused => Sprite == 0xFF;
}

/// <summary>
/// One Lufia 1 map, parsed from its compressed map-data resource. Formats are documented in
/// research-script-engine.md ("Map data format", "World map").
///
/// Town/dungeon maps store a Width*Height layer of u16 metatile numbers. The world map (map 01) stores
/// 240 block records at 0x40, a chunk table (4 block bytes per 2x2 chunk: TL TR BL BR) after them, and a
/// (Width/2)*(Height/2) u16 chunk grid; <see cref="Layer"/> is expanded to blocks for editing and turned
/// back into chunks (reusing existing ones, adding new ones) when saving.
///
/// Every map ends with an object block: 8 counts, 8 u16 section offsets, an unknown prefix, then sections
/// A exits (12 bytes), B arrival points (6), C characters (14), D (12), E spots (12), F (9), G routes and H
/// (variable). Exits and arrival points can be added and removed; the block is rebuilt and every other
/// byte is kept exactly.
/// </summary>
public sealed class LufiaMap
{
    // ── ROM tables ─────────────────────────────────────────────────────────
    public const int MapDataSelectTable = 0xE19B;   // map data resource = 0xB2 + table[map]
    public const int TilesSelectTable   = 0xE134;   // tile graphics resource = 0xB1 + table[tileset]
    public const int BlocksSelectTable  = 0xE140;   // metatile resource = 0xB1 + table[tileset]
    public const int MapScriptTable     = 0x18200;  // 5 bytes per map: script block offset (u24), ?, event base
    public const int SharedPaletteRows  = 0x11300;  // palette rows 0-1
    public const int MapPaletteTable    = 0x17040;  // rows 2-7: table + (paletteIndex + 1) * 0xC0
    public const int WorldMap           = 0x01;
    public const int CompositeTable     = 0xE14B;   // per tileset: LoROM pointer to (ground, overlay) word pairs

    // World map graphics (not named in its header; found by matching the game's VRAM/CGRAM)
    public const int WorldTilesResource = 0x124;
    public const int WorldPaletteIndex  = 17;
    private const int WorldBlocksOffset = 0x40;

    public int MapId { get; }
    public int ResourceId { get; }
    public int CompressedOffset { get; }
    public int CompressedSize { get; }
    public byte[] Original { get; }

    public bool IsWorld { get; }
    public int Tileset { get; }
    public int PaletteIndex { get; }
    public int Width { get; }
    public int Height { get; }
    public int LayerOffset { get; }
    public int ObjectsOffset { get; }

    /// <summary>World map: the 9-byte block records stored in the map data.</summary>
    public byte[] WorldBlocks { get; } = Array.Empty<byte>();
    private readonly int _chunkTable, _chunkCount;
    private readonly ushort[] _grid = Array.Empty<ushort>();

    /// <summary>
    /// First "composite" metatile, or -1 if this map has none. When a map loads, the game ($01:BEDF)
    /// replaces every metatile at or above header word 0x1E with a ground block (BG2) and puts an
    /// overlay block (BG1: tree tops, roofs, bridge rails) on a second layer, using the tileset's
    /// composite table. Only maps with header 0x10 bit 1 set, 0x11 bit 7 clear and 0x23 zero do this.
    /// </summary>
    public int CompositeThreshold { get; }

    /// <summary>Metatile layer, Width*Height entries (low 10 bits = metatile number).</summary>
    public ushort[] Layer { get; }

    /// <summary>
    /// Second metatile layer drawn on BG2 behind the map (the intro's clouds; header 0x16/0x18 = its size,
    /// 0x2C = its offset), or null. Uses the same tileset as <see cref="Layer"/>. Read-only: saving keeps it as loaded.
    /// </summary>
    public ushort[]? Layer2 { get; }
    public int Layer2Width { get; }
    public int Layer2Height { get; }
    /// <summary>Header byte 0x23: how the second layer behaves (2 = drifts diagonally, the intro's clouds).</summary>
    public int Layer2Mode { get; }

    public List<MapExit>    Exits    { get; } = new();
    public List<MapArrival> Arrivals { get; } = new();
    public List<MapNpc>     Npcs     { get; } = new();
    public List<MapSpot>    Spots    { get; } = new();
    /// <summary>Movement paths (section G); path n of commands 14/15 is Paths[n - 1].</summary>
    public List<MovePath>   Paths    { get; } = new();

    /// <summary>Counts and offsets of the 8 object sections (relative to the object block), as loaded.</summary>
    public int[] SectionCounts  { get; } = new int[8];
    public int[] SectionOffsets { get; } = new int[8];

    private static readonly int[] SectionSizes = { 12, 6, 14, 12, 12, 9 };

    private LufiaMap(int mapId, int resourceId, int offset, int compressedSize, byte[] data)
    {
        MapId = mapId;
        ResourceId = resourceId;
        CompressedOffset = offset;
        CompressedSize = compressedSize;
        Original = data;
        IsWorld = mapId == WorldMap;

        Width         = U16(data, 0x12);
        Height        = U16(data, 0x14);
        LayerOffset   = (int)U32(data, 0x28);
        ObjectsOffset = (int)U32(data, 0x30);
        Layer = new ushort[Width * Height];

        if (IsWorld)
        {
            Tileset = -1;
            PaletteIndex = WorldPaletteIndex;
            CompositeThreshold = -1;
            int blocks = U16(data, 0x1E);
            WorldBlocks = data.AsSpan(WorldBlocksOffset, blocks * 9).ToArray();
            _chunkTable = WorldBlocksOffset + blocks * 9;
            _chunkCount = (LayerOffset - _chunkTable) / 4;
            int gw = Width / 2, gh = Height / 2;
            _grid = new ushort[gw * gh];
            for (int cy = 0; cy < gh; cy++)
                for (int cx = 0; cx < gw; cx++)
                {
                    ushort g = (ushort)U16(data, LayerOffset + (cy * gw + cx) * 2);
                    _grid[cy * gw + cx] = g;
                    int chunk = _chunkTable + (g & 0x0FFF) * 4;
                    for (int q = 0; q < 4; q++)
                        Layer[(cy * 2 + (q >> 1)) * Width + cx * 2 + (q & 1)] = data[chunk + q];
                }
        }
        else
        {
            Tileset       = data[0x08];
            PaletteIndex  = data[0x09];
            CompositeThreshold = (data[0x10] & 0x02) != 0 && (data[0x11] & 0x80) == 0 && data[0x23] == 0
                ? U16(data, 0x1E) : -1;
            for (int i = 0; i < Layer.Length; i++)
                Layer[i] = (ushort)U16(data, LayerOffset + i * 2);
            int l2 = (int)U32(data, 0x2C), w2 = U16(data, 0x16), h2 = U16(data, 0x18);
            if (l2 != LayerOffset && w2 > 0 && h2 > 0 && l2 >= LayerOffset + Layer.Length * 2 && l2 + w2 * h2 * 2 <= ObjectsOffset)
            {
                Layer2Width = w2; Layer2Height = h2; Layer2Mode = data[0x23];
                Layer2 = new ushort[w2 * h2];
                for (int i = 0; i < Layer2.Length; i++) Layer2[i] = (ushort)U16(data, l2 + i * 2);
            }
        }

        ParseObjects(data);
    }

    // ── Loading ────────────────────────────────────────────────────────────

    public static int MapDataResource(RomBuffer rom, int mapId) => 0xB2 + rom.ReadByte(MapDataSelectTable + mapId);

    /// <summary>True when the map's data starts with its own number in decimal (how the original tools labelled it).</summary>
    public static bool IsValidMap(RomBuffer rom, int mapId, out byte[]? data, out int compressedSize)
    {
        data = null;
        compressedSize = 0;
        try
        {
            int res = MapDataResource(rom, mapId);
            int off = LufiaCompression.ResourceOffset(rom, res);
            if (off < 0x8000 || off >= rom.Length - 4) return false;
            data = LufiaCompression.Decompress(rom, off, out compressedSize);
            if (data.Length < 0x40) return false;
            string tag = System.Text.Encoding.ASCII.GetString(data, 0, 3);
            return int.TryParse(tag, out int n) && n == mapId;
        }
        catch
        {
            return false;
        }
    }

    public static LufiaMap Load(RomBuffer rom, int mapId)
    {
        int res = MapDataResource(rom, mapId);
        int off = LufiaCompression.ResourceOffset(rom, res);
        var data = LufiaCompression.Decompress(rom, off, out int size);
        return new LufiaMap(mapId, res, off, size, data);
    }

    /// <summary>Event base number for door/trigger events of this map (script table byte 4).</summary>
    public static int EventBase(RomBuffer rom, int mapId) => rom.ReadByte(MapScriptTable + mapId * 5 + 4);

    /// <summary>File offset of the script for event <paramref name="ev"/>, or -1 if the event is unused.</summary>
    public static int EventScriptOffset(RomBuffer rom, int mapId, int ev)
    {
        int p = MapScriptTable + mapId * 5;
        int block = 0x18000 + (rom.ReadByte(p) | (rom.ReadByte(p + 1) << 8) | (rom.ReadByte(p + 2) << 16));
        if (block + ev * 2 + 1 >= rom.Length) return -1;
        int rel = rom.ReadUInt16Le(block + ev * 2);
        return rel == 0xFFFF ? -1 : block + rel;
    }

    // ── Objects ────────────────────────────────────────────────────────────

    private void ParseObjects(byte[] d)
    {
        int ob = ObjectsOffset;
        if (ob <= 0 || ob + 24 > d.Length) return;
        for (int i = 0; i < 8; i++)
        {
            SectionCounts[i]  = d[ob + i];
            SectionOffsets[i] = U16(d, ob + 8 + i * 2);
        }

        Exits.AddRange(ParseExits(d, ob));

        int b = ob + SectionOffsets[1];
        for (int i = 0; i < SectionCounts[1]; i++, b += 6)
            Arrivals.Add(new MapArrival { Index = i, Flags = U16(d, b), X = U16(d, b + 2), Y = U16(d, b + 4) });

        int c = ob + SectionOffsets[2];
        for (int i = 0; i < SectionCounts[2]; i++, c += 14)
            Npcs.Add(new MapNpc
            {
                Index = i, Sprite = d[c], Flags = d[c + 1], X = U16(d, c + 2), Y = U16(d, c + 4),
                BoxX1 = U16(d, c + 6), BoxY1 = U16(d, c + 8), BoxX2 = U16(d, c + 10), BoxY2 = U16(d, c + 12),
            });

        int e = ob + SectionOffsets[4];
        for (int i = 0; i < SectionCounts[4] && e + 12 <= d.Length; i++, e += 12)
            Spots.Add(new MapSpot { Index = i, X1 = U16(d, e + 4), Y1 = U16(d, e + 6), X2 = U16(d, e + 8), Y2 = U16(d, e + 10) });

        int g = ob + SectionOffsets[6];
        for (int i = 0; i < SectionCounts[6] && g + 6 <= d.Length; i++)
        {
            var path = new MovePath { Flags = d[g], StartX = U16(d, g + 2), StartY = U16(d, g + 4) };
            int steps = d[g + 1];
            g += 6;
            for (int k = 0; k < steps && g + 3 <= d.Length; k++, g += 3)
                path.Steps.Add(new MoveStep { Mode = d[g], DirByte = d[g + 1], Tiles = d[g + 2] });
            Paths.Add(path);
        }
    }

    /// <summary>Exit records (section A) of an object block at <paramref name="ob"/>.</summary>
    private static List<MapExit> ParseExits(byte[] d, int ob)
    {
        var list = new List<MapExit>();
        if (ob <= 0 || ob + 24 > d.Length) return list;
        int a = ob + U16(d, ob + 8);
        for (int i = 0; i < d[ob] && a + 12 <= d.Length; i++, a += 12)
            list.Add(new MapExit
            {
                Index = i, Arrival = d[a], Flags = d[a + 1], DestMap = d[a + 2], Byte3 = d[a + 3],
                X1 = U16(d, a + 4), Y1 = U16(d, a + 6), X2 = U16(d, a + 8), Y2 = U16(d, a + 10),
            });
        return list;
    }

    /// <summary>The exits of any map, read straight from the ROM.</summary>
    public static List<MapExit> ReadExits(RomBuffer rom, int mapId)
    {
        int off = LufiaCompression.ResourceOffset(rom, MapDataResource(rom, mapId));
        var d = LufiaCompression.Decompress(rom, off, out _);
        return d.Length < 0x34 ? new List<MapExit>() : ParseExits(d, (int)U32(d, 0x30));
    }

    // ── Adding / removing exits and arrival points ─────────────────────────

    /// <summary>Add an exit (rectangle in blocks, X2/Y2 exclusive) leading to <paramref name="destMap"/>.</summary>
    public MapExit AddExit(int x1, int y1, int x2, int y2, int destMap, int arrival)
    {
        // copy the transition type of an existing exit so the new one behaves like a normal door/edge
        var template = Exits.FirstOrDefault(e => !e.IsUnused);
        var exit = new MapExit
        {
            Index = Exits.Count, Arrival = arrival, DestMap = destMap,
            Flags = template?.Flags ?? 0x00, Byte3 = template?.Byte3 ?? 0x00,
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        };
        Exits.Add(exit);
        return exit;
    }

    public void RemoveExit(MapExit exit)
    {
        Exits.Remove(exit);
        for (int i = 0; i < Exits.Count; i++) Exits[i].Index = i;
    }

    /// <summary>Add an arrival point. Its number is the next free index (existing numbers never change).</summary>
    public MapArrival AddArrival(int x, int y)
    {
        var template = Arrivals.FirstOrDefault();
        var a = new MapArrival { Index = Arrivals.Count, X = x, Y = y, Flags = template?.Flags ?? 0 };
        Arrivals.Add(a);
        return a;
    }

    /// <summary>Only the last arrival point can be removed, so the numbers other exits use stay valid.</summary>
    public bool CanRemoveArrival(MapArrival a) => Arrivals.Count > 0 && Arrivals[^1] == a;

    public void RemoveArrival(MapArrival a)
    {
        if (CanRemoveArrival(a)) Arrivals.Remove(a);
    }

    public int OriginalArrivalCount => SectionCounts[1];
    public int OriginalExitCount => SectionCounts[0];

    // ── Saving ─────────────────────────────────────────────────────────────

    /// <summary>Unpacked map data with all edits applied; unknown bytes are kept exactly.</summary>
    public byte[] BuildData()
    {
        var d = (byte[])Original.Clone();
        int shiftFrom = int.MaxValue, delta = 0;   // byte range moved by a grown world chunk table

        if (IsWorld)
        {
            var (chunks, grid) = BuildWorldChunks();
            int gw = Width / 2;
            var body = new List<byte>(d.AsSpan(0, _chunkTable).ToArray());
            body.AddRange(chunks);
            foreach (var g in grid) { body.Add((byte)g); body.Add((byte)(g >> 8)); }
            int gridEnd = LayerOffset + _grid.Length * 2;
            body.AddRange(d.AsSpan(gridEnd, d.Length - gridEnd).ToArray());
            delta = chunks.Count - _chunkCount * 4;
            shiftFrom = LayerOffset;
            d = body.ToArray();
            // header section offsets after the chunk table move along
            foreach (int h in new[] { 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38 })
            {
                uint v = U32(Original, h);
                if (v >= shiftFrom && v <= Original.Length) PutU32(d, h, (uint)(v + delta));
            }
        }
        else
        {
            for (int i = 0; i < Layer.Length; i++) Put16(d, LayerOffset + i * 2, Layer[i]);
        }

        int ob = ObjectsOffset + (ObjectsOffset >= shiftFrom ? delta : 0);
        var block = BuildObjectBlock(d, ob);
        var result = new byte[ob + block.Length];
        Array.Copy(d, result, ob);
        Array.Copy(block, 0, result, ob, block.Length);

        // header field 0x24 holds the total length on town maps
        if (U32(Original, 0x24) == Original.Length) PutU32(result, 0x24, (uint)result.Length);
        return result;
    }

    /// <summary>World map: chunk table bytes and grid for the current layer (existing chunks reused).</summary>
    private (List<byte> Chunks, ushort[] Grid) BuildWorldChunks()
    {
        var chunks = new List<byte>(Original.AsSpan(_chunkTable, _chunkCount * 4).ToArray());
        var lookup = new Dictionary<int, int>();
        for (int i = 0; i < _chunkCount; i++)
        {
            int key = chunks[i * 4] | (chunks[i * 4 + 1] << 8) | (chunks[i * 4 + 2] << 16) | (chunks[i * 4 + 3] << 24);
            lookup.TryAdd(key, i);
        }
        int gw = Width / 2, gh = Height / 2;
        var grid = new ushort[_grid.Length];
        for (int cy = 0; cy < gh; cy++)
            for (int cx = 0; cx < gw; cx++)
            {
                int i = cy * gw + cx;
                byte b0 = (byte)Layer[(cy * 2) * Width + cx * 2], b1 = (byte)Layer[(cy * 2) * Width + cx * 2 + 1];
                byte b2 = (byte)Layer[(cy * 2 + 1) * Width + cx * 2], b3 = (byte)Layer[(cy * 2 + 1) * Width + cx * 2 + 1];
                int key = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
                int orig = _grid[i] & 0x0FFF;
                int idx;
                if (orig < _chunkCount && chunks[orig * 4] == b0 && chunks[orig * 4 + 1] == b1 &&
                    chunks[orig * 4 + 2] == b2 && chunks[orig * 4 + 3] == b3) idx = orig;   // unchanged
                else if (!lookup.TryGetValue(key, out idx))
                {
                    idx = chunks.Count / 4;
                    if (idx > 0x0FFF) throw new InvalidOperationException("The world map has run out of chunk numbers (4096).");
                    chunks.AddRange(new[] { b0, b1, b2, b3 });
                    lookup[key] = idx;
                }
                grid[i] = (ushort)((_grid[i] & 0xF000) | idx);
            }
        return (chunks, grid);
    }

    /// <summary>Object block with the current exits, arrival points and characters; other sections copied.</summary>
    private byte[] BuildObjectBlock(byte[] src, int srcOb)
    {
        var o = Original;
        int ob = ObjectsOffset;
        int blockLen = o.Length - ob;
        var raw = new byte[8][];
        for (int i = 0; i < 8; i++)
        {
            int start = SectionOffsets[i];
            int end = i < 7 ? SectionOffsets[i + 1] : blockLen;
            raw[i] = o.AsSpan(ob + start, Math.Max(0, end - start)).ToArray();
        }
        var prefix = o.AsSpan(ob + 24, Math.Max(0, SectionOffsets[0] - 24)).ToArray();

        var a = new List<byte>();
        foreach (var e in Exits)
        {
            a.Add((byte)e.Arrival); a.Add((byte)e.Flags); a.Add((byte)e.DestMap); a.Add((byte)e.Byte3);
            Add16(a, e.X1); Add16(a, e.Y1); Add16(a, e.X2); Add16(a, e.Y2);
        }
        var b = new List<byte>();
        foreach (var r in Arrivals) { Add16(b, r.Flags); Add16(b, r.X); Add16(b, r.Y); }
        var c = new List<byte>(raw[2]);
        foreach (var n in Npcs)
        {
            int p = n.Index * 14;
            if (p + 14 > c.Count) continue;
            c[p] = (byte)n.Sprite; c[p + 1] = (byte)n.Flags;
            Set16(c, p + 2, n.X); Set16(c, p + 4, n.Y);
            Set16(c, p + 6, n.BoxX1); Set16(c, p + 8, n.BoxY1); Set16(c, p + 10, n.BoxX2); Set16(c, p + 12, n.BoxY2);
        }

        var g = new List<byte>();
        foreach (var path in Paths)
        {
            if (path.Steps.Count > 255) throw new InvalidOperationException("A path can have at most 255 steps.");
            g.Add((byte)path.Flags); g.Add((byte)path.Steps.Count); Add16(g, path.StartX); Add16(g, path.StartY);
            foreach (var st in path.Steps) { g.Add((byte)st.Mode); g.Add((byte)st.DirByte); g.Add((byte)st.Tiles); }
        }
        var sections = new[] { a.ToArray(), b.ToArray(), c.ToArray(), raw[3], raw[4], raw[5], g.ToArray(), raw[7] };
        var counts = new[] { Exits.Count, Arrivals.Count, SectionCounts[2], SectionCounts[3], SectionCounts[4],
                             SectionCounts[5], Paths.Count, SectionCounts[7] };
        if (Paths.Count > 255) throw new InvalidOperationException("A map can have at most 255 paths.");
        if (counts[0] > 255 || counts[1] > 255) throw new InvalidOperationException("A map can have at most 255 exits and 255 arrival points.");

        var block = new List<byte>();
        foreach (var n in counts) block.Add((byte)n);
        int pos = 24 + prefix.Length;
        foreach (var s in sections) { Add16(block, pos); pos += s.Length; }
        block.AddRange(prefix);
        foreach (var s in sections) block.AddRange(s);
        return block.ToArray();
    }

    public ushort GetTile(int x, int y) => Layer[y * Width + x];
    public void SetTile(int x, int y, int metatile) =>
        Layer[y * Width + x] = IsWorld
            ? (ushort)(metatile & 0xFF)
            : (ushort)((Layer[y * Width + x] & 0xFC00) | (metatile & 0x3FF));

    private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
    private static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    private static void PutU32(byte[] d, int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
    private static void Put16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
    private static void Add16(List<byte> l, int v) { l.Add((byte)v); l.Add((byte)(v >> 8)); }
    private static void Set16(List<byte> l, int o, int v) { l[o] = (byte)v; l[o + 1] = (byte)(v >> 8); }
}
