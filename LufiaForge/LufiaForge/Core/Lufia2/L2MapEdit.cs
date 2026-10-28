namespace LufiaForge.Core.Lufia2;

/// <summary>
/// A Lufia II map being edited in the Maps tab: its two layers, a working copy of its info block (exits, step-on
/// areas, characters, preset points, treasure spots...), the characters' sprites and its block set, tile set and
/// palette set. Nothing goes into the ROM until <see cref="Apply"/>; a <see cref="State"/> is one step of the editor's
/// undo. On Apply the info block is written over its old bytes when it kept its size (and no other map shares it),
/// else into the added space with the map's pointer moved; characters added or taken out change the start-up script
/// (68 id sprite) and the talk events (+6), step-on areas added or taken out get or lose their event (+8).
/// </summary>
public sealed class L2MapEdit
{
    public int MapId { get; }
    public L2Maps.Map Map { get; private set; }
    /// <summary>The info block as edited (u16 size, 21 u16 list offsets, the lists).</summary>
    public byte[] Info { get; private set; }
    /// <summary>The sprite of each map character (actor id 0x50 + n), as it will be created.</summary>
    public Dictionary<int, int> Sprites { get; private set; }
    public int BlockSetIndex { get; set; }
    public int TileSetIndex { get; set; }
    public int PaletteSetIndex { get; set; }

    private readonly byte[] _origInfo;
    private readonly int _origInfoAt;
    private readonly Dictionary<int, int> _origSprites;
    private readonly HashSet<int> _origCharacters, _origTriggers;
    private readonly int _origBlockSet, _origTileSet, _origPaletteSet;
    private readonly int _origWidth, _origHeight;
    private readonly int[][] _origBlocks;
    private readonly byte[][] _origFlags;

    // ---------------------------------------------------------------- lists of the info block (offsets in it)

    public const int Doors = 0x02, Looks = 0x04, Exits = 0x06, Passages = 0x0A, Triggers = 0x0C, Points = 0x0E, Characters = 0x10,
                     Paths = 0x12, Areas = 0x14, Treasure = 0x26;
    public static int RecordSize(int list) => list switch
    {
        Doors => 15, Looks => 10, Exits => 9, Passages => 5, Triggers => 5, Points => 3, Characters => 8, Paths => 6, Areas => 5, Treasure => 4,
        0x1A or 0x1C or 0x1E or 0x20 or 0x28 or 0x2A => 3, _ => 0,
    };

    /// <summary>Biggest map the game has (so a resized map stays within what the game's buffers already hold).</summary>
    public const int MaxWidth = 96, MaxHeight = 88, MaxCells = 7744;

    /// <summary>The treasure of the map (the chest list at $91:B8B5, <see cref="L2Chests"/>), as edited.</summary>
    public List<L2Chests.Rec> Chests { get; private set; }
    private List<L2Chests.Rec> _origChests;

    /// <summary>Whether this is the world map or the sea floor: only the info block (exits, points) is edited here; their
    /// blocks are the Maps tab's world map editor.</summary>
    public bool IsWorld => MapId < 2;

    public L2MapEdit(RomBuffer rom, int map)
    {
        MapId = map;
        // the world maps' blocks are big blocks in their own files: a 256 x 256 grid of 16-pixel blocks stands for them
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
        BlockSetIndex = _origBlockSet = rom.ReadByte(L2Maps.BlockSetList + map);
        TileSetIndex = _origTileSet = rom.ReadByte(L2Maps.TileSetList + map);
        PaletteSetIndex = _origPaletteSet = rom.ReadByte(L2Maps.PaletteSetList + map);
        _origWidth = Map.Layers[0].Width; _origHeight = Map.Layers[0].Height;
        _origBlocks = Map.Layers.Select(l => (int[])l.Blocks.Clone()).ToArray();
        _origFlags = Map.Layers.Select(l => (byte[])l.Flags.Clone()).ToArray();
    }

    public bool HasInfo => Info.Length >= 0x2C;
    public int Width => Map.Layers[0].Width;
    public int Height => Map.Layers[0].Height;

    /// <summary>The records of a list: offset in <see cref="Info"/> and a copy of the bytes.</summary>
    public List<(int Off, byte[] Bytes)> Records(int list)
    {
        var l = new List<(int, byte[])>();
        int size = RecordSize(list);
        if (!HasInfo || size == 0) return l;
        int p = U16(list);
        for (int guard = 0; guard < 128 && p >= 0x2C && p + size <= Info.Length && Info[p] != 0xFF; guard++, p += size)
            l.Add((p, Info.AsSpan(p, size).ToArray()));
        return l;
    }

    private int U16(int at) => Info[at] | Info[at + 1] << 8;
    private void SetU16(int at, int v) { Info[at] = (byte)v; Info[at + 1] = (byte)(v >> 8); }

    /// <summary>Writes record <paramref name="index"/> of a list.</summary>
    public void SetRecord(int list, int index, byte[] bytes)
    {
        var r = Records(list)[index];
        bytes.CopyTo(Info, r.Off);
    }

    /// <summary>Whether another list starts at the same place (the game's empty lists share one FF).</summary>
    private bool Shared(int list) => Enumerable.Range(1, 21).Select(i => i * 2).Any(o => o != list && o + 1 < Info.Length && U16(o) == U16(list));

    /// <summary>Puts the list at the end of the block (copied, with <paramref name="extra"/> added), unless it is the last
    /// thing there already and shared with no other list, in which case it just grows.</summary>
    public void AddRecord(int list, byte[] record)
    {
        int size = RecordSize(list);
        var recs = Records(list);
        int start = U16(list), end = start + recs.Count * size;   // end = its FF
        if (end + 1 == Info.Length && !Shared(list) && start >= 0x2C)
        {
            var grown = Info.Take(end).Concat(record).Append((byte)0xFF).ToArray();
            Info = grown;
        }
        else
        {
            int at = Info.Length;
            Info = Info.Concat(recs.SelectMany(r => r.Bytes)).Concat(record).Append((byte)0xFF).ToArray();
            SetU16(list, at);
        }
        if (Info.Length > 0x0FFF) throw new InvalidOperationException("The map's info block would be bigger than the game can load (4 KB).");
        SetU16(0, Info.Length);
    }

    /// <summary>Takes record <paramref name="index"/> out of a list (the later ones move up).</summary>
    public void RemoveRecord(int list, int index)
    {
        int size = RecordSize(list);
        var recs = Records(list);
        if (index < 0 || index >= recs.Count) return;
        if (Shared(list))
        {
            // its own copy first
            int at = Info.Length;
            Info = Info.Concat(recs.SelectMany(r => r.Bytes)).Append((byte)0xFF).ToArray();
            SetU16(list, at); SetU16(0, Info.Length);
            recs = Records(list);
        }
        int start = U16(list);
        var rest = recs.Where((_, i) => i != index).SelectMany(r => r.Bytes).Append((byte)0xFF).ToArray();
        rest.CopyTo(Info, start);
    }

    // ---------------------------------------------------------------- ids

    /// <summary>A trigger-area id free in the edit and in the map's +8 event list.</summary>
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

    /// <summary>A map character id (0x50 up) nothing uses yet.</summary>
    public int NextCharacterId(RomBuffer rom)
    {
        var used = Records(Characters).Select(r => 0x4F + r.Bytes[0]).ToHashSet();
        used.UnionWith(_origCharacters);
        used.UnionWith(L2EventBlocks.CharacterIds(rom, MapId));
        int id = 0x50;
        while (used.Contains(id)) id++;
        return id;
    }

    /// <summary>A preset point id one higher than the highest.</summary>
    public int NextPointId() => Records(Points).Select(r => (int)r.Bytes[0]).DefaultIfEmpty(-1).Max() + 1;

    // ---------------------------------------------------------------- treasure

    /// <summary>The look (list +04 record) treasure spot record <paramref name="b"/> names, or null.</summary>
    public byte[]? LookOf(byte[] b) => Records(Looks).Select(r => r.Bytes).FirstOrDefault(r => r[0] == b[3]);

    /// <summary>A look that copies a single block onto itself: opening shows nothing (a hidden treasure).</summary>
    public static bool IsSelfLook(byte[]? look) => look != null && look[2] == look[6] && look[3] == look[7] && look[4] == 1 && look[5] == 1 && look[8] == 1 && look[9] == 1;

    /// <summary>Whether treasure spot record <paramref name="b"/> is hidden (no chest picture).</summary>
    public bool IsHidden(byte[] b) => IsSelfLook(LookOf(b));

    /// <summary>The chest record of a treasure spot id, or null.</summary>
    public L2Chests.Rec? ChestOf(int spot) => Chests.FirstOrDefault(c => c.Spot == spot);

    /// <summary>Which layers a look copies (bit 0 ground, bit 1 upper): layer n is copied when any of bits 0x11 &lt;&lt; n is
    /// set ($83:8B6A: both = the whole cell, the low one = the block, the high one = its 04 / 08 walls).</summary>
    public static bool CopiesLayer(int layersByte, int layer) => (layersByte & (0x11 << layer)) != 0;

    /// <summary>What lay under the chest pictures this edit put down or moved (place -> each layer's block and flags), so
    /// a chest moved or taken out gives its block back.</summary>
    public Dictionary<(int X, int Y), (int Block, byte Flags)[]> Under { get; private set; } = new();

    public (int Block, byte Flags)[] CellAt(int x, int y) =>
        Map.Layers.Select(L => (L.Blocks[y * L.Width + x], L.Flags[y * L.Width + x])).ToArray();

    private void SetCell(int x, int y, int layer, (int Block, byte Flags) c)
    {
        var L = Map.Layers[layer];
        L.Blocks[y * L.Width + x] = c.Block; L.Flags[y * L.Width + x] = c.Flags;
    }

    /// <summary>The floor for a block a chest leaves on layer <paramref name="layer"/>: what the 4 blocks around it have
    /// most (leaving out other treasure spots), or nothing (block 0) on the upper layer when that is what they have.</summary>
    private (int Block, byte Flags) FloorAround(int x, int y, int layer)
    {
        var spots = Records(Treasure).Select(r => ((int)r.Bytes[1], (int)r.Bytes[2])).ToHashSet();
        var L = Map.Layers[layer];
        var near = new List<(int B, byte F)>();
        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= L.Width || ny >= L.Height || spots.Contains((nx, ny))) continue;
            near.Add((L.Blocks[ny * L.Width + nx], L.Flags[ny * L.Width + nx]));
        }
        if (near.Count == 0) return (L.Blocks[y * L.Width + x], L.Flags[y * L.Width + x]);
        // the block they have most; its walls (04 above, 08 left) belong to the neighbour's own place, so they are left off
        var (b, f) = near.GroupBy(c => c.B).OrderByDescending(g => g.Count()).First().First();
        return (b, (byte)(f & ~0x0C));
    }

    /// <summary>Paints a chest picture (each layer the look copies) at (x, y), remembering what was there.</summary>
    private void PutPicture(int x, int y, int layersByte, (int Block, byte Flags)[] pic)
    {
        Under[(x, y)] = CellAt(x, y);
        for (int l = 0; l < Map.Layers.Length && l < pic.Length; l++)
            if (CopiesLayer(layersByte, l)) SetCell(x, y, l, pic[l]);
    }

    /// <summary>Takes a chest picture off (x, y): the block gets what lay there before, else the floor around it.</summary>
    private void TakePicture(int x, int y, int layersByte)
    {
        Under.Remove((x, y), out var was);
        for (int l = 0; l < Map.Layers.Length; l++)
            if (CopiesLayer(layersByte, l)) SetCell(x, y, l, was != null && l < was.Length ? was[l] : FloorAround(x, y, l));
    }

    /// <summary>The layers a treasure spot's chest picture is on (0 for a hidden one).</summary>
    public int PictureLayers(byte[] spot) => LookOf(spot) is { } l ? IsSelfLook(l) ? 0 : l[1] : 0;

    /// <summary>The layers of the map as in an earlier state (same size), for moving a picture again during a drag.</summary>
    public void RestoreLayers(State s, Dictionary<(int X, int Y), (int Block, byte Flags)[]> under)
    {
        if (s.W != Width || s.H != Height) return;
        for (int l = 0; l < Map.Layers.Length && l < s.Blocks.Length; l++)
        {
            Array.Copy(s.Blocks[l], Map.Layers[l].Blocks, s.Blocks[l].Length);
            Array.Copy(s.Flags[l], Map.Layers[l].Flags, s.Flags[l].Length);
        }
        Under = new(under);
    }

    /// <summary>Moves a chest's picture with it, from (ox, oy) to (nx, ny): the closed chest goes where it goes, and the
    /// block it stood on gets the floor it had (or the floor around it). Hidden treasure has no picture.</summary>
    public void MoveChestPicture(byte[] spot, int ox, int oy, int nx, int ny)
    {
        int layers = PictureLayers(spot);
        if (layers == 0 || ox == nx && oy == ny || nx >= Width || ny >= Height) return;
        var pic = CellAt(ox, oy);
        TakePicture(ox, oy, layers);
        PutPicture(nx, ny, layers, pic);
    }

    /// <summary>Adds treasure at block (x, y): a chest or a hidden one (a look that copies the spot onto itself, so nothing
    /// shows). A chest takes the look of <paramref name="from"/> (a chest of any map, <see cref="L2ChestLooks"/>: its
    /// blocks are found in this map's block set or added to it, and when this map has no look that opens that way, the
    /// open and closed pictures are kept on two blocks where the camera doesn't go) or, with none given, of a chest of
    /// this map. It gets the next free spot id and an opened flag no treasure of the game uses. Returns its spot id and
    /// a note (blocks added, where the pictures are kept).</summary>
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
        int look;
        string note = "";
        int NewLookId()
        {
            var ids = Records(Looks).Select(r => (int)r.Bytes[0]).ToHashSet();
            int id = Enumerable.Range(0, 0xFF).FirstOrDefault(i => !ids.Contains(i), -1);
            return id >= 0 ? id : throw new InvalidOperationException("The map has no free look number.");
        }
        byte[]? own = from != null && from.Map == MapId ? spots.Select(r => r.Bytes).FirstOrDefault(b => b[0] == from.Spot && b[3] == from.LookId) : null;
        if (hidden)
        {
            look = NewLookId();
            AddRecord(Looks, new byte[] { (byte)look, 0x03, (byte)x, (byte)y, 1, 1, (byte)x, (byte)y, 1, 1 });
        }
        else if (from == null || own != null)
        {
            // a chest of the map (its spot has a chest record and a look that isn't hidden) gives the look and the picture
            var model = own ?? spots.Select(r => r.Bytes).FirstOrDefault(b => ChestOf(b[0] & 0x1F) != null && LookOf(b) is { } l && !IsSelfLook(l));
            if (model == null) throw new InvalidOperationException("This map has no chest of its own: pick the chest picture of another map's chest (Chest picture, under the map).");
            look = model[3];
            PutPicture(x, y, PictureLayers(model), CellAt(model[1], model[2]));
        }
        else
        {
            // worked out first, written only once nothing below refuses the chest (a refused chest leaves the ROM alone)
            var prepared = L2ChestLooks.Prepare(rom, from, this);
            var (closed, open, added) = (prepared.Closed, prepared.Open, prepared.Added);
            // the walls are this map's own: the chest's blocks keep the flags the chosen block has here
            var here = CellAt(x, y);
            closed = closed.Select((c, i) => (c.Block, here[i].Flags)).ToArray();
            open = open.Select((c, i) => (c.Block, here[i].Flags)).ToArray();
            if (L2ChestLooks.LastTiles is { } t)
            {
                string nums = string.Join(", ", t.Tiles.Select(n => n.ToString("X3")));
                string rows = string.Join(" and ", t.Rows);
                note = $"{t.Tiles.Length} chest tile{(t.Tiles.Length == 1 ? "" : "s")} added to tile set {t.TileSet} (tile{(t.Tiles.Length == 1 ? "" : "s")} {nums}, "
                     + (t.CountAfter > t.CountBefore ? $"which grows from {t.CountBefore} to {t.CountAfter} tiles" : "which no block drew")
                     + $"), drawn with colour row{(t.Rows.Length == 1 ? "" : "s")} {rows}, "
                     + (t.Off <= L2ChestLooks.ColourTolerance ? "the closest to the chest's colours. " : $"the closest this map has, though the chest's colours look different here (off by {t.Off} of 765 per pixel). ");
            }
            if (added > 0) note += $"{added} block{(added == 1 ? "" : "s")} of that chest added to block set {BlockSetIndex} (Edit > Undo takes {(L2ChestLooks.LastTiles != null ? "them and the tiles" : "them")} out). ";
            // a look of this map that opens the same way is used again, else a new one with its pictures kept out of sight
            var same = Records(Looks).Select(r => r.Bytes).FirstOrDefault(l => l[1] == from.Layers && l[4] == 1 && l[5] == 1 && !IsSelfLook(l) &&
                l[2] < Width && l[3] < Height && Enumerable.Range(0, Map.Layers.Length).All(i => !CopiesLayer(from.Layers, i) || CellAt(l[2], l[3])[i].Block == open[i].Block));
            if (same != null) { look = same[0]; prepared.Write(); }
            else
            {
                var (sx, sy, seen) = L2ChestLooks.Stash(rom, this, x, y);
                if (sx < 0) throw new InvalidOperationException("There is no room on this map to keep the open chest's picture (two blocks side by side).");
                look = NewLookId();
                prepared.Write();
                PutPicture(sx, sy, from.Layers, open);
                PutPicture(sx + 1, sy, from.Layers, closed);
                Under.Remove((sx, sy)); Under.Remove((sx + 1, sy));
                AddRecord(Looks, new byte[] { (byte)look, (byte)from.Layers, (byte)sx, (byte)sy, 1, 1, (byte)(sx + 1), (byte)sy, 1, 1 });
                note += $"The open and closed chest are kept at ({sx},{sy}) and ({sx + 1},{sy}): opening copies the open one onto the chest"
                        + (seen ? "; the party may see them there, so if they show, move them somewhere out of sight. " : ", out of the camera's reach. ");
            }
            PutPicture(x, y, from.Layers, closed);
        }
        AddRecord(Treasure, new byte[] { (byte)spot, (byte)x, (byte)y, (byte)look });
        Chests.Add(new L2Chests.Rec(spot, flag, item, false));
        return (spot, note.Trim());
    }

    /// <summary>Takes treasure spot record <paramref name="index"/> out with its chest record (and its hidden look, when
    /// nothing else uses it). A chest's picture goes too: the block gets what lay under it, or the floor around it.</summary>
    public void RemoveTreasure(int index)
    {
        var spots = Records(Treasure);
        if (index < 0 || index >= spots.Count) return;
        var b = spots[index].Bytes;
        var look = LookOf(b);
        int layers = PictureLayers(b);
        RemoveRecord(Treasure, index);
        Chests.RemoveAll(c => c.Spot == (b[0] & 0x1F));
        if (layers != 0 && b[1] < Width && b[2] < Height) TakePicture(b[1], b[2], layers);
        if (IsSelfLook(look) && !Records(Treasure).Any(r => r.Bytes[3] == b[3]))
        {
            int li = Records(Looks).FindIndex(r => r.Bytes[0] == b[3]);
            if (li >= 0) RemoveRecord(Looks, li);
        }
    }

    /// <summary>Keeps every hidden treasure's look on its spot (after the spot moved).</summary>
    public void SyncHiddenLooks()
    {
        var looks = Records(Looks);
        foreach (var (_, b) in Records(Treasure))
        {
            int li = looks.FindIndex(r => r.Bytes[0] == b[3]);
            if (li < 0 || !IsSelfLook(looks[li].Bytes) || Records(Treasure).Count(r => r.Bytes[3] == b[3]) != 1) continue;
            var l = looks[li].Bytes;
            if (l[2] == b[1] && l[3] == b[2]) continue;
            l[2] = l[6] = b[1]; l[3] = l[7] = b[2];
            SetRecord(Looks, li, l);
        }
    }

    /// <summary>A map's treasure records as the editors keep them (gold as <see cref="L2Gold.EditorBase"/> + amount).</summary>
    public static List<L2Chests.Rec> EditorRecords(RomBuffer rom, int map) =>
        L2Chests.Records(rom, map).Select(r => r with { Item = L2Gold.ToEditor(rom, r.Item) }).ToList();

    /// <summary>Three-way merge of a map's treasure records by spot: <paramref name="mine"/> (this edit) against
    /// <paramref name="base"/> (what it started from) and <paramref name="theirs"/> (the ROM now). A record this edit
    /// didn't change takes the ROM's version; records the ROM gained (not in the base) stay.</summary>
    public static List<L2Chests.Rec> Merge(IReadOnlyList<L2Chests.Rec> @base, IReadOnlyList<L2Chests.Rec> mine, IReadOnlyList<L2Chests.Rec> theirs)
    {
        var outp = new List<L2Chests.Rec>();
        foreach (var m in mine)
        {
            var b = @base.FirstOrDefault(x => x.Spot == m.Spot);
            var t = theirs.FirstOrDefault(x => x.Spot == m.Spot);
            outp.Add(b != null && b == m && t != null ? t : m);
        }
        foreach (var t in theirs)
            if (!@base.Any(x => x.Spot == t.Spot) && !outp.Any(x => x.Spot == t.Spot)) outp.Add(t);
        return outp;
    }

    /// <summary>The treasure changed in the ROM since this edit read it (the Treasure tab): this edit takes those changes
    /// for the records it didn't change itself, and the ROM's records become its base. Returns (old base, new base), or
    /// null when nothing changed.</summary>
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

    /// <summary>Changes what a treasure spot holds.</summary>
    public void SetTreasureItem(int spot, int item)
    {
        int i = Chests.FindIndex(c => c.Spot == spot);
        if (i >= 0) Chests[i] = Chests[i] with { Item = item };
    }

    // ---------------------------------------------------------------- the whole map

    /// <summary>Gives the map a new size: both layers, kept from the top left, new cells get <paramref name="fill"/>
    /// on the ground (nothing above, no walls).</summary>
    public void Resize(int w, int h, int fill)
    {
        if (w < 16 || h < 14) throw new InvalidOperationException("A map needs at least 16 x 14 blocks (one screen).");
        if (w > MaxWidth || h > MaxHeight || w * h > MaxCells)
            throw new InvalidOperationException($"The biggest map the game has is {MaxWidth} blocks wide, {MaxHeight} high and {MaxCells} blocks in all: stay within that.");
        var layers = new L2Maps.Layer[Map.Layers.Length];
        for (int l = 0; l < layers.Length; l++)
        {
            var old = Map.Layers[l];
            var b = new int[w * h]; var f = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (x < old.Width && y < old.Height) { b[y * w + x] = old.Blocks[y * old.Width + x]; f[y * w + x] = old.Flags[y * old.Width + x]; }
                    else if (l == 0) b[y * w + x] = fill;
                }
            layers[l] = new L2Maps.Layer { Width = w, Height = h, Blocks = b, Flags = f };
        }
        Map = new L2Maps.Map { Id = Map.Id, Name = Map.Name, Layers = layers, BlockSet = Map.BlockSet, TileSet = Map.TileSet };
    }

    /// <summary>The map with the picked block set and tile set (for drawing).</summary>
    public L2Maps.Map ForDrawing => new()
    {
        Id = Map.Id, Name = Map.Name, Layers = Map.Layers,
        BlockSet = L2Maps.BlockSetFileBase + BlockSetIndex, TileSet = L2Maps.TileSetFileBase + TileSetIndex, PaletteSet = PaletteSetIndex,
    };

    // ---------------------------------------------------------------- undo

    /// <summary>Everything the editor can change, for one undo step.</summary>
    public sealed record State(int W, int H, int[][] Blocks, byte[][] Flags, byte[] Info, Dictionary<int, int> Sprites, int BlockSet, int TileSet, int PaletteSet, List<L2Chests.Rec> Chests);

    public State Save() => new(Width, Height, Map.Layers.Select(l => (int[])l.Blocks.Clone()).ToArray(), Map.Layers.Select(l => (byte[])l.Flags.Clone()).ToArray(),
                               (byte[])Info.Clone(), new Dictionary<int, int>(Sprites), BlockSetIndex, TileSetIndex, PaletteSetIndex, new List<L2Chests.Rec>(Chests));

    public void Restore(State s)
    {
        var layers = s.Blocks.Select((b, i) => new L2Maps.Layer { Width = s.W, Height = s.H, Blocks = (int[])b.Clone(), Flags = (byte[])s.Flags[i].Clone() }).ToArray();
        Map = new L2Maps.Map { Id = Map.Id, Name = Map.Name, Layers = layers, BlockSet = Map.BlockSet, TileSet = Map.TileSet };
        Info = (byte[])s.Info.Clone();
        Sprites = new Dictionary<int, int>(s.Sprites);
        BlockSetIndex = s.BlockSet; TileSetIndex = s.TileSet; PaletteSetIndex = s.PaletteSet;
        Chests = new List<L2Chests.Rec>(s.Chests);
    }

    public static bool Same(State a, State b) =>
        a.W == b.W && a.H == b.H && a.BlockSet == b.BlockSet && a.TileSet == b.TileSet && a.PaletteSet == b.PaletteSet &&
        a.Info.AsSpan().SequenceEqual(b.Info) && a.Chests.SequenceEqual(b.Chests) && a.Sprites.Count == b.Sprites.Count && a.Sprites.All(kv => b.Sprites.TryGetValue(kv.Key, out var v) && v == kv.Value) &&
        a.Blocks.Zip(b.Blocks).All(p => p.First.AsSpan().SequenceEqual(p.Second)) && a.Flags.Zip(b.Flags).All(p => p.First.AsSpan().SequenceEqual(p.Second));

    // ---------------------------------------------------------------- what changed

    public bool LayersChanged => Width != _origWidth || Height != _origHeight ||
        Map.Layers.Select((l, i) => !l.Blocks.AsSpan().SequenceEqual(_origBlocks[i]) || !l.Flags.AsSpan().SequenceEqual(_origFlags[i])).Any(x => x);
    public bool InfoChanged => !Info.AsSpan().SequenceEqual(_origInfo);
    public bool SpritesChanged => Sprites.Count != _origSprites.Count || Sprites.Any(kv => !_origSprites.TryGetValue(kv.Key, out var v) || v != kv.Value);
    public bool SetsChanged => BlockSetIndex != _origBlockSet || TileSetIndex != _origTileSet || PaletteSetIndex != _origPaletteSet;
    public bool ChestsChanged => !Chests.SequenceEqual(_origChests);
    public bool HasChanges => LayersChanged || InfoChanged || SpritesChanged || SetsChanged || ChestsChanged;

    // ---------------------------------------------------------------- writing

    /// <summary>Writes every change to the ROM. Returns what was done.</summary>
    public string Apply(RomBuffer rom)
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
            bool shared = Enumerable.Range(0, L2Maps.Count).Count(m => L2MapInfo.Block(rom, m) == _origInfoAt) > 1;
            // (the block may have moved since this edit was read: the world map's goes when treasure lists need its room)
            if (Info.Length == _origInfo.Length && !shared && _origInfoAt >= 0 && L2MapInfo.Block(rom, MapId) == _origInfoAt)
                rom.WriteBytes(_origInfoAt, Info);
            else
            {
                int at = L2Rom.Allocate(rom, Info.Length + 1, upper: true);
                rom.WriteBytes(at, Info.Append((byte)0).ToArray());
                int snes = L2Rom.FileToSnes(at);
                rom.WriteBytes(L2MapInfo.Pointers + MapId * 3, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
            }
            done.Add("exits, areas, characters and points");
        }

        // characters: the start-up script's creates
        if (!IsWorld && (addedChars.Count > 0 || removedChars.Count > 0 || SpritesChanged))
        {
            int init = L2EventBlocks.InitScript(rom, MapId);
            var script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
            if (script.Problem != null) throw new InvalidOperationException("The map's start-up script can't be changed: " + script.Problem);
            // new sprites are FB, FC or FD on a map: those the other scripts and the characters that stay use are kept
            bool Changes(int id) => removedChars.Contains(id) || Sprites.TryGetValue(id, out int v) && (!_origSprites.TryGetValue(id, out int o0) || o0 != v);
            var inUse = L2MapInfo.NewSpriteBytesUsed(rom, MapId, startUp: false);
            foreach (var s in script.Steps)
                if (!s.IsText && s.Op is 0x68 or 0x7B or 0x34 && s.Bytes.Length >= 3 && !Changes(s.Bytes[1])) inUse.Add(s.Bytes[2]);
            byte Game(int sprite) { int b = L2Sprites.ToGame(rom, MapId, sprite, inUse); inUse.Add(b); return (byte)b; }
            foreach (var s in script.Steps.ToList())
            {
                if (s.IsText || s.Op is not (0x68 or 0x7B) || s.Bytes.Length < 3) continue;
                int id = s.Bytes[1];
                if (removedChars.Contains(id))
                {
                    int i = script.Steps.IndexOf(s);
                    var next = i + 1 < script.Steps.Count ? script.Steps[i + 1] : null;
                    foreach (var st in script.Steps) foreach (var j in st.Jumps) if (j.Step == s) j.Step = next;
                    script.Steps.Remove(s);
                }
                else if (Sprites.TryGetValue(id, out int spr) && (!_origSprites.TryGetValue(id, out int o) || o != spr) && s.Op is 0x68 or 0x7B)
                    s.Bytes = new[] { s.Bytes[0], s.Bytes[1], Game(spr) };
            }
            foreach (int id in addedChars.OrderByDescending(x => x))
                script.Steps.Insert(0, new L2Step { Bytes = new byte[] { 0x68, (byte)id, Game(Sprites.GetValueOrDefault(id, 0x0C)) } });
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
            // what the Treasure tab (or anything else) changed in the ROM since this edit was read stays: only the
            // records changed here are written over it
            var merged = Merge(_origChests, Chests, EditorRecords(rom, MapId));
            L2Chests.WriteList(rom, MapId, L2Gold.ToRom(rom, MapId, merged));
            done.Add("treasure");
        }

        // the layers
        if (LayersChanged && !IsWorld) { L2Maps.Write(rom, Map); done.Add("blocks"); }

        // graphics
        if (SetsChanged && !IsWorld)
        {
            rom.WriteByte(L2Maps.BlockSetList + MapId, (byte)BlockSetIndex);
            rom.WriteByte(L2Maps.TileSetList + MapId, (byte)TileSetIndex);
            rom.WriteByte(L2Maps.PaletteSetList + MapId, (byte)PaletteSetIndex);
            done.Add("graphics");
        }
        return done.Count == 0 ? "Nothing changed." : "Written: " + string.Join(", ", done) + ".";
    }

    // ---------------------------------------------------------------- test start

    /// <summary>The map, scene and preset point a new game starts on now.</summary>
    public static (int Map, int Scene, int Point) NewGame(RomBuffer rom) =>
        (rom.ReadByte(L2StoryClear.NewGameMapAt), rom.ReadByte(L2StoryClear.NewGameSceneAt), rom.ReadByte(L2StoryClear.NewGamePresetAt));

    /// <summary>A new game starts on <paramref name="map"/> at preset point <paramref name="point"/>: a scene of its own
    /// is added there (show Maxim, fade in). For test ROMs.</summary>
    public static string SetTestStart(RomBuffer rom, int map, int point)
    {
        if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
        if (map < 2) throw new InvalidOperationException("A new game can't start on the world map (the party would stand in the sea): start in a town next to an exit to the world map.");
        if (!L2StoryClear.PresetPoints(rom, map).Any(p => p.Id == point)) throw new InvalidOperationException($"Map {map:X2} has no preset point {point}.");
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block for the start scene.");
        var scenes = L2EventBlocks.ReadLists(rom, block).Where(e => e.Head == L2EventBlocks.SceneList).Select(e => e.Id).ToHashSet();
        int id = 0xF0;
        while (scenes.Contains(id) && id < 0xFE) id++;
        if (scenes.Contains(id))
            throw new InvalidOperationException("The map has no free scene number.");
        // a scene written before (F0 up, ending "2D 00 59 18 76 00") is used again
        var s = L2EventScript.New(block);
        foreach (var b in new[] { new byte[] { 0x2D, 0x00 }, new byte[] { 0x59, 0x18 }, new byte[] { 0x76 } })
            s.Steps.Insert(s.Steps.Count - 1, new L2Step { Bytes = b });
        var mine = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.SceneList && e.Id >= 0xF0 &&
            rom.ReadBytes(block + e.Offset, 6).SequenceEqual(new byte[] { 0x2D, 0x00, 0x59, 0x18, 0x76, 0x00 }));
        if (mine != null) id = mine.Id; else L2EventBlocks.Add(rom, map, L2EventBlocks.SceneList, id, s);
        rom.WriteByte(L2StoryClear.NewGameMapAt, (byte)map);
        rom.WriteByte(L2StoryClear.NewGameSceneAt, (byte)id);
        rom.WriteByte(L2StoryClear.NewGamePresetAt, (byte)point);
        return $"A new game now starts on map {map:X2} at point {point} (scene {id:X2}).";
    }

    /// <summary>The game's own start: map 02, scene 01, preset point 06 (the opening).</summary>
    public static void SetNormalStart(RomBuffer rom)
    {
        rom.WriteByte(L2StoryClear.NewGameMapAt, 0x02);
        rom.WriteByte(L2StoryClear.NewGameSceneAt, 0x01);
        rom.WriteByte(L2StoryClear.NewGamePresetAt, 0x06);
    }
}
