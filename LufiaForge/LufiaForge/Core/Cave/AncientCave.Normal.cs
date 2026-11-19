using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    // the Ancient Cave in the normal game: the elevator man in the Old Cave (map 88) asks whether to go down to it.
    // a run starts when a cave floor loads from outside: the real party gets backed up (names, party, gold, items,
    // characters: $14B0-$14DA and $14F7-$16D7, the parts of a save that hold them) to save RAM $70:1C00, and the
    // party members get set up again at level 1, the same members in the same order, with nothing. leaving (the
    // "leave" choice at an arrival door, or winning on floor 100) loads map 88 again and the real party comes back
    // (stats worked out again like loading a save does). saving in the cave keeps the backup in the save slot too
    // (slot + $560, bytes the game doesn't use), and loading that save brings it back, so a run survives a save.
    //   $00:922B  22 SaveHook EA          saving: the backup into the slot, before its checksum (was LDA $16 / JSR $9419)
    //   $00:931D  22 LoadHook             loading: as before ($08:E884), then the slot's backup (was JSL $08E884)
    private const string NormalSource = """
        !BACKUP = $701C00           ; 0x2B bytes of $14B0-$14DA, then 0x1E1 of $14F7-$16D7
        !SLOTBACKUP = $0560         ; in a save slot
        !DPSAVE = $701E40           ; direct page $00-$FF while the party is changed (the map loader keeps its state there)

        EnterCave:                  ; M = 8 (from MapLoadHook): back up the real party, then the run's party
            PHP
            REP #$10
            PHX
            JSR SaveDP
            LDX.w #$0000
        -   LDA $7E14B0,x
            STA !BACKUP,x
            INX
            CPX.w #$002B
            BCC -
            LDX.w #$0000
        -   LDA $7E14F7,x
            STA !BACKUP+$2B,x
            INX
            CPX.w #$01E1
            BCC -
            LDX.w #$0000            ; the same members (character numbers, $14D6), in the same order
        -   LDA $7E14D2,x
            CMP.b #$FF
            BEQ +
            LDA $7E14D6,x
        +   STA $7FFF2C,x
            INX
            CPX.w #4
            BCC -
            JSL CaveParty
            REP #$10
            LDX.w #$0000            ; the walking order as before (each character keeps their slot)
        -   LDA !BACKUP+$22,x
            STA $7E14D2,x
            INX
            CPX.w #4
            BCC -
            JSR Restats
            REP #$10
            JSR RestoreDP
            PLX
            PLP
            RTS

        Restats:                    ; the party's stats worked out again, as loading a save does ($00:9313)
            PHB
            SEP #$30
            LDA.b #$00
            PHA
            PLB
            LDX.b #$03
        -   JSL $08F857
            DEX
            BPL -
            JSL $08E884
            PLB
            RTS

        SaveDP:                     ; M = 8, X = 16
            LDX.w #$0000
        -   LDA.b $00,x
            STA !DPSAVE,x
            INX
            CPX.w #$0100
            BCC -
            RTS

        RestoreDP:                  ; M = 8, X = 16
            LDX.w #$0000
        -   LDA !DPSAVE,x
            STA.b $00,x
            INX
            CPX.w #$0100
            BCC -
            RTS

        LeaveCave:                  ; M = 8: the real party back
            PHP
            PHB
            REP #$10
            PHX
            PHY
            JSR SaveDP
            LDX.w #$0000
        -   LDA !BACKUP,x
            STA $7E14B0,x
            INX
            CPX.w #$002B
            BCC -
            LDX.w #$0000
        -   LDA !BACKUP+$2B,x
            STA $7E14F7,x
            INX
            CPX.w #$01E1
            BCC -
            JSR Restats
            REP #$10
            SEP #$20
            JSR RestoreDP
            REP #$30
            PLY
            PLX
            PLB
            PLP
            RTS

        SaveHook:                   ; $00:922B, M = 8, [$1F] = the save slot
            PLA                     ; drop the JSL's return address
            PLA
            PLA
            PHP
            REP #$30
            LDX.w #$0000
            LDY.w #!SLOTBACKUP
            SEP #$20
        -   LDA !BACKUP,x
            STA [$1F],y
            INX
            INY
            CPX.w #$020C
            BCC -
            PLP
            PEA $922F               ; JSR $9419 (the checksum) comes back to $00:9230 as before
            LDA $16
            JML $009419

        LoadHook:                   ; $00:931D, after a save was loaded
            JSL $08E884             ; as before
            PHP
            REP #$30
            PHX
            PHY
            LDX.w #$0000
            LDY.w #!SLOTBACKUP
            SEP #$20
        -   LDA [$1F],y
            STA !BACKUP,x
            INX
            INY
            CPX.w #$020C
            BCC -
            REP #$30
            PLY
            PLX
            PLP
            RTL
        """;

    private const int SaveSite = 0x0122B, LoadSite = 0x0131D;   // $00:922B, $00:931D
    private static readonly byte[] SaveOriginal = { 0xA5, 0x16, 0x20, 0x19, 0x94 };
    private static readonly byte[] LoadOriginal = { 0x22, 0x84, 0xE8, 0x08 };

    public const int ElevatorMap = 0x88, ElevatorEvent = 1, ReturnArrival = 1, StartArrival = 1;   // arrival 1: by the man and the elevator (2 is the hall below)
    private static readonly byte[] EntranceMark = { 0x03, 0x03 };

    // the elevator man's question at the start of his event (map 88 event 1): yes = down to the cave, no = his
    // own lines, or (locked, elevator start) nothing, since the elevator is closed
    private static void AddEntrance(RomBuffer rom, int caveMap, bool locked = false, int neverFlag = 0)
    {
        RemoveEntrance(rom);
        var script = EventScript.Load(rom, ElevatorMap, ElevatorEvent) ?? throw new InvalidOperationException("Map 88's event 1 (the Old Cave elevator man) wasn't found.");
        var first = script.Ops[0];
        var go = new ScriptOp { Bytes = new byte[] { 0x4C, (byte)caveMap, 0x01 } };
        var end = new ScriptOp { Bytes = new byte[] { 0x00 } };
        var no = locked ? end : first;
        var ask = new ScriptOp { Bytes = new byte[] { 0x03, 0x03, 0, 0, 0, 0, 0, 0 } };
        ask.Targets.Add(new JumpRef { Op = go }); ask.Targets.Add(new JumpRef { Op = no }); ask.Targets.Add(new JumpRef { Op = no });
        script.Ops.Insert(0, ask);
        script.NewText(0x0C, "Shall I take you down[NL]to the Ancient Cave?", 1);
        script.NewText(0x0C, " Yes.[NL] No.", 2);
        script.Ops.Insert(3, new ScriptOp { Bytes = new byte[] { 0x00 } });   // (the menu runs at this end, then goes to the answer's target)
        script.Ops.Insert(4, go);
        script.Ops.Insert(5, end);
        if (locked)
        {
            // never taken (a flag that never gets set here): keeps his own lines part of the event as far as Lufia
            // Forge can tell, so taking the question out again leaves the event as it was
            var keep = new ScriptOp { Bytes = new byte[] { 0x04, (byte)neverFlag, 0x00, 0x00 } };
            keep.Targets.Add(new JumpRef { Op = first });
            script.Ops.Insert(5, keep);
        }
        script.Save(true);
    }

    // elevator start

    private const int PrologueMap = 0x93, PrologueEvent = 2;

    // elevator start: the prologue's first event (map 93 event 2), once it has set the party up and before the
    // Fortress's conversation, turns the opening's self-advancing text off (6A), fades in (the Fortress starts dark),
    // warps to the elevator room by the man (4C 88 01) and goes on with the elevator man's event (51 88 ee: his
    // question, run in map 88's own script block, so the room's events are its own afterwards). see AddStart for what
    // keeps the Fortress's conversation from playing later
    private static void AddStart(RomBuffer rom, int neverFlag)
    {
        RemoveStart(rom);
        var script = EventScript.Load(rom, PrologueMap, PrologueEvent) ?? throw new InvalidOperationException("The prologue's first event (map 93 event 2) wasn't found.");
        int insertAt = script.Ops.FindIndex(o => o.IsText);
        if (insertAt < 0) throw new InvalidOperationException("The prologue's first event has no text to start the elevator room before.");
        while (insertAt > 0 && !script.Ops[insertAt - 1].IsText && script.Ops[insertAt - 1].Bytes[0] == 0x69) insertAt--;   // (its "text advances by itself" too)
        var conversation = script.Ops[insertAt];
        // the game can come back to this event after the chain (once a later event ends, like leaving the cave): it
        // lands on toEnd and the event ends, instead of the Fortress's conversation playing in the elevator room.
        // the two jumps on a flag that's never set at New Game (the floor 100 flag) never get taken. they keep toEnd
        // and the conversation part of the event as far as Lufia Forge can tell (a chain ends an event as far as it
        // can see), so taking the start out again leaves the event as it was
        var toEnd = new ScriptOp { Bytes = new byte[] { 0x01, 0x00, 0x00 } };
        toEnd.Targets.Add(new JumpRef { Op = script.Ops[^1] });
        var keepEnd = new ScriptOp { Bytes = new byte[] { 0x04, (byte)neverFlag, 0x00, 0x00 } };
        keepEnd.Targets.Add(new JumpRef { Op = toEnd });
        var keepRest = new ScriptOp { Bytes = new byte[] { 0x04, (byte)neverFlag, 0x00, 0x00 } };
        keepRest.Targets.Add(new JumpRef { Op = conversation });
        var ops = StartOps(rom).Select(b => new ScriptOp { Bytes = b }).ToList();
        ops.Insert(ops.Count - 1, keepEnd);
        ops.Insert(ops.Count - 1, keepRest);
        ops.Add(toEnd);
        script.Ops.InsertRange(insertAt, ops);
        script.Save(true);
    }

    // 6A (text waits for a button), fade in, warp by the man, his event (AddStart adds the jumps and the go-to
    // around the chain)
    private static byte[][] StartOps(RomBuffer rom) => new[]
    {
        new byte[] { 0x6A },
        new byte[] { 0x18, 0x02 },
        new byte[] { 0x5A },
        new byte[] { 0x4C, ElevatorMap, StartArrival },
        new byte[] { 0x51, ElevatorMap, (byte)((ElevatorEvent - LufiaMap.EventBase(rom, ElevatorMap)) & 0xFF) },
    };

    // takes the elevator start out of the prologue again (if it's there)
    private static void RemoveStart(RomBuffer rom)
    {
        var script = EventScript.Load(rom, PrologueMap, PrologueEvent);
        if (script == null) return;
        var head = StartOps(rom).Take(4).ToArray();
        bool Is(int i, int op, int length) => i < script.Ops.Count && !script.Ops[i].IsText && script.Ops[i].Bytes.Length == length && script.Ops[i].Bytes[0] == op;
        for (int i = 0; i + 8 <= script.Ops.Count; i++)
            if (Enumerable.Range(0, 4).All(k => !script.Ops[i + k].IsText && script.Ops[i + k].Bytes.SequenceEqual(head[k])) &&
                Is(i + 4, 0x04, 4) && Is(i + 5, 0x04, 4) && Is(i + 6, 0x51, 3) && Is(i + 7, 0x01, 3))
            {
                script.Ops.RemoveRange(i, 8);
                script.Save(true);
                return;
            }
    }

    // takes the elevator man's question out again (if it's there)
    private static void RemoveEntrance(RomBuffer rom)
    {
        var script = EventScript.Load(rom, ElevatorMap, ElevatorEvent);
        if (script == null || script.Ops.Count < 6 || script.Ops[0].IsText || !script.Ops[0].Bytes.Take(2).SequenceEqual(EntranceMark)) return;
        static bool Go(ScriptOp op) => !op.IsText && op.Bytes.Length == 3 && op.Bytes[0] == 0x4C;
        static bool Keep(ScriptOp op) => !op.IsText && op.Bytes.Length == 4 && op.Bytes[0] == 0x04;
        int count = Go(script.Ops[3]) ? 5                                           // (the first version, without the end before the warp)
              : script.Ops.Count >= 7 && Go(script.Ops[4]) ? (Keep(script.Ops[5]) ? 7 : 6)   // (7: locked, with the jump that keeps his lines)
              : 0;
        if (count == 0) return;
        script.Ops.RemoveRange(0, count);
        script.Save(true);
    }
}
