namespace LufiaForge.Core.Maps;

// an exit/door rectangle (object section A, 12 bytes)
public sealed class MapExit
{
    public int Index { get; set; }
    // arrival point number in the destination map (FF = unused)
    public int Arrival { get; set; }
    // raw byte 1 (high nibble = transition type)
    public int Flags { get; set; }
    // destination map number, 0 = another spot on the same map
    public int DestMap { get; set; }
    public int Byte3 { get; set; }
    // rectangle in metatiles, X2/Y2 are exclusive
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public bool IsUnused => Arrival == 0xFF || (X2 <= X1 && Y2 <= Y1);
}

// an arrival point (object section B, 6 bytes): where the party shows up when arriving
public sealed class MapArrival
{
    public int Index { get; set; }
    public int Flags { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

// an object spot (object section E, 12 bytes: 4 runtime bytes + rectangle). the map's setup script fills in what
// it is when the map loads: a treasure chest, a hidden item, a door or a trigger (see MapSetupScript)
public sealed class MapSpot
{
    public int Index { get; init; }
    public int X1 { get; init; }
    public int Y1 { get; init; }
    // exclusive
    public int X2 { get; init; }
    // exclusive
    public int Y2 { get; init; }
}

// a movement path (object section G): 6-byte header [flags, step count, start X (u16), start Y (u16)] then 3-byte steps.
// event commands 14/15 run it ("character walks path n", 1-based). decoded from $01:9D62-$9F41
public sealed class MovePath
{
    // header flags: bit 0 = the event doesn't wait, bit 1 = repeat forever, bits 2-3 = camera follows,
    // bit 4 = walk to the start instead of appearing there
    public int Flags { get; set; }
    public int StartX { get; set; }
    public int StartY { get; set; }
    public List<MoveStep> Steps { get; } = new();

    public bool NoWait { get => (Flags & 1) != 0; set => Flags = value ? Flags | 1 : Flags & ~1; }
    public bool Repeat { get => (Flags & 2) != 0; set => Flags = value ? Flags | 2 : Flags & ~2; }
    public bool CameraFollows { get => (Flags & 0x0C) != 0; set => Flags = value ? Flags | 0x04 : Flags & ~0x0C; }
    public bool WalkToStart { get => (Flags & 0x10) != 0; set => Flags = value ? Flags | 0x10 : Flags & ~0x10; }

    // tiles visited: the start, then the end of every step
    public List<(int X, int Y)> Points()
    {
        var list = new List<(int, int)> { (StartX, StartY) };
        int x = StartX, y = StartY;
        foreach (var step in Steps)
        {
            (int dx, int dy) = step.Direction switch { 0 => (1, 0), 1 => (-1, 0), 2 => (0, 1), _ => (0, -1) };
            x += dx * step.Tiles; y += dy * step.Tiles;
            list.Add((x, y));
        }
        return list;
    }
}

// one step of a path: walk Tiles tiles in Direction
public sealed class MoveStep
{
    // byte 0: bit 0 = layer, bits 2-4 = facing (0 = face the way it walks, 1-4 = face right/left/down/up, 5 = don't turn),
    // bits 5-7 = speed (index into the speed table $01:E237)
    public int Mode { get; set; }
    // byte 1 (bits 0-1 = direction: 0 right, 1 left, 2 down, 3 up, the other bits are kept)
    public int DirByte { get; set; }
    public int Tiles { get; set; }

    public int Direction { get => DirByte & 3; set => DirByte = (DirByte & ~3) | (value & 3); }
    public int Speed { get => (Mode >> 5) & 7; set => Mode = (Mode & 0x1F) | ((value & 7) << 5); }
    public int Facing { get => (Mode >> 2) & 7; set => Mode = (Mode & ~0x1C) | ((value & 7) << 2); }
    public bool UpperLayer { get => (Mode & 1) != 0; set => Mode = value ? Mode | 1 : Mode & ~1; }
}

// a step-on trigger area (object section D): walking into it runs event (event base + index + 1), once the map's
// setup script turns it on (opcode 81+index). the game fills in bytes 0-3 at run time
public sealed class MapTrigger
{
    public int Index { get; init; }
    public byte[] Runtime { get; set; } = new byte[4];
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    // a turned-off area (no size), kept so the areas after it keep their numbers and events
    public bool IsUnused => X2 <= X1 || Y2 <= Y1;
}

// an NPC (object section C, 14 bytes). talking to NPC n runs event n + 1 of the map's script block (MapEvents.TalkEvent)
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

// one Lufia 1 map, parsed from its compressed map-data resource. the formats are written up in
// research-script-engine.md ("Map data format", "World map").
// town/dungeon maps store a Width*Height layer of u16 metatile numbers. the world map (map 01) stores 240 block
// records at 0x40, a chunk table after them (4 block bytes per 2x2 chunk: TL TR BL BR), and a (Width/2)*(Height/2)
// u16 chunk grid. Layer gets expanded to blocks for editing and turned back into chunks when saving (reusing the
// ones that exist, adding new ones).
// every map ends with an object block: 8 counts, 8 u16 section offsets, an unknown prefix, then sections
// A exits (12 bytes), B arrival points (6), C characters (14), D (12), E spots (12), F (9), G routes and H
// (variable). exits and arrival points can be added and removed, the block gets rebuilt and every other
// byte is kept exactly
public sealed class LufiaMap
{
    // rom tables
    public const int MapDataSelectTable = 0xE19B;   // map data resource = 0xB2 + table[map]
    public const int TilesSelectTable   = 0xE134;   // tile graphics resource = 0xB1 + table[tileset]
    public const int BlocksSelectTable  = 0xE140;   // metatile resource = 0xB1 + table[tileset]
    public const int MapScriptTable     = 0x18200;  // 5 bytes per map: script block offset (u24), ?, event base
    public const int SharedPaletteRows  = 0x11300;  // palette rows 0-1
    public const int MapPaletteTable    = 0x17040;  // rows 2-7: table + (paletteIndex + 1) * 0xC0
    public const int MapPaletteCount    = 20;       // pointers at $01:E160
    public const int WorldMap           = 0x01;
    public const int CompositeTable     = 0xE14B;   // per tileset: LoROM pointer to (ground, overlay) word pairs

    // world map graphics (not named in its header, found by matching the game's VRAM/CGRAM)
    public const int WorldTilesResource = 0x124;
    public const int WorldPaletteIndex  = 17;
    private const int WorldBlocksOffset = 0x40;

    public int MapId { get; }
    public int ResourceId { get; }
    public int CompressedOffset { get; }
    public int CompressedSize { get; }
    public byte[] Original { get; }

    public bool IsWorld { get; }
    public int Tileset { get; private set; }
    public int PaletteIndex { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    private readonly int _origWidth, _origHeight;
    public int LayerOffset { get; }
    public int ObjectsOffset { get; }

    // world map: the 9-byte block records stored in the map data
    public byte[] WorldBlocks { get; } = Array.Empty<byte>();
    private readonly int _chunkTable, _chunkCount;
    private readonly ushort[] _grid = Array.Empty<ushort>();

    // first "composite" metatile, or -1 if this map has none. when a map loads, the game ($01:BEDF) replaces every
    // metatile at or over header word 0x1E with a ground block (BG2) and puts an overlay block (BG1: tree tops,
    // roofs, bridge rails) on a second layer, going by the tileset's composite table. only maps with header 0x10
    // bit 1 set, 0x11 bit 7 clear and 0x23 zero do this
    public int CompositeThreshold { get; private set; }

    // metatile layer, Width*Height entries (low 10 bits = metatile number)
    public ushort[] Layer { get; private set; }

    // second metatile layer drawn on BG2 behind the map (the intro's clouds, header 0x16/0x18 = its size,
    // 0x2C = its offset), or null. same tileset as Layer. read-only, saving keeps it as it was loaded
    public ushort[]? Layer2 { get; }
    public int Layer2Width { get; }
    public int Layer2Height { get; }
    // header byte 0x23: how the second layer behaves (2 = drifts diagonally, the intro's clouds)
    public int Layer2Mode { get; }

    public List<MapExit>    Exits    { get; } = new();
    public List<MapArrival> Arrivals { get; } = new();
    public List<MapNpc>     Npcs     { get; } = new();
    // how many characters the map had when it loaded (later ones are new, saving switches them on in the setup
    // script, see SetupScripts.SyncCharacters)
    public int LoadedNpcCount { get; private set; }
    // characters that were hidden (sprite FF) when the map loaded. if one gets a sprite again, saving switches it on
    public HashSet<int> HiddenAtLoad { get; private set; } = new();
    public void MarkNpcsSaved() { LoadedNpcCount = Npcs.Count; HiddenAtLoad = Npcs.Where(n => n.IsUnused).Select(n => n.Index).ToHashSet(); }
    public List<MapSpot>    Spots    { get; } = new();
    public List<MapTrigger> Triggers { get; } = new();
    // movement paths (section G), path n of commands 14/15 is Paths[n - 1]
    public List<MovePath>   Paths    { get; } = new();

    // counts and offsets of the 8 object sections (relative to the object block), as loaded
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
        _origWidth = Width; _origHeight = Height;
        LayerOffset   = (int)U32(data, 0x28);
        ObjectsOffset = (int)U32(data, 0x30);
        Layer = new ushort[Width * Height];

        if (IsWorld)
        {
            Tileset = -1;
            PaletteIndex = WorldPaletteIndex;
            CompositeThreshold = -1;
            int blockCount = U16(data, 0x1E);
            WorldBlocks = data.AsSpan(WorldBlocksOffset, blockCount * 9).ToArray();
            _chunkTable = WorldBlocksOffset + blockCount * 9;
            _chunkCount = (LayerOffset - _chunkTable) / 4;
            int gridWidth = Width / 2, gridHeight = Height / 2;
            _grid = new ushort[gridWidth * gridHeight];
            for (int cy = 0; cy < gridHeight; cy++)
                for (int cx = 0; cx < gridWidth; cx++)
                {
                    ushort gridWord = (ushort)U16(data, LayerOffset + (cy * gridWidth + cx) * 2);
                    _grid[cy * gridWidth + cx] = gridWord;
                    int chunk = _chunkTable + (gridWord & 0x0FFF) * 4;
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
            int layer2Offset = (int)U32(data, 0x2C), width2 = U16(data, 0x16), height2 = U16(data, 0x18);
            if (layer2Offset != LayerOffset && width2 > 0 && height2 > 0 && layer2Offset >= LayerOffset + Layer.Length * 2 && layer2Offset + width2 * height2 * 2 <= ObjectsOffset)
            {
                Layer2Width = width2; Layer2Height = height2; Layer2Mode = data[0x23];
                Layer2 = new ushort[width2 * height2];
                for (int i = 0; i < Layer2.Length; i++) Layer2[i] = (ushort)U16(data, layer2Offset + i * 2);
            }
        }

        ParseObjects(data);
    }

    // loading

    public static int MapDataResource(RomBuffer rom, int mapId) => 0xB2 + rom.ReadByte(MapDataSelectTable + mapId);

    // true when the map's data starts with its own number in decimal (how the original tools labelled it)
    public static bool IsValidMap(RomBuffer rom, int mapId, out byte[]? data, out int compressedSize)
    {
        data = null;
        compressedSize = 0;
        try
        {
            int resource = MapDataResource(rom, mapId);
            int offset = LufiaCompression.ResourceOffset(rom, resource);
            if (offset < 0x8000 || offset >= rom.Length - 4) return false;
            data = LufiaCompression.Decompress(rom, offset, out compressedSize);
            if (data.Length < 0x40) return false;
            string tag = System.Text.Encoding.ASCII.GetString(data, 0, 3);
            return int.TryParse(tag, out int number) && number == mapId;
        }
        catch
        {
            return false;
        }
    }

    public static LufiaMap Load(RomBuffer rom, int mapId)
    {
        int resource = MapDataResource(rom, mapId);
        int offset = LufiaCompression.ResourceOffset(rom, resource);
        var data = LufiaCompression.Decompress(rom, offset, out int size);
        return new LufiaMap(mapId, resource, offset, size, data);
    }

    // event base number for this map's door/trigger events (script table byte 4)
    public static int EventBase(RomBuffer rom, int mapId) => rom.ReadByte(MapScriptTable + mapId * 5 + 4);

    // file offset of the script for event ev, or -1 if the event is unused
    public static int EventScriptOffset(RomBuffer rom, int mapId, int ev)
    {
        int entry = MapScriptTable + mapId * 5;
        int block = 0x18000 + (rom.ReadByte(entry) | (rom.ReadByte(entry + 1) << 8) | (rom.ReadByte(entry + 2) << 16));
        if (block + ev * 2 + 1 >= rom.Length) return -1;
        int relative = rom.ReadUInt16Le(block + ev * 2);
        // FFFF = none. an entry pointing into the table itself isn't a script either: entry 0 of most maps is the
        // event base (a marker), and 0 / 1 mean none, and the table is at least base + 1 entries long. when we read
        // that as a script, editing it wrote over the table
        if (relative == 0xFFFF || relative < 2 * (EventBase(rom, mapId) + 1)) return -1;
        return block + relative;
    }

    // objects

    private void ParseObjects(byte[] data)
    {
        int objects = ObjectsOffset;
        if (objects <= 0 || objects + 24 > data.Length) return;
        for (int i = 0; i < 8; i++)
        {
            SectionCounts[i]  = data[objects + i];
            SectionOffsets[i] = U16(data, objects + 8 + i * 2);
        }

        Exits.AddRange(ParseExits(data, objects));

        int arrival = objects + SectionOffsets[1];
        for (int i = 0; i < SectionCounts[1]; i++, arrival += 6)
            Arrivals.Add(new MapArrival { Index = i, Flags = U16(data, arrival), X = U16(data, arrival + 2), Y = U16(data, arrival + 4) });

        int npc = objects + SectionOffsets[2];
        for (int i = 0; i < SectionCounts[2]; i++, npc += 14)
            Npcs.Add(new MapNpc
            {
                Index = i, Sprite = data[npc], Flags = data[npc + 1], X = U16(data, npc + 2), Y = U16(data, npc + 4),
                BoxX1 = U16(data, npc + 6), BoxY1 = U16(data, npc + 8), BoxX2 = U16(data, npc + 10), BoxY2 = U16(data, npc + 12),
            });
        LoadedNpcCount = Npcs.Count;
        HiddenAtLoad = Npcs.Where(n => n.IsUnused).Select(n => n.Index).ToHashSet();

        int trigger = objects + SectionOffsets[3];
        for (int i = 0; i < SectionCounts[3] && trigger + 12 <= data.Length; i++, trigger += 12)
            Triggers.Add(new MapTrigger { Index = i, Runtime = data[trigger..(trigger + 4)], X1 = U16(data, trigger + 4), Y1 = U16(data, trigger + 6), X2 = U16(data, trigger + 8), Y2 = U16(data, trigger + 10) });
        LoadedTriggerCount = _triggersParsed = Triggers.Count;

        int spot = objects + SectionOffsets[4];
        for (int i = 0; i < SectionCounts[4] && spot + 12 <= data.Length; i++, spot += 12)
            Spots.Add(new MapSpot { Index = i, X1 = U16(data, spot + 4), Y1 = U16(data, spot + 6), X2 = U16(data, spot + 8), Y2 = U16(data, spot + 10) });

        int pathOffset = objects + SectionOffsets[6];
        for (int i = 0; i < SectionCounts[6] && pathOffset + 6 <= data.Length; i++)
        {
            var path = new MovePath { Flags = data[pathOffset], StartX = U16(data, pathOffset + 2), StartY = U16(data, pathOffset + 4) };
            int steps = data[pathOffset + 1];
            pathOffset += 6;
            for (int k = 0; k < steps && pathOffset + 3 <= data.Length; k++, pathOffset += 3)
                path.Steps.Add(new MoveStep { Mode = data[pathOffset], DirByte = data[pathOffset + 1], Tiles = data[pathOffset + 2] });
            Paths.Add(path);
        }
    }

    // exit records (section A) of an object block at objects
    private static List<MapExit> ParseExits(byte[] data, int objects)
    {
        var list = new List<MapExit>();
        if (objects <= 0 || objects + 24 > data.Length) return list;
        int exit = objects + U16(data, objects + 8);
        for (int i = 0; i < data[objects] && exit + 12 <= data.Length; i++, exit += 12)
            list.Add(new MapExit
            {
                Index = i, Arrival = data[exit], Flags = data[exit + 1], DestMap = data[exit + 2], Byte3 = data[exit + 3],
                X1 = U16(data, exit + 4), Y1 = U16(data, exit + 6), X2 = U16(data, exit + 8), Y2 = U16(data, exit + 10),
            });
        return list;
    }

    // the exits of any map, read straight from the rom
    public static List<MapExit> ReadExits(RomBuffer rom, int mapId)
    {
        int offset = LufiaCompression.ResourceOffset(rom, MapDataResource(rom, mapId));
        var data = LufiaCompression.Decompress(rom, offset, out _);
        return data.Length < 0x34 ? new List<MapExit>() : ParseExits(data, (int)U32(data, 0x30));
    }

    // adding / removing exits and arrival points

    // adds an exit (rectangle in blocks, X2/Y2 exclusive) leading to destMap
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

    // adds an arrival point. its number is the next free index (existing numbers never change)
    public MapArrival AddArrival(int x, int y)
    {
        var template = Arrivals.FirstOrDefault();
        var arrival = new MapArrival { Index = Arrivals.Count, X = x, Y = y, Flags = template?.Flags ?? 0 };
        Arrivals.Add(arrival);
        return arrival;
    }

    // only the last arrival point can be removed, so the numbers other exits use stay valid
    public bool CanRemoveArrival(MapArrival arrival) => Arrivals.Count > 0 && Arrivals[^1] == arrival;

    public void RemoveArrival(MapArrival arrival)
    {
        if (CanRemoveArrival(arrival)) Arrivals.Remove(arrival);
    }

    public int OriginalArrivalCount => SectionCounts[1];
    public int OriginalExitCount => SectionCounts[0];

    // saving

    // unpacked map data with all the edits in, unknown bytes are kept exactly
    public byte[] BuildData()
    {
        var data = (byte[])Original.Clone();
        int shiftFrom = int.MaxValue, delta = 0;   // byte range moved by a grown world chunk table
        if (IsWorld) Array.Copy(WorldBlocks, 0, data, WorldBlocksOffset, WorldBlocks.Length);   // block records (Block editor)

        if (IsWorld)
        {
            var (chunks, grid) = BuildWorldChunks();
            int gridWidth = Width / 2;
            var body = new List<byte>(data.AsSpan(0, _chunkTable).ToArray());
            body.AddRange(chunks);
            foreach (var gridWord in grid) { body.Add((byte)gridWord); body.Add((byte)(gridWord >> 8)); }
            int gridEnd = LayerOffset + _grid.Length * 2;
            body.AddRange(data.AsSpan(gridEnd, data.Length - gridEnd).ToArray());
            delta = chunks.Count - _chunkCount * 4;
            shiftFrom = LayerOffset;
            data = body.ToArray();
            // header section offsets after the chunk table move along
            foreach (int headerField in new[] { 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38 })
            {
                uint value = U32(Original, headerField);
                if (value >= shiftFrom && value <= Original.Length) PutU32(data, headerField, (uint)(value + delta));
            }
        }
        else if (Width != _origWidth || Height != _origHeight)
        {
            // resized: new layer, everything after the old layer moves along
            int oldEnd = LayerOffset + _origWidth * _origHeight * 2;
            var body = new List<byte>(data.AsSpan(0, LayerOffset).ToArray());
            foreach (var cell in Layer) { body.Add((byte)cell); body.Add((byte)(cell >> 8)); }
            body.AddRange(data.AsSpan(oldEnd, data.Length - oldEnd).ToArray());
            delta = Layer.Length * 2 - (oldEnd - LayerOffset);
            shiftFrom = oldEnd;
            data = body.ToArray();
            Put16(data, 0x12, Width); Put16(data, 0x14, Height);
            foreach (int headerField in new[] { 0x2C, 0x30, 0x34, 0x38 })
            {
                uint value = U32(Original, headerField);
                if (value >= shiftFrom && value <= Original.Length) PutU32(data, headerField, (uint)(value + delta));
            }
        }
        else
        {
            for (int i = 0; i < Layer.Length; i++) Put16(data, LayerOffset + i * 2, Layer[i]);
        }

        int objects = ObjectsOffset + (ObjectsOffset >= shiftFrom ? delta : 0);
        var block = BuildObjectBlock(data, objects);
        var result = new byte[objects + block.Length];
        Array.Copy(data, result, objects);
        Array.Copy(block, 0, result, objects, block.Length);

        // header field 0x24 holds the total length on town maps
        if (U32(Original, 0x24) == Original.Length) PutU32(result, 0x24, (uint)result.Length);
        return result;
    }

    // world map: chunk table bytes and grid for the current layer (reusing the chunks that exist)
    private (List<byte> Chunks, ushort[] Grid) BuildWorldChunks()
    {
        var chunks = new List<byte>(Original.AsSpan(_chunkTable, _chunkCount * 4).ToArray());
        var lookup = new Dictionary<int, int>();
        for (int i = 0; i < _chunkCount; i++)
        {
            int key = chunks[i * 4] | (chunks[i * 4 + 1] << 8) | (chunks[i * 4 + 2] << 16) | (chunks[i * 4 + 3] << 24);
            lookup.TryAdd(key, i);
        }
        int gridWidth = Width / 2, gridHeight = Height / 2;
        var grid = new ushort[_grid.Length];
        for (int cy = 0; cy < gridHeight; cy++)
            for (int cx = 0; cx < gridWidth; cx++)
            {
                int i = cy * gridWidth + cx;
                byte b0 = (byte)Layer[(cy * 2) * Width + cx * 2], b1 = (byte)Layer[(cy * 2) * Width + cx * 2 + 1];
                byte b2 = (byte)Layer[(cy * 2 + 1) * Width + cx * 2], b3 = (byte)Layer[(cy * 2 + 1) * Width + cx * 2 + 1];
                int key = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
                int originalChunk = _grid[i] & 0x0FFF;
                int chunkIndex;
                if (originalChunk < _chunkCount && chunks[originalChunk * 4] == b0 && chunks[originalChunk * 4 + 1] == b1 &&
                    chunks[originalChunk * 4 + 2] == b2 && chunks[originalChunk * 4 + 3] == b3) chunkIndex = originalChunk;   // unchanged
                else if (!lookup.TryGetValue(key, out chunkIndex))
                {
                    chunkIndex = chunks.Count / 4;
                    if (chunkIndex > 0x0FFF) throw new InvalidOperationException("The world map has run out of chunk numbers (4096).");
                    chunks.AddRange(new[] { b0, b1, b2, b3 });
                    lookup[key] = chunkIndex;
                }
                grid[i] = (ushort)((_grid[i] & 0xF000) | chunkIndex);
            }
        return (chunks, grid);
    }

    // object block with the current exits, arrival points and characters, the other sections copied
    private byte[] BuildObjectBlock(byte[] src, int srcOb)
    {
        var original = Original;
        int objects = ObjectsOffset;
        int blockLen = original.Length - objects;
        var raw = new byte[8][];
        for (int i = 0; i < 8; i++)
        {
            int start = SectionOffsets[i];
            int end = i < 7 ? SectionOffsets[i + 1] : blockLen;
            raw[i] = original.AsSpan(objects + start, Math.Max(0, end - start)).ToArray();
        }
        var prefix = original.AsSpan(objects + 24, Math.Max(0, SectionOffsets[0] - 24)).ToArray();

        var exitBytes = new List<byte>();
        foreach (var exit in Exits)
        {
            exitBytes.Add((byte)exit.Arrival); exitBytes.Add((byte)exit.Flags); exitBytes.Add((byte)exit.DestMap); exitBytes.Add((byte)exit.Byte3);
            Add16(exitBytes, exit.X1); Add16(exitBytes, exit.Y1); Add16(exitBytes, exit.X2); Add16(exitBytes, exit.Y2);
        }
        var arrivalBytes = new List<byte>();
        foreach (var arrival in Arrivals) { Add16(arrivalBytes, arrival.Flags); Add16(arrivalBytes, arrival.X); Add16(arrivalBytes, arrival.Y); }
        // NPCs: 14 bytes each, in number order (the list can grow / shrink at the end)
        var npcBytes = new List<byte>();
        foreach (var npc in Npcs.OrderBy(n => n.Index))
        {
            npcBytes.Add((byte)npc.Sprite); npcBytes.Add((byte)npc.Flags);
            Add16(npcBytes, npc.X); Add16(npcBytes, npc.Y); Add16(npcBytes, npc.BoxX1); Add16(npcBytes, npc.BoxY1); Add16(npcBytes, npc.BoxX2); Add16(npcBytes, npc.BoxY2);
        }

        // step-on areas: 12 bytes each (the 4 run-time bytes kept, then the rectangle)
        var triggerBytes = new List<byte>();
        foreach (var trigger in Triggers.OrderBy(t => t.Index))
        {
            triggerBytes.AddRange(trigger.Runtime.Length == 4 ? trigger.Runtime : new byte[] { 1, 0, 0, 0 });
            Add16(triggerBytes, trigger.X1); Add16(triggerBytes, trigger.Y1); Add16(triggerBytes, trigger.X2); Add16(triggerBytes, trigger.Y2);
        }
        // keep the original bytes when nothing changed (records the parser couldn't read stay exact)
        // (a section the parser couldn't read whole is always kept as it was)
        bool partial = _triggersParsed != SectionCounts[3];
        byte[] triggerSection = partial || Triggers.Count == SectionCounts[3] && triggerBytes.Count == raw[3].Length && TriggersUnchanged(raw[3]) ? raw[3] : triggerBytes.ToArray();

        var pathBytes = new List<byte>();
        foreach (var path in Paths)
        {
            if (path.Steps.Count > 255) throw new InvalidOperationException("A path can have at most 255 steps.");
            pathBytes.Add((byte)path.Flags); pathBytes.Add((byte)path.Steps.Count); Add16(pathBytes, path.StartX); Add16(pathBytes, path.StartY);
            foreach (var step in path.Steps) { pathBytes.Add((byte)step.Mode); pathBytes.Add((byte)step.DirByte); pathBytes.Add((byte)step.Tiles); }
        }
        var sections = new[] { exitBytes.ToArray(), arrivalBytes.ToArray(), npcBytes.ToArray(), triggerSection, raw[4], raw[5], pathBytes.ToArray(), raw[7] };
        if (Triggers.Count > 63) throw new InvalidOperationException("A map can have at most 63 step-on areas.");
        var counts = new[] { Exits.Count, Arrivals.Count, Npcs.Count, partial ? SectionCounts[3] : Triggers.Count, SectionCounts[4],
                             SectionCounts[5], Paths.Count, SectionCounts[7] };
        if (Paths.Count > 255) throw new InvalidOperationException("A map can have at most 255 paths.");
        if (counts[0] > 255 || counts[1] > 255) throw new InvalidOperationException("A map can have at most 255 exits and 255 arrival points.");

        var block = new List<byte>();
        foreach (var count in counts) block.Add((byte)count);
        int pos = 24 + prefix.Length;
        foreach (var section in sections) { Add16(block, pos); pos += section.Length; }
        block.AddRange(prefix);
        foreach (var section in sections) block.AddRange(section);
        return block.ToArray();
    }

    private bool TriggersUnchanged(byte[] raw)
    {
        for (int i = 0; i < Triggers.Count; i++)
        {
            var trigger = Triggers[i]; int offset = i * 12;
            if (!raw.AsSpan(offset, 4).SequenceEqual(trigger.Runtime) || U16(raw, offset + 4) != trigger.X1 || U16(raw, offset + 6) != trigger.Y1 || U16(raw, offset + 8) != trigger.X2 || U16(raw, offset + 10) != trigger.Y2)
                return false;
        }
        return true;
    }

    // how many step-on areas the map had when it loaded (later ones are new, Apply switches them on and gives
    // them an event)
    public int LoadedTriggerCount { get; private set; }
    private int _triggersParsed;
    // false when the map's step-on area section couldn't be read whole (then it can't be edited)
    public bool TriggersEditable => _triggersParsed == SectionCounts[3];
    public void MarkTriggersSaved() => LoadedTriggerCount = Triggers.Count;

    // adds a step-on area (it gets event base + its number + 1 when the map is applied)
    public MapTrigger AddTrigger(int x1, int y1, int x2, int y2, int index = -1)
    {
        // areas before the wanted number get added empty (switched off), so it gets the event it should
        while (index > Triggers.Count && Triggers.Count < 62)
            Triggers.Add(new MapTrigger { Index = Triggers.Count, Runtime = new byte[] { 1, 0, 0, 0 } });
        if (Triggers.Count >= 63) throw new InvalidOperationException("A map can have at most 63 step-on areas.");
        if (!TriggersEditable) throw new InvalidOperationException("This map's step-on areas can't be read whole, so new ones can't be added.");
        var trigger = new MapTrigger { Index = Triggers.Count, Runtime = new byte[] { 1, 0, 0, 0 }, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
        Triggers.Add(trigger);
        return trigger;
    }

    // removes a step-on area: the last one really goes, others get emptied so later areas keep their events
    public void RemoveTrigger(MapTrigger trigger)
    {
        if (Triggers.Count > 0 && Triggers[^1] == trigger && trigger.Index >= LoadedTriggerCount) Triggers.Remove(trigger);
        else { trigger.X2 = trigger.X1; trigger.Y2 = trigger.Y1; }
    }

    // everything the Map Editor changes before Apply (blocks, size, exits, arrival points, characters, routes),
    // copied, for its undo / redo
    public sealed class State
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public ushort[] Layer { get; init; } = Array.Empty<ushort>();
        public List<MapExit> Exits { get; init; } = new();
        public List<MapArrival> Arrivals { get; init; } = new();
        public List<MapNpc> Npcs { get; init; } = new();
        public List<MovePath> Paths { get; init; } = new();
        public List<MapTrigger> Triggers { get; init; } = new();
        public int Tileset { get; init; }
        public int Palette { get; init; }
        public int Threshold { get; init; } = -1;

        public bool SameAs(State other) =>
            Tileset == other.Tileset && Palette == other.Palette && Threshold == other.Threshold && Width == other.Width && Height == other.Height && Layer.AsSpan().SequenceEqual(other.Layer) &&
            Exits.Count == other.Exits.Count && Exits.Zip(other.Exits).All(p => Key(p.First) == Key(p.Second)) &&
            Arrivals.Count == other.Arrivals.Count && Arrivals.Zip(other.Arrivals).All(p => Key(p.First) == Key(p.Second)) &&
            Npcs.Count == other.Npcs.Count && Npcs.Zip(other.Npcs).All(p => Key(p.First) == Key(p.Second)) &&
            Paths.Count == other.Paths.Count && Paths.Zip(other.Paths).All(p => Key(p.First) == Key(p.Second)) &&
            Triggers.Count == other.Triggers.Count && Triggers.Zip(other.Triggers).All(p => Key(p.First) == Key(p.Second));

        public static string Key(MapExit e) => $"{e.Index} {e.Arrival} {e.Flags} {e.DestMap} {e.Byte3} {e.X1} {e.Y1} {e.X2} {e.Y2}";
        public static string Key(MapArrival a) => $"{a.Index} {a.Flags} {a.X} {a.Y}";
        public static string Key(MapNpc n) => $"{n.Index} {n.Sprite} {n.Flags} {n.X} {n.Y} {n.BoxX1} {n.BoxY1} {n.BoxX2} {n.BoxY2}";
        public static string Key(MapTrigger t) => $"{t.Index} {Convert.ToHexString(t.Runtime)} {t.X1} {t.Y1} {t.X2} {t.Y2}";
        public static string Key(MovePath p) => $"{p.Flags} {p.StartX} {p.StartY} " + string.Join(",", p.Steps.Select(s => $"{s.Mode}/{s.DirByte}/{s.Tiles}"));
    }

    private static MapExit Copy(MapExit e) => new() { Index = e.Index, Arrival = e.Arrival, Flags = e.Flags, DestMap = e.DestMap, Byte3 = e.Byte3, X1 = e.X1, Y1 = e.Y1, X2 = e.X2, Y2 = e.Y2 };
    private static MapArrival Copy(MapArrival a) => new() { Index = a.Index, Flags = a.Flags, X = a.X, Y = a.Y };
    private static MapNpc Copy(MapNpc n) => new() { Index = n.Index, Sprite = n.Sprite, Flags = n.Flags, X = n.X, Y = n.Y, BoxX1 = n.BoxX1, BoxY1 = n.BoxY1, BoxX2 = n.BoxX2, BoxY2 = n.BoxY2 };
    private static MapTrigger Copy(MapTrigger t) => new() { Index = t.Index, Runtime = (byte[])t.Runtime.Clone(), X1 = t.X1, Y1 = t.Y1, X2 = t.X2, Y2 = t.Y2 };
    private static MovePath Copy(MovePath path)
    {
        var copy = new MovePath { Flags = path.Flags, StartX = path.StartX, StartY = path.StartY };
        foreach (var step in path.Steps) copy.Steps.Add(new MoveStep { Mode = step.Mode, DirByte = step.DirByte, Tiles = step.Tiles });
        return copy;
    }

    public State Capture() => new()
    {
        Width = Width, Height = Height, Layer = (ushort[])Layer.Clone(),
        Exits = Exits.Select(Copy).ToList(), Arrivals = Arrivals.Select(Copy).ToList(),
        Npcs = Npcs.Select(Copy).ToList(), Paths = Paths.Select(Copy).ToList(), Triggers = Triggers.Select(Copy).ToList(),
        Tileset = Tileset, Palette = PaletteIndex, Threshold = CompositeThreshold,
    };

    public void Restore(State state)
    {
        Width = state.Width; Height = state.Height; Layer = (ushort[])state.Layer.Clone();
        Exits.Clear(); Exits.AddRange(state.Exits.Select(Copy));
        Arrivals.Clear(); Arrivals.AddRange(state.Arrivals.Select(Copy));
        Npcs.Clear(); Npcs.AddRange(state.Npcs.Select(Copy));
        Paths.Clear(); Paths.AddRange(state.Paths.Select(Copy));
        Triggers.Clear(); Triggers.AddRange(state.Triggers.Select(Copy));
        if (!IsWorld && (state.Tileset != Tileset || state.Palette != PaletteIndex)) SetTilesetAndPalette(state.Tileset, state.Palette);
        if (state.Threshold >= 0 && state.Threshold != CompositeThreshold) { CompositeThreshold = state.Threshold; Original[0x1E] = (byte)state.Threshold; Original[0x1F] = (byte)(state.Threshold >> 8); }
    }

    // changes the map's size (town / dungeon maps): the layer keeps its top-left part, new cells get fill.
    // objects outside the new size stay where they are (move them in the editor)
    public void Resize(int width, int height, int fill)
    {
        if (IsWorld) throw new InvalidOperationException("The world map's size is fixed.");
        if (width < 16 || height < 14 || width > 255 || height > 255) throw new InvalidOperationException("A map is 16-255 blocks wide and 14-255 high (the screen is 16 x 14).");
        var newLayer = new ushort[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                newLayer[y * width + x] = x < Width && y < Height ? Layer[y * Width + x] : (ushort)(fill & 0x3FF);
        Layer = newLayer; Width = width; Height = height;
    }

    // raises the composite threshold by count and renumbers the composite cells to match
    // (they keep their pair table entries, number - threshold stays the same)
    public void ShiftComposites(int count)
    {
        if (CompositeThreshold < 0) throw new InvalidOperationException("This map has no composite blocks.");
        int threshold = CompositeThreshold;
        for (int i = 0; i < Layer.Length; i++)
        {
            int cell = Layer[i];
            if ((cell & 0x3FF) >= threshold) Layer[i] = (ushort)((cell & 0xFC00) | ((cell & 0x3FF) + count));
        }
        CompositeThreshold = threshold + count;
        Original[0x1E] = (byte)CompositeThreshold; Original[0x1F] = (byte)(CompositeThreshold >> 8);
    }

    // lets the map use blocks under threshold as normal blocks: its composite cells get renumbered (they keep their
    // pair table entries, so the map looks the same). false if the composites don't fit
    public bool RaiseThreshold(int threshold)
    {
        if (CompositeThreshold < 0 || threshold <= CompositeThreshold) return true;
        int highestBlock = Layer.Max(c => c & 0x3FF);
        if (highestBlock >= CompositeThreshold && highestBlock + (threshold - CompositeThreshold) > 0x3FF) return false;
        ShiftComposites(threshold - CompositeThreshold);
        return true;
    }

    // header bytes the editor can change: tileset (+0x08) and palette (+0x09). takes effect when the map is
    // saved and loaded again
    public void SetTilesetAndPalette(int tileset, int palette)
    {
        if (IsWorld) throw new InvalidOperationException("The world map's graphics are fixed.");
        Original[0x08] = (byte)tileset; Original[0x09] = (byte)palette;
        Tileset = tileset; PaletteIndex = palette;
    }

    // adds an NPC at the end (talking to NPC n runs event n + 1 of the map's script), or with number index. the
    // numbers before it are kept as hidden characters (sprite FF), so it gets the event it should
    public MapNpc AddNpc(int sprite, int x, int y, int index = -1)
    {
        if (index < 0) index = Npcs.Count;
        if (index >= 32) throw new InvalidOperationException("A map can have at most 32 NPCs.");
        if (index < Npcs.Count) throw new InvalidOperationException($"Character {index + 1} is already there.");
        while (Npcs.Count < index)
            Npcs.Add(new MapNpc { Index = Npcs.Count, Sprite = 0xFF, X = 0, Y = 0, BoxX1 = 0, BoxY1 = 0, BoxX2 = 1, BoxY2 = 1 });
        var npc = new MapNpc { Index = Npcs.Count, Sprite = sprite, Flags = 0x20, X = x, Y = y, BoxX1 = Math.Max(0, x - 2), BoxY1 = Math.Max(0, y - 2), BoxX2 = Math.Min(Width, x + 3), BoxY2 = Math.Min(Height, y + 3) };
        Npcs.Add(npc);
        return npc;
    }

    // removes the last NPC (the others keep their numbers and events), any other NPC gets hidden instead
    // (sprite FF = unused)
    public void RemoveNpc(MapNpc npc)
    {
        if (Npcs.Count > 0 && Npcs[^1] == npc) Npcs.RemoveAt(Npcs.Count - 1);
        else npc.Sprite = 0xFF;
    }

    public ushort GetTile(int x, int y) => Layer[y * Width + x];
    public void SetTile(int x, int y, int metatile) =>
        Layer[y * Width + x] = IsWorld
            ? (ushort)(metatile & 0xFF)
            : (ushort)((Layer[y * Width + x] & 0xFC00) | (metatile & 0x3FF));

    private static int U16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);
    private static uint U32(byte[] data, int offset) => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    private static void PutU32(byte[] data, int offset, uint value) { data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8); data[offset + 2] = (byte)(value >> 16); data[offset + 3] = (byte)(value >> 24); }
    private static void Put16(byte[] data, int offset, int value) { data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8); }
    private static void Add16(List<byte> list, int value) { list.Add((byte)value); list.Add((byte)(value >> 8)); }
    private static void Set16(List<byte> list, int offset, int value) { list[offset] = (byte)value; list[offset + 1] = (byte)(value >> 8); }
}
