using System.IO;

namespace LufiaForge.Core.Maps;

/// <summary>
/// Lufia 1: "Start from a clean slate". Every map keeps its layout, tiles, exits, arrival points and doors, but loses
/// its people, their walking paths, its step-on areas, its treasure (chests, gold, hidden items, examine spots) and
/// every event and cutscene; New Game starts on a chosen map and arrival point with the Hero.
/// <list type="bullet">
/// <item>Map data: no characters (section C), paths (G) or step-on areas (D). Map 4F, the title stage, keeps its
/// own: its character and areas are what start the opening.</item>
/// <item>Setup scripts ($01:98AF) are rebuilt along the path a new game takes (no story flag set: the tests 05/07/09
/// are true, 06/08/0A false, $01:DDE7; 04 jumps when the test is true), keeping the current map, music and palette
/// settings (01-03, 0B-0D), arrival points (21+n) and doors (C1+n with FE); characters (41+n), areas (81+n) and the
/// other spot contents are left out.</item>
/// <item>Events: every map's script table entry (0x18200 + map*5) points at one shared empty table (FFFF = no event,
/// the game's own convention) with event base 0; map 4F gets a table whose entry 8, the opening (the game runs it as event 6 with 4F's base 2), fades out and warps
/// to the start: the prologue's four heroes leave (1B 04-07, as at the prologue's end), the Hero joins (1A 00 00: a new
/// game's party list is empty), story flag 80 ("prologue done"), the leader shown again (the opening hides the party),
/// a warp (5B), the map's song (02 in its setup) and a fade in as the game's own scenes after a warp.</item>
/// </list>
/// </summary>
public static class StoryClear
{
    public const int OpeningMap = 0x4F, OpeningEntry = 8;   // the opening: table entry 8 (event 6, map 4F's base being 2)

    public sealed record Result(int Maps, int People, int Areas, int Treasure, int Events);

    /// <summary>A map's setup script as a new game runs it, without characters, areas or treasure.</summary>
    public static byte[] NewGameSetup(byte[] s, out int people, out int areas, out int treasure)
    {
        people = areas = treasure = 0;
        var o = new List<byte> { s[0], s[1] };   // the leading word ($0787)
        bool cond = false;
        var seen = new HashSet<int>();
        for (int p = 2, guard = 0; p < s.Length && guard < 2000 && seen.Add(p); guard++)
        {
            int op = s[p];
            if (op == 0x00) break;
            switch (op)
            {
                case 0x04:
                    int to = s[p + 1] | s[p + 2] << 8;
                    if (cond) { cond = false; p = to; continue; }
                    cond = false; p += 3; continue;
                case 0x05: cond = true; p += 2; continue;            // flag clear
                case 0x06: cond = false; p += 2; continue;           // flag set
                case 0x07: p += 2; cond = true; continue;            // cond |= clear
                case 0x08: p += 2; continue;                         // cond |= set
                case 0x09: p += 2; continue;                         // cond &= clear
                case 0x0A: p += 2; cond = false; continue;           // cond &= set
                case 0x01 or 0x02 or 0x03 or 0x0C or 0x0D: o.AddRange(s.AsSpan(p, 2).ToArray()); p += 2; continue;
                case 0x0B: o.AddRange(s.AsSpan(p, 3).ToArray()); p += 3; continue;
            }
            if (op < 0x20) throw new InvalidDataException($"Unknown setup command {op:X2}.");
            if (op < 0x40) { o.AddRange(s.AsSpan(p, 4).ToArray()); p += 4; continue; }       // arrival point
            if (op < 0x80) { if (op > 0x40) people++; p += 1; continue; }                     // character on
            if (op < 0xC0) { areas++; p += 2; continue; }                                    // step-on area
            if (s[p + 2] == 0xFE) o.AddRange(s.AsSpan(p, 4).ToArray()); else treasure++;      // doors stay
            p += 4;
        }
        o.Add(0x00);
        return o.ToArray();
    }

    /// <summary>The map's song: setup command 02 (map 4F's 02 1C is the opening's song 1C).</summary>
    private static int SongOf(byte[] setup)
    {
        int song = -1;
        for (int p = 2; p < setup.Length && setup[p] != 0x00;)
        {
            int op = setup[p];
            if (op == 0x02) song = setup[p + 1];
            p += op switch { 0x0B => 3, < 0x20 => 2, < 0x40 => 4, < 0x80 => 1, < 0xC0 => 2, _ => 4 };
        }
        return song;
    }

    public static Result Clear(RomBuffer rom, int startMap, int startArrival)
    {
        if (startArrival < 1) throw new ArgumentOutOfRangeException(nameof(startArrival));
        var maps = MapCatalog.Scan(rom).Select(m => m.MapId).Distinct().OrderBy(i => i).ToList();
        if (!maps.Contains(startMap)) throw new InvalidOperationException($"Map {startMap:X2} isn't in this ROM.");
        int people = 0, areas = 0, treasure = 0, events = 0, song = -1;
        foreach (int id in maps)
        {
            events += EventScript.PlausibleEvents(rom, id).Count;
            if (id == OpeningMap) continue;   // the title stage: its character and areas start the opening
            // the map data: no characters, paths or step-on areas
            var m = LufiaMap.Load(rom, id);
            if (m.Npcs.Count > 0 || m.Paths.Count > 0 || m.Triggers.Count > 0)
            {
                m.Npcs.Clear(); m.Paths.Clear(); m.Triggers.Clear();
                MapWriter.Save(rom, m, true);
            }
            // the setup script, as a new game runs it
            var setup = NewGameSetup(SetupScripts.Read(rom, id), out int pp, out int aa, out int tt);
            people += pp; areas += aa; treasure += tt;
            if (id == startMap) song = SongOf(setup);
            SetupScripts.Replace(rom, id, setup);
        }

        // events: one shared empty table, and the opening
        var space = ExpansionSpace.Load(rom);
        foreach (var e in space.Entries.Where(e => e.Kind == ExpansionSpace.Kind.ScriptBlock).ToList()) space.Free(e.Start);
        const int Entries = 256;   // every event number a map's base and a byte can reach
        var empty = Enumerable.Repeat((byte)0xFF, Entries * 2).Append((byte)0x00).ToArray();
        int emptyAt = space.AllocateInOneBank(empty.Length, ExpansionSpace.Kind.ScriptBlock, 0xFFF, EventScript.ScriptLimit);
        var opening = new List<byte>(Enumerable.Repeat((byte)0xFF, (OpeningEntry + 1) * 2));
        int code = opening.Count;
        opening[OpeningEntry * 2] = (byte)code; opening[OpeningEntry * 2 + 1] = (byte)(code >> 8);
        opening.AddRange(new byte[]
        {
            0x19, 0x02, 0x5A,                          // fade out (the file select runs in this wait, as in the opening)
            0x1B, 0x04, 0x1B, 0x05, 0x1B, 0x06, 0x1B, 0x07,   // the prologue's four heroes leave
            0x1A, 0x00, 0x00,                          // and the Hero joins (a new game's party list is empty)
            0x06, 0x80,                                // story flag 80, "prologue done"
            0x5F, 0x01,                                // show the leader (the opening hides the party)
            0x5B, (byte)startMap, (byte)startArrival,  // warp, the screen staying black
        });
        if (song >= 0) opening.AddRange(new byte[] { 0x0A, (byte)song });   // after 5B the music is silent until a script plays a song
        opening.AddRange(new byte[]
        {
            0x19, 0x01, 0x5A, 0x50, 0x06, 0x18, 0x02, 0x5A,   // fade in as the game's own scenes after a warp (the sprites' colours too)
            0x00,
        });
        int openingAt = space.AllocateInOneBank(opening.Count, ExpansionSpace.Kind.ScriptBlock, OpeningMap, EventScript.ScriptLimit);
        space.Save();
        rom.WriteBytes(emptyAt, empty);
        rom.WriteBytes(openingAt, opening.ToArray());
        foreach (int id in maps.Append(OpeningMap).Distinct())
        {
            int block = (id == OpeningMap ? openingAt : emptyAt) - 0x18000;
            int t = LufiaMap.MapScriptTable + id * 5;
            rom.WriteBytes(t, new[] { (byte)block, (byte)(block >> 8), (byte)(block >> 16) });
            if (id != OpeningMap) rom.WriteByte(t + 4, 0);   // event base (4F keeps its own: the game runs the opening by event number)
        }
        rom.FixChecksum();
        return new Result(maps.Count, people, areas, treasure, events);
    }
}
