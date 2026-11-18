namespace LufiaForge.Core;

// which ram the game leaves free and which of it our patches use, so new patches can find room and two patches never
// share a byte by accident (Clashes).
// i measured the free areas in bizhawk (2026-10-22): all 128 KB of work ram compared every 8 frames with what it had
// after power-on, over 14 runs of a random-button player (the prologue and its fights, a save in alekia, the world map,
// alekia, its castle, treck's tower, herat, arubus, frederia, the finale, kirof, medan castle, the grenoble cave,
// platina, so field, menus and 14 battles). these bytes never got written. the game doesn't clear bank 7F at power-on,
// so a patch has to set up whatever it keeps there. shops, inns, the ship, saving, the credits and the biggest fights
// weren't covered, so an area with Free.Sure false wants a try in the game first.
// more room: SaveRamPatch asks for 32 KB of cartridge ram, which adds $70:2000-$70:7FFF (24 KB, kept with the power
// off). the game's own 8 KB also has $70:1830-$70:1BFF free (after its three save slots and signature)
public static class RamMap
{
    // a stretch of ram in snes addresses (bank:offset), End is the first byte after it
    public readonly record struct Area(int Start, int End)
    {
        public int Length => End - Start;
        public bool Overlaps(Area other) => Start < other.End && other.Start < End;
        public override string ToString() => $"${Start >> 16:X2}:{Start & 0xFFFF:X4}-${Start >> 16:X2}:{(End - 1) & 0xFFFF:X4}";
    }

    // ram the game never wrote in the survey. Sure = also clear of the places the survey couldn't reach (an area next
    // to the fight records, or a buffer that bigger scenes might fill, isn't)
    public readonly record struct Free(Area Area, bool Sure, string Note);

    // ram one of our patches uses
    public readonly record struct Use(Area Area, string Patch, string Note);

    private static Area A(int start, int endInclusive) => new(start, endInclusive + 1);

    public static readonly IReadOnlyList<Free> FreeAreas = new Free[]
    {
        new(A(0x7E195A, 0x7E1DFF), true, "low RAM: reachable with 16-bit addresses from code in banks 00-3F/80-BF (the stack sits above, at $1FA2-$1FFF)"),
        new(A(0x7E4900, 0x7E4FFF), true, "cleared at power-on, never used after"),
        new(A(0x7E8400, 0x7E8FFF), false, "just below the tileset unpacked at $7E:9000"),
        new(A(0x7EE900, 0x7EEBFF), false, "after the 8 monster fight records ($7E:E500-$E8FF)"),
        new(A(0x7EED20, 0x7EEFFF), false, "near the fight records"),
        new(A(0x7EF820, 0x7EFEFF), false, "below the colour fade buffer ($7E:FF00)"),
        new(A(0x7FEE18, 0x7FFFFF), true, "after the map data (the world map, the biggest, ends at $7F:EE17)"),
        new(A(0x701830, 0x701BFF), true, "cartridge RAM after the save slots and signature"),
        new(A(SaveRamPatch.FreeStart, SaveRamPatch.FreeEnd - 1), true, "only with the 32 KB cartridge RAM patch"),
    };

    public static readonly IReadOnlyList<Use> Uses = new Use[]
    {
        new(A(0x7E4F60, 0x7E4FFF), "Emotes", "state and saved colours"),
        new(A(0x7EE570, 0x7EE575), "Monster AI commands", "in each fight record (+$70-$75; slots 0-7, $80 apart)"),
        new(A(0x7E1C00, 0x7E1DFF), "Monster AI script space", "the script of the monster whose turn it is, copied from the added space"),
        new(A(0x7FFD00, 0x7FFDDB), "Visible monsters", "path walking, step-on areas"),
        new(A(0x7FFEC0, 0x7FFEC8), "Visible monsters", "the hero's tile, turn count, waiting monster, grace steps"),
        new(A(0x7FFC00, 0x7FFC3F), "Visible monsters", "the turn each actor last moved on"),
        new(A(0x7FFE00, 0x7FFEBF), "Battle backgrounds", "colours kept for the fade back up"),
        new(A(0x7FFF00, 0x7FFF1F), "Monster AI commands", "scratch"),
        new(A(0x7FFF21, 0x7FFF2F), "Ancient Cave", "run state, random numbers, spawn point, spell chest"),
        new(A(0x7FFF70, 0x7FFF7F), "Ancient Cave", "loot picks, spell picks, recent gear"),
        new(A(0x7FFF80, 0x7FFFBF), "Ancient Cave", "spell chest candidates"),
        new(A(0x7FFFC0, 0x7FFFC2), "Spell animations", "sheet state"),
        new(A(0x7FE000, 0x7FED5F), "Ancient Cave", "floor generator (only while a floor is made; the map data reuses it after)"),
        new(A(0x701C00, 0x701F3F), "Ancient Cave", "party backup and direct page while in the cave (cartridge RAM)"),
    };

    // pairs of uses that share bytes (should be none)
    public static List<(Use A, Use B)> Clashes()
    {
        var list = new List<(Use, Use)>();
        for (int i = 0; i < Uses.Count; i++)
            for (int j = i + 1; j < Uses.Count; j++)
                if (Uses[i].Area.Overlaps(Uses[j].Area)) list.Add((Uses[i], Uses[j]));
        return list;
    }

    // the free parts of the free areas, what no patch uses yet
    public static List<(Area Area, bool Sure, string Note)> Unused()
    {
        var list = new List<(Area, bool, string)>();
        foreach (var free in FreeAreas)
        {
            var parts = new List<Area> { free.Area };
            foreach (var use in Uses)
                parts = parts.SelectMany(part => !part.Overlaps(use.Area) ? new[] { part }
                    : new[] { new Area(part.Start, use.Area.Start), new Area(use.Area.End, part.End) }.Where(x => x.Length > 0)).ToList();
            list.AddRange(parts.Select(part => (part, free.Sure, free.Note)));
        }
        return list;
    }
}
