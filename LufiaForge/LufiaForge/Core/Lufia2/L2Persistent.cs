namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II's persistent characters (story characters): actors 10-4F (Tia 10 in Elcid, the elder of Chaed 2D, ...).
/// No map script makes them; the game keeps where each one is in WRAM ($081E + n: its id, FF = none; $085E + n: map;
/// $089E + n: character start; $08DE + n: sprite) and, after a map's start-up script, puts every one whose map it is on
/// that map at the map's character start with that number (map info +10; $83:B34C). A new game fills those from a
/// table in bank $87 ($83:ADDF: the word at $87:8003 points at it from $87:8000; 64 records of 4 bytes: id (FF =
/// none), sprite, map, start). Event command 35 id map start sends one to another map ($80:B2B1; it is hidden at once
/// when it is on the map shown, and stands on the new map from the next time that map loads). "Hide a character" on
/// one lasts until its map loads again. The game is done with one by sending it to Arek Daos Shrine (map 02), starts
/// 07-0E there, which all stand at (0, 37) outside the room. Record 4F is the game's own: the map-load code reads its
/// bytes as a parked ship's place ($83:B3A2).
/// </summary>
public static class L2Persistent
{
    public const int First = 0x10, Count = 0x40;
    /// <summary>The last one a hack can use (4F's record is the parked ship's).</summary>
    public const int LastUsable = 0x4E;
    /// <summary>Where the game sends a story character it is done with (Tia, when she joins the party).</summary>
    public const int AwayMap = 0x02, AwaySpot = 0x07;

    /// <summary>One of the game's "away" places: Arek Daos Shrine starts 07-0E, outside the room.</summary>
    public static bool IsAway(int map, int spot) => map == AwayMap && spot is >= 0x07 and <= 0x0E;

    /// <summary>A place a persistent character stands: on <see cref="Map"/> at character start <see cref="Spot"/>;
    /// <see cref="From"/> is -1 for where a new game puts it, else the map whose event sends it there.</summary>
    public sealed record Place(int Id, int Sprite, int Map, int Spot, int From);

    /// <summary>File offset of the new-game table.</summary>
    public static int Table(RomBuffer rom) => L2Events.HeaderTable + rom.ReadUInt16Le(L2Events.HeaderTable + 3);

    private static bool TableOk(RomBuffer rom) => Table(rom) is var t && t >= 0 && t + Count * 4 <= rom.Length;

    /// <summary>The new-game record of <paramref name="id"/>: id (FF = none), sprite, map, start.</summary>
    public static byte[] Record(RomBuffer rom, int id) => rom.ReadBytes(Table(rom) + (id - First) * 4, 4);

    /// <summary>The game has this story character (its record isn't FF): events can send it and it stands on its map.
    /// One whose record is FF never shows up anywhere, even when an event sends it.</summary>
    public static bool Exists(RomBuffer rom, int id) => id is >= First and <= LastUsable && TableOk(rom) && rom.ReadByte(Table(rom) + (id - First) * 4) != 0xFF;

    /// <summary>Where a new game puts <paramref name="id"/>, with its sprite (one of the game's 251: a new sprite
    /// belongs to one map, and a story character goes from map to map).</summary>
    public static void Set(RomBuffer rom, int id, int sprite, int map, int spot)
    {
        if (id is < First or > LastUsable) throw new InvalidOperationException($"Story characters are 10-{LastUsable:X2}.");
        if (sprite is < 0 or >= L2Sprites.GameCount) throw new InvalidOperationException("A story character needs one of the game's sprites (00-FA): a new sprite belongs to one map, and a story character can go to any map.");
        if (!TableOk(rom)) throw new InvalidOperationException("The story characters' table isn't where the game keeps it.");
        rom.WriteBytes(Table(rom) + (id - First) * 4, new[] { (byte)id, (byte)sprite, (byte)map, (byte)spot });
    }

    /// <summary>Takes <paramref name="id"/> out of the game (its record FF): it never shows up again.</summary>
    public static void Remove(RomBuffer rom, int id)
    {
        if (id is < First or > LastUsable || !TableOk(rom)) return;
        rom.WriteBytes(Table(rom) + (id - First) * 4, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
    }

    /// <summary>Where a new game puts each persistent character (id, sprite, map, spot).</summary>
    public static List<Place> Start(RomBuffer rom)
    {
        var list = new List<Place>();
        if (!TableOk(rom)) return list;
        int t = Table(rom);
        for (int i = 0; i < Count; i++)
        {
            var e = rom.ReadBytes(t + i * 4, 4);
            if (e[0] != 0xFF) list.Add(new Place(First + i, e[1], e[2], e[3], -1));
        }
        return list;
    }

    /// <summary>Every place a persistent character stands: where a new game puts it and every map an event (35) sends
    /// it to, with its sprite from the table. Read from every map's scripts once per ROM version (<see cref="Uses"/>).</summary>
    public static List<Place> All(RomBuffer rom)
    {
        var start = Start(rom);
        var sprite = start.ToDictionary(p => p.Id, p => p.Sprite);
        var list = new List<Place>(start);
        foreach (var u in Uses(rom))
            if (u.ToMap >= 0 && sprite.TryGetValue(u.Id, out int s))
            {
                var p = new Place(u.Id, s, u.ToMap, u.ToSpot, u.Map);
                if (!list.Any(x => x.Id == p.Id && x.Map == p.Map && x.Spot == p.Spot)) list.Add(p);
            }
        return list;
    }

    /// <summary>The persistent characters that stand on <paramref name="map"/> at some point, by character start.</summary>
    public static List<Place> On(IEnumerable<Place> all, int map) => all.Where(p => p.Map == map).ToList();

    // ------------------------------------------------------------------ where they are used

    /// <summary>A step or event that names a story character: <see cref="How"/> "sends", "talked to" or "uses";
    /// <see cref="Head"/> / <see cref="ListId"/> the event (head -1: the start-up script), <see cref="At"/> the step's
    /// file offset; a send's map and start in <see cref="ToMap"/> / <see cref="ToSpot"/> (else -1).</summary>
    public sealed record Use(int Id, string How, int Map, int Head, int ListId, int At, string Where, int ToMap = -1, int ToSpot = -1);

    private static RomBuffer? _rom;
    private static int _version = -1;
    private static List<Use> _uses = new();

    /// <summary>Every use of a story character in every map's events (kept until the ROM changes).</summary>
    public static IReadOnlyList<Use> Uses(RomBuffer rom)
    {
        if (ReferenceEquals(rom, _rom) && _version == rom.Version) return _uses;
        var list = new List<Use>();
        int maps = L2Maps.Count(rom);
        for (int m = 0; m < maps; m++)
        {
            try { UsesOn(rom, m, list); } catch (Exception) { }
        }
        _rom = rom; _version = rom.Version; _uses = list;
        return list;
    }

    private static void UsesOn(RomBuffer rom, int m, List<Use> list)
    {
        void Walk(int head, int id, string where, int start, int jumpBase)
        {
            foreach (var o in L2Script.Walk(rom, new[] { start }, jumpBase).Where(o => !o.IsText))
            {
                var b = rom.ReadBytes(o.At, o.Length);
                if (b[0] == 0x35 && b.Length == 4)
                {
                    if (b[1] is >= First and < First + Count) list.Add(new Use(b[1], "sends", m, head, id, o.At, where, b[2], b[3]));
                    continue;
                }
                if (L2EventCommands.Find(b[0]) is not { } def) continue;
                foreach (var f in def.FieldsFor(b))
                    if (f.Kind is L2Kind.Who or L2Kind.WhoOrAll)
                    {
                        int v;
                        try { v = f.Get(b); } catch (Exception) { continue; }
                        if (v is >= First and < First + Count && !list.Any(u => u.At == o.At && u.Id == v)) list.Add(new Use(v, "uses", m, head, id, o.At, where));
                    }
            }
        }
        int init = L2EventBlocks.InitScript(rom, m);
        Walk(L2FlagIndex.InitHead, -1, "Start-up script", init, L2EventBlocks.InitBase(init));
        int block = L2Events.Block(rom, m);
        if (block < 0) return;
        foreach (var e in L2EventBlocks.ReadLists(rom, block))
        {
            if (!e.IsScript) continue;
            string title = L2FlagIndex.Lists.FirstOrDefault(l => l.Head == e.Head).Title ?? $"List {e.Head:X2}";
            string where = e.Head == L2EventBlocks.TalkList ? $"{title} {(e.Id >= 0x50 ? $"character {e.Id - 0x4F}" : $"story character {e.Id:X2}")}" : $"{title} {e.Id:X2}";
            if (e.Head == L2EventBlocks.TalkList && e.Id is >= First and < First + Count) list.Add(new Use(e.Id, "talked to", m, e.Head, e.Id, block + e.Offset, where));
            Walk(e.Head, e.Id, where, block + e.Offset, block);
        }
    }

    /// <summary>A story character nothing uses: its record is FF, no event names it and it has no name of the user's.
    /// -1 when none is left.</summary>
    public static int FreeId(RomBuffer rom)
    {
        var used = Uses(rom).Select(u => u.Id).ToHashSet();
        for (int id = First; id <= LastUsable; id++)
            if (!Exists(rom, id) && !used.Contains(id) && L2FlagNotes.Name(L2FlagIndex.Kind.Character, id) == null) return id;
        return -1;
    }

    /// <summary>A new story character with sprite <paramref name="sprite"/>, away (on no map the party sees) until
    /// an event sends it to a map or its new-game place is set. Returns its id.</summary>
    public static int New(RomBuffer rom, int sprite)
    {
        int id = FreeId(rom);
        if (id < 0) throw new InvalidOperationException($"All the story characters (10-{LastUsable:X2}) are taken.");
        Set(rom, id, sprite, AwayMap, AwaySpot);
        return id;
    }

    /// <summary>A character start on (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="map"/> for a
    /// story character: one there already that no map character uses, else a new one (standing still) with a number
    /// no map character has. Returns its number.</summary>
    public static int StartAt(RomBuffer rom, int map, int x, int y)
    {
        var made = L2MapInfo.Stages(rom, map).Select(c => c.Id).ToHashSet();
        foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
            if (b[1] == x && b[2] == y && !made.Contains(0x4F + b[0])) return b[0];
        var used = L2EventBlocks.CharacterIds(rom, map);
        // past the numbers map characters and field monsters use (actors 50-7F: starts 01-30), so none made later
        // shares it ($80:C01D finds a start by its number in the list, whatever the number)
        int n = 0x31;
        while (n < 0xFF && used.Contains(0x4F + n)) n++;
        if (n >= 0xFF) throw new InvalidOperationException("The map has no character start number left.");
        int w = 255, h = 255;
        try { var mm = L2Maps.Read(rom, map); w = Math.Min(255, mm.Layers.Max(l => l.Width)); h = Math.Min(255, mm.Layers.Max(l => l.Height)); } catch (Exception) { }
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Characters, 8, new[] { (byte)n, (byte)x, (byte)y, (byte)0, (byte)0, (byte)w, (byte)h, (byte)0x04 }))
            throw new InvalidOperationException("The map's info block would be too big.");
        return n;
    }

    /// <summary>Why map character <paramref name="charId"/> can't become a story character, or null.</summary>
    public static string? WhyNotStory(RomBuffer rom, int map, int charId)
    {
        if (charId is < 0x50 or > 0x7F) return "Only a map character (made by the start-up script) can become a story character.";
        int init = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
        if (script.Problem != null) return "The map's start-up script can't be changed: " + script.Problem;
        var creates = script.Steps.Where(s => !s.IsText && s.Op is 0x68 or 0x7B && s.Bytes.Length >= 3 && s.Bytes[1] == charId).ToList();
        if (creates.Count == 0) return "The start-up script doesn't make it.";
        if (creates.Count > 1) return "The start-up script makes it in more than one place (other sprites under other story flags).";
        if (creates[0].Op == 0x7B) return "A field monster can't be a story character.";
        if (creates[0].Bytes[2] >= L2Sprites.FirstSlot) return "It has one of this map's new sprites; a story character needs one of the game's (a new sprite belongs to one map).";
        if (L2MapInfo.Stages(rom, map).FirstOrDefault(c => c.Id == charId) is { When.Length: > 0 } c)
            return $"It is only made while {c.When}. A story character stands on its map until an event sends it away: set When… to Always first.";
        if (!L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Any(r => r.Bytes[0] == charId - 0x4F)) return "It has no character start.";
        if (FreeId(rom) < 0) return $"All the story characters (10-{LastUsable:X2}) are taken.";
        return null;
    }

    /// <summary>Map character <paramref name="charId"/> becomes a story character: a free id gets its sprite, this map
    /// and its character start as its new-game place; its create goes out of the start-up script; every step of the
    /// map's events that names it, and its talk event, use the new id. Returns the new id.</summary>
    public static int MakeStory(RomBuffer rom, int map, int charId) => L2Rom.AllOrNothing(rom, () => MakeStoryCore(rom, map, charId));

    private static int MakeStoryCore(RomBuffer rom, int map, int charId)
    {
        if (WhyNotStory(rom, map, charId) is { } why) throw new InvalidOperationException(why);
        int id = FreeId(rom);
        int init = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
        var create = script.Steps.First(s => !s.IsText && s.Op == 0x68 && s.Bytes.Length >= 3 && s.Bytes[1] == charId);
        int sprite = create.Bytes[2];
        int k = script.Steps.IndexOf(create);
        var next = k + 1 < script.Steps.Count ? script.Steps[k + 1] : null;
        foreach (var st in script.Steps)
            for (int j = 0; j < st.Jumps.Count; j++)
                if (st.Jumps[j].Step == create) st.Jumps[j] = new L2Jump { Step = next };
        script.Steps.RemoveAt(k);
        Rename(script, charId, id);
        L2EventBlocks.SaveInit(rom, map, script);

        int block = L2Events.Block(rom, map);
        if (block >= 0)
        {
            var lists = L2EventBlocks.ReadLists(rom, block);
            var byOffset = new Dictionary<int, L2EventScript>();
            var changed = new Dictionary<L2EventBlocks.Entry, L2EventScript>();
            foreach (var e in lists.Where(e => e.IsScript))
            {
                if (!byOffset.TryGetValue(e.Offset, out var sc))
                {
                    sc = L2EventScript.Read(rom, block + e.Offset, block);
                    if (sc.Problem != null) continue;
                    byOffset[e.Offset] = sc;
                    if (!Rename(sc, charId, id)) continue;
                }
                if (sc.Problem == null && Names(sc, id)) changed[e] = sc;
            }
            bool talk = lists.Any(e => e.Head == L2EventBlocks.TalkList && e.Id == charId);
            if (changed.Count > 0 || talk)
                L2EventBlocks.SaveAll(rom, map, changed, e => e.Head == L2EventBlocks.TalkList && e.Id == charId ? id : e.Id);
        }
        Set(rom, id, sprite, map, charId - 0x4F);
        return id;
    }

    /// <summary>Every step of <paramref name="script"/> that names <paramref name="from"/> (character fields, "said
    /// by") names <paramref name="to"/>; true when one did.</summary>
    private static bool Rename(L2EventScript script, int from, int to)
    {
        bool any = false;
        foreach (var s in script.Steps)
        {
            if (s.IsText)
            {
                if (s.Bytes.Length >= 2 && s.Bytes[0] == 0x13 && s.Bytes[1] == from) { var t = (byte[])s.Bytes.Clone(); t[1] = (byte)to; s.Bytes = t; any = true; }
                continue;
            }
            if (L2EventCommands.Find(s.Op) is not { } def) continue;
            var b = (byte[])s.Bytes.Clone();
            bool changed = false;
            foreach (var p in def.FieldsFor(b))
                if (p.Kind is L2Kind.Who or L2Kind.WhoOrAll)
                    try { if (p.Get(b) == from) { p.Set(b, to); changed = true; } } catch (Exception) { }
            if (changed) { s.Bytes = b; any = true; }
        }
        return any;
    }

    private static bool Names(L2EventScript script, int id) =>
        script.Steps.Any(s => s.IsText ? s.Bytes.Length >= 2 && s.Bytes[0] == 0x13 && s.Bytes[1] == id :
            L2EventCommands.Find(s.Op)?.FieldsFor(s.Bytes).Any(p => p.Kind is L2Kind.Who or L2Kind.WhoOrAll && Safe(p, s.Bytes) == id) == true);

    private static int Safe(L2ParamDef p, byte[] b) { try { return p.Get(b); } catch (Exception) { return -1; } }
}
