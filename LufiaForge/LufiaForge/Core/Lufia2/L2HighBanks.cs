namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Data Lufia Forge put in banks $40-$7D of an 8 MB ROM (file 0x600000-) before 2026-11-14: old Snes9x versions (1.51,
/// 1.52 and others with its Map_JumboLoROMMap) show file 0x400000-0x5FFFFF there again, so the game read the copy of bank
/// $00 instead (a map whose info was there reset the game as the party walked in: Artemis, map 78). Everything
/// <see cref="L2Rom.Allocate"/> ever put there is found by what points at it and moved to a data bank of $01-$3F, which
/// every emulator reads: map info blocks, the new maps' table block (LF2MX), the moved encounter zone table, the map name
/// corner code and new sprites' tiles. The bytes left behind are not cleared.
/// </summary>
public static class L2HighBanks
{
    private static bool High(int at) => at >= L2Rom.UpperDataStart;

    /// <summary>What still reads from banks $40-$7D, one line each (empty when nothing does).</summary>
    public static List<string> Found(RomBuffer rom)
    {
        var list = new List<string>();
        if (!rom.IsLufia2 || !L2Rom.IsExLoRom(rom)) return list;
        if (High(L2NewMaps.Block(rom))) list.Add("the new maps' tables");
        var infos = Enumerable.Range(0, L2Maps.Count(rom)).Where(m => High(L2MapInfo.Block(rom, m))).ToList();
        if (infos.Count > 0) list.Add(MapList(infos, "map info of "));
        if (High(L2Encounters.ZoneBlock(rom))) list.Add("the encounter zone table");
        if (High(L2MapNames.CornerCode(rom).At)) list.Add("the map name box's corner code");
        int sprites = HighSprites(rom).Count;
        if (sprites > 0) list.Add($"the tiles of {sprites} new sprite{(sprites == 1 ? "" : "s")}");
        return list;
    }

    public static bool Needed(RomBuffer rom) => Found(rom).Count > 0;

    /// <summary>Moves all of it (one piece of work: all or nothing). Returns what was moved, one line each.</summary>
    public static List<string> Move(RomBuffer rom) => L2Rom.AllOrNothing(rom, () =>
    {
        var done = Found(rom);
        if (done.Count == 0) return done;
        int Room(int size) => L2Rom.Allocate(rom, size, upper: true);
        // the table block first: the maps' info pointers live in it
        if (High(L2NewMaps.Block(rom))) L2NewMaps.MoveBlock(rom, Room(L2NewMaps.TableBlockSize));
        var moved = new Dictionary<int, int>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int b = L2MapInfo.Block(rom, m);
            if (!High(b)) continue;
            if (!moved.TryGetValue(b, out int to))
            {
                int size = rom.ReadUInt16Le(b);
                to = Room(size + 1);
                rom.WriteBytes(to, rom.ReadBytes(b, size));
                rom.WriteByte(to + size, 0);
                moved[b] = to;
            }
            int snes = L2Rom.FileToSnes(to);
            rom.WriteBytes(L2MapInfo.PointerAt(rom, m), new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        }
        if (High(L2Encounters.ZoneBlock(rom))) L2Encounters.MoveZoneBlock(rom, Room(L2Encounters.ZoneBlockSize));
        var (corner, cornerSize) = L2MapNames.CornerCode(rom);
        if (High(corner)) L2MapNames.MoveCornerCode(rom, Room(cornerSize));
        var tiles = new Dictionary<int, int>();
        foreach (var (entry, at, bytes) in HighSprites(rom))
        {
            if (!tiles.TryGetValue(at, out int to))
            {
                int size = HighSprites(rom).Where(s => s.At == at).Max(s => s.Bytes);
                to = Room(size);
                rom.WriteBytes(to, rom.ReadBytes(at, size));
                tiles[at] = to;
            }
            int snes = L2Rom.FileToSnes(to);
            rom.WriteBytes(entry + 2, new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        }
        if (Found(rom) is { Count: > 0 } left) throw new InvalidOperationException("Still read from banks $40-$7D after the move: " + string.Join(", ", left));
        L2Rom.ForgetUpperRun(rom);
        rom.FixChecksum();
        return done;
    });

    /// <summary>Sprites whose tiles are in banks $40-$7D: the entry's file offset, the tiles' and their size.</summary>
    private static List<(int Entry, int At, int Bytes)> HighSprites(RomBuffer rom)
    {
        var list = new List<(int, int, int)>();
        for (int s = 0; s < L2Sprites.Count(rom); s++)
        {
            int e;
            try { e = L2Sprites.EntryAt(rom, s); } catch (Exception) { continue; }
            if (e < 0 || e + 5 > rom.Length) continue;
            int at = L2Rom.SnesToFile((int)rom.ReadUInt24Le(e + 2));
            if (High(at) && at < rom.Length) list.Add((e, at, L2Sprites.TileCount(rom.ReadByte(e)) * 32));
        }
        return list;
    }

    private static string MapList(List<int> maps, string what) =>
        what + (maps.Count > 8 ? $"{maps.Count} maps" : (maps.Count > 1 ? "maps " : "map ") + string.Join(", ", maps.Select(m => m.ToString("X2"))));
}
