namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II: "Start from a clean slate" (Edit menu). Every map keeps what it is built of (tiles, collision, exits,
/// doors, secret passages, preset points, puzzles) and loses its people, its events and cutscenes, and its treasure;
/// a new game starts on a chosen map at one of its preset points.
/// <list type="bullet">
/// <item>People: the map's start-up script (map header's second u24, from $87:8000) creates them with 68 / 7B id
/// sprite. Each of those becomes 1C, a jump to the next command (start-up jumps count from $87:8000), so the rest of
/// the script (map mode 69, music 4B and its story conditions, battle background 74) runs as before.</item>
/// <item>Events: the map's event block ("PH") lists +2 (run as the map loads; entries with id 03 are the key item of
/// its locked doors and stay), +4 (scenes), +6 (talking), +8 (stepping on a trigger), +A and +C are emptied (FF).</item>
/// <item>Treasure, cutscene triggers and the characters' places: the map info block's lists +0x26 (chests), +0x0C
/// (cutscene triggers) and +0x10 (character starts: a character listed there still shows up without a create) are
/// emptied.</item>
/// <item>New game: the code at $83:ACCD loads map 02 and runs its scene 01 at preset point 06 (LDA #$02 / STA $05AC /
/// STA $05AE ... LDA #$01 / STA $05B8 / LDA #$06 / STA $05B0); the map and the preset point are changed there, and
/// the start map gets a scene 01 (show the leader, fade in) where its old scripts were (a scene that isn't there
/// would leave the script pointer at $FFFF, $80:C18F).</item>
/// </list>
/// </summary>
public static class L2StoryClear
{
    public const int NewGameMapAt = 0x1ACCE, NewGameSceneAt = 0x1ACD9, NewGamePresetAt = 0x1ACDE;

    public sealed record Result(int Maps, int People, int Events, int Treasure);

    /// <summary>A map's preset points (map info list +0x0E: id, x, y), where a new game or a scene can put the party.</summary>
    public static List<(int Id, int X, int Y)> PresetPoints(RomBuffer rom, int map)
    {
        var list = new List<(int, int, int)>();
        int b = L2MapInfo.Block(rom, map);
        if (b < 0 || b + 0x30 > rom.Length) return list;
        int size = rom.ReadUInt16Le(b);
        for (int p = b + rom.ReadUInt16Le(b + 0x0E), guard = 0; guard < 64 && p > b && p + 3 <= b + size && rom.ReadByte(p) != 0xFF; guard++, p += 3)
            list.Add((rom.ReadByte(p), rom.ReadByte(p + 1), rom.ReadByte(p + 2)));
        return list;
    }

    /// <summary>The arrival scene a new game runs on the start map: show Maxim (2D 00), fade in (59 18), wait for the
    /// fade (76), end.</summary>
    private static readonly byte[] StartScene = { 0x2D, 0x00, 0x59, 0x18, 0x76, 0x00 };

    public static bool CanStart(RomBuffer rom) =>
        rom.ReadByte(NewGameMapAt - 1) == 0xA9 && rom.ReadByte(NewGameSceneAt - 1) == 0xA9 && rom.ReadByte(NewGamePresetAt - 1) == 0xA9;

    public static Result Clear(RomBuffer rom, int startMap, int startPreset)
    {
        if (!CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
        if (!PresetPoints(rom, startMap).Any(p => p.Id == startPreset)) throw new InvalidOperationException($"Map {startMap:X2} has no preset point {startPreset:X2}.");
        // the start map's arrival scene goes where its old scripts were: the free run after an old script's start
        int startBlock = L2Events.Block(rom, startMap);
        if (startBlock < 0) throw new InvalidOperationException($"Map {startMap:X2} has no event block for the start.");
        var offsets = L2Events.Entries(rom, startBlock).Select(e => e.Offset).Distinct().OrderBy(o => o).ToList();
        int end = L2Events.BlockSize(rom, startMap, startBlock);
        int sceneAt = -1;
        for (int i = 0; i < offsets.Count && sceneAt < 0; i++)
            if ((i + 1 < offsets.Count ? offsets[i + 1] : end) - offsets[i] >= StartScene.Length + 4) sceneAt = offsets[i];
        if (sceneAt < 0) throw new InvalidOperationException($"Map {startMap:X2} has no room for the start (no event scripts): pick another map.");
        int maps = 0, people = 0, events = 0, treasure = 0;
        var doneBlocks = new HashSet<int>();
        var doneInfo = new HashSet<int>();
        for (int m = 0; m < L2Maps.Count; m++)
        {
            bool any = false;
            // people: the start-up script's 68 / 7B become a jump to the next command
            foreach (var (title, ops) in L2Script.MapScripts(rom, m))
            {
                if (!title.StartsWith("Start-up")) continue;
                foreach (var o in ops)
                {
                    if (o.IsText || o.Length != 3 || o.At < 0x38000 || o.At + 3 > 0x40000) continue;
                    byte op = rom.ReadByte(o.At);
                    if (op is not (0x68 or 0x7B)) continue;
                    int next = o.At + 3 - 0x38000;
                    rom.WriteBytes(o.At, new byte[] { 0x1C, (byte)next, (byte)(next >> 8) });
                    people++; any = true;
                }
            }
            // events: the PH lists (the +2 list keeps its key item entries, id 03)
            int block = L2Events.Block(rom, m);
            if (block >= 0 && doneBlocks.Add(block))
            {
                events += L2Events.Entries(rom, block).Count;
                foreach (int head in new[] { 2, 4, 6, 8, 0x0A, 0x0C })
                {
                    int at = block + rom.ReadUInt16Le(block + head);
                    if (at <= block || at >= rom.Length) continue;
                    var keep = new List<byte>();
                    for (int i = 0; i < 128 && rom.ReadByte(at + i * 3) != 0xFF; i++)
                        if (head == 2 && rom.ReadByte(at + i * 3) == 3) keep.AddRange(rom.ReadBytes(at + i * 3, 3));
                    keep.Add(0xFF);
                    rom.WriteBytes(at, keep.ToArray());
                }
                any = true;
            }
            // treasure and cutscene triggers: the map info lists
            int info = L2MapInfo.Block(rom, m);
            if (info >= 0 && info + 0x30 < rom.Length && doneInfo.Add(info))
            {
                treasure += L2MapInfo.Read(rom, m).Count(e => e.Kind == "Treasure");
                foreach (int head in new[] { 0x26, 0x0C, 0x10 })
                {
                    int at = info + rom.ReadUInt16Le(info + head);
                    if (at > info && at < info + rom.ReadUInt16Le(info)) rom.WriteByte(at, 0xFF);
                }
                any = true;
            }
            if (any) maps++;
        }
        // the start map's scene list: scene 01, the arrival (show the party leader, fade in)
        int script = sceneAt + 4;
        rom.WriteBytes(startBlock + sceneAt, new byte[] { 0x01, (byte)script, (byte)(script >> 8), 0xFF });
        rom.WriteBytes(startBlock + script, StartScene);
        rom.WriteUInt16Le(startBlock + 4, (ushort)sceneAt);
        // the new game
        rom.WriteByte(NewGameMapAt, (byte)startMap);
        rom.WriteByte(NewGameSceneAt, 0x01);
        rom.WriteByte(NewGamePresetAt, (byte)startPreset);
        return new Result(maps, people, events, treasure);
    }
}
