namespace LufiaForge.Core.Maps;

// "test start" for test runs: power-on skips the opening and goes straight to the file select (Continue / Initial),
// same as pressing a button during the opening.
// - the opening ($01:8501, called while the opening plays) waits for A/B/Start. when one is pressed it sets the
//   message speed and music ($0DA0/$0DA1), runs the file select (New Game and the Hero's name, or Continue) and
//   starts the game. the patch skips the wait (BRA at $01:8508), so all of that runs at power-on: nothing the game
//   sets up gets missed and saves load like normal
// - after the file select the opening (map 4F event 08) would still play its narration, so right after its first
//   wait ("TAITO PRESENTS", 6C 04 85 50 14, where the file select runs) we write a goto to its end, the warp to the
//   Fortress of Doom and the chain to event 02, the prologue's start
// - Mode.Fortress: just that, a new game starts the prologue at the Fortress of Doom
// - Mode.SkipPrologue: the prologue's first event (map 93 event 02) does what its last event (map 97 event 10)
//   does: the four prologue heroes leave (1B 04-07), warp to map 02 and chain to its event 01 (the "90 years had
//   passed" text), then the Hero's game starts in Alekia like normal
// - Mode.ChosenMap: the prologue's first event sets the party up, then (before the Fortress's conversation) warps
//   to the chosen map and arrival point (4C mm ee), so the prologue party on any map
// what we changed is kept in a small block ("LFST") so Off can put it back. (older test starts kept the opening's
// first bytes and the demo flag at $01:802D instead, Off puts those back too)
public static class TestStartPatch
{
    public enum Mode { Off = 0, Fortress = 1, SkipPrologue = 2, ChosenMap = 3 }
    private const int DemoFlag = 0x802D;   // LDA #$FF / STA $0D9E at $01:802C (older test starts set it to 0)
    private const int WaitButton = 0x8508; // LDA $30 (the opening's button check in $01:8501)
    private static readonly byte[] WaitOriginal = { 0xA5, 0x30 }, WaitSkipped = { 0x80, 0x10 };   // BRA $851A
    private const int OpeningMap = 0x4F, OpeningEvent = 0x08, PrologueMap = 0x93, PrologueEvent = 0x02;
    private const int Saved = 16;
    private static readonly byte[] Magic = "LFST"u8.ToArray();
    private static readonly byte[] PrologueEnd = { 0x5B, 0x93, 0x01, 0x51, 0x93, 0x02 };
    private static readonly byte[] FirstWait = { 0x6C, 0x04, 0x85, 0x50, 0x14 };
    private static readonly byte[] SkipScript = Convert.FromHexString("5B02011B041B051B061B07510201");
    // block: magic(4) mode(1) map(1) entrance(1) flags(1: 1 = map 93 changed, 2 = goto after the first wait)
    //        opening bytes(16: at the goto; older test starts: at the event's start) 93's event table entry(5)
    private const int BlockSize = 4 + 4 + Saved + 5;

    private static int Block(RomBuffer rom)
    {
        if (rom.Length < MapWriter.ExpandedSize) return 0;
        var entry = ExpansionSpace.Load(rom).Entries.FirstOrDefault(x => x.Kind == ExpansionSpace.Kind.Patch && x.Id == 21);
        return entry != null && rom.ReadBytes(entry.Start, 4).SequenceEqual(Magic) ? entry.Start : 0;
    }

    public static Mode Current(RomBuffer rom) { int block = Block(rom); return block > 0 ? (Mode)rom.ReadByte(block + 4) : Mode.Off; }
    public static (int Map, int Entrance) Target(RomBuffer rom) { int block = Block(rom); return block > 0 ? (rom.ReadByte(block + 5), rom.ReadByte(block + 6)) : (0, 0); }
    public static bool IsApplied(RomBuffer rom) => Current(rom) != Mode.Off;
    public static bool IsOriginal(RomBuffer rom) => rom.ReadByte(DemoFlag) == 0xFF && rom.ReadBytes(WaitButton, 2).SequenceEqual(WaitOriginal);
    // a test start made before we kept the file select (the opening got jumped over and saves couldn't load)
    public static bool IsOldStyle(RomBuffer rom) => IsApplied(rom) && rom.ReadByte(DemoFlag) == 0x00;

    private static int OpeningStart(RomBuffer rom) => LufiaMap.EventScriptOffset(rom, OpeningMap, OpeningEvent);

    // sets the test start (Off puts the game's start back)
    public static void Set(RomBuffer rom, Mode mode, int map = 0, int entrance = 1)
    {
        Restore(rom);
        if (mode == Mode.Off) { rom.FixChecksum(); return; }
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's start isn't the original, so the test start can't be added safely.");
        int openingStart = OpeningStart(rom);
        var opening = rom.ReadBytes(openingStart, 0x400);
        int tail = IndexOf(opening, PrologueEnd), wait = IndexOf(opening, FirstWait);
        if (tail < 0 || wait < 0 || wait > tail) throw new InvalidOperationException("The opening's first wait or its end (warp to the Fortress of Doom) wasn't found.");
        int gotoAt = wait + FirstWait.Length;
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        int blockOffset = freeSpace.Allocate(BlockSize, ExpansionSpace.Kind.Patch, 21);
        freeSpace.Save();
        var block = new byte[BlockSize];
        Magic.CopyTo(block, 0);
        block[4] = (byte)mode; block[5] = (byte)map; block[6] = (byte)entrance; block[7] = 2;
        Array.Copy(opening, gotoAt, block, 8, Saved);
        rom.ReadBytes(LufiaMap.MapScriptTable + PrologueMap * 5, 5).CopyTo(block, 8 + Saved);
        rom.WriteBytes(blockOffset, block);
        // if map 93's events are already in the added space (edited in this project), the changes below go into a
        // copy of its block, so the block the saved entry points at stays as it is (before, saving moved it and freed
        // the old one, and turning the test start off then pointed map 93 at freed bytes)
        if (mode is Mode.SkipPrologue or Mode.ChosenMap)
        {
            int prologueBlock = EventScript.BlockOffset(rom, PrologueMap);
            if (ExpansionSpace.Load(rom).Entries.Any(e => prologueBlock >= e.Start && prologueBlock < e.End)) EventScript.CopyBlockFor(rom, PrologueMap);
        }
        rom.WriteBytes(WaitButton, WaitSkipped);
        rom.WriteBytes(openingStart + gotoAt, new byte[] { 0x01, (byte)tail, (byte)(tail >> 8) });
        if (mode is Mode.SkipPrologue or Mode.ChosenMap)
        {
            var script = EventScript.Load(rom, PrologueMap, PrologueEvent) ?? throw new InvalidOperationException("The prologue's first event wasn't found.");
            if (mode == Mode.SkipPrologue)
            {
                script.Ops.Clear();
                for (int i = 0; i < SkipScript.Length;)
                {
                    int length = SkipScript[i] is 0x5B or 0x51 ? 3 : 2;
                    script.Ops.Add(new ScriptOp { Bytes = SkipScript[i..(i + length)] });
                    i += length;
                }
                script.Ops.Add(new ScriptOp { Bytes = new byte[] { 0x00 } });
            }
            else
            {
                // the prologue's first event runs an event of the chosen map, and that one does the warp: the map loads
                // while its own event runs, like the game's own story warps, so its step-on areas run its events (a warp
                // from the Fortress's event made them run the Fortress's events with the same numbers)
                int arrive = MapEvents.FreeChainedNumber(rom, map);
                MapEvents.Create(rom, map, arrive);
                var warp = EventScript.Load(rom, map, arrive)!;
                warp.Ops.Insert(0, new ScriptOp { Bytes = new byte[] { 0x4C, (byte)map, (byte)Math.Max(1, entrance) } });
                warp.Save(true);
                script = EventScript.Load(rom, PrologueMap, PrologueEvent)!;
                // right after the party is set up (spells, items, gathering), before the first line of the Fortress's
                // conversation, which a test start skips. its "text advances by itself" (69) gets left out too, and the
                // screen fades in first (the Fortress starts dark and its fade-in comes after the first lines, without
                // it the party's sprites stay black on every map)
                int end = script.Ops.FindIndex(o => o.IsText);
                if (end > 0)
                {
                    while (end > 0 && !script.Ops[end - 1].IsText && script.Ops[end - 1].Bytes[0] == 0x69) end--;
                    script.Ops.Insert(end++, new ScriptOp { Bytes = new byte[] { 0x6A } });   // text waits for a button (the opening's auto text is still on, the prologue turns it off later)
                    script.Ops.Insert(end++, new ScriptOp { Bytes = new byte[] { 0x18, 0x02 } });
                    script.Ops.Insert(end++, new ScriptOp { Bytes = new byte[] { 0x5A } });
                }
                else end = script.Ops.FindLastIndex(o => !o.IsText && o.Bytes.Length == 1 && o.Bytes[0] == 0x00);
                if (end < 0) throw new InvalidOperationException("The prologue's first event has no end.");
                script.Ops.Insert(end, new ScriptOp { Bytes = new byte[] { 0x51, (byte)map, (byte)((arrive - LufiaMap.EventBase(rom, map)) & 0xFF) } });
                if (end < script.Ops.Count - 2)
                {
                    // the game can come back to this event after the chain (when a later event ends), and then it goes
                    // straight to its end instead of playing the Fortress's conversation wherever the party is
                    var toEnd = new ScriptOp { Bytes = new byte[] { 0x01, 0x00, 0x00 } };
                    toEnd.Targets.Add(new JumpRef { Op = script.Ops[^1] });
                    script.Ops.Insert(end + 1, toEnd);
                }
            }
            script.Save(true);
            rom.WriteByte(blockOffset + 7, 3);
        }
        rom.FixChecksum();
    }

    // puts the game's start back (the button wait, map 93's events, and for older test starts the opening's first
    // bytes and the demo flag)
    private static void Restore(RomBuffer rom)
    {
        int block = Block(rom);
        if (block == 0) return;
        int flags = rom.ReadByte(block + 7);
        if ((flags & 2) != 0)
        {
            var opening = rom.ReadBytes(OpeningStart(rom), 0x400);
            int wait = IndexOf(opening, FirstWait);
            if (wait >= 0) rom.WriteBytes(OpeningStart(rom) + wait + FirstWait.Length, rom.ReadBytes(block + 8, Saved));
        }
        else if (rom.ReadByte(DemoFlag) == 0x00)
        {
            rom.WriteBytes(OpeningStart(rom), rom.ReadBytes(block + 8, Saved));
            rom.WriteByte(DemoFlag, 0xFF);
        }
        if (rom.ReadBytes(WaitButton, 2).SequenceEqual(WaitSkipped)) rom.WriteBytes(WaitButton, WaitOriginal);
        if ((flags & 1) != 0) rom.WriteBytes(LufiaMap.MapScriptTable + PrologueMap * 5, rom.ReadBytes(block + 8 + Saved, 5));
        var freeSpace = ExpansionSpace.Load(rom);
        freeSpace.Free(block);
        freeSpace.Save();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++) if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
