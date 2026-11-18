namespace LufiaForge.Core.Lufia2;

// lufia 2's persistent characters (story characters): actors 10-4F (Tia 10 in Elcid, the elder of Chaed 2D, ...).
// no map script makes them. the game keeps where each one is in WRAM ($081E + n = its id, FF = none, $085E + n = map,
// $089E + n = character start, $08DE + n = sprite), and after a map's start-up script it puts every one whose map it
// is on that map, at the map's character start with that number (map info +10, $83:B34C).
// a new game fills those from a table in bank $87 ($83:ADDF, the word at $87:8003 points at it from $87:8000, 64
// records of 4 bytes: id (FF = none), sprite, map, start).
// event command 35 id map start sends one to another map ($80:B2B1). it's hidden right away if it's on the map being
// shown, and stands on the new map from the next time that map loads. "Hide a character" on one lasts until its map
// loads again. the game is done with one by sending it to Arek Daos Shrine (map 02), starts 07-0E there, which all
// stand at (0, 37) outside the room. record 4F is the game's own: the map-load code reads its bytes as a parked
// ship's place ($83:B3A2)
public static class L2Persistent
{
    public const int First = 0x10, Count = 0x40;
    // the last one a hack can use (4F's record is the parked ship's)
    public const int LastUsable = 0x4E;
    // where the game sends a story character it's done with (Tia, when she joins the party)
    public const int AwayMap = 0x02, AwaySpot = 0x07;

    // one of the game's "away" places: Arek Daos Shrine starts 07-0E, outside the room
    public static bool IsAway(int map, int spot) => map == AwayMap && spot is >= 0x07 and <= 0x0E;

    // a place a persistent character stands: on Map at character start Spot. From is -1 for where a new game puts
    // it, otherwise the map whose event sends it there
    public sealed record Place(int Id, int Sprite, int Map, int Spot, int From);

    // file offset of the new-game table
    public static int Table(RomBuffer rom) => L2Events.HeaderTable + rom.ReadUInt16Le(L2Events.HeaderTable + 3);

    private static bool TableOk(RomBuffer rom) => Table(rom) is var table && table >= 0 && table + Count * 4 <= rom.Length;

    // the new-game record of id: id (FF = none), sprite, map, start
    public static byte[] Record(RomBuffer rom, int id) => rom.ReadBytes(Table(rom) + (id - First) * 4, 4);

    // true if the game has this story character (its record isn't FF), so events can send it and it stands on its
    // map. one whose record is FF never shows up anywhere, even when an event sends it
    public static bool Exists(RomBuffer rom, int id) => id is >= First and <= LastUsable && TableOk(rom) && rom.ReadByte(Table(rom) + (id - First) * 4) != 0xFF;

    // where a new game puts id, with its sprite. it has to be one of the game's 251, a new sprite belongs to one map
    // and a story character goes from map to map
    public static void Set(RomBuffer rom, int id, int sprite, int map, int spot)
    {
        if (id is < First or > LastUsable) throw new InvalidOperationException($"Story characters are 10-{LastUsable:X2}.");
        if (sprite is < 0 or >= L2Sprites.GameCount) throw new InvalidOperationException("A story character needs one of the game's sprites (00-FA): a new sprite belongs to one map, and a story character can go to any map.");
        if (!TableOk(rom)) throw new InvalidOperationException("The story characters' table isn't where the game keeps it.");
        rom.WriteBytes(Table(rom) + (id - First) * 4, new[] { (byte)id, (byte)sprite, (byte)map, (byte)spot });
    }

    // takes id out of the game (record = FF), it never shows up again
    public static void Remove(RomBuffer rom, int id)
    {
        if (id is < First or > LastUsable || !TableOk(rom)) return;
        rom.WriteBytes(Table(rom) + (id - First) * 4, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
    }

    // where a new game puts each persistent character (id, sprite, map, spot)
    public static List<Place> Start(RomBuffer rom)
    {
        var list = new List<Place>();
        if (!TableOk(rom)) return list;
        int table = Table(rom);
        for (int i = 0; i < Count; i++)
        {
            var record = rom.ReadBytes(table + i * 4, 4);
            if (record[0] != 0xFF) list.Add(new Place(First + i, record[1], record[2], record[3], -1));
        }
        return list;
    }

    // every place a persistent character stands: where a new game puts it and every map an event (35) sends it to,
    // with its sprite from the table. read from every map's scripts once per rom version (Uses)
    public static List<Place> All(RomBuffer rom)
    {
        var start = Start(rom);
        var sprite = start.ToDictionary(p => p.Id, p => p.Sprite);
        var list = new List<Place>(start);
        foreach (var use in Uses(rom))
            if (use.ToMap >= 0 && sprite.TryGetValue(use.Id, out int useSprite))
            {
                var place = new Place(use.Id, useSprite, use.ToMap, use.ToSpot, use.Map);
                if (!list.Any(x => x.Id == place.Id && x.Map == place.Map && x.Spot == place.Spot)) list.Add(place);
            }
        return list;
    }

    // the persistent characters that stand on the map at some point, by character start
    public static List<Place> On(IEnumerable<Place> all, int map) => all.Where(p => p.Map == map).ToList();

    // where they're used

    // a step or event that names a story character. How = "sends", "talked to" or "uses", Head / ListId = the event
    // (head -1 = the start-up script), At = the step's file offset, and a send's map and start in ToMap / ToSpot
    // (otherwise -1)
    public sealed record Use(int Id, string How, int Map, int Head, int ListId, int At, string Where, int ToMap = -1, int ToSpot = -1);

    private static RomBuffer? _rom;
    private static int _version = -1;
    private static List<Use> _uses = new();

    // every use of a story character in every map's events (kept until the rom changes)
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

    private static void UsesOn(RomBuffer rom, int map, List<Use> list)
    {
        void Walk(int head, int id, string where, int start, int jumpBase)
        {
            foreach (var op in L2Script.Walk(rom, new[] { start }, jumpBase).Where(o => !o.IsText))
            {
                var bytes = rom.ReadBytes(op.At, op.Length);
                if (bytes[0] == 0x35 && bytes.Length == 4)
                {
                    if (bytes[1] is >= First and < First + Count) list.Add(new Use(bytes[1], "sends", map, head, id, op.At, where, bytes[2], bytes[3]));
                    continue;
                }
                if (L2EventCommands.Find(bytes[0]) is not { } def) continue;
                foreach (var field in def.FieldsFor(bytes))
                    if (field.Kind is L2Kind.Who or L2Kind.WhoOrAll)
                    {
                        int who;
                        try { who = field.Get(bytes); } catch (Exception) { continue; }
                        if (who is >= First and < First + Count && !list.Any(u => u.At == op.At && u.Id == who)) list.Add(new Use(who, "uses", map, head, id, op.At, where));
                    }
            }
        }
        int initScript = L2EventBlocks.InitScript(rom, map);
        Walk(L2FlagIndex.InitHead, -1, "Start-up script", initScript, L2EventBlocks.InitBase(initScript));
        int block = L2Events.Block(rom, map);
        if (block < 0) return;
        foreach (var e in L2EventBlocks.ReadLists(rom, block))
        {
            if (!e.IsScript) continue;
            string title = L2FlagIndex.Lists.FirstOrDefault(l => l.Head == e.Head).Title ?? $"List {e.Head:X2}";
            string where = e.Head == L2EventBlocks.TalkList ? $"{title} {(e.Id >= 0x50 ? $"character {e.Id - 0x4F}" : $"story character {e.Id:X2}")}" : $"{title} {e.Id:X2}";
            if (e.Head == L2EventBlocks.TalkList && e.Id is >= First and < First + Count) list.Add(new Use(e.Id, "talked to", map, e.Head, e.Id, block + e.Offset, where));
            Walk(e.Head, e.Id, where, block + e.Offset, block);
        }
    }

    // a story character nothing uses: its record is FF, no event names it and the user hasn't named it.
    // -1 if none are left
    public static int FreeId(RomBuffer rom)
    {
        var used = Uses(rom).Select(u => u.Id).ToHashSet();
        for (int id = First; id <= LastUsable; id++)
            if (!Exists(rom, id) && !used.Contains(id) && L2FlagNotes.Name(L2FlagIndex.Kind.Character, id) == null) return id;
        return -1;
    }

    // a new story character with that sprite, away (on no map the party sees) until an event sends it to a map or
    // its new-game place is set. returns its id
    public static int New(RomBuffer rom, int sprite)
    {
        int id = FreeId(rom);
        if (id < 0) throw new InvalidOperationException($"All the story characters (10-{LastUsable:X2}) are taken.");
        Set(rom, id, sprite, AwayMap, AwaySpot);
        return id;
    }

    // a character start on (x, y) of the map for a story character: one that's there already and no map character
    // uses, otherwise a new one (standing still) with a number no map character has. returns its number
    public static int StartAt(RomBuffer rom, int map, int x, int y)
    {
        var made = L2MapInfo.Stages(rom, map).Select(c => c.Id).ToHashSet();
        foreach (var (_, record) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
            if (record[1] == x && record[2] == y && !made.Contains(0x4F + record[0])) return record[0];
        var used = L2EventBlocks.CharacterIds(rom, map);
        // past the numbers map characters and field monsters use (actors 50-7F = starts 01-30), so nothing made later
        // shares it ($80:C01D finds a start by its number in the list, whatever the number is)
        int startNumber = 0x31;
        while (startNumber < 0xFF && used.Contains(0x4F + startNumber)) startNumber++;
        if (startNumber >= 0xFF) throw new InvalidOperationException("The map has no character start number left.");
        int w = 255, h = 255;
        try { var mapData = L2Maps.Read(rom, map); w = Math.Min(255, mapData.Layers.Max(l => l.Width)); h = Math.Min(255, mapData.Layers.Max(l => l.Height)); } catch (Exception) { }
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Characters, 8, new[] { (byte)startNumber, (byte)x, (byte)y, (byte)0, (byte)0, (byte)w, (byte)h, (byte)0x04 }))
            throw new InvalidOperationException("The map's info block would be too big.");
        return startNumber;
    }

    // why map character charId can't become a story character, or null
    public static string? WhyNotStory(RomBuffer rom, int map, int charId)
    {
        if (charId is < 0x50 or > 0x7F) return "Only a map character (made by the start-up script) can become a story character.";
        int initScript = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, initScript, L2EventBlocks.InitBase(initScript));
        if (script.Problem != null) return "The map's start-up script can't be changed: " + script.Problem;
        var creates = script.Steps.Where(s => !s.IsText && s.Op is 0x68 or 0x7B && s.Bytes.Length >= 3 && s.Bytes[1] == charId).ToList();
        if (creates.Count == 0) return "The start-up script doesn't make it.";
        if (creates.Count > 1) return "The start-up script makes it in more than one place (other sprites under other story flags).";
        if (creates[0].Op == 0x7B) return "A field monster can't be a story character.";
        if (creates[0].Bytes[2] >= L2Sprites.FirstSlot) return "It has one of this map's new sprites; a story character needs one of the game's (a new sprite belongs to one map).";
        if (L2MapInfo.Stages(rom, map).FirstOrDefault(c => c.Id == charId) is { When.Length: > 0 } stage)
            return $"It is only made while {stage.When}. A story character stands on its map until an event sends it away: set When… to Always first.";
        if (!L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Any(r => r.Bytes[0] == charId - 0x4F)) return "It has no character start.";
        if (FreeId(rom) < 0) return $"All the story characters (10-{LastUsable:X2}) are taken.";
        return null;
    }

    // turns map character charId into a story character. a free id gets its sprite, and this map and its character
    // start as its new-game place. its create comes out of the start-up script, and every step of the map's events
    // that names it, plus its talk event, use the new id. returns the new id
    public static int MakeStory(RomBuffer rom, int map, int charId) => L2Rom.AllOrNothing(rom, () => MakeStoryCore(rom, map, charId));

    private static int MakeStoryCore(RomBuffer rom, int map, int charId)
    {
        if (WhyNotStory(rom, map, charId) is { } why) throw new InvalidOperationException(why);
        int id = FreeId(rom);
        int initScript = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, initScript, L2EventBlocks.InitBase(initScript));
        var create = script.Steps.First(s => !s.IsText && s.Op == 0x68 && s.Bytes.Length >= 3 && s.Bytes[1] == charId);
        int sprite = create.Bytes[2];
        int createIndex = script.Steps.IndexOf(create);
        var next = createIndex + 1 < script.Steps.Count ? script.Steps[createIndex + 1] : null;
        foreach (var step in script.Steps)
            for (int j = 0; j < step.Jumps.Count; j++)
                if (step.Jumps[j].Step == create) step.Jumps[j] = new L2Jump { Step = next };
        script.Steps.RemoveAt(createIndex);
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
                if (!byOffset.TryGetValue(e.Offset, out var eventScript))
                {
                    eventScript = L2EventScript.Read(rom, block + e.Offset, block);
                    if (eventScript.Problem != null) continue;
                    byOffset[e.Offset] = eventScript;
                    if (!Rename(eventScript, charId, id)) continue;
                }
                if (eventScript.Problem == null && Names(eventScript, id)) changed[e] = eventScript;
            }
            bool talk = lists.Any(e => e.Head == L2EventBlocks.TalkList && e.Id == charId);
            if (changed.Count > 0 || talk)
                L2EventBlocks.SaveAll(rom, map, changed, e => e.Head == L2EventBlocks.TalkList && e.Id == charId ? id : e.Id);
        }
        Set(rom, id, sprite, map, charId - 0x4F);
        return id;
    }

    // every step of script that names from (character fields, "said by") now names to. true if any did
    private static bool Rename(L2EventScript script, int from, int to)
    {
        bool any = false;
        foreach (var step in script.Steps)
        {
            if (step.IsText)
            {
                if (step.Bytes.Length >= 2 && step.Bytes[0] == 0x13 && step.Bytes[1] == from) { var textBytes = (byte[])step.Bytes.Clone(); textBytes[1] = (byte)to; step.Bytes = textBytes; any = true; }
                continue;
            }
            if (L2EventCommands.Find(step.Op) is not { } def) continue;
            var bytes = (byte[])step.Bytes.Clone();
            bool changed = false;
            foreach (var field in def.FieldsFor(bytes))
                if (field.Kind is L2Kind.Who or L2Kind.WhoOrAll)
                    try { if (field.Get(bytes) == from) { field.Set(bytes, to); changed = true; } } catch (Exception) { }
            if (changed) { step.Bytes = bytes; any = true; }
        }
        return any;
    }

    private static bool Names(L2EventScript script, int id) =>
        script.Steps.Any(s => s.IsText ? s.Bytes.Length >= 2 && s.Bytes[0] == 0x13 && s.Bytes[1] == id :
            L2EventCommands.Find(s.Op)?.FieldsFor(s.Bytes).Any(p => p.Kind is L2Kind.Who or L2Kind.WhoOrAll && Safe(p, s.Bytes) == id) == true);

    private static int Safe(L2ParamDef field, byte[] bytes) { try { return field.Get(bytes); } catch (Exception) { return -1; } }
}
