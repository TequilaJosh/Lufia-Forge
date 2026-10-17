using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// A new cutscene command the game didn't have: <c>6F aa ee tt</c> shows emote bubble ee (<see cref="EmoteGraphics"/>)
/// over actor aa (1 = the leader, 2-5 followers, 6 the vehicle, 7 + n = map character n) for tt frames
/// (FF = until hidden, 00 = hide it now). The script carries on at once; a wait after it keeps the bubble on screen.
///
/// The bubble is four 8x8 sprites, drawn in the OAM buffer slots the field's sprite builder left free, right above the
/// actor's own sprite, so it follows the actor and the camera. Its tiles go to sprite tiles FC-FF (the row the game
/// keeps for shadows; FE/FF are empty, FC/FD hold the same small graphic in every map and are put back afterwards) and its colours to sprite palette 3
/// (field characters use 4-7; also saved and put back). One bubble is shown at a time: a new one replaces it. Hooks:
/// <code>
/// rename routine +10  JML Cmd              commands 00-7F other than 6E (X = command x 2) come here first
/// $01:A6D6  22 Hook1b                      a character's sprite is built: note where its OAM entry goes (was LDY $0C / LDA $02,X)
/// $01:A487  22 Hook1 EA EA                 the same for other actors (was LDA $0D,X / ASL / STA $4202)
/// $01:A3D3  22 Hook2                       all sprites are built: add the bubble, count its frames (was LDA #$FF / STA $3E)
/// $00:821D  22 Nmi EA                      vblank, before the OAM upload: copy tiles and colours (was REP #$20 / LDA #$0000)
/// </code>
/// Work RAM $7E:4F60-$4FFF (unused by the game in towns and cutscenes) holds the state and the saved colours.
/// Needs <see cref="RenamePatch"/> (it owns the event dispatcher hook), which is added with it.
/// </summary>
public static class EmotePatch
{
    public const int Op = 0x6F;
    public static int Count => EmoteGraphics.Names.Length;

    private const int Hook1Site = 0x0A487;   // $01:A487
    private const int Hook1bSite = 0x0A6D6;  // $01:A6D6
    private const int Hook2Site = 0x0A3D3;   // $01:A3D3
    private const int NmiSite = 0x0021D;     // $00:821D
    private static readonly byte[] Hook1Original = { 0xB5, 0x0D, 0x0A, 0x8D, 0x02, 0x42 };
    private static readonly byte[] Hook1bOriginal = { 0xA4, 0x0C, 0xB5, 0x02 };
    private static readonly byte[] Hook2Original = { 0xA9, 0xFF, 0x85, 0x3E };
    private static readonly byte[] NmiOriginal = { 0xC2, 0x20, 0xA9, 0x00, 0x00 };
    private static readonly byte[] CmdOriginal = { 0x5C, 0x7A, 0xC6, 0x01 };   // rename routine +10: JML $01C67A
    private const int CmdSiteInRename = 10;
    private static readonly byte[] Magic = "LFEM"u8.ToArray();
    private const byte Version = 1;

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static bool Matches(RomBuffer rom, int at, byte[] b) => at >= 0 && at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);

    public static bool IsOriginal(RomBuffer rom) =>
        Matches(rom, Hook1Site, Hook1Original) && Matches(rom, Hook1bSite, Hook1bOriginal) && Matches(rom, Hook2Site, Hook2Original) && Matches(rom, NmiSite, NmiOriginal);

    public static bool IsApplied(RomBuffer rom) => BlockOffset(rom) > 0;

    /// <summary>File offset of the patch's block (its magic), or 0.</summary>
    public static int BlockOffset(RomBuffer rom)
    {
        int r = RenamePatch.RoutineOffset(rom);
        if (r == 0 || !RenamePatch.IsApplied(rom) || rom.ReadByte(r + CmdSiteInRename) != 0x5C) return 0;
        int cmd = FileOffset(rom.ReadByte(r + CmdSiteInRename + 1) | rom.ReadByte(r + CmdSiteInRename + 2) << 8 | rom.ReadByte(r + CmdSiteInRename + 3) << 16);
        int block = cmd - CmdOffset;
        return block > 0 && Matches(rom, block, Magic) ? block : 0;
    }

    /// <summary>True when the bytes at <paramref name="p"/> are an emote command (used by the event decoder).</summary>
    public static bool IsCommandAt(RomBuffer rom, int p) =>
        p + 4 <= rom.Length && rom.ReadByte(p) == Op && rom.ReadByte(p + 1) is >= 1 and <= 0x3F && rom.ReadByte(p + 2) < Count && IsApplied(rom);

    /// <summary>Adds the command (a no-op when it's there; an older version is brought up to date).</summary>
    public static void Apply(RomBuffer rom)
    {
        int old = BlockOffset(rom);
        if (old > 0 && rom.ReadByte(old + 4) == Version) return;
        if (old == 0 && !IsOriginal(rom))
            throw new InvalidOperationException("The game's field sprite code ($01:A3D3, $01:A487, $00:821D) isn't the original, so the emote command can't be added safely.");
        RenamePatch.Apply(rom);
        int rename = RenamePatch.RoutineOffset(rom);
        if (old == 0 && !Matches(rom, rename + CmdSiteInRename, CmdOriginal))
            throw new InvalidOperationException("The event dispatcher hook isn't the one Lufia Forge expects, so the emote command can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        if (old > 0) space.Free(old);
        var probe = Build(0x808000);
        int at = space.AllocateInOneBank(probe.Length, ExpansionSpace.Kind.Patch, 31);
        space.Save();
        int snes = SnesAddress(at);
        rom.WriteBytes(at, Build(snes));
        byte[] To(byte op, int offset) { int a = snes + offset; return new[] { op, (byte)a, (byte)(a >> 8), (byte)(a >> 16) }; }
        rom.WriteBytes(rename + CmdSiteInRename, To(0x5C, CmdOffset));
        rom.WriteBytes(Hook1Site, To(0x22, Hook1Offset)); rom.WriteBytes(Hook1Site + 4, new byte[] { 0xEA, 0xEA });
        rom.WriteBytes(Hook1bSite, To(0x22, Hook1bOffset));
        rom.WriteBytes(Hook2Site, To(0x22, Hook2Offset));
        rom.WriteBytes(NmiSite, To(0x22, NmiOffset)); rom.WriteByte(NmiSite + 4, 0xEA);
        rom.FixChecksum();
    }

    /// <summary>Events that show emotes, found while the patch is in.</summary>
    public static List<(int Map, int Event)> UsedBy(RomBuffer rom)
    {
        var list = new List<(int, int)>();
        if (!IsApplied(rom)) return list;
        foreach (var m in MapCatalog.Scan(rom))
            for (int ev = 0, n = EventScript.SlotCount(rom, m.MapId); ev < n; ev++)
            {
                try
                {
                    var s = EventScript.Load(rom, m.MapId, ev);
                    if (s != null && s.Ops.Any(o => !o.IsText && o.Bytes.Length > 0 && o.Bytes[0] == Op)) list.Add((m.MapId, ev));
                }
                catch { }
            }
        return list;
    }

    public static void Remove(RomBuffer rom)
    {
        int block = BlockOffset(rom);
        if (block == 0) return;
        var used = UsedBy(rom);
        if (used.Count > 0)
            throw new InvalidOperationException("Events still show emotes (" +
                string.Join(", ", used.Take(6).Select(u => $"map {u.Map:X2} event {u.Event}")) + (used.Count > 6 ? ", ..." : "") +
                "). Take the emote lines out of those events first; removing the patch now would make them crash the game.");
        rom.WriteBytes(RenamePatch.RoutineOffset(rom) + CmdSiteInRename, CmdOriginal);
        rom.WriteBytes(Hook1Site, Hook1Original);
        rom.WriteBytes(Hook1bSite, Hook1bOriginal);
        rom.WriteBytes(Hook2Site, Hook2Original);
        rom.WriteBytes(NmiSite, NmiOriginal);
        var space = ExpansionSpace.Load(rom);
        space.Free(block);
        space.Save();
        rom.FixChecksum();
    }

    private static readonly int CmdOffset, Hook1Offset, Hook1bOffset, Hook2Offset, NmiOffset;

    static EmotePatch()
    {
        var a = Assemble(0x808000);
        CmdOffset = a.Offset("Cmd"); Hook1Offset = a.Offset("Hook1"); Hook1bOffset = a.Offset("Hook1b"); Hook2Offset = a.Offset("Hook2"); NmiOffset = a.Offset("Nmi");
    }

    private static byte[] Build(int snes) => Assemble(snes).Build(snes);

    // work RAM (bank 7E)
    private const int MagicW = 0x4F60, Actor = 0x4F62, OamPtr = 0x4F64, Timer = 0x4F66, Saved = 0x4F68, SaveReq = 0x4F6A,
        UpReq = 0x4F6C, RestReq = 0x4F6E, Guard = 0x4F70, Icon = 0x4F72, Ex = 0x4F74, Ey = 0x4F76, Tile = 0x4F78, Tmp = 0x4F7A,
        By = 0x4F7C, Bx = 0x4F7E, Slot = 0x4F80, ActE = 0x4F82, CgSave = 0x4FE0;
    private const int ShownMark = 0x4D45, GuardMark = 0x464C;   // "EM", "LF"
    private const int TilesVram = 0x4FC0;                       // sprite tile FC (sprites at word $4000)
    private const byte PalCg = 0xB0;                            // sprite palette 3
    private const byte Attr = 0x36;                             // palette 3, in front of everything
    /// <summary>
    /// What sprite tiles FC/FD hold in every field map (the game loads them with the shadows); put back from here,
    /// as reading VRAM back doesn't work on every emulator.
    /// </summary>
    private const string TilesFcFd = "CC33EE11AA558877DD22FF000000000033001100550077002200000000000000" +
                                     "AC10AC10F800C830C83078000000000010001000000030003000000000000000";

    private static byte Lo(int v) => (byte)v;
    private static byte Hi(int v) => (byte)(v >> 8);

    private static Asm65816 Assemble(int snes)
    {
        var a = new Asm65816();
        a.B(Magic).B(Version);
        // A16 helpers
        void Lda(int addr) => a.B(0xAD, Lo(addr), Hi(addr));
        void Sta(int addr) => a.B(0x8D, Lo(addr), Hi(addr));
        void Stz(int addr) => a.B(0x9C, Lo(addr), Hi(addr));
        void LdaI(int v) => a.B(0xA9, Lo(v), Hi(v));
        void CmpI(int v) => a.B(0xC9, Lo(v), Hi(v));
        void AndI(int v) => a.B(0x29, Lo(v), Hi(v));
        void AdcI(int v) => a.B(0x18, 0x69, Lo(v), Hi(v));
        void LdaL(int addr) => a.Long(0xAF, 0x7E0000 | addr);
        void StaL(int addr) => a.Long(0x8F, 0x7E0000 | addr);
        void Rep() => a.B(0xC2, 0x20);
        void Sep() => a.B(0xE2, 0x20);
        void Dma(int dmap, int srcLow, byte srcBank, int size)   // M=8 on entry and exit; channel 7
        {
            Rep(); LdaI(dmap); a.B(0x8D, 0x70, 0x43); LdaI(srcLow); a.B(0x8D, 0x72, 0x43); LdaI(size); a.B(0x8D, 0x75, 0x43);
            Sep(); a.B(0xA9, srcBank, 0x8D, 0x74, 0x43, 0xA9, 0x80, 0x8D, 0x0B, 0x42);
        }
        void VramAddr() { Rep(); LdaI(TilesVram); a.B(0x8D, 0x16, 0x21); Sep(); }
        void CgAddr() => a.B(0xA9, PalCg, 0x8D, 0x21, 0x21);

        // ── the event dispatcher (M = 8, X = 16, X = command x 2, DB = the script's bank, Y = the script pointer) ──
        a.Label("Cmd").B(0xE0, Op * 2, 0x00).Br(0xF0, "emote").B(CmdOriginal);
        a.Label("emote");
        for (int i = 0; i < 3; i++) a.Abs(0x20, "rd").B(0x48);                  // actor, emote, frames on the stack
        a.B(0x5A, 0x8B, 0xA9, 0x7E, 0x48, 0xAB);                                 // PHY PHB, DB = 7E
        Rep();                                                                    // 1,S = B  2-3 = Y  4 = frames  5 = emote  6 = actor
        a.B(0xA3, 0x04); AndI(0xFF); a.Br(0xD0, "show");
        a.Abs(0x20, "Hide").Br(0x80, "cdone");
        a.Label("show"); CmpI(0xFF); a.Br(0xD0, "t1"); LdaI(0xFFFF);
        a.Label("t1"); Sta(Timer);
        a.B(0xA3, 0x05); AndI(0xFF); CmpI(Count); a.Br(0x90, "i1"); LdaI(0);
        a.Label("i1"); Sta(Icon);
        a.B(0xA3, 0x06); AndI(0xFF); a.B(0x0A, 0x0A, 0x0A, 0x0A, 0x0A); AdcI(0x07DE); Sta(Actor);   // actor record $07DE + 32 x id
        Stz(OamPtr); Stz(RestReq);
        Lda(Saved); a.Br(0xF0, "fresh");
        LdaI(1); Sta(UpReq); a.Br(0x80, "shown");
        a.Label("fresh"); LdaI(1); Sta(Saved); Sta(SaveReq); Sta(UpReq);
        a.Label("shown"); LdaI(GuardMark); Sta(Guard); LdaI(ShownMark); Sta(MagicW);
        a.Label("cdone"); Sep(); a.B(0xAB, 0x7A, 0x68, 0x68, 0x68);              // PLB PLY, drop the operands
        a.Long(0x5C, 0x01C66E);                                                   // JML: next command

        // reads the next script byte (as the engine's own reader $01:C740: next bank at $10000)
        a.Label("rd").B(0xB9, 0x00, 0x00, 0xC8).Br(0xD0, "rd_ok")
         .B(0x48, 0x8B, 0x68, 0x1A, 0x48, 0xAB, 0x68, 0xA0, 0x00, 0x80)
         .Label("rd_ok").B(0x60);

        // hides the bubble (DB = 7E, A = 16-bit): its tiles go back at the next vblank
        a.Label("Hide"); Stz(MagicW); Stz(UpReq);
        Lda(Saved); a.Br(0xF0, "h_done");
        Lda(SaveReq); a.Br(0xF0, "h_rest");
        Stz(SaveReq); Stz(Saved); a.B(0x60);                                      // nothing was copied yet
        a.Label("h_rest"); LdaI(1); Sta(RestReq);
        a.Label("h_done").B(0x60);

        // ── an actor's sprite is about to be built (M = 8, X = 16 = the actor's record, DB = 01) ──
        a.Label("Hook1").Abs(0x20, "Note").B(Hook1Original[0], Hook1Original[1], Hook1Original[2]).Long(0x8F, 0x004202).B(0x6B);
        a.Label("Hook1b").Abs(0x20, "Note").B(Hook1bOriginal).B(0x6B);
        // if X is the bubble's actor, keep where its first OAM entry goes ($0C)
        a.Label("Note").B(0x08); Rep(); a.B(0x48, 0x8A); a.Long(0xCF, 0x7E0000 | Actor); a.Br(0xD0, "nt_x");
        a.B(0xA5, 0x0C); StaL(OamPtr);
        a.Label("nt_x").B(0x68, 0x28, 0x60);

        // ── all sprites are built (M = 8, X = 16, DB = 01); $0C = the next free OAM entry ──
        a.Label("Hook2").B(0x08, 0x8B, 0xC2, 0x30, 0xDA, 0x5A, 0xF4, 0x7E, 0x7E, 0xAB, 0xAB);
        Lda(MagicW); CmpI(ShownMark); a.Br(0xF0, "h2on").Abs(0x4C, "h2done");
        a.Label("h2on"); Lda(Guard); CmpI(GuardMark); a.Br(0xF0, "h2g").Abs(0x4C, "h2done");
        a.Label("h2g"); Lda(OamPtr); a.Br(0xD0, "h2a").Abs(0x4C, "tick");
        a.Label("h2a"); Sta(ActE); Stz(OamPtr);
        a.B(0xA5, 0x0C); CmpI(0x0206); a.Br(0x90, "h2b").Abs(0x4C, "tick");
        a.Label("h2b"); Sta(Slot);
        a.B(0xAE, Lo(ActE), Hi(ActE)).B(0xBD, 0x01, 0x00); AndI(0xFF); CmpI(0xE0); a.Br(0x90, "h2c").Abs(0x4C, "tick");
        a.Label("h2c").B(0x38, 0xE9, 0x10, 0x00); AndI(0xFF); Sta(By);           // 16 pixels above the actor's top
        Lda(ActE); a.Abs(0x20, "GetX9").B(0xEB); Sta(Tmp);                        // x9 << 8
        a.B(0xAE, Lo(ActE), Hi(ActE)).B(0xBD, 0x00, 0x00); AndI(0xFF); a.B(0x0D, Lo(Tmp), Hi(Tmp)); Sta(Bx);
        void Piece(int dx, int dy, int tile, int slotAdd)
        {
            Lda(Bx); if (dx != 0) AdcI(dx); Sta(Ex);
            Lda(By); if (dy != 0) { AdcI(dy); AndI(0xFF); } Sta(Ey);
            LdaI(tile); Sta(Tile);
            Lda(Slot); if (slotAdd != 0) AdcI(slotAdd); a.Abs(0x20, "Put");
        }
        Piece(0, 0, 0xFC, 0); Piece(8, 0, 0xFD, 4); Piece(0, 8, 0xFE, 0x100); Piece(8, 8, 0xFF, 0x104);
        a.Label("tick"); Lda(Timer); CmpI(0xFFFF); a.Br(0xF0, "h2done").B(0x3A); Sta(Timer); a.Br(0xD0, "h2done");
        a.Abs(0x20, "Hide");
        a.Label("h2done").B(0xC2, 0x30, 0x7A, 0xFA, 0xAB, 0x28).B(Hook2Original).B(0x6B);

        // A = an OAM entry's address → A = its 9th x bit (A/X/Y = 16-bit; X, Y changed)
        a.Label("GetX9").B(0x38, 0xE9, 0x0A, 0x01, 0x4A, 0x4A, 0x48); AndI(3); a.B(0xAA, 0x68, 0x4A, 0x4A); AdcI(0x030A); a.B(0xA8);
        a.B(0xB9, 0x00, 0x00); AndI(0xFF);
        a.Label("gx_l").B(0xE0, 0x00, 0x00).Br(0xF0, "gx_d").B(0x4A, 0x4A, 0xCA).Br(0x80, "gx_l");
        a.Label("gx_d"); AndI(1); a.B(0x60);

        // writes OAM entry A (an address) from Ex (9-bit), Ey, Tile: an 8x8 sprite (A/X/Y = 16-bit; X, Y changed)
        a.Label("Put").B(0xA8); Sep();
        a.B(0xAD, Lo(Ex), Hi(Ex), 0x99, 0x00, 0x00).B(0xAD, Lo(Ey), Hi(Ey), 0x99, 0x01, 0x00)
         .B(0xAD, Lo(Tile), Hi(Tile), 0x99, 0x02, 0x00).B(0xA9, Attr, 0x99, 0x03, 0x00);
        Rep(); a.B(0x98, 0x38, 0xE9, 0x0A, 0x01, 0x4A, 0x4A, 0x48); AndI(3); a.B(0xAA, 0x68, 0x4A, 0x4A); AdcI(0x030A); a.B(0xA8);
        Sep(); a.B(0xAD, Lo(Ex + 1), Hi(Ex + 1), 0x29, 0x01, 0x8D, Lo(Tmp), Hi(Tmp)).B(0xA9, 0x03, 0x8D, Lo(Tmp + 1), Hi(Tmp + 1));
        a.Label("pt_l").B(0xE0, 0x00, 0x00).Br(0xF0, "pt_d")
         .B(0x0E, Lo(Tmp), Hi(Tmp), 0x0E, Lo(Tmp), Hi(Tmp), 0x0E, Lo(Tmp + 1), Hi(Tmp + 1), 0x0E, Lo(Tmp + 1), Hi(Tmp + 1), 0xCA).Br(0x80, "pt_l");
        a.Label("pt_d").B(0xAD, Lo(Tmp + 1), Hi(Tmp + 1), 0x49, 0xFF, 0x39, 0x00, 0x00, 0x0D, Lo(Tmp), Hi(Tmp), 0x99, 0x00, 0x00);
        Rep(); a.B(0x60);

        // ── vblank, before the OAM upload (M = 8, X = 16, DB = 00) ──
        a.Label("Nmi"); Rep(); LdaL(Guard); CmpI(GuardMark); a.Br(0xF0, "n_go").Abs(0x4C, "n_out");
        a.Label("n_go"); Sep(); a.B(0xA9, 0x80, 0x8D, 0x15, 0x21);
        // save tiles FC/FD and palette 3
        Rep(); LdaL(SaveReq); Sep(); a.Br(0xD0, "n_sv").Abs(0x4C, "n_up");
        a.Label("n_sv");
        CgAddr(); Dma(0x3B80, CgSave, 0x7E, 0x20);
        Rep(); LdaI(0); StaL(SaveReq); Sep();
        // copy the bubble in
        a.Label("n_up"); Rep(); LdaL(UpReq); Sep(); a.Br(0xD0, "n_u1").Abs(0x4C, "n_rs");
        a.Label("n_u1"); VramAddr();
        Rep(); LdaI(0x1801); a.B(0x8D, 0x70, 0x43); LdaL(Icon); a.B(0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A);
        a.B(0x18, 0x69).Word("Gfx"); a.B(0x8D, 0x72, 0x43); LdaI(0x80); a.B(0x8D, 0x75, 0x43);
        Sep(); a.B(0xA9, (byte)(snes >> 16), 0x8D, 0x74, 0x43, 0xA9, 0x80, 0x8D, 0x0B, 0x42);
        CgAddr();
        Rep(); LdaI(0x2200); a.B(0x8D, 0x70, 0x43); a.B(0xA9).Word("Pal"); a.B(0x8D, 0x72, 0x43); LdaI(0x20); a.B(0x8D, 0x75, 0x43);
        Sep(); a.B(0xA9, (byte)(snes >> 16), 0x8D, 0x74, 0x43, 0xA9, 0x80, 0x8D, 0x0B, 0x42);
        Rep(); LdaI(0); StaL(UpReq); Sep();
        // put tiles FC-FF and the saved colours back
        a.Label("n_rs"); Rep(); LdaL(RestReq); Sep(); a.Br(0xD0, "n_r1").Abs(0x4C, "n_out");
        a.Label("n_r1"); VramAddr();
        Rep(); LdaI(0x1801); a.B(0x8D, 0x70, 0x43); a.B(0xA9).Word("Fcfd"); a.B(0x8D, 0x72, 0x43); LdaI(0x80); a.B(0x8D, 0x75, 0x43);
        Sep(); a.B(0xA9, (byte)(snes >> 16), 0x8D, 0x74, 0x43, 0xA9, 0x80, 0x8D, 0x0B, 0x42);
        CgAddr(); Dma(0x2200, CgSave, 0x7E, 0x20);
        Rep(); LdaI(0); StaL(RestReq); StaL(Saved);
        a.Label("n_out"); Rep(); LdaI(0); a.B(0x6B);

        a.Label("Fcfd").B(Convert.FromHexString(TilesFcFd)).B(new byte[64]);   // and FE/FF empty again
        a.Label("Pal"); foreach (var c in EmoteGraphics.Palette) a.B((byte)c, (byte)(c >> 8));
        a.Label("Gfx").B(EmoteGraphics.Tiles);
        return a;
    }
}
