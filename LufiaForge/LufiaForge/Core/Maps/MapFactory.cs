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
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        int res = ResourceIds.Add(rom, data);
        if (res - 0xB2 > 0xFF) throw new InvalidOperationException("No resource ids left that a map can use (they must be below 1B2).");
        rom.WriteByte(SelectTable + id, (byte)(res - 0xB2));
        rom.WriteBytes(ScriptTable + id * 5, rom.ReadBytes(ScriptTable + template * 5, 5));
        rom.WriteByte(AreaTable + id, rom.ReadByte(AreaTable + template));
        if (blank)
        {
            rom.WriteUInt16Le(SetupTable + id * 2, (ushort)(EmptySetupScript(rom) - SetupTable));
            EventScript.NewBlockFor(rom, id, 32);
            var m = LufiaMap.Load(rom, id);
            for (int i = 0; i < m.Layer.Length; i++) m.Layer[i] = (ushort)(fill & 0x3FF);
            foreach (var n in m.Npcs) n.Sprite = 0xFF;
            m.Exits.Clear();
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

    /// <summary>A setup script that does nothing: three zero bytes in bank 03 (leading word 0, then end), found in the
    /// ROM (the scripts are addressed with 16-bit offsets from 0x18000, so a new one can't go elsewhere).</summary>
    public static int EmptySetupScript(RomBuffer rom)
    {
        for (int p = 0x18400; p + 3 <= 0x20000; p++)
            if (rom.ReadByte(p) == 0 && rom.ReadByte(p + 1) == 0 && rom.ReadByte(p + 2) == 0) return p;
        throw new InvalidOperationException("No empty setup script found in bank 03.");
    }
}
