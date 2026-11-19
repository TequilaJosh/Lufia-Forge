namespace LufiaForge.Core.Lufia2;

// a lufia 2 map being edited in the Maps tab: its two layers, a working copy of its info block (exits, step-on
// areas, characters, preset points, treasure spots...), the characters' sprites and its block set, tile set and
// palette set. nothing goes into the rom until Apply, and a State is one step of the editor's undo.
// on Apply the info block is written over its old bytes if it kept its size (and no other map shares it),
// otherwise into the added space and the map's pointer moves. characters added or taken out change the start-up
// script (68 id sprite) and the talk events (+6), step-on areas added or taken out get or lose their event (+8)
public sealed class L2MapEdit
{
    public int MapId { get; }
    public L2Maps.Map Map { get; private set; }
    // the info block as edited (u16 size, 21 u16 list offsets, then the lists)
    public byte[] Info { get; private set; }
    // the sprite of each map character (actor id 0x50 + n), as it'll be created
    public Dictionary<int, int> Sprites { get; private set; }
    public int BlockSetIndex { get; set; }
    public int TileSetIndex { get; set; }
    public int PaletteSetIndex { get; set; }
    // tile animation (which tiles move, and where the palettes start) and colour cycling. they go with the tile set
    // and palette set, so the map's graphics are the five together
    public int AnimIndex { get; set; }
    public int ColourAnimIndex { get; set; }
    // the ground layer's scroll mode (L2Maps.LayerMode), 0 scrolls with the map
    public int GroundMode { get; set; }
    private readonly int _origGroundMode;

    private readonly byte[] _origInfo;
    private readonly int _origInfoAt;
    private readonly Dictionary<int, int> _origSprites;
    private readonly HashSet<int> _origCharacters, _origTriggers;
    private readonly int _origBlockSet, _origTileSet, _origPaletteSet, _origAnim, _origColourAnim;
    private readonly int _origWidth, _origHeight;
    private readonly int[][] _origBlocks;
    private readonly byte[][] _origFlags;

    // lists of the info block (offsets in it)

    public const int Doors = 0x02, Looks = 0x04, Exits = 0x06, Passages = 0x0A, Triggers = 0x0C, Points = 0x0E, Characters = 0x10,
                     Paths = 0x12, Areas = 0x14, Treasure = 0x26;
    public static int RecordSize(int list) => list switch
    {
        Doors => 15, Looks => 10, Exits => 9, Passages => 5, Triggers => 5, Points => 3, Characters => 8, Paths => 6, Areas => 5, Treasure => 4,
        0x1A or 0x1C or 0x1E or 0x20 or 0x28 or 0x2A => 3, _ => 0,
    };

    // biggest map the game has, so a resized map stays within what the game's buffers already hold
    public const int MaxWidth = 96, MaxHeight = 88, MaxCells = 7744;

    // the map's treasure (the chest list at $91:B8B5, L2Chests), as edited
    public List<L2Chests.Rec> Chests { get; private set; }
    private List<L2Chests.Rec> _origChests;

    // world map or sea floor: only the info block (exits, points) is edited here, their blocks are in the
    // Maps tab's world map editor
    public bool IsWorld => MapId < 2;

    public L2MapEdit(RomBuffer rom, int map)
    {
        MapId = map;
        // the world maps' blocks are big blocks in their own files, a 256 x 256 grid of 16-pixel blocks stands in for them
        Map = map < 2 ? new L2Maps.Map { Id = map, Name = "", Layers = new[] { new L2Maps.Layer { Width = 256, Height = 256, Blocks = new int[65536], Flags = new byte[65536] } } }
                      : L2Maps.Read(rom, map);
        Chests = map < 2 ? new() : EditorRecords(rom, map);
        _origChests = new List<L2Chests.Rec>(Chests);
        _origInfoAt = L2MapInfo.Block(rom, map);
        _origInfo = _origInfoAt >= 0 && _origInfoAt + 2 <= rom.Length ? rom.ReadBytes(_origInfoAt, rom.ReadUInt16Le(_origInfoAt)) : Array.Empty<byte>();
        Info = (byte[])_origInfo.Clone();
        _origSprites = map < 2 ? new() : L2MapInfo.CharacterSprites(rom, map);
        Sprites = new Dictionary<int, int>(_origSprites);
        _origCharacters = Records(Characters).Select(r => 0x4F + r.Bytes[0]).ToHashSet();
        _origTriggers = Records(Triggers).Select(r => (int)r.Bytes[0]).ToHashSet();
        BlockSetIndex = _origBlockSet = rom.ReadByte(L2Maps.BlockSetAt(rom, map));
        TileSetIndex = _origTileSet = rom.ReadByte(L2Maps.TileSetAt(rom, map));
        PaletteSetIndex = _origPaletteSet = rom.ReadByte(L2Maps.PaletteSetAt(rom, map));
        AnimIndex = _origAnim = rom.ReadByte(L2Maps.AnimIndexAt(rom, map));
        ColourAnimIndex = _origColourAnim = rom.ReadByte(L2Maps.ColourAnimAt(rom, map));
        GroundMode = _origGroundMode = map < 2 ? 0 : L2Maps.LayerMode(rom, map, 0);
        _origWidth = Map.Layers[0].Width; _origHeight = Map.Layers[0].Height;
        _origBlocks = Map.Layers.Select(l => (int[])l.Blocks.Clone()).ToArray();
        _origFlags = Map.Layers.Select(l => (byte[])l.Flags.Clone()).ToArray();
    }

    public bool HasInfo => Info.Length >= 0x2C;
    public int Width => Map.Layers[0].Width;
    public int Height => Map.Layers[0].Height;

    // the records of a list: their offset in Info and a copy of the bytes
    public List<(int Off, byte[] Bytes)> Records(int list)
    {
        var records = new List<(int, byte[])>();
        int size = RecordSize(list);
        if (!HasInfo || size == 0) return records;
        int offset = U16(list);
        for (int guard = 0; guard < 128 && offset >= 0x2C && offset + size <= Info.Length && Info[offset] != 0xFF; guard++, offset += size)
            records.Add((offset, Info.AsSpan(offset, size).ToArray()));
        return records;
    }

    private int U16(int offset) => Info[offset] | Info[offset + 1] << 8;
    private void SetU16(int offset, int value) { Info[offset] = (byte)value; Info[offset + 1] = (byte)(value >> 8); }

    // writes record number index of a list
    public void SetRecord(int list, int index, byte[] bytes)
    {
        var record = Records(list)[index];
        bytes.CopyTo(Info, record.Off);
    }

    // true if another list starts at the same place (the game's empty lists share one FF)
    private bool Shared(int list) => Enumerable.Range(1, 21).Select(i => i * 2).Any(o => o != list && o + 1 < Info.Length && U16(o) == U16(list));

    // copies the list to the end of the block with record added, unless it's already the last thing there and
    // no other list shares it, then it just grows
    public void AddRecord(int list, byte[] record)
    {
        int size = RecordSize(list);
        var records = Records(list);
        int start = U16(list), end = start + records.Count * size;   // end = its FF
        if (end + 1 == Info.Length && !Shared(list) && start >= 0x2C)
        {
            var grown = Info.Take(end).Concat(record).Append((byte)0xFF).ToArray();
            Info = grown;
        }
        else
        {
            int newStart = Info.Length;
            Info = Info.Concat(records.SelectMany(r => r.Bytes)).Concat(record).Append((byte)0xFF).ToArray();
            SetU16(list, newStart);
        }
        if (Info.Length > 0x0FFF) throw new InvalidOperationException("The map's info block would be bigger than the game can load (4 KB).");
        SetU16(0, Info.Length);
    }

    // takes record number index out of a list (the later ones move up)
    public void RemoveRecord(int list, int index)
    {
        int size = RecordSize(list);
        var records = Records(list);
        if (index < 0 || index >= records.Count) return;
        if (Shared(list))
        {
            // give it its own copy first
            int copyStart = Info.Length;
            Info = Info.Concat(records.SelectMany(r => r.Bytes)).Append((byte)0xFF).ToArray();
            SetU16(list, copyStart); SetU16(0, Info.Length);
            records = Records(list);
        }
        int start = U16(list);
        var rest = records.Where((_, i) => i != index).SelectMany(r => r.Bytes).Append((byte)0xFF).ToArray();
        rest.CopyTo(Info, start);
    }

    // ids

    // a trigger area id that's free in the edit and in the map's +8 event list
    public int NextTriggerId(RomBuffer rom)
    {
        var used = Records(Triggers).Select(r => (int)r.Bytes[0]).ToHashSet();
        used.UnionWith(_origTriggers);
        int block = L2Events.Block(rom, MapId);
        if (block >= 0) used.UnionWith(L2EventBlocks.ReadLists(rom, block).Where(e => e.Head == L2EventBlocks.TriggerList).Select(e => e.Id));
        int id = 1;
        while (used.Contains(id)) id++;
        return id;
    }

    // a map character id (0x50 up) nothing uses yet
    public int NextCharacterId(RomBuffer rom)
    {
        var used = Records(Characters).Select(r => 0x4F + r.Bytes[0]).ToHashSet();
        used.UnionWith(_origCharacters);
        used.UnionWith(L2EventBlocks.CharacterIds(rom, MapId));
        int id = 0x50;
        while (used.Contains(id)) id++;
        return id;
    }

    // a preset point id one higher than the highest
    public int NextPointId() => Records(Points).Select(r => (int)r.Bytes[0]).DefaultIfEmpty(-1).Max() + 1;

    // a path id one higher than the highest (FF ends the list, so FE at most)
    public int NextPathId()
    {
        int id = Records(Paths).Select(r => (int)r.Bytes[0]).DefaultIfEmpty(0).Max() + 1;
        return id <= 0xFE ? id : throw new InvalidOperationException("The map has no path number left.");
    }

    // writes path id (a new record, or the one with that id). its walk program goes at the end of the block and the
    // old program stays since other paths may share it, unless the old one was the block's last bytes, then it's replaced
    public void SetPath(int id, int x, int y, int flags, byte[] program)
    {
        var records = Records(Paths);
        int index = records.FindIndex(r => r.Bytes[0] == id);
        int programAt;
        if (index >= 0 && (records[index].Bytes[4] | records[index].Bytes[5] << 8) is int oldAt && L2Paths.ProgramAt(Info, oldAt) is { } oldProgram &&
            oldAt + oldProgram.Length == Info.Length && records.Count(r => (r.Bytes[4] | r.Bytes[5] << 8) == oldAt) == 1)
        {
            programAt = oldAt;
            Info = Info.Take(oldAt).Concat(program).ToArray();
        }
        else
        {
            programAt = Info.Length;
            Info = Info.Concat(program).ToArray();
        }
        if (Info.Length > 0x0FFF) throw new InvalidOperationException("The map's info block would be bigger than the game can load (4 KB).");
        SetU16(0, Info.Length);
        var record = new[] { (byte)id, (byte)x, (byte)y, (byte)flags, (byte)programAt, (byte)(programAt >> 8) };
        if (index >= 0) SetRecord(Paths, index, record); else AddRecord(Paths, record);
    }

    // treasure

    // the look (list +04 record) a treasure spot record names, or null
    public byte[]? LookOf(byte[] spotRecord) => Records(Looks).Select(r => r.Bytes).FirstOrDefault(r => r[0] == spotRecord[3]);

    // a look that copies a single block onto itself: opening shows nothing (hidden treasure)
    public static bool IsSelfLook(byte[]? look) => look != null && look[2] == look[6] && look[3] == look[7] && look[4] == 1 && look[5] == 1 && look[8] == 1 && look[9] == 1;

    // true if the treasure spot record is hidden (no chest picture)
    public bool IsHidden(byte[] spotRecord) => IsSelfLook(LookOf(spotRecord));

    // the chest record of a treasure spot id, or null
    public L2Chests.Rec? ChestOf(int spot) => Chests.FirstOrDefault(c => c.Spot == spot);

    // which layers a look copies (bit 0 ground, bit 1 upper). layer n is copied if any of bits 0x11 << n is set
    // ($83:8B6A: both = the whole cell, the low one = the block, the high one = its 04 / 08 walls)
    public static bool CopiesLayer(int layersByte, int layer) => (layersByte & (0x11 << layer)) != 0;

    // what was under the chest pictures this edit put down or moved (place -> each layer's block and flags), so a
    // chest that's moved or taken out gives its block back
    public Dictionary<(int X, int Y), (int Block, byte Flags)[]> Under { get; private set; } = new();

    public (int Block, byte Flags)[] CellAt(int x, int y) =>
        Map.Layers.Select(layer => (layer.Blocks[y * layer.Width + x], layer.Flags[y * layer.Width + x])).ToArray();

    private void SetCell(int x, int y, int layer, (int Block, byte Flags) cell)
    {
        var mapLayer = Map.Layers[layer];
        mapLayer.Blocks[y * mapLayer.Width + x] = cell.Block; mapLayer.Flags[y * mapLayer.Width + x] = cell.Flags;
    }

    // the floor for a block a chest leaves on that layer: whatever the 4 blocks around it have most (not counting
    // other treasure spots), or nothing (block 0) on the upper layer if that's what they have
    private (int Block, byte Flags) FloorAround(int x, int y, int layer)
    {
        var spots = Records(Treasure).Select(r => ((int)r.Bytes[1], (int)r.Bytes[2])).ToHashSet();
        var mapLayer = Map.Layers[layer];
        var neighbours = new List<(int B, byte F)>();
        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= mapLayer.Width || ny >= mapLayer.Height || spots.Contains((nx, ny))) continue;
            neighbours.Add((mapLayer.Blocks[ny * mapLayer.Width + nx], mapLayer.Flags[ny * mapLayer.Width + nx]));
        }
        if (neighbours.Count == 0) return (mapLayer.Blocks[y * mapLayer.Width + x], mapLayer.Flags[y * mapLayer.Width + x]);
        // the block they have most. its walls (04 above, 08 left) belong to the neighbour's own place, so leave them off
        var (block, flags) = neighbours.GroupBy(c => c.B).OrderByDescending(g => g.Count()).First().First();
        return (block, (byte)(flags & ~0x0C));
    }

    // paints a chest picture (each layer the look copies) at (x, y) and remembers what was there
    private void PutPicture(int x, int y, int layersByte, (int Block, byte Flags)[] picture)
    {
        Under[(x, y)] = CellAt(x, y);
        for (int layer = 0; layer < Map.Layers.Length && layer < picture.Length; layer++)
            if (CopiesLayer(layersByte, layer)) SetCell(x, y, layer, picture[layer]);
    }

    // takes a chest picture off (x, y): the block gets back what was there before, or the floor around it
    private void TakePicture(int x, int y, int layersByte)
    {
        Under.Remove((x, y), out var before);
        for (int layer = 0; layer < Map.Layers.Length; layer++)
            if (CopiesLayer(layersByte, layer)) SetCell(x, y, layer, before != null && layer < before.Length ? before[layer] : FloorAround(x, y, layer));
    }

    // the layers a treasure spot's chest picture is on (0 for a hidden one)
    public int PictureLayers(byte[] spot) => LookOf(spot) is { } look ? IsSelfLook(look) ? 0 : look[1] : 0;

    // puts the layers back as they were in an earlier state (same size), for moving a picture again during a drag
    public void RestoreLayers(State state, Dictionary<(int X, int Y), (int Block, byte Flags)[]> under)
    {
        if (state.W != Width || state.H != Height) return;
        for (int layer = 0; layer < Map.Layers.Length && layer < state.Blocks.Length; layer++)
        {
            Array.Copy(state.Blocks[layer], Map.Layers[layer].Blocks, state.Blocks[layer].Length);
            Array.Copy(state.Flags[layer], Map.Layers[layer].Flags, state.Flags[layer].Length);
        }
        Under = new(under);
    }

    // moves a chest's picture along with it from (ox, oy) to (nx, ny): the closed chest goes to the new spot and
    // the block it stood on gets its old floor back (or the floor around it). hidden treasure has no picture
    public void MoveChestPicture(byte[] spot, int ox, int oy, int nx, int ny)
    {
        int layers = PictureLayers(spot);
        if (layers == 0 || ox == nx && oy == ny || nx >= Width || ny >= Height) return;
        var picture = CellAt(ox, oy);
        TakePicture(ox, oy, layers);
        PutPicture(nx, ny, layers, picture);
    }

    // adds treasure at block (x, y): a chest, or a hidden one (a look that copies the spot onto itself so nothing
    // shows). a chest takes the look of from (a chest of any map, L2ChestLooks). its blocks are found in this map's
    // block set or added to it, and if this map has no look that opens that way, the open and closed pictures are
    // kept on two blocks where the camera doesn't go. with no from it takes the look of one of this map's chests.
    // it gets the next free spot id and an opened flag no treasure in the game uses.
    // returns its spot id and a note (blocks added, where the pictures are kept)
    public (int Spot, string Note) AddTreasure(RomBuffer rom, int x, int y, bool hidden, int item, L2ChestLooks.Look? from = null)
    {
        if (IsWorld) throw new InvalidOperationException("The world maps can't have treasure.");
        if (x < 0 || y < 0 || x >= Width || y >= Height) throw new InvalidOperationException("That block is outside the map.");
        var spots = Records(Treasure);
        if (spots.Any(r => r.Bytes[1] == x && r.Bytes[2] == y)) throw new InvalidOperationException("There is treasure on that block already.");
        var usedIds = spots.Select(r => r.Bytes[0] & 0x1F).Concat(Chests.Select(c => c.Spot)).ToHashSet();
        int spot = Enumerable.Range(0, 32).FirstOrDefault(i => !usedIds.Contains(i), -1);
        if (spot < 0) throw new InvalidOperationException("The map has 32 treasures already (the most the game's records can number).");
        int flag = L2Chests.FreeFlag(rom, Chests.Select(c => c.Flag), MapId);
        if (flag < 0) throw new InvalidOperationException("All 512 opened flags are used by treasure already.");
        int lookId;
        string note = "";
        int NewLookId()
        {
            var ids = Records(Looks).Select(r => (int)r.Bytes[0]).ToHashSet();
            int id = Enumerable.Range(0, 0xFF).FirstOrDefault(i => !ids.Contains(i), -1);
            return id >= 0 ? id : throw new InvalidOperationException("The map has no free look number.");
        }
        byte[]? ownChest = from != null && from.Map == MapId ? spots.Select(r => r.Bytes).FirstOrDefault(b => b[0] == from.Spot && b[3] == from.LookId) : null;
        if (hidden)
        {
            lookId = NewLookId();
            AddRecord(Looks, new byte[] { (byte)lookId, 0x03, (byte)x, (byte)y, 1, 1, (byte)x, (byte)y, 1, 1 });
        }
        else if (from == null || ownChest != null)
        {
            // a chest of this map (its spot has a chest record and a look that isn't hidden) gives the look and the picture
            var model = ownChest ?? spots.Select(r => r.Bytes).FirstOrDefault(b => ChestOf(b[0] & 0x1F) != null && LookOf(b) is { } l && !IsSelfLook(l));
            if (model == null) throw new InvalidOperationException("This map has no chest of its own: pick the chest picture of another map's chest (Chest picture, under the map).");
            lookId = model[3];
            PutPicture(x, y, PictureLayers(model), CellAt(model[1], model[2]));
        }
        else
        {
            // worked out first and only written once nothing below refuses the chest (a refused chest leaves the rom alone)
            var prepared = L2ChestLooks.Prepare(rom, from, this);
            var (closed, open, added) = (prepared.Closed, prepared.Open, prepared.Added);
            // the walls are this map's own, so the chest's blocks keep the flags the chosen block has here
            var here = CellAt(x, y);
            closed = closed.Select((c, i) => (c.Block, here[i].Flags)).ToArray();
            open = open.Select((c, i) => (c.Block, here[i].Flags)).ToArray();
            if (L2ChestLooks.LastTiles is { } tiles)
            {
                string nums = string.Join(", ", tiles.Tiles.Select(n => n.ToString("X3")));
                string rows = string.Join(" and ", tiles.Rows);
                note = $"{tiles.Tiles.Length} chest tile{(tiles.Tiles.Length == 1 ? "" : "s")} added to tile set {tiles.TileSet} (tile{(tiles.Tiles.Length == 1 ? "" : "s")} {nums}, "
                     + (tiles.CountAfter > tiles.CountBefore ? $"which grows from {tiles.CountBefore} to {tiles.CountAfter} tiles" : "which no block drew")
                     + $"), drawn with colour row{(tiles.Rows.Length == 1 ? "" : "s")} {rows}, "
                     + (tiles.Off <= L2ChestLooks.ColourTolerance ? "the closest to the chest's colours. " : $"the closest this map has, though the chest's colours look different here (off by {tiles.Off} of 765 per pixel). ");
            }
            if (added > 0) note += $"{added} block{(added == 1 ? "" : "s")} of that chest added to block set {BlockSetIndex} (Edit > Undo takes {(L2ChestLooks.LastTiles != null ? "them and the tiles" : "them")} out). ";
            // reuse a look of this map that opens the same way, otherwise make a new one with its pictures kept out of sight
            var sameLook = Records(Looks).Select(r => r.Bytes).FirstOrDefault(l => l[1] == from.Layers && l[4] == 1 && l[5] == 1 && !IsSelfLook(l) &&
                l[2] < Width && l[3] < Height && Enumerable.Range(0, Map.Layers.Length).All(i => !CopiesLayer(from.Layers, i) || CellAt(l[2], l[3])[i].Block == open[i].Block));
            if (sameLook != null) { lookId = sameLook[0]; prepared.Write(); }
            else
            {
                var (stashX, stashY, seen) = L2ChestLooks.Stash(rom, this, x, y);
                if (stashX < 0) throw new InvalidOperationException("There is no room on this map to keep the open chest's picture (two blocks side by side).");
                lookId = NewLookId();
                prepared.Write();
                PutPicture(stashX, stashY, from.Layers, open);
                PutPicture(stashX + 1, stashY, from.Layers, closed);
                Under.Remove((stashX, stashY)); Under.Remove((stashX + 1, stashY));
                AddRecord(Looks, new byte[] { (byte)lookId, (byte)from.Layers, (byte)stashX, (byte)stashY, 1, 1, (byte)(stashX + 1), (byte)stashY, 1, 1 });
                note += $"The open and closed chest are kept at ({stashX},{stashY}) and ({stashX + 1},{stashY}): opening copies the open one onto the chest"
                        + (seen ? "; the party may see them there, so if they show, move them somewhere out of sight. " : ", out of the camera's reach. ");
            }
            PutPicture(x, y, from.Layers, closed);
        }
        AddRecord(Treasure, new byte[] { (byte)spot, (byte)x, (byte)y, (byte)lookId });
        Chests.Add(new L2Chests.Rec(spot, flag, item, false));
        return (spot, note.Trim());
    }

    // takes treasure spot record number index out along with its chest record (and its hidden look, if nothing else
    // uses it). a chest's picture goes too, the block gets back what was under it or the floor around it
    public void RemoveTreasure(int index)
    {
        var spots = Records(Treasure);
        if (index < 0 || index >= spots.Count) return;
        var spotRecord = spots[index].Bytes;
        var look = LookOf(spotRecord);
        int layers = PictureLayers(spotRecord);
        RemoveRecord(Treasure, index);
        Chests.RemoveAll(c => c.Spot == (spotRecord[0] & 0x1F));
        if (layers != 0 && spotRecord[1] < Width && spotRecord[2] < Height) TakePicture(spotRecord[1], spotRecord[2], layers);
        if (IsSelfLook(look) && !Records(Treasure).Any(r => r.Bytes[3] == spotRecord[3]))
        {
            int lookIndex = Records(Looks).FindIndex(r => r.Bytes[0] == spotRecord[3]);
            if (lookIndex >= 0) RemoveRecord(Looks, lookIndex);
        }
    }

    // keeps every hidden treasure's look on its spot (after the spot moved)
    public void SyncHiddenLooks()
    {
        var looks = Records(Looks);
        foreach (var (_, spotRecord) in Records(Treasure))
        {
            int lookIndex = looks.FindIndex(r => r.Bytes[0] == spotRecord[3]);
            if (lookIndex < 0 || !IsSelfLook(looks[lookIndex].Bytes) || Records(Treasure).Count(r => r.Bytes[3] == spotRecord[3]) != 1) continue;
            var look = looks[lookIndex].Bytes;
            if (look[2] == spotRecord[1] && look[3] == spotRecord[2]) continue;
            look[2] = look[6] = spotRecord[1]; look[3] = look[7] = spotRecord[2];
            SetRecord(Looks, lookIndex, look);
        }
    }

    // a map's treasure records the way the editors keep them (gold as L2Gold.EditorBase + amount)
    public static List<L2Chests.Rec> EditorRecords(RomBuffer rom, int map) =>
        L2Chests.Records(rom, map).Select(r => r with { Item = L2Gold.ToEditor(rom, r.Item) }).ToList();

    // three-way merge of a map's treasure records by spot: mine (this edit) against base (what it started from)
    // and theirs (the rom now). a record this edit didn't change takes the rom's version, and records the rom
    // gained (not in base) stay
    public static List<L2Chests.Rec> Merge(IReadOnlyList<L2Chests.Rec> @base, IReadOnlyList<L2Chests.Rec> mine, IReadOnlyList<L2Chests.Rec> theirs)
    {
        var merged = new List<L2Chests.Rec>();
        foreach (var mineRec in mine)
        {
            var baseRec = @base.FirstOrDefault(x => x.Spot == mineRec.Spot);
            var theirRec = theirs.FirstOrDefault(x => x.Spot == mineRec.Spot);
            merged.Add(baseRec != null && baseRec == mineRec && theirRec != null ? theirRec : mineRec);
        }
        foreach (var theirRec in theirs)
            if (!@base.Any(x => x.Spot == theirRec.Spot) && !merged.Any(x => x.Spot == theirRec.Spot)) merged.Add(theirRec);
        return merged;
    }

    // the treasure changed in the rom since this edit read it (the Treasure tab): this edit takes those changes for
    // the records it didn't change itself, and the rom's records become its base.
    // returns (old base, new base), or null if nothing changed
    public (List<L2Chests.Rec> Old, List<L2Chests.Rec> New)? RebaseChests(RomBuffer rom)
    {
        if (IsWorld) return null;
        var now = EditorRecords(rom, MapId);
        if (now.SequenceEqual(_origChests)) return null;
        var old = _origChests;
        Chests = Merge(old, Chests, now);
        _origChests = now;
        return (old, now);
    }

    // changes what a treasure spot holds
    public void SetTreasureItem(int spot, int item)
    {
        int i = Chests.FindIndex(c => c.Spot == spot);
        if (i >= 0) Chests[i] = Chests[i] with { Item = item };
    }

    // the whole map

    // gives the map a new size. both layers are kept from the top left, new cells get fill on the ground
    // (nothing above, no walls)
    public void Resize(int w, int h, int fill)
    {
        if (w < 16 || h < 14) throw new InvalidOperationException("A map needs at least 16 x 14 blocks (one screen).");
        if (w > MaxWidth || h > MaxHeight || w * h > MaxCells)
            throw new InvalidOperationException($"The biggest map the game has is {MaxWidth} blocks wide, {MaxHeight} high and {MaxCells} blocks in all: stay within that.");
        var layers = new L2Maps.Layer[Map.Layers.Length];
        for (int l = 0; l < layers.Length; l++)
        {
            var old = Map.Layers[l];
            var blocks = new int[w * h]; var flags = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (x < old.Width && y < old.Height) { blocks[y * w + x] = old.Blocks[y * old.Width + x]; flags[y * w + x] = old.Flags[y * old.Width + x]; }
                    else if (l == 0) blocks[y * w + x] = fill;
                }
            layers[l] = new L2Maps.Layer { Width = w, Height = h, Blocks = blocks, Flags = flags };
        }
        Map = new L2Maps.Map { Id = Map.Id, Name = Map.Name, Layers = layers, BlockSet = Map.BlockSet, TileSet = Map.TileSet };
    }

    // the map with the picked block set and tile set, for drawing
    public L2Maps.Map ForDrawing => new()
    {
        Id = Map.Id, Name = Map.Name, Layers = Map.Layers,
        BlockSet = L2Maps.BlockSetFileBase + BlockSetIndex, TileSet = L2Maps.TileSetFileBase + TileSetIndex, PaletteSet = PaletteSetIndex, AnimIndex = AnimIndex,
    };

    // undo

    // everything the editor can change, for one undo step
    public sealed record State(int W, int H, int[][] Blocks, byte[][] Flags, byte[] Info, Dictionary<int, int> Sprites, int BlockSet, int TileSet, int PaletteSet, List<L2Chests.Rec> Chests, int Anim, int ColourAnim, int GroundMode);

    public State Save() => new(Width, Height, Map.Layers.Select(l => (int[])l.Blocks.Clone()).ToArray(), Map.Layers.Select(l => (byte[])l.Flags.Clone()).ToArray(),
                               (byte[])Info.Clone(), new Dictionary<int, int>(Sprites), BlockSetIndex, TileSetIndex, PaletteSetIndex, new List<L2Chests.Rec>(Chests), AnimIndex, ColourAnimIndex, GroundMode);

    public void Restore(State state)
    {
        var layers = state.Blocks.Select((b, i) => new L2Maps.Layer { Width = state.W, Height = state.H, Blocks = (int[])b.Clone(), Flags = (byte[])state.Flags[i].Clone() }).ToArray();
        Map = new L2Maps.Map { Id = Map.Id, Name = Map.Name, Layers = layers, BlockSet = Map.BlockSet, TileSet = Map.TileSet };
        Info = (byte[])state.Info.Clone();
        Sprites = new Dictionary<int, int>(state.Sprites);
        BlockSetIndex = state.BlockSet; TileSetIndex = state.TileSet; PaletteSetIndex = state.PaletteSet; AnimIndex = state.Anim; ColourAnimIndex = state.ColourAnim; GroundMode = state.GroundMode;
        Chests = new List<L2Chests.Rec>(state.Chests);
    }

    public static bool Same(State a, State b) =>
        a.W == b.W && a.H == b.H && a.BlockSet == b.BlockSet && a.TileSet == b.TileSet && a.PaletteSet == b.PaletteSet && a.Anim == b.Anim && a.ColourAnim == b.ColourAnim && a.GroundMode == b.GroundMode &&
        a.Info.AsSpan().SequenceEqual(b.Info) && a.Chests.SequenceEqual(b.Chests) && a.Sprites.Count == b.Sprites.Count && a.Sprites.All(kv => b.Sprites.TryGetValue(kv.Key, out var v) && v == kv.Value) &&
        a.Blocks.Zip(b.Blocks).All(p => p.First.AsSpan().SequenceEqual(p.Second)) && a.Flags.Zip(b.Flags).All(p => p.First.AsSpan().SequenceEqual(p.Second));

    // what changed

    public bool LayersChanged => Width != _origWidth || Height != _origHeight ||
        Map.Layers.Select((l, i) => !l.Blocks.AsSpan().SequenceEqual(_origBlocks[i]) || !l.Flags.AsSpan().SequenceEqual(_origFlags[i])).Any(x => x);
    public bool InfoChanged => !Info.AsSpan().SequenceEqual(_origInfo);
    // the character's sprite was changed here (not written yet)
    public bool SpritesEdited(int id) => Sprites.TryGetValue(id, out int sprite) && (!_origSprites.TryGetValue(id, out int origSprite) || origSprite != sprite);

    public bool SpritesChanged => Sprites.Count != _origSprites.Count || Sprites.Any(kv => !_origSprites.TryGetValue(kv.Key, out var v) || v != kv.Value);
    public bool SetsChanged => BlockSetIndex != _origBlockSet || TileSetIndex != _origTileSet || PaletteSetIndex != _origPaletteSet ||
                               AnimIndex != _origAnim || ColourAnimIndex != _origColourAnim;
    public bool ChestsChanged => !Chests.SequenceEqual(_origChests);
    public bool ModeChanged => GroundMode != _origGroundMode;
    public bool HasChanges => ModeChanged || LayersChanged || InfoChanged || SpritesChanged || SetsChanged || ChestsChanged;

    // writing

    // writes every change to the rom and returns what was done. if a change fails (a start-up script that can't be
    // changed, too many characters, no room) the rom stays as it was, info block too (L2Rom.AllOrNothing)
    public string Apply(RomBuffer rom) => L2Rom.AllOrNothing(rom, () => ApplyCore(rom));

    private string ApplyCore(RomBuffer rom)
    {
        var done = new List<string>();
        int block = L2Events.Block(rom, MapId);
        var characters = Records(Characters).Select(r => 0x4F + r.Bytes[0]).ToHashSet();
        var triggers = Records(Triggers).Select(r => (int)r.Bytes[0]).ToHashSet();
        var removedChars = _origCharacters.Except(characters).ToList();
        var addedChars = characters.Except(_origCharacters).ToList();
        var removedTriggers = _origTriggers.Except(triggers).ToList();
        var addedTriggers = triggers.Except(_origTriggers).ToList();
        if ((addedTriggers.Count > 0 || removedTriggers.Count > 0 || removedChars.Count > 0) && block < 0)
            throw new InvalidOperationException("This map has no event block, so step-on areas and character events can't change.");

        // the info block
        if (InfoChanged)
        {
            bool shared = Enumerable.Range(0, L2Maps.Count(rom)).Count(m => L2MapInfo.Block(rom, m) == _origInfoAt) > 1;
            // the block may have moved since this edit was read (the world map's moves when treasure lists need its room)
            if (Info.Length == _origInfo.Length && !shared && _origInfoAt >= 0 && L2MapInfo.Block(rom, MapId) == _origInfoAt)
                rom.WriteBytes(_origInfoAt, Info);
            else
            {
                int infoAt = L2Rom.Allocate(rom, Info.Length + 1, upper: true);
                rom.WriteBytes(infoAt, Info.Append((byte)0).ToArray());
                int snesAddress = L2Rom.FileToSnes(infoAt);
                rom.WriteBytes(L2MapInfo.PointerAt(rom, MapId), new[] { (byte)snesAddress, (byte)(snesAddress >> 8), (byte)(snesAddress >> 16) });
            }
            done.Add("exits, areas, characters and points");
        }

        // characters: the creates in the start-up script
        if (!IsWorld && (addedChars.Count > 0 || removedChars.Count > 0 || SpritesChanged))
        {
            int initScript = L2EventBlocks.InitScript(rom, MapId);
            var script = L2EventScript.Read(rom, initScript, L2EventBlocks.InitBase(initScript));
            if (script.Problem != null) throw new InvalidOperationException("The map's start-up script can't be changed: " + script.Problem);
            // new sprites on a map are FB, FC or FD. we keep the ones the other scripts and the characters that stay use
            bool Changes(int id) => removedChars.Contains(id) || Sprites.TryGetValue(id, out int sprite) && (!_origSprites.TryGetValue(id, out int origSprite) || origSprite != sprite);
            var inUse = L2MapInfo.NewSpriteBytesUsed(rom, MapId, startUp: false);
            foreach (var step in script.Steps)
                if (!step.IsText && step.Op is 0x68 or 0x7B or 0x34 && step.Bytes.Length >= 3 && !Changes(step.Bytes[1])) inUse.Add(step.Bytes[2]);
            byte GameSpriteByte(int sprite) { int spriteByte = L2Sprites.ToGame(rom, MapId, sprite, inUse); inUse.Add(spriteByte); return (byte)spriteByte; }
            foreach (var step in script.Steps.ToList())
            {
                if (step.IsText || step.Op is not (0x68 or 0x7B) || step.Bytes.Length < 3) continue;
                int id = step.Bytes[1];
                if (removedChars.Contains(id))
                {
                    int i = script.Steps.IndexOf(step);
                    var next = i + 1 < script.Steps.Count ? script.Steps[i + 1] : null;
                    foreach (var other in script.Steps) foreach (var jump in other.Jumps) if (jump.Step == step) jump.Step = next;
                    script.Steps.Remove(step);
                }
                else if (Sprites.TryGetValue(id, out int sprite) && (!_origSprites.TryGetValue(id, out int origSprite) || origSprite != sprite) && step.Op is 0x68 or 0x7B)
                    step.Bytes = new[] { step.Bytes[0], step.Bytes[1], GameSpriteByte(sprite) };
            }
            foreach (int id in addedChars.OrderByDescending(x => x))
                script.Steps.Insert(0, new L2Step { Bytes = new byte[] { 0x68, (byte)id, GameSpriteByte(Sprites.GetValueOrDefault(id, 0x0C)) } });
            if (script.Steps.Count(s => s.Op is 0x68 or 0x7B) > 31) throw new InvalidOperationException("The map would have more characters than the game can show (31).");
            L2EventBlocks.SaveInit(rom, MapId, script);
            done.Add("characters");
        }

        // events that go with characters and areas
        if (block >= 0)
        {
            foreach (var e in L2EventBlocks.ReadLists(rom, block).ToList())
                if (e.Head == L2EventBlocks.TalkList && removedChars.Contains(e.Id) || e.Head == L2EventBlocks.TriggerList && removedTriggers.Contains(e.Id))
                    L2EventBlocks.Remove(rom, MapId, e);
            foreach (int id in addedTriggers)
                if (!L2EventBlocks.ReadLists(rom, L2Events.Block(rom, MapId)).Any(e => e.Head == L2EventBlocks.TriggerList && e.Id == id))
                    L2EventBlocks.Add(rom, MapId, L2EventBlocks.TriggerList, id, L2EventScript.New(L2Events.Block(rom, MapId)));
        }

        // treasure
        if (ChestsChanged)
        {
            if (TableGuard.LockReason(rom, "Treasure") is { } locked) throw new InvalidOperationException(locked);
            // whatever the Treasure tab (or anything else) changed in the rom since this edit was read stays, only the
            // records changed here get written over it
            var merged = Merge(_origChests, Chests, EditorRecords(rom, MapId));
            L2Chests.WriteList(rom, MapId, L2Gold.ToRom(rom, MapId, merged));
            done.Add("treasure");
        }

        if ((LayersChanged || ModeChanged) && !IsWorld) { L2Maps.Write(rom, Map, GroundMode); done.Add(ModeChanged && !LayersChanged ? "ground layer scrolling" : "blocks"); }

        // graphics
        if (SetsChanged && !IsWorld)
        {
            rom.WriteByte(L2Maps.BlockSetAt(rom, MapId), (byte)BlockSetIndex);
            rom.WriteByte(L2Maps.TileSetAt(rom, MapId), (byte)TileSetIndex);
            rom.WriteByte(L2Maps.PaletteSetAt(rom, MapId), (byte)PaletteSetIndex);
            rom.WriteByte(L2Maps.AnimIndexAt(rom, MapId), (byte)AnimIndex);
            rom.WriteByte(L2Maps.ColourAnimAt(rom, MapId), (byte)ColourAnimIndex);
            done.Add("graphics");
        }
        return done.Count == 0 ? "Nothing changed." : "Written: " + string.Join(", ", done) + ".";
    }

    // test start

    // the map, scene and preset point a new game starts on right now
    public static (int Map, int Scene, int Point) NewGame(RomBuffer rom) =>
        (rom.ReadByte(L2StoryClear.NewGameMapAt), rom.ReadByte(L2StoryClear.NewGameSceneAt), rom.ReadByte(L2StoryClear.NewGamePresetAt));

    // makes a new game start on map at preset point point, with a scene of its own added there (show Maxim, fade in).
    // with play (an event of the map, a stepping-on cutscene) the scene goes on with a copy of that event's steps so it
    // plays as the game starts. it's a copy because a jump from one event into another can't be laid out again, the
    // block's scripts move. for test roms, or a hack's own start
    public static string SetTestStart(RomBuffer rom, int map, int point, IReadOnlyCollection<int>? flags = null, IReadOnlyDictionary<int, int>? counters = null,
        (int Head, int Id)? play = null, IReadOnlyList<(int Id, int Map, int Spot)>? place = null, IReadOnlyList<int>? party = null)
    {
        if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
        if (map < 2) throw new InvalidOperationException("A new game can't start on the world map (the party would stand in the sea): start in a town next to an exit to the world map.");
        if (!L2StoryClear.PresetPoints(rom, map).Any(p => p.Id == point)) throw new InvalidOperationException($"Map {map:X2} has no preset point {point}.");
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block for the start scene.");
        L2EventScript Scene(int eventBlock)
        {
            L2EventScript script;
            if (play != null)
            {
                var entry = L2EventBlocks.ReadLists(rom, eventBlock).FirstOrDefault(e => e.Head == play.Value.Head && e.Id == play.Value.Id)
                        ?? throw new InvalidOperationException("The cutscene to play is no longer in the map's lists.");
                script = L2EventScript.Read(rom, eventBlock + entry.Offset, eventBlock);
                if (script.Problem != null) throw new InvalidOperationException("The cutscene can't be copied into the start: " + script.Problem);
            }
            else script = L2EventScript.New(eventBlock);
            // AA = puzzle switches 00-1F off ($80:AC18). otherwise a test start keeps whatever was in RAM and a puzzle's
            // set-up would draw it solved
            script.Steps.InsertRange(0, new[] { new byte[] { 0x2D, 0x00 }, new byte[] { 0x59, 0x18 }, new byte[] { 0x76 }, new byte[] { 0xAA } }.Select(b => new L2Step { Bytes = b }));
            return script;
        }
        // reuse a scene we wrote before (F0 up, starts with "2D 00 59 18 76")
        bool IsMine(int eventBlock, L2EventBlocks.Entry e) => e.Head == L2EventBlocks.SceneList && e.Id >= 0xF0 &&
            rom.ReadBytes(eventBlock + e.Offset, 5).SequenceEqual(new byte[] { 0x2D, 0x00, 0x59, 0x18, 0x76 });
        var mine = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => IsMine(block, e));
        int id;
        if (mine != null) { id = mine.Id; L2EventBlocks.Save(rom, map, mine, Scene(block)); }
        else
        {
            var scenes = L2EventBlocks.ReadLists(rom, block).Where(e => e.Head == L2EventBlocks.SceneList).Select(e => e.Id).ToHashSet();
            id = 0xF0;
            while (scenes.Contains(id) && id < 0xFE) id++;
            if (scenes.Contains(id)) throw new InvalidOperationException("The map has no free scene number.");
            L2EventBlocks.Add(rom, map, L2EventBlocks.SceneList, id, Scene(block));
        }
        if ((flags?.Count ?? 0) + (counters?.Count ?? 0) + (place?.Count ?? 0) > 0 || PartyChanges(rom, party).Count > 0)
            return StartWithFlags(rom, map, id, point, flags, counters, place, party);
        rom.WriteByte(L2StoryClear.NewGameMapAt, (byte)map);
        rom.WriteByte(L2StoryClear.NewGameSceneAt, (byte)id);
        rom.WriteByte(L2StoryClear.NewGamePresetAt, (byte)point);
        return $"A new game now starts on map {map:X2} at point {point} (scene {id:X2}).";
    }

    // a new game starts with story flags on and counters set. a scene of its own (F0 up: "1A ff"s, "1D cc nn"s, then
    // "16 map scene point") sets them and sends the party to scene of the map, so the map loads again with them set
    // (its characters, its "on entering" event and the cutscene's checks all see them).
    // a scene we wrote before in that shape gets reused
    public static string StartWithFlags(RomBuffer rom, int map, int scene, int point, IReadOnlyCollection<int>? flags, IReadOnlyDictionary<int, int>? counters,
        IReadOnlyList<(int Id, int Map, int Spot)>? place = null, IReadOnlyList<int>? party = null)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block for the start scene.");
        var script = L2EventScript.New(block);
        var steps = new List<L2Step>();
        // the party the story has here: heroes join (2B, their "in the party" flag on) or leave (2C, flag off) like in the game
        var changes = PartyChanges(rom, party);
        foreach (var (hero, joins) in changes)
        {
            steps.Add(new L2Step { Bytes = new byte[] { (byte)(joins ? 0x2B : 0x2C), (byte)hero } });
            steps.Add(new L2Step { Bytes = new byte[] { (byte)(joins ? 0x1A : 0x1B), (byte)L2StoryParty.HeroFlag[hero] } });
        }
        foreach (int f in (flags ?? Array.Empty<int>()).Distinct().OrderBy(f => f)) steps.Add(new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.On, f) });
        foreach (var kv in (counters ?? new Dictionary<int, int>()).OrderBy(kv => kv.Key)) steps.Add(new L2Step { Bytes = new byte[] { 0x1D, (byte)kv.Key, (byte)kv.Value } });
        // persistent characters the cutscene uses get sent to their place on this map (35 id map spot), like an event would
        foreach (var (persistentId, persistentMap, spot) in place ?? Array.Empty<(int, int, int)>()) steps.Add(new L2Step { Bytes = new byte[] { 0x35, (byte)persistentId, (byte)persistentMap, (byte)spot } });
        steps.Add(new L2Step { Bytes = new byte[] { 0x16, (byte)map, (byte)scene, (byte)point } });
        script.Steps.InsertRange(0, steps);
        var lists = L2EventBlocks.ReadLists(rom, block);
        var mine = lists.FirstOrDefault(e => e.Head == L2EventBlocks.SceneList && e.Id >= 0xF0 && IsFlagSetter(rom, block + e.Offset));
        int id;
        if (mine != null) { id = mine.Id; L2EventBlocks.Save(rom, map, mine, script); }
        else
        {
            var used = lists.Where(e => e.Head == L2EventBlocks.SceneList).Select(e => e.Id).ToHashSet();
            id = 0xF0;
            while (used.Contains(id) && id < 0xFE) id++;
            if (used.Contains(id)) throw new InvalidOperationException("The map has no free scene number.");
            L2EventBlocks.Add(rom, map, L2EventBlocks.SceneList, id, script);
        }
        rom.WriteByte(L2StoryClear.NewGameMapAt, (byte)map);
        rom.WriteByte(L2StoryClear.NewGameSceneAt, (byte)id);
        rom.WriteByte(L2StoryClear.NewGamePresetAt, (byte)point);
        var what = new List<string>();
        if (flags?.Count > 0) what.Add("story flag" + (flags.Count > 1 ? "s " : " ") + string.Join(" ", flags.OrderBy(f => f).Select(f => f.ToString("X2"))) + " on");
        if (counters?.Count > 0) what.Add(string.Join(", ", counters.OrderBy(kv => kv.Key).Select(kv => $"counter {kv.Key:X2} = {kv.Value}")));
        if (place?.Count > 0) what.Add("persistent character" + (place.Count > 1 ? "s " : " ") + string.Join(" ", place.Select(p => p.Id.ToString("X2"))) + " here");
        if (party != null && party.Count > 0) what.Add("the party " + string.Join(", ", party.Select(h => L2EventCommands.HeroNames[h])));
        return $"A new game now starts on map {map:X2} at point {point} with {string.Join(" and ", what)}.";
    }

    // the joins and leaves that turn the new game's party into party (heroes 1-6, Maxim stays the leader)
    public static List<(int Hero, bool Joins)> PartyChanges(RomBuffer rom, IReadOnlyList<int>? party)
    {
        var changes = new List<(int, bool)>();
        if (party == null || party.Count == 0) return changes;
        var start = L2Characters.ReadStart(rom);
        var now = start.Party.Take(Math.Clamp(start.PartySize, 1, 4)).Where(h => h > 0 && h < 7).ToList();
        foreach (int h in now.Where(h => !party.Contains(h))) changes.Add((h, false));
        foreach (int h in party.Where(h => h > 0 && h < 7 && !now.Contains(h))) changes.Add((h, true));
        return changes;
    }

    // a scene StartWithFlags made: only flag and counter sets and persistent characters sent, then "go to a map"
    private static bool IsFlagSetter(RomBuffer rom, int offset)
    {
        for (int guard = 0; guard < 300 && offset + 4 < rom.Length; guard++)
        {
            int op = rom.ReadByte(offset);
            if (op is 0x1A or 0x1B or 0x2B or 0x2C or L2ExtraFlags.OpOn) offset += 2;
            else if (op == 0x1D) offset += 3;
            else if (op == 0x35) offset += 4;
            else return op == 0x16;   // the end after it goes when it's laid out again, "go to" never falls through
        }
        return false;
    }

    // the game's own start: map 02, scene 01, preset point 06 (the opening)
    public static void SetNormalStart(RomBuffer rom)
    {
        rom.WriteByte(L2StoryClear.NewGameMapAt, 0x02);
        rom.WriteByte(L2StoryClear.NewGameSceneAt, 0x01);
        rom.WriteByte(L2StoryClear.NewGamePresetAt, 0x06);
    }
}
