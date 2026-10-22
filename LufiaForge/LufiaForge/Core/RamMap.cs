namespace LufiaForge.Core;

/// <summary>
/// Which RAM the game leaves free, and which of it Lufia Forge's patches use, so new patches can find room and two
/// patches never share a byte by accident (<see cref="Clashes"/>).
/// <para>
/// The free areas were measured in BizHawk (2026-10-22): all 128 KB of work RAM was compared every 8 frames with what
/// it held after power-on, over 14 runs of a random-button player (the prologue and its fights, a save in Alekia, the
/// world map, Alekia, its castle, Treck's tower, Herat, Arubus, Frederia, the finale, Kirof, Medan Castle, the Grenoble
/// cave, Platina; field, menus, 14 battles). These bytes were never written. The game doesn't clear bank 7F at power-on,
/// so a patch must set up what it keeps there. Shops, inns, the ship, saving, the credits and the biggest fights weren't
/// covered, so an area marked <see cref="Free.Sure"/> false wants a try in the game first.
/// </para>
/// <para>
/// More room: <see cref="SaveRamPatch"/> asks for 32 KB of cartridge RAM, which adds $70:2000-$70:7FFF (24 KB, kept with
/// the power off). The game's own 8 KB also has $70:1830-$70:1BFF free (after its three save slots and signature).
/// </para>
/// </summary>
public static class RamMap
{
    /// <summary>A stretch of RAM, SNES addresses (bank:offset); <see cref="End"/> is the first byte after it.</summary>
    public readonly record struct Area(int Start, int End)
    {
        public int Length => End - Start;
        public bool Overlaps(Area o) => Start < o.End && o.Start < End;
        public override string ToString() => $"${Start >> 16:X2}:{Start & 0xFFFF:X4}-${Start >> 16:X2}:{(End - 1) & 0xFFFF:X4}";
    }

    /// <summary>RAM the game never wrote in the survey. <paramref name="Sure"/>: also clear of the places the survey
    /// couldn't reach (an area next to the fight records or a buffer that bigger scenes might fill is not).</summary>
    public readonly record struct Free(Area Area, bool Sure, string Note);

    /// <summary>RAM a Lufia Forge patch uses.</summary>
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

    /// <summary>Pairs of uses that share bytes (should be none).</summary>
    public static List<(Use A, Use B)> Clashes()
    {
        var list = new List<(Use, Use)>();
        for (int i = 0; i < Uses.Count; i++)
            for (int j = i + 1; j < Uses.Count; j++)
                if (Uses[i].Area.Overlaps(Uses[j].Area)) list.Add((Uses[i], Uses[j]));
        return list;
    }

    /// <summary>The free parts of the free areas: what no patch uses yet.</summary>
    public static List<(Area Area, bool Sure, string Note)> Unused()
    {
        var list = new List<(Area, bool, string)>();
        foreach (var f in FreeAreas)
        {
            var parts = new List<Area> { f.Area };
            foreach (var u in Uses)
                parts = parts.SelectMany(p => !p.Overlaps(u.Area) ? new[] { p }
                    : new[] { new Area(p.Start, u.Area.Start), new Area(u.Area.End, p.End) }.Where(x => x.Length > 0)).ToList();
            list.AddRange(parts.Select(p => (p, f.Sure, f.Note)));
        }
        return list;
    }
}
