using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Monsters you can see, as in Lufia II: on a map that has them, monster characters walk about and a fight starts when
/// one reaches the party; random battles are off there. Beaten monsters vanish, and come back when the map is entered
/// again. (Not on the world map.)
/// <para>
/// A monster is an ordinary map character n (sprite, place, wander box) whose talk event (n) is
/// <c>1F gg</c> (a battle with encounter group gg, its own, as a random battle would pick: $142D = group) then
/// <c>5C n 00</c> (hide it). A 256-entry table (first monster character, count) says which characters of each map are
/// monsters, and the step check for random battles ($01:881D) is hooked to make none on those maps (their setup scripts
/// stay as they were, so taking the monsters off brings the random battles back):
/// <code>
/// $01:8820  22 NoRandom / D0 4E / EA   (was LDA $07B4 / BNE $8874 / LDA $94; BNE $8874 stays)
/// </code>
/// </para>
/// <para>
/// Moving like Lufia II's (seen in Lufia II: they stand still while the hero does, take a step for each of his,
/// drift towards him): the map characters' wander routine ($01:9FF1, X = the character's record, $18 = actor id,
/// map character n = actor n + 8) is hooked where it checks the "frozen" bit; for a monster it takes a step only when
/// the hero has reached a new tile, towards him within 6 tiles (else at random), inside its box, not onto him, with
/// the game's own target ($01:A053) and wall ($01:AFAC) checks:
/// <code>
/// $01:9FFF  22 Step         (was LDA $1D,X / BIT #$20; BNE $9FDC stays: not 0 = only animate)
/// </code>
/// When a monster stands next to the hero (both still) it waits there, and the talk check ($01:8394, A pressed) is
/// made to happen with the hero facing it, so the game runs its event as if he had talked to it:
/// <code>
/// $01:8394  22 Talk         (was LDA $30 / AND #$80; BEQ $840E stays)
/// </code>
/// A beaten monster (hidden by its event: $1D bit 80) is taken out of the map's actor list ($07BE, 32 actor ids),
/// which the wandering and the "who is on that tile" check of talking ($01:87DC) go through.
/// After a fight the other monsters can't start one until the hero has taken <see cref="GraceSteps"/> steps.
/// RAM: $7F:FEC0-FEC7 (the hero's tile, the turn count, the waiting monster and its direction, the grace steps left),
/// $7F:FF80-FFBF (the turn each actor last moved on).
/// </para>
/// <para>
/// Talking to character n runs event n of the map's script block (the talk passes n + 1 and the table is read from 1),
/// and from the map the character's record names ($1C), which can still be the map it came from: the talk is made to
/// use the map the party is on. The monsters take characters whose events nobody else uses (no script, no step-on area,
/// no "run event"); skipped ones stay unused. A cutscene that brings in characters a map doesn't list (the opening on
/// map 4F does) can clash with them, so such maps want a try in the game.
/// </para>
/// </summary>
public static class VisibleMonsters
{
    private const int StepSite = 0x9FFF;        // $01:9FFF
    private static readonly byte[] StepOriginal = { 0xB5, 0x1D, 0x89, 0x20 };
    private const int TalkSite = 0x8394;        // $01:8394
    private static readonly byte[] TalkOriginal = { 0xA5, 0x30, 0x29, 0x80 };
    private const int RandomSite = 0x8820;      // $01:8820
    private static readonly byte[] RandomOriginal = { 0xAD, 0xB4, 0x07, 0xD0, 0x4F, 0xA5, 0x94 };
    private const string Magic = "LFVM";
    private const int Header = 8, MapTable = 0x300, BehaviourTable = MapTable + 512, BlockSize = BehaviourTable + 256 * 16;
    /// <summary>The block's version (header byte 4): 2 added the behaviour table; an older block is rebuilt.</summary>
    private const int Version = 2;
    /// <summary>Behaviour bits (a byte per monster: map x 16 + its number among the map's monsters).</summary>
    private const int KeepsToItself = 0x01, ChasesInItsArea = 0x02;
    public const int ChaseRange = 6;
    /// <summary>Characters a map can have (actor records $07DE + 32 x (n + 8) run up to the event engine's RAM; the
    /// biggest towns have 32).</summary>
    public const int MaxCharacters = 32;
    /// <summary>Steps the hero can take after a fight before another monster can start one.</summary>
    public const int GraceSteps = 3;

    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);

    private static string Source(int maps, int behaviour) => $$"""
        !MAPS = ${{maps:X6}}
        !BEHAV = ${{behaviour:X6}}
        !BEH = $7FFEC8
        !HEROX = $7FFEC0
        !HEROY = $7FFEC2
        !TURN = $7FFEC4
        !PEND = $7FFEC5
        !PDIR = $7FFEC6
        !SAFE = $7FFEC7
        !LAST = $7FFF80

        Step:                       ; M = 8, X = 16, X = the character's record, $18 = its actor id
            JSR IsMonster
            BCS .monster
            LDA $1D,x               ; (replaced) frozen?
            BIT.b #$20
            RTL
        .monster
            LDA $1D,x               ; beaten (hidden by its event): out of the map's actor list, so it can't be
            BIT.b #$80              ; talked to (fought) again, until the map is entered again
            BNE .gone
            LDA $0A,x
            BNE .go                 ; a step under way: carry on
            JSR Turn
            JSR Contact
            BCS .rest               ; next to the hero: wait for the talk check
            PHX
            REP #$20
            LDA $18
            AND.w #$003F
            TAX
            SEP #$20
            LDA !TURN
            CMP !LAST,x
            BEQ .done               ; already moved this turn
            STA !LAST,x
            PLX
            JSR Choose
            STA $1B
            PHX
            JSL Target              ; $12/$14 = the tile that way, $1A = its direction bit
            PLX
            SEP #$20
            LDA $12                 ; inside the box
            CMP $11,x
            BCC .rest
            CMP $15,x
            BCS .rest
            LDA $14
            CMP $13,x
            BCC .rest
            CMP $17,x
            BCS .rest
            JSR Hero                ; not onto the hero
            LDA $0001,y
            CMP $12
            BNE +
            LDA $0004,y
            CMP $14
            BEQ .rest
        +   PHX
            JSL Wall
            PLX
            SEP #$20
            BCS .rest
            LDA $1A                 ; the step (as the game's wandering does)
            STA $0A,x
            LDA $1B
            STA $0B,x
        .go
            LDA.b #$00
            RTL
        .done
            PLX
        .rest
            LDA.b #$01              ; only animate
            RTL
        .gone
            JSR Sweep
            BRA .rest

        Sweep:                      ; M = 8, X = 16: beaten monsters (hidden: $1D bit 80) out of the actor list; all kept
            PHB
            PHX
            PHY
            LDA $18
            PHA
            LDA.b #$7E
            PHA
            PLB
            LDY.w #$001F
        .next
            LDA $07BE,y
            BEQ .skip
            STA $18
            JSR IsMonster
            BCC .skip
            REP #$20
            LDA $18
            AND.w #$003F
            ASL #5
            CLC
            ADC.w #$07DE
            TAX
            SEP #$20
            LDA $1D,x
            BIT.b #$80
            BEQ .skip
            LDA.b #$00
            STA $07BE,y
        .skip
            DEY
            BPL .next
            PLA
            STA $18
            PLY
            PLX
            PLB
            RTS

        Behav:                      ; A = this monster's behaviour byte (M = 8, X = 16; X kept)
            PHX
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            SEP #$20
            LDA $18
            SEC
            SBC.b #$08
            SEC
            SBC !MAPS,x             ; its number among the map's monsters
            REP #$20
            AND.w #$000F
            PHA
            TXA
            ASL #3                  ; map x 16
            CLC
            ADC $01,s
            TAX
            PLA
            SEP #$20
            LDA !BEHAV,x
            PLX
            RTS

        IsMonster:                  ; C = this character is one of the map's monsters; X kept
            PHX
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            SEP #$20
            LDA !MAPS+1,x           ; how many
            BEQ .no
            PHA
            LDA $18
            SEC
            SBC.b #$08
            BCC .no2
            SEC
            SBC !MAPS,x             ; minus the first
            BCC .no2
            CMP $01,s
            BCS .no2
            PLA
            PLX
            SEC
            RTS
        .no2
            PLA
        .no
            PLX
            CLC
            RTS

        Hero:                       ; Y = the hero's record (X kept)
            REP #$20
            LDA $8D
            AND.w #$00FF
            ASL #5
            CLC
            ADC.w #$07DE
            TAY
            SEP #$20
            RTS

        Turn:                       ; a new turn each time the hero reaches a new tile
            JSR Hero
            LDA $0001,y
            CMP !HEROX
            BNE .new
            LDA $0004,y
            CMP !HEROY
            BEQ .same
        .new
            LDA $0001,y
            STA !HEROX
            LDA $0004,y
            STA !HEROY
            LDA !TURN
            INC
            STA !TURN
            LDA.b #$00              ; (a waiting monster is forgotten once he moves on)
            STA !PEND
            LDA !SAFE               ; a step of the grace after a fight
            BEQ .same
            DEC
            STA !SAFE
        .same
            RTS

        Contact:                    ; C = next to the hero, he on a tile and it still (then it waits and the talk check is set up)
            LDA !SAFE               ; not right after a fight
            BEQ +
            CMP.b #{{GraceSteps + 1}}
            BCC .no
            LDA.b #$00              ; (more is what RAM held at power-on)
            STA !SAFE
        +   JSR Hero
            LDA $0000,y             ; the hero exactly on a tile (not between two)
            ORA $0003,y
            BNE .no
            LDA $0001,y             ; dx = monster - hero
            STA $1A
            LDA $01,x
            SEC
            SBC $1A
            STA $1A
            LDA $0004,y             ; dy
            STA $1B
            LDA $04,x
            SEC
            SBC $1B
            STA $1B
            LDA $1A
            BEQ .vertical
            LDA $1B
            BNE .no
            LDA $1A
            CMP.b #$01
            BEQ .right
            CMP.b #$FF
            BNE .no
            LDA.b #$01              ; the monster is to his left
            BRA .found
        .right
            LDA.b #$00
            BRA .found
        .vertical
            LDA $1B
            CMP.b #$01
            BEQ .down
            CMP.b #$FF
            BNE .no
            LDA.b #$03              ; above him
            BRA .found
        .down
            LDA.b #$02
        .found
            STA !PDIR
            LDA !PEND
            BNE +
            LDA $18
            STA !PEND
        +   SEC
            RTS
        .no
            CLC
            RTS

        Choose:                     ; A = a direction (0 right, 1 left, 2 down, 3 up): towards the hero when near
            JSR Behav
            STA !BEH
            BIT.b #{{KeepsToItself}}
            BNE .random             ; it doesn't come after the party
            JSR Hero
            LDA $0001,y             ; the hero inside its roaming area
            CMP $11,x
            BCC .random
            CMP $15,x
            BCS .random
            LDA $0004,y
            CMP $13,x
            BCC .random
            CMP $17,x
            BCS .random
            LDA $0001,y             ; dx = hero - monster
            SEC
            SBC $01,x
            STA $1A
            LDA $0004,y
            SEC
            SBC $04,x
            STA $1B
            LDA $1A                 ; |dx| -> $16, |dy| -> $17
            BPL +
            EOR.b #$FF
            INC
        +   STA $16
            LDA $1B
            BPL +
            EOR.b #$FF
            INC
        +   STA $17
            LDA !BEH                ; (one that roams only part of the map comes from anywhere in it)
            BIT.b #{{ChasesInItsArea}}
            BNE +
            LDA $17
            CLC
            ADC $16
            CMP.b #{{ChaseRange + 1}}
            BCS .random
        +
            PHX                     ; now and then a random step anyway (round walls)
            LDA.b #$04
            JSL $08B829
            PLX
            CMP.b #$00
            BEQ .random
            LDA $17                 ; |dy| > |dx|: up or down
            CMP $16
            BEQ .sideways
            BCS .updown
        .sideways
            LDA $1A
            BMI +
            LDA.b #$00
            RTS
        +   LDA.b #$01
            RTS
        .updown
            LDA $1B
            BMI +
            LDA.b #$02
            RTS
        +   LDA.b #$03
            RTS
        .random
            PHX
            LDA.b #$04
            JSL $08B829
            PLX
            RTS

        Talk:                       ; M = 8, in place of LDA $30 / AND #$80 (A pressed?) before BEQ
            JSR Sweep               ; (a monster beaten just now can't be talked to)
            LDA !PEND
            BEQ .normal
            PHX
            REP #$20                ; its event comes from this map's script block (the talk reads the map from
            AND.w #$003F            ; the character's $1C, which can still name the map it came from)
            ASL #5
            CLC
            ADC.w #$07DE
            TAX
            SEP #$20
            LDA $077F
            STA $1C,x
            LDA.b #$00
            STA !PEND
            LDA.b #{{GraceSteps}}   ; then the others leave him alone for a few steps
            STA !SAFE
            REP #$20                ; the hero faces the monster
            LDA $8D
            AND.w #$00FF
            ASL #5
            CLC
            ADC.w #$07DE
            TAX
            SEP #$20
            LDA !PDIR
            STA $0B,x
            PLX
            SEP #$20
            LDA.b #$80              ; a fresh press for TRB $34
            TSB $34
            RTL
        .normal
            LDA $30
            AND.b #$80
            RTL

        NoRandom:                   ; M = 8, X = 8: A not 0 = no random battle on this step
            LDA $07B4               ; (replaced: LDA $07B4 / BNE, LDA $94 / BNE)
            ORA $94
            BNE .out
            PHP
            REP #$10
            PHX
            REP #$20
            LDA $077F
            AND.w #$00FF
            ASL
            TAX
            SEP #$20
            LDA !MAPS+1,x           ; the map's monster count
            PLX
            PLP
            CMP.b #$00
        .out
            RTL

        Target:
            PEA $AF2B               ; its RTS goes to the RTL at $01:AF2C
            JML $01A053
        Wall:
            PEA $AF2B
            JML $01AFAC
        """;

    public static bool IsApplied(RomBuffer rom) => Block(rom) > 0;
    public static bool IsOriginal(RomBuffer rom) =>
        rom.ReadBytes(StepSite, 4).SequenceEqual(StepOriginal) && rom.ReadBytes(TalkSite, 4).SequenceEqual(TalkOriginal) &&
        rom.ReadBytes(RandomSite, RandomOriginal.Length).SequenceEqual(RandomOriginal);

    private static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(StepSite) != 0x22) return -1;
        int b = FileOffset(rom.ReadByte(StepSite + 1) | rom.ReadByte(StepSite + 2) << 8 | rom.ReadByte(StepSite + 3) << 16) - Header;
        return b >= 0 && b + 4 < rom.Length && rom.ReadBytes(b, 4).SequenceEqual(Magic.Select(c => (byte)c)) ? b : -1;
    }

    /// <summary>Which characters of a map are monsters: (first character, count), count 0 = none.</summary>
    public static (int First, int Count) Monsters(RomBuffer rom, int mapId)
    {
        int b = Block(rom);
        return b < 0 ? (0, 0) : (rom.ReadByte(b + MapTable + mapId * 2), rom.ReadByte(b + MapTable + mapId * 2 + 1));
    }

    /// <summary>Puts the code in (once).</summary>
    public static void Apply(RomBuffer rom)
    {
        int old = Block(rom);
        if (old > 0 && rom.ReadByte(old + 4) == Version) return;
        if (old < 0 && !IsOriginal(rom)) throw new InvalidOperationException("The game's code at $01:9FFF, $01:8394 or $01:8820 isn't the original (another patch changed it), so visible monsters can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        // (a block of an older version: its table of monsters comes along, the code is new)
        var maps = old > 0 ? rom.ReadBytes(old + MapTable, 512) : new byte[512];
        var space = ExpansionSpace.Load(rom);
        if (old > 0 && space.At(old)?.Start == old) space.Free(old);
        int b = space.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 0);
        space.Save();
        int snes = SnesAddress(b);
        var a = new Asm65816();
        a.Source(Source(snes + MapTable, snes + BehaviourTable));
        var code = a.Build(snes + Header);
        if (Header + code.Length > MapTable) throw new InvalidOperationException("The visible monster code doesn't fit.");
        rom.WriteBytes(b, Magic.Select(c => (byte)c).ToArray());
        rom.WriteByte(b + 4, Version);
        rom.WriteBytes(b + Header, code);
        rom.WriteBytes(b + MapTable, maps);
        // monsters of an older block came after the party within 6 tiles: bits 0
        rom.WriteBytes(b + BehaviourTable, new byte[256 * 16]);
        int step = snes + Header + a.Offset("Step"), talk = snes + Header + a.Offset("Talk");
        rom.WriteBytes(StepSite, new byte[] { 0x22, (byte)step, (byte)(step >> 8), (byte)(step >> 16) });
        rom.WriteBytes(TalkSite, new byte[] { 0x22, (byte)talk, (byte)(talk >> 8), (byte)(talk >> 16) });
        int noRandom = snes + Header + a.Offset("NoRandom");
        rom.WriteBytes(RandomSite, new byte[] { 0x22, (byte)noRandom, (byte)(noRandom >> 8), (byte)(noRandom >> 16), 0xD0, 0x4E, 0xEA });
        rom.FixChecksum();
    }

    /// <summary>The map's encounter group from its setup script (command 0D gg), or -1.</summary>
    public static int Group(RomBuffer rom, int mapId)
    {
        var s = SetupScripts.Read(rom, mapId);
        var (ops, _) = SetupScripts.Parse(s);
        foreach (int p in ops) if (s[p] == 0x0D) return s[p + 1];
        return -1;
    }

    /// <summary>The group the map's monsters fight (from the first one's event), or -1.</summary>
    public static int MonsterGroup(RomBuffer rom, int mapId)
    {
        var (first, count) = Monsters(rom, mapId);
        if (count == 0) return -1;
        try
        {
            var ev = EventScript.Load(rom, mapId, first);
            return ev?.Ops.FirstOrDefault(o => !o.IsText && o.Bytes is [0x1F, _])?.Bytes[1] ?? -1;
        }
        catch { return -1; }
    }

    /// <summary>A monster on a map: where it starts, its sprite, how far it may wander (0 = anywhere on the map) and
    /// the encounter group it fights.</summary>
    public sealed record Monster(int X, int Y, int Sprite, int Range, int Group, bool Chases = true);

    /// <summary>The block has the behaviour table (version 2; before it, every monster came after the party).</summary>
    private static bool HasBehaviours(RomBuffer rom) => Block(rom) is int b && b > 0 && rom.ReadByte(b + 4) == Version;

    /// <summary>The map's monsters as placed.</summary>
    public static List<Monster> Placed(RomBuffer rom, LufiaMap map)
    {
        var (first, count) = Monsters(rom, map.MapId);
        if (count == 0) return new();
        int GroupOf(int n)
        {
            int o = LufiaMap.EventScriptOffset(rom, map.MapId, n);
            return o >= 0 && rom.ReadByte(o) == 0x1F ? rom.ReadByte(o + 1) : -1;
        }
        return map.Npcs.Where(n => n.Index >= first && n.Index < first + count)
                       .Select(n => new Monster(n.X, n.Y, n.Sprite,
                                     n.BoxX1 == 0 && n.BoxY1 == 0 && n.BoxX2 >= map.Width && n.BoxY2 >= map.Height ? 0 : Math.Max(n.X - n.BoxX1, n.BoxX2 - 1 - n.X),
                                     GroupOf(n.Index),
                                     !HasBehaviours(rom) || (rom.ReadByte(Block(rom) + BehaviourTable + map.MapId * 16 + Math.Min(15, n.Index - first)) & KeepsToItself) == 0))
                       .ToList();
    }

    private static bool IsMonsterEvent(EventScript? ev, int n) =>
        ev != null && ev.Ops.Count == 3 && ev.Ops[0].Bytes is [0x1F, _] && ev.Ops[1].Bytes is [0x5C, var who, 0x00] && who == n && ev.Ops[2].Bytes is [0x00];

    private static byte[] MonsterScript(int n, int group) => new byte[] { 0x1F, (byte)group, 0x5C, (byte)n, 0x00, 0x00 };

    /// <summary>Slot <paramref name="ev"/> holds what taking a monster off leaves: its script with the first byte
    /// ended (00 gg 5C n 00 00).</summary>
    private static bool IsRemovedMonster(RomBuffer rom, int mapId, int ev)
    {
        if (!HasScript(rom, mapId, ev)) return false;
        int o = LufiaMap.EventScriptOffset(rom, mapId, ev);
        if (o < 0 || o + 6 > rom.Length) return false;
        var x = rom.ReadBytes(o, 6);
        return x[0] == 0x00 && x[2] == 0x5C && x[3] == ev && x[4] == 0x00 && x[5] == 0x00;
    }

    /// <summary>The monster events of <paramref name="chars"/>: rewritten where a removed monster's script is, the
    /// rest added after the map's script block (copied to the added space once, for all of them).</summary>
    private static void WriteEvents(RomBuffer rom, int mapId, List<(int N, int Group)> chars)
    {
        var fresh = new List<int>();
        var groups = chars.ToDictionary(c => c.N, c => c.Group);
        foreach (var (n, group) in chars)
        {
            if (IsRemovedMonster(rom, mapId, n)) rom.WriteBytes(LufiaMap.EventScriptOffset(rom, mapId, n), MonsterScript(n, group));
            else fresh.Add(n);
        }
        if (fresh.Count == 0) return;
        int b = EventScript.BlockOffset(rom, mapId), len = MapEvents.BlockLength(rom, mapId);
        var block = new List<byte>(rom.ReadBytes(b, len));
        var rels = new List<int>();
        foreach (int n in fresh) { rels.Add(block.Count); block.AddRange(MonsterScript(n, groups[n])); }
        block.Add(0x00);   // guard
        if (block.Count > 0xFFFF) throw new InvalidOperationException("This map's events are too big to add monsters.");
        var data = block.ToArray();
        for (int i = 0; i < fresh.Count; i++) { data[fresh[i] * 2] = (byte)rels[i]; data[fresh[i] * 2 + 1] = (byte)(rels[i] >> 8); }
        var space = ExpansionSpace.Load(rom);
        int at = space.Allocate(data.Length, ExpansionSpace.Kind.ScriptBlock, mapId, EventScript.ScriptLimit);
        rom.WriteBytes(at, data);
        int t = at - 0x18000, p = LufiaMap.MapScriptTable + mapId * 5;
        rom.WriteBytes(p, new[] { (byte)t, (byte)(t >> 8), (byte)(t >> 16) });
        if (b >= MapWriter.ExpansionStart && space.At(b)?.Start == b && !MapCatalog.Scan(rom).Any(m => m.MapId != mapId && EventScript.BlockOffset(rom, m.MapId) == b))
            space.Free(b);
        space.Save();
        rom.FixChecksum();
    }

    /// <summary>Slot <paramref name="ev"/> has a script of its own (not FFFF, nor 0 / 1, which mean no event).</summary>
    private static bool HasScript(RomBuffer rom, int mapId, int ev)
    {
        if (!MapEvents.HasBlock(rom, mapId)) return false;
        int p = EventScript.BlockOffset(rom, mapId) + ev * 2;
        if (p + 1 >= rom.Length) return false;
        int rel = rom.ReadUInt16Le(p);
        return rel != 0xFFFF && rel >= 2;
    }

    /// <summary>
    /// Gives a map (not the world map) monsters, in place of the ones it has: one character per place in
    /// <paramref name="places"/> with <paramref name="sprite"/>, wandering anywhere on the map (<paramref name="range"/>
    /// 0) or up to <paramref name="range"/> tiles from where it starts, each fighting encounter group
    /// <paramref name="group"/> (-1 = the group they fight now, else the one the map's setup script gives random
    /// battles) when it reaches the party. The map has no random battles while it has monsters.
    /// </summary>
    public static void AddToMap(RomBuffer rom, int mapId, IReadOnlyList<(int X, int Y)> places, int sprite, int range = 0, int group = -1)
    {
        if (places.Count == 0) { RemoveFromMap(rom, mapId); return; }
        if (group <= 0) group = MonsterGroup(rom, mapId);
        if (group <= 0) group = Group(rom, mapId);
        if (group <= 0) throw new InvalidOperationException($"Map {mapId:X2} has no random battles: pick the group its monsters fight.");
        AddToMap(rom, mapId, places.Select(p => new Monster(p.X, p.Y, sprite, range, group)).ToList());
    }

    /// <summary>
    /// Gives a map (not the world map) <paramref name="monsters"/>, in place of the ones it has: each its own sprite,
    /// wandering range and encounter group (the fight its event starts when it reaches the party). The map has no
    /// random battles while it has monsters.
    /// </summary>
    public static void AddToMap(RomBuffer rom, int mapId, IReadOnlyList<Monster> monsters)
    {
        if (mapId == LufiaMap.WorldMap) throw new InvalidOperationException("Visible monsters aren't for the world map.");
        if (monsters.Count == 0) { RemoveFromMap(rom, mapId); return; }
        if (monsters.FirstOrDefault(m => m.Group is < 1 or > 0xFF) is { } bad)
            throw new InvalidOperationException($"The monster at {bad.X},{bad.Y} has no encounter group.");
        var places = monsters.Select(m => (m.X, m.Y)).ToList();
        Apply(rom);
        RemoveFromMap(rom, mapId);
        var map = LufiaMap.Load(rom, mapId);
        // talking to character n runs event n of the block (seen in game: the talk passes n + 1, the table is read
        // from 1), a step-on area runs event base + area + 1 and "run event" rr runs base + rr: the monsters take
        // characters whose events nobody else uses (characters skipped on the way stay unused, never shown)
        int eventBase = LufiaMap.EventBase(rom, mapId), slots = MapEvents.HasBlock(rom, mapId) ? MapEvents.TableLength(rom, mapId) : 0;
        var taken = new HashSet<int>();
        foreach (var t in map.Triggers) taken.Add((eventBase + t.Index + 1) & 0xFF);
        for (int ev = 0; ev < slots; ev++)
        {
            if (!HasScript(rom, mapId, ev) || IsRemovedMonster(rom, mapId, ev)) continue;
            taken.Add(ev);
            EventScript? es;
            try { es = EventScript.Load(rom, mapId, ev); } catch { continue; }
            if (es == null) continue;
            foreach (var op in es.Ops)
                if (!op.IsText && op.Bytes.Length >= 3 && op.Bytes[0] == 0x51 && op.Bytes[1] == mapId) taken.Add(MapEvents.Chained(rom, op.Bytes));
        }
        bool Free(int n) => !taken.Contains(n) && (n >= map.Npcs.Count || map.Npcs[n].IsUnused);
        int first = 0;
        while (first < MaxCharacters && !Enumerable.Range(first, places.Count).All(Free)) first++;
        if (first + places.Count > MaxCharacters)
            throw new InvalidOperationException($"Map {mapId:X2} has no room for {places.Count} monsters: a map can have {MaxCharacters} characters, and the free ones (with no event of their own) don't reach that many.");
        for (int i = map.Npcs.Count; i < first + places.Count; i++)
            map.Npcs.Add(new MapNpc { Index = i, Sprite = 0xFF, X = 0, Y = 0, BoxX1 = 0, BoxY1 = 0, BoxX2 = 1, BoxY2 = 1 });
        for (int i = 0; i < monsters.Count; i++)
        {
            var (x, y, sprite, range, _, _) = monsters[i];
            var (x1, y1, x2, y2) = range <= 0 ? (0, 0, map.Width, map.Height)
                : (Math.Max(0, x - range), Math.Max(0, y - range), Math.Min(map.Width, x + range + 1), Math.Min(map.Height, y + range + 1));
            map.Npcs[first + i] = new MapNpc { Index = first + i, Sprite = sprite, Flags = 0x20, X = x, Y = y, BoxX1 = x1, BoxY1 = y1, BoxX2 = x2, BoxY2 = y2 };
        }
        MapEvents.EnsureTable(rom, mapId, first + places.Count + 1);
        WriteEvents(rom, mapId, monsters.Select((m, i) => (first + i, m.Group)).ToList());
        MapWriter.Save(rom, map, true);
        CompactSetup(rom, mapId);
        int b = Block(rom);
        rom.WriteByte(b + MapTable + mapId * 2, (byte)first);
        rom.WriteByte(b + MapTable + mapId * 2 + 1, (byte)places.Count);
        var behaviour = new byte[16];
        for (int i = 0; i < monsters.Count && i < 16; i++)
            behaviour[i] = (byte)((monsters[i].Chases ? 0 : KeepsToItself) | (monsters[i].Range > 0 ? ChasesInItsArea : 0));
        rom.WriteBytes(b + BehaviourTable + mapId * 16, behaviour);
        rom.FixChecksum();
    }

    /// <summary>Drops the "nothing" commands (40) at the start of the map's setup script: switching characters off
    /// leaves them where their "switch on" (41 + n) was, and the new ones go in front, so the script would grow a
    /// little with every change. Jumps (04 ww, from the script's start) move with the rest.</summary>
    private static void CompactSetup(RomBuffer rom, int mapId)
    {
        byte[] s;
        try { s = SetupScripts.Read(rom, mapId); } catch (InvalidOperationException) { return; }
        int k = 0;
        while (2 + k < s.Length && s[2 + k] == SetupScripts.Nothing) k++;
        if (k == 0) return;
        var (ops, _) = SetupScripts.Parse(s);
        var result = new byte[s.Length - k];
        s.AsSpan(0, 2).CopyTo(result);
        s.AsSpan(2 + k).CopyTo(result.AsSpan(2));
        foreach (int p in ops)
            if (p >= 2 + k && s[p] == 0x04)
            {
                int q = p - k, w = result[q + 1] | result[q + 2] << 8;
                if (w >= 2 + k) w -= k; else if (w > 2) w = 2;
                result[q + 1] = (byte)w; result[q + 2] = (byte)(w >> 8);
            }
        SetupScripts.Replace(rom, mapId, result);
    }

    /// <summary>Takes the map's monsters off (their characters become unused, their events empty): random battles
    /// come back as the map's setup script has them.</summary>
    public static void RemoveFromMap(RomBuffer rom, int mapId)
    {
        var (first, count) = Monsters(rom, mapId);
        if (count == 0) return;
        var map = LufiaMap.Load(rom, mapId);
        for (int n = first; n < first + count && n < map.Npcs.Count; n++)
        {
            var old = map.Npcs[n];
            map.Npcs[n] = new MapNpc { Index = n, Sprite = 0xFF, X = old.X, Y = old.Y, BoxX1 = 0, BoxY1 = 0, BoxX2 = 1, BoxY2 = 1 };
            EventScript? ev = null;
            try { ev = EventScript.Load(rom, mapId, n); } catch { }
            if (IsMonsterEvent(ev, n)) rom.WriteByte(LufiaMap.EventScriptOffset(rom, mapId, n), 0x00);   // ends at once (the slot stays as it is)
        }
        MapWriter.Save(rom, map, true);
        CompactSetup(rom, mapId);
        int b = Block(rom);
        rom.WriteByte(b + MapTable + mapId * 2, 0);
        rom.WriteByte(b + MapTable + mapId * 2 + 1, 0);
        if (HasBehaviours(rom)) rom.WriteBytes(b + BehaviourTable + mapId * 16, new byte[16]);
        rom.FixChecksum();
    }

    /// <summary>Floor a monster can stand on and get around from: enterable from every side; not near an exit or an
    /// arrival point (the party comes in there); not a step-on area or a character's tile; in the part of the map the
    /// arrival points lead to (or its biggest open space).</summary>
    public static List<(int X, int Y)> OpenTiles(RomBuffer rom, LufiaMap map)
    {
        var ts = MapTileset.ForMap(rom, map);
        int w = map.Width, h = map.Height;
        var open = new bool[w * h];
        var stand = new bool[w * h];
        for (int i = 0; i < open.Length; i++)
        {
            int blk = map.Layer[i] & 0x3FF, a;
            if (ts.CompositeParts(blk) is var (g, o)) a = ts.Attribute(g) | ts.Attribute(o); else a = ts.Attribute(blk);
            open[i] = (a & BlockSet.AttrBlocked) == 0;
            // (stand on floor with no one-sided walls either, so it can step off any way)
            stand[i] = (a & (BlockSet.AttrBlocked | BlockSet.AttrLeft | BlockSet.AttrRight | BlockSet.AttrUp | BlockSet.AttrDown)) == 0;
        }
        var avoid = new bool[w * h];
        void Mark(int x1, int y1, int x2, int y2)
        {
            for (int y = Math.Max(0, y1); y < Math.Min(h, y2); y++)
                for (int x = Math.Max(0, x1); x < Math.Min(w, x2); x++) avoid[y * w + x] = true;
        }
        foreach (var e in map.Exits) Mark(e.X1 - 1, e.Y1 - 1, e.X2 + 1, e.Y2 + 1);
        foreach (var a in map.Arrivals) Mark(a.X - 3, a.Y - 3, a.X + 4, a.Y + 4);
        foreach (var t in map.Triggers.Where(t => !t.IsUnused)) Mark(t.X1, t.Y1, t.X2, t.Y2);
        foreach (var n in map.Npcs.Where(n => !n.IsUnused)) Mark(n.X, n.Y, n.X + 1, n.Y + 1);
        // open spaces, numbered; the ones with an arrival point are where the party walks
        var region = new int[w * h];
        var sizes = new List<int> { 0 };
        for (int s = 0; s < open.Length; s++)
        {
            if (!open[s] || region[s] != 0) continue;
            int r = sizes.Count, size = 0;
            var q = new Queue<int>(); q.Enqueue(s); region[s] = r;
            while (q.Count > 0)
            {
                int c = q.Dequeue(); size++;
                int cx = c % w, cy = c / w;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (open[ni] && region[ni] == 0) { region[ni] = r; q.Enqueue(ni); }
                }
            }
            sizes.Add(size);
        }
        List<(int, int)> In(HashSet<int> regions)
        {
            var list = new List<(int, int)>();
            for (int i = 0; i < open.Length; i++)
                if (stand[i] && !avoid[i] && regions.Contains(region[i])) list.Add((i % w, i / w));
            return list;
        }
        var walked = map.Arrivals.Where(a => a.X >= 0 && a.Y >= 0 && a.X < w && a.Y < h).Select(a => region[a.Y * w + a.X]).Where(r => r != 0).ToHashSet();
        var found = In(walked);
        if (found.Count == 0 && sizes.Count > 1) found = In(new HashSet<int> { Enumerable.Range(1, sizes.Count - 1).MaxBy(r => sizes[r]) });
        return found;
    }

    /// <summary><paramref name="count"/> places for monsters on the map's open floor (<see cref="OpenTiles"/>), spread
    /// out: each about as far from the others as can be, the first at random.</summary>
    public static List<(int X, int Y)> AutoPlaces(RomBuffer rom, LufiaMap map, int count, int seed)
    {
        var tiles = OpenTiles(rom, map);
        var rnd = new Random(seed);
        var chosen = new List<(int X, int Y)>();
        if (tiles.Count == 0) return chosen;
        chosen.Add(tiles[rnd.Next(tiles.Count)]);
        while (chosen.Count < count && chosen.Count < tiles.Count)
        {
            var best = tiles.Where(t => !chosen.Contains(t))
                            .Select(t => (t, d: chosen.Min(c => Math.Abs(c.X - t.X) + Math.Abs(c.Y - t.Y))))
                            .OrderByDescending(x => x.d).Take(4).ToList();
            chosen.Add(best[rnd.Next(best.Count)].t);
        }
        return chosen;
    }
}
