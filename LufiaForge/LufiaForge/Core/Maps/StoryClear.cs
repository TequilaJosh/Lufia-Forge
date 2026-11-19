using System.IO;

namespace LufiaForge.Core.Maps;

// lufia 1: "Start from a clean slate". every map keeps its layout, tiles, exits, arrival points and doors, but loses
// its people, their walking paths, its step-on areas, its treasure (chests, gold, hidden items, examine spots) and
// every event and cutscene. New Game starts on a chosen map and arrival point with the Hero.
// - map data: no characters (section C), paths (G) or step-on areas (D). map 4F, the title stage, keeps its own,
//   since its character and areas are what start the opening
// - setup scripts ($01:98AF) get rebuilt along the path a new game takes (no story flag set: the tests 05/07/09 are
//   true, 06/08/0A false, $01:DDE7, and 04 jumps when the test is true). we keep the current map, music and palette
//   settings (01-03, 0B-0D), arrival points (21+n) and doors (C1+n with FE). characters (41+n), areas (81+n) and the
//   other spot contents are left out
// - events: every map's script table entry (0x18200 + map*5) points at one shared empty table (FFFF = no event, the
//   game's own convention) with event base 0. map 4F gets a table whose entry 8, the opening (the game runs it as
//   event 6 with 4F's base 2), fades out and warps to the start: the prologue's four heroes leave (1B 04-07, like at
//   the prologue's end), the Hero joins (1A 00 00, a new game's party list is empty), story flag 80 ("prologue
//   done"), the leader shown again (the opening hides the party), a warp (5B), the map's song (02 in its setup) and a
//   fade in like the game's own scenes do after a warp
public static class StoryClear
{
    public const int OpeningMap = 0x4F, OpeningEntry = 8;   // the opening: table entry 8 (event 6, map 4F's base being 2)

    public sealed record Result(int Maps, int People, int Areas, int Treasure, int Events);

    // a map's setup script the way a new game runs it, without characters, areas or treasure
    public static byte[] NewGameSetup(byte[] script, out int people, out int areas, out int treasure)
    {
        people = areas = treasure = 0;
        var output = new List<byte> { script[0], script[1] };   // the leading word ($0787)
        bool cond = false;
        var seen = new HashSet<int>();
        for (int pos = 2, guard = 0; pos < script.Length && guard < 2000 && seen.Add(pos); guard++)
        {
            int op = script[pos];
            if (op == 0x00) break;
            switch (op)
            {
                case 0x04:
                    int target = script[pos + 1] | script[pos + 2] << 8;
                    if (cond) { cond = false; pos = target; continue; }
                    cond = false; pos += 3; continue;
                case 0x05: cond = true; pos += 2; continue;            // flag clear
                case 0x06: cond = false; pos += 2; continue;           // flag set
                case 0x07: pos += 2; cond = true; continue;            // cond |= clear
                case 0x08: pos += 2; continue;                         // cond |= set
                case 0x09: pos += 2; continue;                         // cond &= clear
                case 0x0A: pos += 2; cond = false; continue;           // cond &= set
                case 0x01 or 0x02 or 0x03 or 0x0C or 0x0D: output.AddRange(script.AsSpan(pos, 2).ToArray()); pos += 2; continue;
                case 0x0B: output.AddRange(script.AsSpan(pos, 3).ToArray()); pos += 3; continue;
            }
            if (op < 0x20) throw new InvalidDataException($"Unknown setup command {op:X2}.");
            if (op < 0x40) { output.AddRange(script.AsSpan(pos, 4).ToArray()); pos += 4; continue; }       // arrival point
            if (op < 0x80) { if (op > 0x40) people++; pos += 1; continue; }                     // character on
            if (op < 0xC0) { areas++; pos += 2; continue; }                                    // step-on area
            if (script[pos + 2] == 0xFE) output.AddRange(script.AsSpan(pos, 4).ToArray()); else treasure++;      // doors stay
            pos += 4;
        }
        output.Add(0x00);
        return output.ToArray();
    }

    // the map's song: setup command 02 (map 4F's 02 1C is the opening's song 1C)
    private static int SongOf(byte[] setup)
    {
        int song = -1;
        for (int pos = 2; pos < setup.Length && setup[pos] != 0x00;)
        {
            int op = setup[pos];
            if (op == 0x02) song = setup[pos + 1];
            pos += op switch { 0x0B => 3, < 0x20 => 2, < 0x40 => 4, < 0x80 => 1, < 0xC0 => 2, _ => 4 };
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
            var map = LufiaMap.Load(rom, id);
            if (map.Npcs.Count > 0 || map.Paths.Count > 0 || map.Triggers.Count > 0)
            {
                map.Npcs.Clear(); map.Paths.Clear(); map.Triggers.Clear();
                MapWriter.Save(rom, map, true);
            }
            // the setup script, the way a new game runs it
            var setup = NewGameSetup(SetupScripts.Read(rom, id), out int mapPeople, out int mapAreas, out int mapTreasure);
            people += mapPeople; areas += mapAreas; treasure += mapTreasure;
            if (id == startMap) song = SongOf(setup);
            SetupScripts.Replace(rom, id, setup);
        }

        // events: one shared empty table, and the opening
        var freeSpace = ExpansionSpace.Load(rom);
        foreach (var entry in freeSpace.Entries.Where(e => e.Kind == ExpansionSpace.Kind.ScriptBlock).ToList()) freeSpace.Free(entry.Start);
        const int Entries = 256;   // every event number a map's base and a byte can reach
        var empty = Enumerable.Repeat((byte)0xFF, Entries * 2).Append((byte)0x00).ToArray();
        int emptyAt = freeSpace.AllocateInOneBank(empty.Length, ExpansionSpace.Kind.ScriptBlock, 0xFFF, EventScript.ScriptLimit);
        var opening = new List<byte>(Enumerable.Repeat((byte)0xFF, (OpeningEntry + 1) * 2));
        int code = opening.Count;
        opening[OpeningEntry * 2] = (byte)code; opening[OpeningEntry * 2 + 1] = (byte)(code >> 8);
        opening.AddRange(new byte[]
        {
            0x19, 0x02, 0x5A,                          // fade out (the file select runs in this wait, like in the opening)
            0x1B, 0x04, 0x1B, 0x05, 0x1B, 0x06, 0x1B, 0x07,   // the prologue's four heroes leave
            0x1A, 0x00, 0x00,                          // and the Hero joins (a new game's party list is empty)
            0x06, 0x80,                                // story flag 80, "prologue done"
            0x5F, 0x01,                                // show the leader (the opening hides the party)
            0x5B, (byte)startMap, (byte)startArrival,  // warp, the screen staying black
        });
        if (song >= 0) opening.AddRange(new byte[] { 0x0A, (byte)song });   // after 5B the music is silent until a script plays a song
        opening.AddRange(new byte[]
        {
            0x19, 0x01, 0x5A, 0x50, 0x06, 0x18, 0x02, 0x5A,   // fade in like the game's own scenes after a warp (the sprites' colours too)
            0x00,
        });
        int openingAt = freeSpace.AllocateInOneBank(opening.Count, ExpansionSpace.Kind.ScriptBlock, OpeningMap, EventScript.ScriptLimit);
        freeSpace.Save();
        rom.WriteBytes(emptyAt, empty);
        rom.WriteBytes(openingAt, opening.ToArray());
        foreach (int id in maps.Append(OpeningMap).Distinct())
        {
            int block = (id == OpeningMap ? openingAt : emptyAt) - 0x18000;
            int tableEntry = LufiaMap.MapScriptTable + id * 5;
            rom.WriteBytes(tableEntry, new[] { (byte)block, (byte)(block >> 8), (byte)(block >> 16) });
            if (id != OpeningMap) rom.WriteByte(tableEntry + 4, 0);   // event base (4F keeps its own, the game runs the opening by event number)
        }
        rom.FixChecksum();
        return new Result(maps.Count, people, areas, treasure, events);
    }
}
