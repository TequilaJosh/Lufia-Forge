namespace LufiaForge.Core.Maps;

/// <summary>
/// Creates new maps. A map number is a byte; every map has entries in four tables, all filled for the new map:
/// <list type="bullet">
/// <item>map data: resource 0xB2 + byte $01:E19B[map] (read at $01:BD92) - a new resource id (<see cref="ResourceIds"/>);</item>
/// <item>setup script (chests, doors, arrivals; run when the map loads): u16 at 0x18000 + map*2;</item>
/// <item>event scripts: 5 bytes at 0x18200 + map*5 (block offset, flag base, event base);</item>
/// <item>area name: byte $01:E93E[map] (read at $01:933C / $01:9841: the place shown when saving; 164 entries).</item>
/// </list>
/// New maps take free numbers between 02 and A3 that have no data, setup or events (the game leaves about 35 such
/// holes), so every table already has room for them.
/// </summary>
public static class MapFactory
{
    public const int SelectTable = 0xE19B, SetupTable = 0x18000, ScriptTable = 0x18200, AreaTable = 0xE93E;
    public const int Limit = 0xA4;

    /// <summary>Map numbers a new map can take.</summary>
    public static List<int> FreeIds(RomBuffer rom)
    {
        var used = MapCatalog.Scan(rom).Select(m => m.MapId).ToHashSet();
        return Enumerable.Range(2, Limit - 2).Where(i => !used.Contains(i) && rom.ReadByte(SelectTable + i) == 0 &&
            rom.ReadUInt16Le(SetupTable + i * 2) == 0xFFFF && !SetupScripts.IsMoved(rom, i) &&
            rom.ReadByte(ScriptTable + i * 5) == 0xFF && rom.ReadByte(ScriptTable + i * 5 + 1) == 0xFF && rom.ReadByte(ScriptTable + i * 5 + 2) == 0xFF).ToList();
    }

    /// <summary>
    /// A new map copied from <paramref name="template"/>: same layout, objects and graphics, its own copy of the event
    /// block. Its setup script (chests, doors) is shared with the template. Returns the new map's number.
    /// </summary>
    public static int Copy(RomBuffer rom, int template) => Create(rom, template, blank: false, fill: 0);

    /// <summary>
    /// A new blank map with <paramref name="template"/>'s size and graphics: every cell is block
    /// <paramref name="fill"/>, NPCs hidden, no exits, its own empty event block and an empty setup script.
    /// </summary>
    public static int Blank(RomBuffer rom, int template, int fill) => Create(rom, template, blank: true, fill: fill);

    private static int Create(RomBuffer rom, int template, bool blank, int fill)
    {
        var free = FreeIds(rom);
        if (free.Count == 0) throw new InvalidOperationException("No free map numbers are left.");
        int id = free[0];
        var src = LufiaMap.Load(rom, template);
        if (src.IsWorld) throw new InvalidOperationException("The world map can't be copied.");
        var data = (byte[])src.Original.Clone();
        // the data starts with the map's number in decimal (how the original tools labelled maps; Lufia Forge lists maps by it)
        var label = System.Text.Encoding.ASCII.GetBytes(id.ToString("D3"));
        label.CopyTo(data, 0);
        // (checked before anything is written, so a refused map leaves nothing half made)
        if (ResourceIds.Count(rom) - 0xB2 > 0xFF) throw new InvalidOperationException("No resource ids left that a map can use (they must be below 1B2).");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        int res = ResourceIds.Add(rom, data);
        rom.WriteByte(SelectTable + id, (byte)(res - 0xB2));
        rom.WriteBytes(ScriptTable + id * 5, rom.ReadBytes(ScriptTable + template * 5, 5));
        rom.WriteByte(AreaTable + id, rom.ReadByte(AreaTable + template));
        if (blank)
        {
            rom.WriteUInt16Le(SetupTable + id * 2, (ushort)(EmptySetupScript(rom) - SetupTable));
            // its own empty script in the added space (the three zero bytes found in bank 03 aren't a real script,
            // and saving characters onto it would change it for everyone)
            SetupScripts.Replace(rom, id, new byte[] { 0x00, 0x00, 0x00 });
            EventScript.NewBlockFor(rom, id, 32);
            var m = LufiaMap.Load(rom, id);
            for (int i = 0; i < m.Layer.Length; i++) m.Layer[i] = (ushort)(fill & 0x3FF);
            // nothing of the template's objects: no characters, exits, step-on areas or routes, and one arrival
            // point in the middle (exits of other maps lead to arrival points by number)
            m.Npcs.Clear();
            m.Exits.Clear();
            m.Triggers.Clear();
            m.Paths.Clear();
            var a0 = m.Arrivals.FirstOrDefault() ?? m.AddArrival(m.Width / 2, m.Height / 2);
            a0.X = m.Width / 2; a0.Y = m.Height / 2;
            m.Arrivals.RemoveAll(a => a != a0);
            MapWriter.Save(rom, m, true);
        }
        else
        {
            rom.WriteUInt16Le(SetupTable + id * 2, rom.ReadUInt16Le(SetupTable + template * 2));
            // a template whose script was moved: the copy gets its own (a moved script belongs to one map)
            if (SetupScripts.IsMoved(rom, template)) SetupScripts.Replace(rom, id, SetupScripts.Read(rom, template));
            EventScript.CopyBlockFor(rom, id);
        }
        rom.FixChecksum();
        return id;
    }

    /// <summary>What still leads to a map: exits of other maps, events that warp there (4C / 5B) or run its events
    /// (51), and the test start. Deleting the map makes those crash the game, so they're listed first.</summary>
    public static List<string> References(RomBuffer rom, int id, Func<int, string> mapLabel)
    {
        var refs = new List<string>();
        foreach (var m in MapCatalog.Scan(rom))
        {
            if (m.MapId == id) continue;
            try
            {
                var map = LufiaMap.Load(rom, m.MapId);
                foreach (var e in map.Exits.Where(e => !e.IsUnused && e.DestMap == id))
                    refs.Add($"Exit E{e.Index} of {mapLabel(m.MapId)}");
            }
            catch { }
            int slots;
            try { slots = MapEvents.TableLength(rom, m.MapId); } catch { continue; }
            var seen = new HashSet<int>();
            for (int ev = 0; ev < slots; ev++)
            {
                int start = LufiaMap.EventScriptOffset(rom, m.MapId, ev);
                if (start < 0 || !seen.Add(start)) continue;
                EventScript? s;
                try { s = EventScript.Load(rom, m.MapId, ev); } catch { continue; }
                if (s == null) continue;
                int warps = s.Ops.Count(o => !o.IsText && o.Bytes.Length >= 3 && o.Bytes[0] is 0x4C or 0x5B or 0x51 && o.Bytes[1] == id);
                if (warps > 0) refs.Add($"Event {ev} of {mapLabel(m.MapId)} ({warps} warp / run-event command{(warps == 1 ? "" : "s")})");
            }
        }
        if (TestStartPatch.Current(rom) == TestStartPatch.Mode.ChosenMap && TestStartPatch.Target(rom).Map == id) refs.Add("The test start (it puts the party on this map)");
        return refs;
    }

    /// <summary>True for maps made with Lufia Forge (their data is in the added space).</summary>
    public static bool IsNewMap(RomBuffer rom, int id)
    {
        try { return LufiaCompression.ResourceOffset(rom, LufiaMap.MapDataResource(rom, id)) >= MapWriter.ExpansionStart; }
        catch { return false; }
    }

    /// <summary>
    /// Deletes a map: its number becomes free again (for New map), and the added space its data, events and setup
    /// script used is given back (unless another map shares them). Anything still leading to it is left as it is.
    /// </summary>
    public static void Delete(RomBuffer rom, int id)
    {
        if (id <= LufiaMap.WorldMap) throw new InvalidOperationException("The world map can't be deleted.");
        if (!LufiaMap.IsValidMap(rom, id, out _, out _)) throw new InvalidOperationException($"Map {id:X2} doesn't exist.");
        var space = ExpansionSpace.Load(rom);
        int data = LufiaCompression.ResourceOffset(rom, LufiaMap.MapDataResource(rom, id));
        int block = EventScript.BlockOffset(rom, id);
        bool blockShared = MapCatalog.Scan(rom).Any(m => m.MapId != id && EventScript.BlockOffset(rom, m.MapId) == block);
        if (space.At(data) is { } d && d.Start == data && d.Kind is ExpansionSpace.Kind.Resource or ExpansionSpace.Kind.MapData) space.Free(data);
        if (!blockShared && space.At(block) is { Kind: ExpansionSpace.Kind.ScriptBlock } b && b.Start == block) space.Free(block);
        space.Save();
        SetupScripts.Forget(rom, id);
        rom.WriteByte(SelectTable + id, 0);
        rom.WriteUInt16Le(SetupTable + id * 2, 0xFFFF);
        rom.WriteBytes(ScriptTable + id * 5, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        rom.FixChecksum();
    }

    /// <summary>A setup script that does nothing: three zero bytes in bank 03 (leading word 0, then end), found in the
    /// ROM (the scripts are addressed with 16-bit offsets from 0x18000, so a new one can't go elsewhere).</summary>
    public static int EmptySetupScript(RomBuffer rom)
    {
        for (int p = 0x18400; p + 3 <= 0x20000; p++)
            if (rom.ReadByte(p) == 0 && rom.ReadByte(p + 1) == 0 && rom.ReadByte(p + 2) == 0) return p;
        throw new InvalidOperationException("No empty setup script found in bank 03.");
    }
}
