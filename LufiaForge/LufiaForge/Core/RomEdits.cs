using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

/// <summary>
/// What has been done to a ROM, read from the ROM itself (so it is the same in every session): its size, the
/// blocks Lufia Forge keeps in its space table above 1 MB, the built-in patches, title animation and data other
/// hacks are known to add. Everything an editor shows is read the same way - through the game's own pointers and
/// that table - so a ROM edited over many sessions opens with every change in place.
/// </summary>
public static class RomEdits
{
    public static List<(string Label, string Value)> Describe(RomBuffer rom)
    {
        var list = new List<(string, string)>();
        int kb = rom.Length / 1024;
        string size = rom.Length switch
        {
            MapWriter.ExpansionStart => "1 MB (the original size)",
            MapWriter.ExpandedSize => "2 MB (expanded)",
            MapWriter.MaxSize => "4 MB (expanded: the most a Lufia cartridge can address)",
            _ => $"{kb / 1024.0:0.#} MB (expanded by another hack)",
        };
        list.Add(("ROM size:", size));

        if (rom.Length >= MapWriter.ExpandedSize)
        {
            var space = ExpansionSpace.Load(rom);
            var e = space.Entries;
            int Count(ExpansionSpace.Kind k) => e.Count(x => x.Kind == k);
            long room = rom.Length - ExpansionSpace.FirstUsable - ExpansionSpace.TableSize;
            list.Add(("Lufia Forge edits:",
                $"{Count(ExpansionSpace.Kind.MapData)} maps, {Count(ExpansionSpace.Kind.ScriptBlock)} event script blocks, " +
                $"{Count(ExpansionSpace.Kind.Resource)} graphics/music/data resources and {Count(ExpansionSpace.Kind.Patch)} patch routines stored in the added space"));
            long kept = Union(e.Where(x => x.Kind == ExpansionSpace.Kind.Unknown));
            if (kept > 0)
                list.Add(("Kept safe:", $"{kept / 1024:N0} KB of other data in the added space (other hacks or older copies) is never overwritten"));
            list.Add(("Added space free:", $"{Math.Max(0, room - Union(e)) / 1024:N0} KB of {room / 1024:N0} KB"));
        }

        var patches = new List<string>();
        if (RetargetPatch.IsApplied(rom)) patches.Add("party members retarget");
        else if (RetargetPatch.RoutineOffset(rom) > 0) patches.Add("party members retarget (older version: single targets only - add it again in the Patch Manager for group spells too)");
        if (EncounterPatch.IsApplied(rom)) patches.Add("hold L to avoid battles");
        if (SubgroupPatch.IsApplied(rom)) patches.Add("more than 67 encounter subgroups");
        if (RenamePatch.IsApplied(rom)) patches.Add("rename command for events");
        list.Add(("Built-in patches:", patches.Count == 0 ? "none" : string.Join(", ", patches)));

        try
        {
            var t = TitleScreen.Load(rom);
            if (t.Cycles.Any(c => c.On)) list.Add(("Title screen:", "animated (colour cycling of " + string.Join(" and ", Enumerable.Range(0, 2).Where(k => t.Cycles[k].On).Select(k => $"row {k + 2}")) + ")"));
        }
        catch { }

        var hacks = new List<string>();
        if (ItemDescriptions.Find(rom) != null) hacks.Add("Lufia Restored (item descriptions - editable in the Items tab)");
        if (hacks.Count > 0) list.Add(("Other hacks found:", string.Join(", ", hacks)));
        return list;
    }

    /// <summary>Bytes covered by the blocks (overlaps counted once).</summary>
    private static long Union(IEnumerable<ExpansionSpace.Entry> blocks)
    {
        long total = 0; int end = int.MinValue;
        foreach (var b in blocks.OrderBy(x => x.Start))
        {
            int s = Math.Max(b.Start, end);
            if (b.End > s) total += b.End - s;
            end = Math.Max(end, b.End);
        }
        return total;
    }

    /// <summary>One line for the status bar when a ROM is opened.</summary>
    public static string Summary(RomBuffer rom)
    {
        var d = Describe(rom);
        if (rom.Length == MapWriter.ExpansionStart && d.Any(x => x.Item1 == "Built-in patches:" && x.Item2 == "none") && d.Count <= 2)
            return "";
        return string.Join("  |  ", d.Where(x => x.Item1 is "ROM size:" or "Lufia Forge edits:" or "Other hacks found:").Select(x => $"{x.Item1} {x.Item2}"));
    }
}
