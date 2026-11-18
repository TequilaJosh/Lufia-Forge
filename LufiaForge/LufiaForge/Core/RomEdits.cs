using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

// what's been done to a rom, read from the rom itself (so it's the same in every session): its size, the blocks we keep
// in the space table above 1 MB, the built-in patches, title animation and data other hacks are known to add.
// everything an editor shows is read the same way, through the game's own pointers and that table, so a rom edited
// over lots of sessions opens with every change in place
public static class RomEdits
{
    public static List<(string Label, string Value)> Describe(RomBuffer rom)
    {
        var list = new List<(string, string)>();
        var identity = RomIdentity.Identify(rom.Original ?? rom.ToArray(), rom.Game);
        list.Add(("Started from:", identity.What + (identity.ChangedParts.Count > 0 ? $" (it changed: {string.Join(", ", identity.ChangedParts)})" : "")));
        var moved = TableGuard.CheckAll(rom).Where(r => r.State == TableGuard.State.MovedByAnotherHack).Select(r => r.Table.Name).ToList();
        if (moved.Count > 0) list.Add(("Kept as they are:", $"another hack moved the {string.Join(", ", moved)}; Lufia Forge doesn't write there"));
        int sizeKb = rom.Length / 1024;
        string size = rom.Length switch
        {
            MapWriter.ExpansionStart => "1 MB (the original size)",
            MapWriter.ExpandedSize => "2 MB (expanded)",
            MapWriter.MaxSize => "4 MB (expanded: the most a Lufia cartridge can address)",
            _ => $"{sizeKb / 1024.0:0.#} MB (expanded by another hack)",
        };
        list.Add(("ROM size:", size));

        if (rom.Length >= MapWriter.ExpandedSize)
        {
            var freeSpace = ExpansionSpace.Load(rom);
            var entries = freeSpace.Entries;
            int Count(ExpansionSpace.Kind kind) => entries.Count(x => x.Kind == kind);
            long room = rom.Length - ExpansionSpace.FirstUsable - ExpansionSpace.TableSize;
            list.Add(("Lufia Forge edits:",
                $"{Count(ExpansionSpace.Kind.MapData)} maps, {Count(ExpansionSpace.Kind.ScriptBlock)} event script blocks, " +
                $"{Count(ExpansionSpace.Kind.Resource)} graphics/music/data resources and {Count(ExpansionSpace.Kind.Patch)} patch routines stored in the added space"));
            long kept = Union(entries.Where(x => x.Kind == ExpansionSpace.Kind.Unknown));
            if (kept > 0)
                list.Add(("Kept safe:", $"{kept / 1024:N0} KB of other data in the added space (other hacks or older copies) is never overwritten"));
            list.Add(("Added space free:", $"{Math.Max(0, room - Union(entries)) / 1024:N0} KB of {room / 1024:N0} KB"));
        }

        var patches = new List<string>();
        if (RetargetPatch.IsApplied(rom)) patches.Add("party members retarget");
        else if (RetargetPatch.RoutineOffset(rom) > 0) patches.Add("party members retarget (older version: single targets only - add it again in the Patch Manager for group spells too)");
        if (EncounterPatch.IsApplied(rom)) patches.Add("hold L to avoid battles");
        if (SubgroupPatch.IsApplied(rom)) patches.Add("more than 67 encounter subgroups");
        if (RenamePatch.IsApplied(rom)) patches.Add("rename command for events");
        if (EmotePatch.IsApplied(rom)) patches.Add("emote bubbles for cutscenes");
        if (Cave.AncientCave.Read(rom) is { } cave) patches.Add($"Ancient Cave (maps {cave.Map1:X2}/{cave.Map2:X2}{cave.Mode switch { Cave.AncientCave.CaveMode.CaveOnly => ", cave-only New Game", Cave.AncientCave.CaveMode.ElevatorStart => ", New Game at the Old Cave elevator", _ => ", from the Old Cave" }})");
        if (AguroMagicPatch.IsApplied(rom)) patches.Add("spells and MP for Guy / Aguro");
        else if (AguroMagicPatch.NeedsUpdate(rom)) patches.Add("spells and MP for Guy / Aguro (older version: click Update in the Patch Manager)");
        if (SpellTablePatch.IsApplied(rom)) patches.Add($"up to 128 spells, 64 per character ({SpellTablePatch.NewCount(rom)} added)");
        else if (SpellTablePatch.NeedsUpdate(rom)) patches.Add("new spells (older version: click Update on \"Up to 128 spells\" in the Patch Manager)");
        if (FontPatch.IsApplied(rom)) patches.Add("Lufia II font");
        if (ResourceIds.IsMoved(rom)) patches.Add($"resource table moved ({ResourceIds.Count(rom) - ResourceIds.OriginalCount} new resources)");
        list.Add(("Built-in patches:", patches.Count == 0 ? "none" : string.Join(", ", patches)));

        try
        {
            var title = TitleScreen.Load(rom);
            if (title.Cycles.Any(c => c.On)) list.Add(("Title screen:", "animated (colour cycling of " + string.Join(" and ", Enumerable.Range(0, 2).Where(k => title.Cycles[k].On).Select(k => $"row {k + 2}")) + ")"));
        }
        catch { }

        var hacks = new List<string>();
        if (ItemDescriptions.Find(rom) != null) hacks.Add("Lufia Restored (item descriptions - editable in the Items tab)");
        if (hacks.Count > 0) list.Add(("Other hacks found:", string.Join(", ", hacks)));
        return list;
    }

    // bytes the blocks cover (overlaps counted once)
    private static long Union(IEnumerable<ExpansionSpace.Entry> blocks)
    {
        long total = 0; int end = int.MinValue;
        foreach (var block in blocks.OrderBy(x => x.Start))
        {
            int start = Math.Max(block.Start, end);
            if (block.End > start) total += block.End - start;
            end = Math.Max(end, block.End);
        }
        return total;
    }

    // one line for the status bar when a rom gets opened
    public static string Summary(RomBuffer rom)
    {
        var lines = Describe(rom);
        if (rom.Length == MapWriter.ExpansionStart && lines.Any(x => x.Item1 == "Built-in patches:" && x.Item2 == "none") && lines.Count <= 2)
            return "";
        return string.Join("  |  ", lines.Where(x => x.Item1 is "ROM size:" or "Lufia Forge edits:" or "Other hacks found:").Select(x => $"{x.Item1} {x.Item2}"));
    }
}
