namespace LufiaForge.Core.Lufia2;

/// <summary>
/// A Lufia II map's info block (LEdit's L2MapHeader "presets", checked on the US ROM): a 3-byte SNES pointer per map
/// at $CF:FCBC (file 0x27FCBC) to a block of u16 size + 21 u16 list offsets; each list holds records starting with
/// an id byte, FF ends it. Coordinates are in 16x16 blocks.
/// </summary>
public static class L2MapInfo
{
    public const int Pointers = 0x27FCBC;

    /// <summary>A thing on the map: a kind, an area (x1,y1)-(x2,y2) inclusive, and what it does.</summary>
    public sealed record Entry(string Kind, int Id, int X1, int Y1, int X2, int Y2, string Detail, int DestMap = -1, int DestX = 0, int DestY = 0);

    private static readonly (int Offset, string Kind, int Size)[] Lists =
    {
        (0x02, "Door", 15), (0x06, "Exit", 9), (0x0A, "Secret passage", 5), (0x0C, "Cutscene trigger", 5),
        (0x10, "Character start", 8), (0x1A, "Bomb spot", 3), (0x1C, "Sword spot", 3), (0x1E, "Arrow spot", 3),
        (0x20, "Action spot", 3), (0x26, "Treasure", 4), (0x28, "Movable object", 3), (0x2A, "Hammer spot", 3),
    };

    public static int Block(RomBuffer rom, int map)
    {
        int a = rom.ReadByte(Pointers + map * 3) | rom.ReadByte(Pointers + map * 3 + 1) << 8 | rom.ReadByte(Pointers + map * 3 + 2) << 16;
        return a == 0 ? -1 : L2Rom.SnesToFile(a);
    }

    public static List<Entry> Read(RomBuffer rom, int map)
    {
        var list = new List<Entry>();
        int b = Block(rom, map);
        if (b < 0 || b >= rom.Length) return list;
        int size = rom.ReadUInt16Le(b);
        foreach (var (off, kind, sz) in Lists)
        {
            int p = b + rom.ReadUInt16Le(b + off);
            for (int guard = 0; guard < 64 && p > b && p < b + size && rom.ReadByte(p) != 0xFF; guard++, p += sz)
            {
                var r = rom.ReadBytes(p, sz);
                list.Add(kind switch
                {
                    "Door" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], $"opens onto ({r[5]},{r[6]})-({r[7]},{r[8]})"),
                    "Exit" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], $"to map {r[8]:X2} at ({r[6]},{r[7]})", r[8], r[6], r[7]),
                    "Secret passage" or "Cutscene trigger" => new Entry(kind, r[0], r[1], r[2], r[3], r[4], kind == "Cutscene trigger" ? $"starts trigger {r[0]:X2}'s scene" : ""),
                    "Character start" => new Entry(kind, r[0], r[1], r[2], r[1], r[2], $"character {r[0] + 0x4F:X2}, walks in ({r[3]},{r[4]})-({r[5]},{r[6]})"),
                    "Treasure" => new Entry(kind, r[0], r[1], r[2], r[1], r[2], ""),
                    _ => new Entry(kind, r[0], r[1], r[2], r[1], r[2], ""),
                });
            }
        }
        return list;
    }
}
