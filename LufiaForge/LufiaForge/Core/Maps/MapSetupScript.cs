using LufiaForge.Modules.GameData;

namespace LufiaForge.Core.Maps;

public enum SpotKind { Chest, Gold, EmptyChest, Hidden, Door, Trigger }

/// <summary>
/// What a map's setup script puts in one object spot (section E record): opcode C1+n, word value, flag.
/// </summary>
public sealed class SpotContent
{
    /// <summary>Section E record number.</summary>
    public int Spot { get; init; }
    /// <summary>Low byte of the value word: the item for chests and hidden items.</summary>
    public int Low { get; init; }
    /// <summary>High byte of the value word: FC chest, FD hidden item, FE door, FF trigger; else gold.</summary>
    public int High { get; init; }
    /// <summary>Per-map "already taken / opened" flag number.</summary>
    public int Flag { get; init; }
    /// <summary>File offset of the 4-byte command in the setup script.</summary>
    public int ScriptOffset { get; init; }
    /// <summary>True when the command sits after a story-flag check, so it only applies at some point of the story.</summary>
    public bool Conditional { get; init; }

    public int Value => Low | (High << 8);

    public SpotKind Kind => High switch
    {
        0xFC => Low == 0 ? SpotKind.EmptyChest : SpotKind.Chest,
        0xFD => SpotKind.Hidden,
        0xFE => SpotKind.Door,
        0xFF => SpotKind.Trigger,
        _    => SpotKind.Gold,
    };

    public bool IsTreasure => Kind is SpotKind.Chest or SpotKind.Gold or SpotKind.EmptyChest or SpotKind.Hidden;

    public string Describe(RomBuffer rom) => Kind switch
    {
        SpotKind.Chest      => $"Chest: {MapSetupScript.ItemName(rom, Low)}",
        SpotKind.EmptyChest => "Chest: (empty)",
        SpotKind.Gold       => $"Chest: {Value} GP",
        SpotKind.Hidden     => $"Hidden item: {MapSetupScript.ItemName(rom, Low)}",
        SpotKind.Door       => $"Door (type {Low & 0x0F})",
        _                   => $"Trigger ({Low:X2})",
    };
}

/// <summary>
/// Each map's setup script, run by the interpreter at $01:98AF when the map loads. Its pointer is at
/// file 0x18000 + map*2 (offset relative to 0x18000). After a leading u16, opcodes are:
/// 00 end; 01-03, 05-0A, 0C, 0D: one operand byte (05-0A test story flags); 04 ww: conditional jump;
/// 0B: two bytes; 21+n bb ww: arrival n; 41+n: character n on; 81+n bb: section D record n;
/// C1+n ww ff: section E record n = value ww, flag ff.
/// </summary>
public static class MapSetupScript
{
    public const int PointerTable = 0x18000;

    public static int Offset(RomBuffer rom, int mapId) =>
        PointerTable + rom.ReadUInt16Le(PointerTable + mapId * 2);

    /// <summary>All section E assignments in the map's setup script, in script order.</summary>
    public static List<SpotContent> ReadSpots(RomBuffer rom, int mapId)
    {
        var list = new List<SpotContent>();
        int p = Offset(rom, mapId) + 2;
        bool conditional = false;
        for (int steps = 0; steps < 1000 && p < rom.Length - 4; steps++)
        {
            int op = rom.ReadByte(p);
            if (op == 0x00) break;
            if (op < 0x20)
            {
                if (op is 0x04 or (>= 0x05 and <= 0x0A)) conditional = true;
                p += op switch { 0x04 or 0x0B => 3, <= 0x0D => 2, _ => -1 };
                if (op > 0x0D) break;      // unknown opcode: stop rather than misread
            }
            else if (op < 0x40) p += 4;
            else if (op < 0x80) p += 1;
            else if (op < 0xC0) p += 2;
            else
            {
                if (op > 0xC0)
                    list.Add(new SpotContent
                    {
                        Spot = op - 0xC1, Low = rom.ReadByte(p + 1), High = rom.ReadByte(p + 2),
                        Flag = rom.ReadByte(p + 3), ScriptOffset = p, Conditional = conditional,
                    });
                p += 4;
            }
        }
        return list;
    }

    /// <summary>
    /// Step-on trigger areas (section D) switched on by the setup script ($01:9ABC): opcode 81+n bb enables
    /// area n, which then runs event (map event base + n + 1) when stepped on ($01:9494); bb is a parameter
    /// stored with it (FF = special case).
    /// </summary>
    public static List<(int Area, int Event, bool Conditional, int ScriptOffset)> ReadTriggers(RomBuffer rom, int mapId)
    {
        var list = new List<(int, int, bool, int)>();
        int start = Offset(rom, mapId);
        int eventBase = LufiaMap.EventBase(rom, mapId);
        int p = start + 2;
        bool conditional = false;
        for (int steps = 0; steps < 1000 && p < rom.Length - 4; steps++)
        {
            int op = rom.ReadByte(p);
            if (op == 0x00) break;
            if (op < 0x20)
            {
                if (op is 0x04 or (>= 0x05 and <= 0x0A)) conditional = true;
                if (op > 0x0D) break;
                p += op is 0x04 or 0x0B ? 3 : 2;
            }
            else if (op < 0x40) p += 4;
            else if (op < 0x80) p += 1;
            else if (op < 0xC0)
            {
                int bb = rom.ReadByte(p + 1);
                // the area's byte 3 = n+1 is the event number (added to the map's event base, $01:94DE);
                // byte 1 = lead + bb is a separate parameter
                if (op > 0x80 && bb != 0xFF) list.Add((op - 0x81, (eventBase + op - 0x81 + 1) & 0xFF, conditional, p));
                p += 2;
            }
            else p += 4;
        }
        return list;
    }

    public static string ItemName(RomBuffer rom, int id)
    {
        int table = GameDataOffsets.ItemPointerTable;
        int o = table + rom.ReadUInt16Le(table + id * 2);
        var chars = rom.ReadBytes(o, GameDataOffsets.ItemNameLen).Select(b => b == (byte)'@' ? ' ' : (char)b);
        string name = new string(chars.ToArray()).TrimEnd(' ', '\0');
        return name.Length == 0 ? $"item {id:X2}" : name;
    }
}

/// <summary>Which exits (on any map, including the world map) lead to each arrival point.</summary>
public sealed class MapLinks
{
    public readonly record struct Source(int MapId, int Exit);

    private readonly Dictionary<(int map, int arrival), List<Source>> _into = new();

    public static MapLinks Build(RomBuffer rom, IEnumerable<int> mapIds)
    {
        var links = new MapLinks();
        foreach (int id in mapIds)
        {
            List<MapExit> exits;
            try { exits = LufiaMap.ReadExits(rom, id); }
            catch { continue; }
            foreach (var e in exits.Where(e => !e.IsUnused))
            {
                var key = (e.DestMap == 0 ? id : e.DestMap, e.Arrival);
                if (!links._into.TryGetValue(key, out var l)) links._into[key] = l = new();
                l.Add(new Source(id, e.Index));
            }
        }
        return links;
    }

    public IReadOnlyList<Source> Into(int mapId, int arrival) =>
        _into.TryGetValue((mapId, arrival), out var l) ? l : Array.Empty<Source>();
}
