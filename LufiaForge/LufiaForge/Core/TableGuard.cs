using LufiaForge.Core.Lufia2;

namespace LufiaForge.Core;

/// <summary>
/// Whether the tables Lufia Forge reads at fixed places are still where the game's code looks for them. Each table is
/// tied to the places in the game's code that point at it (an LDA long with its address, or an immediate with it),
/// found by scanning the clean US ROMs (refscan) and read in the code. When another hack moved a table it changed those
/// bytes: the pages that edit the table then stay read-only, so nothing is written over the hack's data (the old spot
/// may hold something else now). Tables Lufia Forge moves itself, or follows from the code (the monster table the fan
/// hacks moved to $D0:A000), count as in place.
/// </summary>
public static class TableGuard
{
    public sealed record Table(GameKind Game, string Name, string[] Pages, (int At, byte[] Bytes)[] Sites, Func<RomBuffer, bool>? MovedByLufiaForge = null)
    {
        /// <summary>The game's own table (file offset, length): no writes there once another hack moved it.</summary>
        public (int At, int Length) Old { get; init; }
    }

    public enum State { InPlace, Followed, MovedByAnotherHack }

    public sealed record Result(Table Table, State State, string Note);

    private static (int, byte[]) S(int at, string hex) => (at, hex.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray());

    /// <summary>Lufia II (US): the tables and the code that reads them.</summary>
    public static readonly Table[] Lufia2 =
    {
        new(GameKind.Lufia2, "items", new[] { "Items" }, new[] { S(0xF29C, "bf 69 cf 96"), S(0xF2A1, "69 69 cf") }) { Old = (0xB4F69, 0x1D3 * 2) },
        new(GameKind.Lufia2, "item names", new[] { "Items" }, new[] { S(0xF2C2, "b9 e8 c7") }) { Old = (0xF47E8, 0x1D3 * 12) },
        new(GameKind.Lufia2, "spells", new[] { "Spells" }, new[] { S(0xF450, "bf 5b fa 95"), S(0xF455, "69 5b fa") }, L2Magic.IsInstalled) { Old = (0xAFA5B, 0x28 * 2) },
        new(GameKind.Lufia2, "IP attacks", new[] { "IP attacks" }, new[] { S(0xF463, "bf 10 8f 84") }, L2Magic.IsInstalled) { Old = (0x20F10, 0xA8 * 2) },
        new(GameKind.Lufia2, "IP attack effects", new[] { "IP attacks" }, new[] { S(0xF46D, "bf ef f1 95"), S(0xF472, "69 ef f1") }, L2Magic.IsInstalled) { Old = (0xAF1EF, 0x8D * 2) },
        new(GameKind.Lufia2, "capsule monsters", new[] { "Capsule monsters" }, new[] { S(0x143A2, "bf b8 dc 97") }) { Old = (0xBDCB8, 35 * 2) },
        new(GameKind.Lufia2, "capsule monster names", new[] { "Capsule monsters" }, new[] { S(0x142EA, "bf 58 d5 8e") }) { Old = (0x75558, 7 * 5) },
        new(GameKind.Lufia2, "capsule growth curves", new[] { "Capsule monsters" }, new[] { S(0x15348, "bf 20 f4 a6") }) { Old = (0x137420, 0x1E * 8) },
        new(GameKind.Lufia2, "capsule attacks", new[] { "Capsule monsters" }, new[] { S(0xF478, "bf 3b f6 97") }) { Old = (0xBF63B, 0x54 * 2) },
        new(GameKind.Lufia2, "hero starting stats", new[] { "Characters" }, new[] { S(0xF95E, "b9 3c b9") }) { Old = (0xBB93C, 7 * 14) },
        new(GameKind.Lufia2, "hero growth", new[] { "Characters" }, new[] { S(0xF8B2, "a9 2c b6") }) { Old = (0xBB62C, 7 * 0x70) },
        new(GameKind.Lufia2, "new game records", new[] { "Characters" }, new[] { S(0xEC8A, "a2 a1 b2") }) { Old = (0x2B2A1, 0xF4) },
        new(GameKind.Lufia2, "Ancient Cave records", new[] { "Characters" }, new[] { S(0xED4C, "a2 95 b3") }) { Old = (0x2B395, 0xC0) },
        new(GameKind.Lufia2, "treasure", new[] { "Treasure" }, new[] { S(0x685D, "bf b5 b8 91") }) { Old = (0x8B8B5, 0x100) },
        new(GameKind.Lufia2, "staff roll", new[] { "Credits" }, new[] { S(0x73448, "bf ff 9a 91") }) { Old = (0x89AFF, 0x6D5) },
        new(GameKind.Lufia2, "battle backgrounds", new[] { "Graphics" }, new[] { S(0xB9D5, "bf 40 fd 97") }) { Old = (0xBFD40, 24 * 9) },
        new(GameKind.Lufia2, "map sprite colours", new[] { "Graphics" }, new[] { S(0x296B1, "bf 00 bf a6") }) { Old = (0x133F00, 0x100) },
        new(GameKind.Lufia2, "map info", new[] { "Maps", "Exits", "Treasure", "Events" }, new[] { S(0x1B60C, "bf bc fc cf") }) { Old = (0x27FCBC, 0xF2 * 3) },
        new(GameKind.Lufia2, "packed files", new[] { "Maps", "Music", "Graphics" }, new[] { S(0xEA9, "bf 00 80 a7") }) { Old = (0x138000, 0x2A8 * 3) },
        new(GameKind.Lufia2, "shops", new[] { "Shops" }, new[] { S(0x16729, "a2 9f ee") }) { Old = (0xBEE9F, 0x100 * 2) },
        new(GameKind.Lufia2, "monster groups", new[] { "Encounters" }, new[] { S(0x837D, "bd ac b9") }) { Old = (0xBB9AC, 0xD8 * 2) },
        new(GameKind.Lufia2, "world map battle zones", new[] { "Encounters" }, new[] { S(0x31D22, "bf 94 c4 97"), S(0x31D59, "bf 93 c4 97") }) { Old = (0xBC493, 17 * 10) },
        new(GameKind.Lufia2, "map encounters", new[] { "Encounters" }, new[] { S(0x83B2, "b9 93 be") }) { Old = (0xBBE93, 0xF2 * 2) },
        new(GameKind.Lufia2, "shared battle scripts", new[] { "Monsters", "Capsule monsters" }, new[] { S(0x2BDA2, "bf dd fa 96") }) { Old = (0xB7ADD, 0x29 * 2) },
        new(GameKind.Lufia2, "monster attack names", new[] { "Monsters" }, new[] { S(0x2954D, "bc d7 f2") }) { Old = (0x2F2D7, 0x8A * 2) },
    };

    /// <summary>Lufia 1 (US): tables Lufia Forge doesn't move itself, and the code that reads them.</summary>
    public static readonly Table[] Lufia1 =
    {
        new(GameKind.Lufia1, "map data select table", new[] { "Map Editor" }, new[] { S(0xBD96, "bf 9b e1 01") }) { Old = (0xE19B, 0x100) },
        new(GameKind.Lufia1, "map records", new[] { "Map Editor" }, new[] { S(0xAC5C, "bf 5f e2 01") }) { Old = (0xE25F, 0x100 * 2) },
        new(GameKind.Lufia1, "event scripts", new[] { "Events", "Cutscenes" }, new[] { S(0xC2A8, "b9 00 82") }) { Old = (0x18200, 0x100 * 5) },
        new(GameKind.Lufia1, "characters", new[] { "Game Data" }, new[] { S(0x46B08, "bd 25 ed") }) { Old = (0x46D25, 7 * 29) },
        new(GameKind.Lufia1, "monster groups", new[] { "Game Data" }, new[] { S(0x4556A, "f4 60 da") }) { Old = (0x45A60, 0x100 * 2) },
    };

    public static IEnumerable<Table> For(RomBuffer rom) => rom.IsLufia2 ? Lufia2 : rom.Game == GameKind.Lufia1 ? Lufia1 : Array.Empty<Table>();

    public static Result Check(RomBuffer rom, Table t)
    {
        bool same = t.Sites.All(s => s.At + s.Bytes.Length <= rom.Length && rom.ReadBytes(s.At, s.Bytes.Length).AsSpan().SequenceEqual(s.Bytes));
        if (same) return new(t, State.InPlace, "");
        if (t.MovedByLufiaForge?.Invoke(rom) == true) return new(t, State.Followed, "moved by Lufia Forge");
        return new(t, State.MovedByAnotherHack, $"Another hack changed where the game reads the {t.Name}, so Lufia Forge doesn't write to them (it would write over that hack's data).");
    }

    public static List<Result> CheckAll(RomBuffer rom) => For(rom).Select(t => Check(rom, t)).ToList();

    /// <summary>Makes the game's own spot of every table another hack moved read-only in this ROM (the editors that use it
    /// are locked too; this also stops any other path writing there).</summary>
    public static void Protect(RomBuffer rom)
    {
        rom.Protected.Clear();
        foreach (var r in CheckAll(rom))
            if (r.State == State.MovedByAnotherHack && r.Table.Old.Length > 0)
                rom.Protected.Add((r.Table.Old.At, r.Table.Old.At + r.Table.Old.Length,
                    $"Not written: another hack changed where the game keeps its {r.Table.Name}, and the old place may hold that hack's data now."));
    }

    /// <summary>Why a page must stay read-only in this ROM, or null when it can be edited.</summary>
    public static string? LockReason(RomBuffer rom, string page)
    {
        var moved = CheckAll(rom).Where(r => r.State == State.MovedByAnotherHack && r.Table.Pages.Contains(page)).Select(r => r.Table.Name).ToList();
        return moved.Count == 0 ? null
            : $"Read only: another hack changed where the game keeps its {string.Join(", ", moved)} (it reads them somewhere else now), so changing them here could write over that hack's data.";
    }
}
