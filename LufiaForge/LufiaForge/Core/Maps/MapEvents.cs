namespace LufiaForge.Core.Maps;

// adding and deleting a map's events, and what starts them.
// a map's event table (at its script block, u16 offsets from the block, FFFF = none) goes: talk events of characters
// 0.., an empty entry at the map's event base, the step-on area events (area n runs event base + n + 1), an empty
// entry closing the table, then the code. jumps inside events count from the event's own start, so the table can
// grow (the block gets copied with a longer table and every offset moved by the same amount).
// deleting never leaves an empty slot behind something that starts it (talking to a character with no event, an
// enabled area or a "run event" pointing at FFFF runs garbage). instead the event becomes a one-command event (end),
// its step-on areas get switched off and "run event" commands leading to it become an end
public static class MapEvents
{
    // events made for "after another event" start this far above the event base, out of the way of new step-on areas
    public const int ChainedFrom = 0x41;

    public static int BlockLength(RomBuffer rom, int mapId)
    {
        int block = EventScript.BlockOffset(rom, mapId);
        if (block >= MapWriter.ExpansionStart && ExpansionSpace.Load(rom).At(block) is { } entry && entry.Start == block) return entry.End - block;
        return Math.Min(Math.Min(EventScript.NextBlockStart(rom, block), rom.Length) - block, 0xC000);
    }

    // how many entries the map's event table has: up to the empty entry that closes it after the event base (or
    // where the code starts, if that comes first)
    public static int TableLength(RomBuffer rom, int mapId)
    {
        int block = EventScript.BlockOffset(rom, mapId), baseEv = LufiaMap.EventBase(rom, mapId), blockLength = BlockLength(rom, mapId);
        int byCode = EventScript.SlotCount(rom, mapId);
        int entries = 0;
        for (int i = baseEv + 1; i < 256 && (i + 1) * 2 <= blockLength; i++)
        {
            int relative = rom.ReadUInt16Le(block + i * 2);
            if (relative == 0xFFFF) { entries = i + 1; break; }
            if (relative >= blockLength) break;   // past the table, it's code
        }
        // entries before the closing one have to point past the table (if not it's code and the table is shorter)
        if (entries > byCode)
            for (int i = 0; i < entries; i++)
            {
                int relative = rom.ReadUInt16Le(block + i * 2);
                if (i == 0 && baseEv > 0 && relative == baseEv) continue;   // (the base, see EventScript.SlotCount)
                if (relative != 0xFFFF && relative < entries * 2 && i < byCode) { entries = byCode; break; }
            }
        return Math.Max(byCode, entries);
    }

    // whether the map has a script block at all (some don't: FFFFFF in the map script table, no events)
    public static bool HasBlock(RomBuffer rom, int mapId)
    {
        int entry = LufiaMap.MapScriptTable + mapId * 5;
        return (rom.ReadByte(entry) & rom.ReadByte(entry + 1) & rom.ReadByte(entry + 2)) != 0xFF;
    }

    // gives the map a table of at least count entries (the new ones empty), copying its script block to the added
    // space with the longer table (a map with no block gets a new one)
    public static void EnsureTable(RomBuffer rom, int mapId, int count)
    {
        count = Math.Min(256, count);
        bool hasBlock = HasBlock(rom, mapId);
        int have = hasBlock ? TableLength(rom, mapId) : 0;
        if (have >= count) return;
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        int oldBlock = hasBlock ? EventScript.BlockOffset(rom, mapId) : -1, blockLength = hasBlock ? BlockLength(rom, mapId) : 0;
        int shift = (count - have) * 2;
        var block = new byte[blockLength + shift + 1];
        for (int i = 0; i < count; i++)
        {
            int relative = i < have ? rom.ReadUInt16Le(oldBlock + i * 2) : 0xFFFF;
            // (0 and 1 mean "no event" here, the way SlotCount reads them. shifted they'd look like the first event and
            // cut the table short, like map 09's event 0 did. entry 0 of a shared block holds the event base, kept)
            bool baseMark = i == 0 && LufiaMap.EventBase(rom, mapId) is int eventBase && eventBase > 0 && relative == eventBase;
            if (relative != 0xFFFF && relative >= 2 && !baseMark) relative += shift;
            block[i * 2] = (byte)relative; block[i * 2 + 1] = (byte)(relative >> 8);
        }
        if (hasBlock) rom.ReadBytes(oldBlock + have * 2, blockLength - have * 2).CopyTo(block, count * 2);
        if (block.Length > 0xFFFF) throw new InvalidOperationException("This map's events are too big to add more.");
        var freeSpace = ExpansionSpace.Load(rom);
        int newBlock = freeSpace.Allocate(block.Length, ExpansionSpace.Kind.ScriptBlock, mapId, EventScript.ScriptLimit);
        rom.WriteBytes(newBlock, block);
        int blockPointer = newBlock - 0x18000, tableEntry = LufiaMap.MapScriptTable + mapId * 5;
        rom.WriteBytes(tableEntry, new[] { (byte)blockPointer, (byte)(blockPointer >> 8), (byte)(blockPointer >> 16) });
        if (hasBlock && oldBlock >= MapWriter.ExpansionStart && freeSpace.At(oldBlock)?.Start == oldBlock && !MapCatalog.Scan(rom).Any(m => m.MapId != mapId && EventScript.BlockOffset(rom, m.MapId) == oldBlock))
            freeSpace.Free(oldBlock);
        freeSpace.Save();
        rom.FixChecksum();
    }

    public static bool HasEvent(RomBuffer rom, int mapId, int ev) =>
        ev < TableLength(rom, mapId) && LufiaMap.EventScriptOffset(rom, mapId, ev) >= 0;

    // makes event ev (an empty one, just end) if it doesn't exist, growing the table if it has to
    public static void Create(RomBuffer rom, int mapId, int ev)
    {
        if (HasEvent(rom, mapId, ev)) return;
        EnsureTable(rom, mapId, ev + 2);   // + the closing empty entry
        EventScript.CreateEvent(rom, mapId, ev);
    }

    // a free event number for an event that another event starts (above where step-on areas go)
    public static int FreeChainedNumber(RomBuffer rom, int mapId)
    {
        int from = Math.Min(255, LufiaMap.EventBase(rom, mapId) + ChainedFrom);
        for (int ev = from; ev < 255; ev++) if (!HasEvent(rom, mapId, ev)) return ev;
        for (int ev = from - 1; ev > LufiaMap.EventBase(rom, mapId); ev--) if (!HasEvent(rom, mapId, ev)) return ev;
        throw new InvalidOperationException("This map has no free event numbers left.");
    }

    // who starts an event: characters (talk), step-on areas (switched on by the setup script) and "run event"
    // commands in any map's events
    public sealed record Starters(List<int> Characters, List<int> Areas, List<(int Map, int Event, int Line)> Chains)
    {
        public bool Any => Characters.Count + Areas.Count + Chains.Count > 0;
    }

    public static Starters FindStarters(RomBuffer rom, int mapId, int ev, IEnumerable<int>? allMaps = null)
    {
        var characters = new List<int>();
        try { characters = LufiaMap.Load(rom, mapId).Npcs.Where(n => !n.IsUnused && TalkEvent(n.Index) == ev).Select(n => n.Index).ToList(); } catch { }
        var areas = MapSetupScript.ReadTriggers(rom, mapId).Where(t => t.Event == ev).Select(t => t.Area).Distinct().ToList();
        var chains = new List<(int, int, int)>();
        foreach (int otherMap in allMaps ?? MapCatalog.Scan(rom).Select(x => x.MapId))
        {
            int slots;
            try { slots = TableLength(rom, otherMap); } catch { continue; }
            var seen = new HashSet<int>();
            for (int otherEvent = 0; otherEvent < slots; otherEvent++)
            {
                int start = LufiaMap.EventScriptOffset(rom, otherMap, otherEvent);
                if (start < 0 || !seen.Add(start)) continue;
                EventScript? script;
                try { script = EventScript.Load(rom, otherMap, otherEvent); } catch { continue; }
                if (script == null) continue;
                for (int i = 0; i < script.Ops.Count; i++)
                {
                    var bytes = script.Ops[i].Bytes;
                    if (!script.Ops[i].IsText && bytes.Length >= 3 && bytes[0] == 0x51 && bytes[1] == mapId && Chained(rom, bytes) == ev)
                        chains.Add((otherMap, otherEvent, i));
                }
            }
        }
        return new Starters(characters, areas, chains);
    }

    // deletes an event: it becomes empty (end), its step-on areas get switched off, and the "run event" commands
    // leading to it become an end in their events
    public static void Delete(RomBuffer rom, int mapId, int ev, Starters starters)
    {
        // "run event" commands leading here become an end. "run event" never returns, so the event ended there (if we
        // took it out it would carry on into whatever bytes follow it, the next event's code or data)
        foreach (var group in starters.Chains.GroupBy(c => (c.Map, c.Event)))
        {
            var script = EventScript.Load(rom, group.Key.Map, group.Key.Event);
            if (script == null) continue;
            int changed = 0;
            foreach (var op in script.Ops.Where(o => !o.IsText && o.Bytes.Length >= 3 && o.Bytes[0] == 0x51 && o.Bytes[1] == mapId && Chained(rom, o.Bytes) == ev))
            {
                op.Bytes = new byte[] { 0x00 };
                changed++;
            }
            if (changed > 0) script.Save(true);
        }
        foreach (int area in starters.Areas) SetAreaEnabled(rom, mapId, area, false);
        if (HasEvent(rom, mapId, ev))
        {
            var script = EventScript.Load(rom, mapId, ev);
            if (script != null)
            {
                script.Ops.Clear();
                script.Ops.Add(new ScriptOp { Bytes = new byte[] { 0x00 } });
                script.Save(true);
            }
        }
        rom.FixChecksum();
    }

    // the event a "run event" command (51 map n) runs, n counts from that map's event base
    public static int Chained(RomBuffer rom, byte[] bytes) => (LufiaMap.EventBase(rom, bytes[1]) + bytes[2]) & 0xFF;

    // adds "run event ev of this map" at the end of event fromEv (before its last end command), so ev plays
    // right after it
    public static void ChainAfter(RomBuffer rom, int mapId, int fromEv, int ev)
    {
        var script = EventScript.Load(rom, mapId, fromEv) ?? throw new InvalidOperationException($"Event {fromEv} has no script.");
        int relative = (ev - LufiaMap.EventBase(rom, mapId)) & 0xFF;
        int end = script.Ops.FindLastIndex(o => !o.IsText && o.Bytes is [0x00]);
        var op = new ScriptOp { Bytes = new byte[] { 0x51, (byte)mapId, (byte)relative } };
        if (end < 0) script.Ops.Add(op); else script.Ops.Insert(end, op);
        script.Save(true);
    }

    // the first lines of a new event that only plays once: if the flag is set, end, if not set it
    public static void MakeOnce(RomBuffer rom, int mapId, int ev, int flag)
    {
        var script = EventScript.Load(rom, mapId, ev) ?? throw new InvalidOperationException($"Event {ev} has no script.");
        // 04 ff jj jj = if flag set, go to (the end). jump targets get filled in from Targets when saving
        var end = script.Ops.FindLastIndex(o => !o.IsText && o.Bytes is [0x00]);
        if (end < 0) { script.Ops.Add(new ScriptOp { Bytes = new byte[] { 0x00 } }); end = script.Ops.Count - 1; }
        var check = new ScriptOp { Bytes = new byte[] { 0x04, (byte)flag, 0, 0 } };
        check.Targets.Add(new JumpRef { Op = script.Ops[end] });
        script.Ops.Insert(0, new ScriptOp { Bytes = new byte[] { 0x06, (byte)flag } });
        script.Ops.Insert(0, check);
        script.Save(true);
    }

    // characters

    // the event that talking to character npc (its index in the map's list, from 0) runs: n + 1, seen in the game
    // (the talk's $16 = actor - 7, $01:C2BA, the Ancient Cave and the visible monsters rely on it). entry 0 of most
    // maps' event tables is the event base, a marker, not an event
    public static int TalkEvent(int npc) => npc + 1;

    // the number a new character gets (talking to character n runs event n + 1): the first one from the map's count
    // whose event slot nothing uses. we skip numbers with an event (a cutscene, a step-on area's, a chained one), the
    // event base (the empty entry the step-on area events count from) and the events of the map's areas that don't
    // have one yet. -1 if none is left under 32. a number past the event table grows it when the map gets written
    public static int FreeCharacterNumber(RomBuffer rom, LufiaMap map)
    {
        int baseEv = LufiaMap.EventBase(rom, map.MapId);
        var areas = map.Triggers.Select(t => AreaEvent(rom, map.MapId, t.Index)).ToHashSet();
        for (int number = map.Npcs.Count; number < 32; number++)
        {
            int ev = TalkEvent(number);
            if (ev != baseEv && !areas.Contains(ev) && !HasEvent(rom, map.MapId, ev)) return number;
        }
        return -1;
    }

    // step-on areas

    // the event a step-on area runs
    public static int AreaEvent(RomBuffer rom, int mapId, int area) => (LufiaMap.EventBase(rom, mapId) + area + 1) & 0xFF;

    // the number a new step-on area gets: the first one after the map's areas whose event (base + n + 1) is free,
    // so it can't run an event that's already used for something else
    public static int NextAreaIndex(RomBuffer rom, LufiaMap map)
    {
        for (int number = map.Triggers.Count; number < 63; number++)
        {
            int ev = AreaEvent(rom, map.MapId, number);
            if (ev <= LufiaMap.EventBase(rom, map.MapId)) break;   // wrapped past 255
            if (!HasEvent(rom, map.MapId, ev) && !map.Npcs.Any(c => TalkEvent(c.Index) == ev)) return number;
        }
        throw new InvalidOperationException("This map has no free number left for a new step-on area.");
    }

    // areas the map's setup script switches on (anywhere, also after a story check)
    public static HashSet<int> EnabledAreas(RomBuffer rom, int mapId) =>
        MapSetupScript.ReadTriggers(rom, mapId).Select(t => t.Area).ToHashSet();

    // switches a step-on area on (a command at the start of the setup script, so it always applies) or off
    // (its commands become two "nothing" commands, same length)
    public static void SetAreaEnabled(RomBuffer rom, int mapId, int area, bool on)
    {
        if (area is < 0 or > 0x3E) throw new InvalidOperationException("A map can have at most 63 step-on areas.");
        var script = SetupScripts.Read(rom, mapId);
        var (ops, _) = SetupScripts.Parse(script);
        var mine = ops.Where(p => script[p] == 0x81 + area).ToList();
        if (on == mine.Count > 0 && (!on || mine.Any(p => !IsBehindCheck(script, ops, p)))) return;
        if (!on)
        {
            var newScript = (byte[])script.Clone();
            foreach (int pos in mine) { newScript[pos] = SetupScripts.Nothing; newScript[pos + 1] = SetupScripts.Nothing; }
            SetupScripts.Replace(rom, mapId, newScript);
            return;
        }
        // bb: a slot of its own (the game keeps a word per enabled area)
        int bb = ops.Where(p => script[p] is > 0x80 and < 0xC0).Select(p => (int)script[p + 1]).DefaultIfEmpty(-1).Max() + 1;
        SetupScripts.Insert(rom, mapId, new[] { (byte)(0x81 + area), (byte)bb });
    }

    // after a map's areas were edited: new areas get switched on and get an (empty) event, areas that were switched
    // on and are deleted now (no size) get switched off
    public static void SyncTriggers(RomBuffer rom, LufiaMap map, bool allowExpand)
    {
        if (!map.TriggersEditable) return;
        var added = map.Triggers.Where(t => t.Index >= map.LoadedTriggerCount && !t.IsUnused).ToList();
        var enabled = EnabledAreas(rom, map.MapId);
        var removed = map.Triggers.Where(t => t.IsUnused && enabled.Contains(t.Index)).ToList();
        // areas switched on that the map doesn't have at all anymore (taken off the end, an Undo after a save) get
        // switched off too, otherwise the setup script switches on an area past the map's list
        var gone = enabled.Where(i => i >= map.Triggers.Count).Distinct().ToList();
        if (added.Count == 0 && removed.Count == 0 && gone.Count == 0) { map.MarkTriggersSaved(); return; }
        if (!allowExpand && rom.Length < MapWriter.ExpandedSize)
            throw new InvalidOperationException("New step-on areas need the ROM expanded to 2 MB.");
        foreach (var trigger in added)
        {
            Create(rom, map.MapId, AreaEvent(rom, map.MapId, trigger.Index));
            SetAreaEnabled(rom, map.MapId, trigger.Index, true);
        }
        foreach (var trigger in removed) SetAreaEnabled(rom, map.MapId, trigger.Index, false);
        foreach (int area in gone) SetAreaEnabled(rom, map.MapId, area, false);
        map.MarkTriggersSaved();
    }

    // true when a command comes after a story check (so it only applies sometimes)
    private static bool IsBehindCheck(byte[] script, List<int> ops, int pos) => ops.Any(q => q < pos && script[q] is 0x04 or (>= 0x05 and <= 0x0A));
}
