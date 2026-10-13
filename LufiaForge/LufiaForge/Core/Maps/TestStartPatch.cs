namespace LufiaForge.Core.Maps;

/// <summary>
/// "Test start" for test runs: power-on skips the opening (and the title screen) and goes straight to a test start.
/// <list type="bullet">
/// <item>Power-on ($01:802C) runs the opening as a demo ($0D9E = FF); the patch makes it the real game ($0D9E = 0).</item>
/// <item>The opening is map 4F event 08 (the narration); it ends with a warp to map 93 and a chain to event 02, the
/// prologue's start. Its first command is overwritten:
/// <list type="bullet">
/// <item><see cref="Mode.Fortress"/>: goto that end: the prologue starts at the Fortress of Doom with Maxim's party.</item>
/// <item><see cref="Mode.SkipPrologue"/>: what the prologue's last event (map 97 event 10) does: the four prologue
/// heroes leave (1B 04-07), warp to map 02 and chain to its event 01 (the "90 years had passed" text), then the Hero's
/// game starts in Alekia as usual.</item>
/// <item><see cref="Mode.ChosenMap"/>: as Fortress, and the prologue's first event (map 93 event 02, which sets the
/// party up) ends with a warp to the chosen map and arrival point (4C mm ee): the prologue party on any map.</item>
/// </list></item>
/// </list>
/// What was overwritten is kept in a small block ("LFST") so Off puts it back. Saves can't be loaded while it is on
/// (the title screen is skipped): it's for test ROMs.
/// </summary>
public static class TestStartPatch
{
    public enum Mode { Off = 0, Fortress = 1, SkipPrologue = 2, ChosenMap = 3 }
    private const int DemoFlag = 0x802D;   // LDA #$FF / STA $0D9E at $01:802C
    private const int OpeningMap = 0x4F, OpeningEvent = 0x08, PrologueMap = 0x93, PrologueEvent = 0x02;
    private const int Saved = 16;
    private static readonly byte[] Magic = "LFST"u8.ToArray();
    private static readonly byte[] PrologueEnd = { 0x5B, 0x93, 0x01, 0x51, 0x93, 0x02 };
    private static readonly byte[] SkipScript = Convert.FromHexString("5B02011B041B051B061B07510201");
    // block: magic(4) mode(1) map(1) entrance(1) 93-changed(1) opening bytes(16) 93's event table entry(5)
    private const int BlockSize = 4 + 4 + Saved + 5;

    private static int Block(RomBuffer rom)
    {
        if (rom.Length < MapWriter.ExpandedSize) return 0;
        var e = ExpansionSpace.Load(rom).Entries.FirstOrDefault(x => x.Kind == ExpansionSpace.Kind.Patch && x.Id == 21);
        return e != null && rom.ReadBytes(e.Start, 4).SequenceEqual(Magic) ? e.Start : 0;
    }

    public static Mode Current(RomBuffer rom) { int b = Block(rom); return b > 0 ? (Mode)rom.ReadByte(b + 4) : Mode.Off; }
    public static (int Map, int Entrance) Target(RomBuffer rom) { int b = Block(rom); return b > 0 ? (rom.ReadByte(b + 5), rom.ReadByte(b + 6)) : (0, 0); }
    public static bool IsApplied(RomBuffer rom) => Current(rom) != Mode.Off;
    public static bool IsOriginal(RomBuffer rom) => rom.ReadByte(DemoFlag) == 0xFF && OpeningStart(rom) > 0;

    private static int OpeningStart(RomBuffer rom) => LufiaMap.EventScriptOffset(rom, OpeningMap, OpeningEvent);

    /// <summary>Sets the test start (Off puts the game's start back).</summary>
    public static void Set(RomBuffer rom, Mode mode, int map = 0, int entrance = 1)
    {
        Restore(rom);
        if (mode == Mode.Off) { rom.FixChecksum(); return; }
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's start isn't the original, so the test start can't be added safely.");
        int ev = OpeningStart(rom);
        var opening = rom.ReadBytes(ev, 0x400);
        int tail = IndexOf(opening, PrologueEnd);
        if (tail < 0) throw new InvalidOperationException("The opening's end (warp to the Fortress of Doom) wasn't found.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int b = space.Allocate(BlockSize, ExpansionSpace.Kind.Patch, 21);
        space.Save();
        var block = new byte[BlockSize];
        Magic.CopyTo(block, 0);
        block[4] = (byte)mode; block[5] = (byte)map; block[6] = (byte)entrance;
        Array.Copy(opening, 0, block, 8, Saved);
        rom.ReadBytes(LufiaMap.MapScriptTable + PrologueMap * 5, 5).CopyTo(block, 8 + Saved);
        rom.WriteBytes(b, block);
        rom.WriteByte(DemoFlag, 0x00);
        // the opening's first command (goto from the event start)
        byte[] first = mode == Mode.SkipPrologue ? SkipScript : new byte[] { 0x01, (byte)tail, (byte)(tail >> 8) };
        rom.WriteBytes(ev, first);
        if (mode == Mode.ChosenMap)
        {
            var s = EventScript.Load(rom, PrologueMap, PrologueEvent) ?? throw new InvalidOperationException("The prologue's first event wasn't found.");
            int end = s.Ops.FindLastIndex(o => !o.IsText && o.Bytes.Length == 1 && o.Bytes[0] == 0x00);
            if (end < 0) throw new InvalidOperationException("The prologue's first event has no end.");
            s.Ops.Insert(end, new ScriptOp { Bytes = new byte[] { 0x4C, (byte)map, (byte)Math.Max(1, entrance) } });
            s.Save(true);
            rom.WriteByte(b + 7, 1);
        }
        rom.FixChecksum();
    }

    /// <summary>Puts the game's start back (the opening's first bytes, the demo flag and map 93's events).</summary>
    private static void Restore(RomBuffer rom)
    {
        int b = Block(rom);
        if (b == 0) return;
        rom.WriteBytes(OpeningStart(rom), rom.ReadBytes(b + 8, Saved));
        if (rom.ReadByte(b + 7) == 1) rom.WriteBytes(LufiaMap.MapScriptTable + PrologueMap * 5, rom.ReadBytes(b + 8 + Saved, 5));
        rom.WriteByte(DemoFlag, 0xFF);
        var space = ExpansionSpace.Load(rom);
        space.Free(b);
        space.Save();
    }

    private static int IndexOf(byte[] h, byte[] n)
    {
        for (int i = 0; i + n.Length <= h.Length; i++) if (h.AsSpan(i, n.Length).SequenceEqual(n)) return i;
        return -1;
    }
}
