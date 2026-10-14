namespace LufiaForge.Core.Maps;

/// <summary>
/// Adding and deleting a map's events, and what starts them.
/// <para>A map's event table (at its script block; u16 offsets from the block, FFFF = none) is laid out as: talk
/// events of characters 0.., an empty entry at the map's event base, the step-on area events (area n runs event
/// base + n + 1), an empty entry closing the table, then the code. Jumps inside events count from the event's own
/// start, so the table can grow (the block is copied with a longer table and every offset moved by the same amount).</para>
/// <para>Deleting never leaves an empty slot behind something that starts it (talking to a character with no
/// event, an enabled area or a "run event" pointing at FFFF runs garbage): the event becomes a one-command event
/// (end), its step-on areas are switched off and "run event" commands leading to it are removed.</para>
/// </summary>
public static class MapEvents
{
    /// <summary>Events made for "after another event" start this far above the event base, out of the way of new step-on areas.</summary>
    public const int ChainedFrom = 0x41;

    private static int BlockLength(RomBuffer rom, int mapId)
    {
        int b = EventScript.BlockOffset(rom, mapId);
        if (b >= MapWriter.ExpansionStart && ExpansionSpace.Load(rom).At(b) is { } e && e.Start == b) return e.End - b;
        int next = int.MaxValue;
        foreach (var m in MapCatalog.Scan(rom))
        {
            int o = EventScript.BlockOffset(rom, m.MapId);
            if (o > b && o < next) next = o;
        }
        return Math.Min(Math.Min(next, rom.Length) - b, 0xC000);
    }

    /// <summary>How many entries the map's event table has: up to the empty entry closing it after the event base (or
    /// where the code starts, when that comes first).</summary>
    public static int TableLength(RomBuffer rom, int mapId)
    {
        int b = EventScript.BlockOffset(rom, mapId), baseEv = LufiaMap.EventBase(rom, mapId), len = BlockLength(rom, mapId);
        int byCode = EventScript.SlotCount(rom, mapId);
        int n = 0;
        for (int i = baseEv + 1; i < 256 && (i + 1) * 2 <= len; i++)
        {
            int rel = rom.ReadUInt16Le(b + i * 2);
            if (rel == 0xFFFF) { n = i + 1; break; }
            if (rel >= len) break;   // past the table: code
        }
        // entries before the closing one must point past the table (else it's code and the table is shorter)
        if (n > byCode)
            for (int i = 0; i < n; i++)
            {
                int rel = rom.ReadUInt16Le(b + i * 2);
                if (rel != 0xFFFF && rel < n * 2 && i < byCode) { n = byCode; break; }
            }
        return Math.Max(byCode, n);
    }

    /// <summary>Gives the map a table of at least <paramref name="count"/> entries (new ones empty), copying its
    /// script block to the added space with the longer table.</summary>
    public static void EnsureTable(RomBuffer rom, int mapId, int count)
    {
        count = Math.Min(256, count);
        int have = TableLength(rom, mapId);
        if (have >= count) return;
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        int b = EventScript.BlockOffset(rom, mapId), len = BlockLength(rom, mapId);
        int shift = (count - have) * 2;
        var block = new byte[len + shift + 1];
        for (int i = 0; i < count; i++)
        {
            int rel = i < have ? rom.ReadUInt16Le(b + i * 2) : 0xFFFF;
            if (rel != 0xFFFF) rel += shift;
            block[i * 2] = (byte)rel; block[i * 2 + 1] = (byte)(rel >> 8);
        }
        rom.ReadBytes(b + have * 2, len - have * 2).CopyTo(block, count * 2);
        if (block.Length > 0xFFFF) throw new InvalidOperationException("This map's events are too big to add more.");
        var space = ExpansionSpace.Load(rom);
        int at = space.Allocate(block.Length, ExpansionSpace.Kind.ScriptBlock, mapId, EventScript.ScriptLimit);
        rom.WriteBytes(at, block);
        int t = at - 0x18000, p = LufiaMap.MapScriptTable + mapId * 5;
        rom.WriteBytes(p, new[] { (byte)t, (byte)(t >> 8), (byte)(t >> 16) });
        if (b >= MapWriter.ExpansionStart && space.At(b)?.Start == b && !MapCatalog.Scan(rom).Any(m => m.MapId != mapId && EventScript.BlockOffset(rom, m.MapId) == b))
            space.Free(b);
        space.Save();
        rom.FixChecksum();
    }

    public static bool HasEvent(RomBuffer rom, int mapId, int ev) =>
        ev < TableLength(rom, mapId) && LufiaMap.EventScriptOffset(rom, mapId, ev) >= 0;

    /// <summary>Makes event <paramref name="ev"/> (an empty one: end) when it has none, growing the table if needed.</summary>
    public static void Create(RomBuffer rom, int mapId, int ev)
    {
        if (HasEvent(rom, mapId, ev)) return;
        EnsureTable(rom, mapId, ev + 2);   // + the closing empty entry
        EventScript.CreateEvent(rom, mapId, ev);
    }

    /// <summary>A free event number for an event started by another event (above where step-on areas go).</summary>
    public static int FreeChainedNumber(RomBuffer rom, int mapId)
    {
        int from = Math.Min(255, LufiaMap.EventBase(rom, mapId) + ChainedFrom);
        for (int ev = from; ev < 255; ev++) if (!HasEvent(rom, mapId, ev)) return ev;
        for (int ev = from - 1; ev > LufiaMap.EventBase(rom, mapId); ev--) if (!HasEvent(rom, mapId, ev)) return ev;
        throw new InvalidOperationException("This map has no free event numbers left.");
    }

    /// <summary>Who starts an event: characters (talk), step-on areas (enabled by the setup script) and "run event"
    /// commands in any map's events.</summary>
    public sealed record Starters(List<int> Characters, List<int> Areas, List<(int Map, int Event, int Line)> Chains)
    {
        public bool Any => Characters.Count + Areas.Count + Chains.Count > 0;
    }

    public static Starters FindStarters(RomBuffer rom, int mapId, int ev, IEnumerable<int>? allMaps = null)
    {
        var chars = new List<int>();
        try { chars = LufiaMap.Load(rom, mapId).Npcs.Where(n => !n.IsUnused && n.Index == ev).Select(n => n.Index).ToList(); } catch { }
        var areas = MapSetupScript.ReadTriggers(rom, mapId).Where(t => t.Event == ev).Select(t => t.Area).Distinct().ToList();
        var chains = new List<(int, int, int)>();
        foreach (int m in allMaps ?? MapCatalog.Scan(rom).Select(x => x.MapId))
        {
            int slots;
            try { slots = TableLength(rom, m); } catch { continue; }
            var seen = new HashSet<int>();
            for (int e = 0; e < slots; e++)
            {
                int start = LufiaMap.EventScriptOffset(rom, m, e);
                if (start < 0 || !seen.Add(start)) continue;
                EventScript? s;
                try { s = EventScript.Load(rom, m, e); } catch { continue; }
                if (s == null) continue;
                for (int i = 0; i < s.Ops.Count; i++)
                {
                    var b = s.Ops[i].Bytes;
                    if (!s.Ops[i].IsText && b.Length >= 3 && b[0] == 0x51 && b[1] == mapId && Chained(rom, b) == ev)
                        chains.Add((m, e, i));
                }
            }
        }
        return new Starters(chars, areas, chains);
    }

    /// <summary>Deletes an event: it becomes empty (end), its step-on areas are switched off, and the "run event"
    /// commands leading to it are removed from their events.</summary>
    public static void Delete(RomBuffer rom, int mapId, int ev, Starters starters)
    {
        // "run event" commands leading here
        foreach (var grp in starters.Chains.GroupBy(c => (c.Map, c.Event)))
        {
            var s = EventScript.Load(rom, grp.Key.Map, grp.Key.Event);
            if (s == null) continue;
            s.Ops.RemoveAll(o => !o.IsText && o.Bytes.Length >= 3 && o.Bytes[0] == 0x51 && o.Bytes[1] == mapId && Chained(rom, o.Bytes) == ev);
            s.Save(true);
        }
        foreach (int area in starters.Areas) SetAreaEnabled(rom, mapId, area, false);
        if (HasEvent(rom, mapId, ev))
        {
            var s = EventScript.Load(rom, mapId, ev);
            if (s != null)
            {
                s.Ops.Clear();
                s.Ops.Add(new ScriptOp { Bytes = new byte[] { 0x00 } });
                s.Save(true);
            }
        }
        rom.FixChecksum();
    }

    /// <summary>The event a "run event" command (51 map n) runs: n counts from that map's event base.</summary>
    public static int Chained(RomBuffer rom, byte[] b) => (LufiaMap.EventBase(rom, b[1]) + b[2]) & 0xFF;

    /// <summary>Adds "run event <paramref name="ev"/> of this map" at the end of event <paramref name="fromEv"/>
    /// (before its last end command), so <paramref name="ev"/> plays right after it.</summary>
    public static void ChainAfter(RomBuffer rom, int mapId, int fromEv, int ev)
    {
        var s = EventScript.Load(rom, mapId, fromEv) ?? throw new InvalidOperationException($"Event {fromEv} has no script.");
        int rel = (ev - LufiaMap.EventBase(rom, mapId)) & 0xFF;
        int end = s.Ops.FindLastIndex(o => !o.IsText && o.Bytes is [0x00]);
        var op = new ScriptOp { Bytes = new byte[] { 0x51, (byte)mapId, (byte)rel } };
        if (end < 0) s.Ops.Add(op); else s.Ops.Insert(end, op);
        s.Save(true);
    }

    /// <summary>The first lines of a new event that plays only once: if the flag is set, end; else set it.</summary>
    public static void MakeOnce(RomBuffer rom, int mapId, int ev, int flag)
    {
        var s = EventScript.Load(rom, mapId, ev) ?? throw new InvalidOperationException($"Event {ev} has no script.");
        // 04 ff jj jj = if flag set, go to (the end); jump targets are filled in from Targets when saving
        var end = s.Ops.FindLastIndex(o => !o.IsText && o.Bytes is [0x00]);
        if (end < 0) { s.Ops.Add(new ScriptOp { Bytes = new byte[] { 0x00 } }); end = s.Ops.Count - 1; }
        var check = new ScriptOp { Bytes = new byte[] { 0x04, (byte)flag, 0, 0 } };
        check.Targets.Add(new JumpRef { Op = s.Ops[end] });
        s.Ops.Insert(0, new ScriptOp { Bytes = new byte[] { 0x06, (byte)flag } });
        s.Ops.Insert(0, check);
        s.Save(true);
    }

    // ── Step-on areas ──────────────────────────────────────────────────────

    /// <summary>The event a step-on area runs.</summary>
    public static int AreaEvent(RomBuffer rom, int mapId, int area) => (LufiaMap.EventBase(rom, mapId) + area + 1) & 0xFF;

    /// <summary>The number a new step-on area gets: the first one after the map's areas whose event (base + n + 1) is
    /// free, so it can't run an event that is already used for something else.</summary>
    public static int NextAreaIndex(RomBuffer rom, LufiaMap map)
    {
        for (int n = map.Triggers.Count; n < 63; n++)
        {
            int ev = AreaEvent(rom, map.MapId, n);
            if (ev <= LufiaMap.EventBase(rom, map.MapId)) break;   // wrapped past 255
            if (!HasEvent(rom, map.MapId, ev) && !map.Npcs.Any(c => c.Index == ev)) return n;
        }
        throw new InvalidOperationException("This map has no free number left for a new step-on area.");
    }

    /// <summary>Areas the map's setup script switches on (anywhere, also after a story check).</summary>
    public static HashSet<int> EnabledAreas(RomBuffer rom, int mapId) =>
        MapSetupScript.ReadTriggers(rom, mapId).Select(t => t.Area).ToHashSet();

    /// <summary>Switches a step-on area on (a command at the start of the setup script, so it always applies) or off
    /// (its commands become two "nothing" commands, same length).</summary>
    public static void SetAreaEnabled(RomBuffer rom, int mapId, int area, bool on)
    {
        if (area is < 0 or > 0x3E) throw new InvalidOperationException("A map can have at most 63 step-on areas.");
        var s = SetupScripts.Read(rom, mapId);
        var (ops, _) = SetupScripts.Parse(s);
        var mine = ops.Where(p => s[p] == 0x81 + area).ToList();
        if (on == mine.Count > 0 && (!on || mine.Any(p => !IsBehindCheck(s, ops, p)))) return;
        if (!on)
        {
            var n = (byte[])s.Clone();
            foreach (int p in mine) { n[p] = SetupScripts.Nothing; n[p + 1] = SetupScripts.Nothing; }
            SetupScripts.Replace(rom, mapId, n);
            return;
        }
        // bb: a slot of its own (the game keeps a word per enabled area)
        int bb = ops.Where(p => s[p] is > 0x80 and < 0xC0).Select(p => (int)s[p + 1]).DefaultIfEmpty(-1).Max() + 1;
        SetupScripts.Insert(rom, mapId, new[] { (byte)(0x81 + area), (byte)bb });
    }

    /// <summary>After a map's areas were edited: new areas are switched on and get an (empty) event, areas that were
    /// switched on and are now deleted (no size) are switched off.</summary>
    public static void SyncTriggers(RomBuffer rom, LufiaMap map, bool allowExpand)
    {
        if (!map.TriggersEditable) return;
        var added = map.Triggers.Where(t => t.Index >= map.LoadedTriggerCount && !t.IsUnused).ToList();
        var enabled = EnabledAreas(rom, map.MapId);
        var removed = map.Triggers.Where(t => t.IsUnused && enabled.Contains(t.Index)).ToList();
        if (added.Count == 0 && removed.Count == 0) { map.MarkTriggersSaved(); return; }
        if (!allowExpand && rom.Length < MapWriter.ExpandedSize)
            throw new InvalidOperationException("New step-on areas need the ROM expanded to 2 MB.");
        foreach (var t in added)
        {
            Create(rom, map.MapId, AreaEvent(rom, map.MapId, t.Index));
            SetAreaEnabled(rom, map.MapId, t.Index, true);
        }
        foreach (var t in removed) SetAreaEnabled(rom, map.MapId, t.Index, false);
        map.MarkTriggersSaved();
    }

    /// <summary>True when a command comes after a story check (so it only applies sometimes).</summary>
    private static bool IsBehindCheck(byte[] s, List<int> ops, int p) => ops.Any(q => q < p && s[q] is 0x04 or (>= 0x05 and <= 0x0A));
}
