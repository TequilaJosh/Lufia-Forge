using LufiaForge.Core.Battle;
using LufiaForge.Modules.GameData;
using LufiaForge.Modules.TextEditor;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    public const int FirstMarker = 0xF0, FirstSpellEvent = 7, MaxSpells = 64;   // (events 3-6: the priest and the merchants)

    // spell chests: in the cave a chest item F0-FF (key items, which the cave never gives) is a spell chest. opening one
    // picks a spell (PickSpell: unlocked for the floor, its hero in the party and not knowing it yet), marks the chest
    // opened as usual, then runs the cave map's event 4 + k (the spell's) instead of the "found" message (map 63's
    // events). with nothing left to learn it gives a potion, like an item chest.
    //   $01:86A3  22 ChestHook EA EA      an item chest opens (was LDA $04 / CMP #$FF / BEQ, the empty chest test)
    //   $01:8719  22 MsgHook EA EA        its message event starts (was STA $16 / LDA #$63 / STA $1A)
    private const string SpellSource = """
        !SPELLEVENT = $7FFF28       ; the event a spell chest asks for (0 = none)

        ChestHook:                  ; M = 8, DB = 0; $04 = the chest's item
            PLA                     ; drop the JSL's return address
            PLA
            PLA
            LDA !AC_INCAVE
            BEQ .normal
            LDA $04
            CMP.b #$F0
            BCC .normal
            JSR PickSpell           ; (Lufia Forge) the spell is chosen now: one nobody in the party knows yet
            BNE +
            LDA.b #$00              ; nothing new to learn here: a potion, as an item chest
            STA !SPELLEVENT
            LDA.b #!CHESTPOTION
            STA $04
            JML $0186A9
        +   STA !SPELLEVENT
            LDA $18                 ; the chest's object, for drawing it opened (the item path keeps it in $02 too)
            STA $02
            JML $0186E8             ; as an item that fit: sound, the chest drawn opened, the statistic, then the
                                    ; message (MsgHook runs our event instead). ($01:86C0 is the bag-full path: it
                                    ; flips the opened flag back and leaves the chest closed)
        .normal
            LDA.b #$00              ; (no spell event: the byte isn't set at power-on, and an item chest's message
            STA !SPELLEVENT         ; must stay the item's)
            LDA $04
            CMP.b #$FF
            BEQ +
            JML $0186A9             ; an item: as before
        +   JML $0186CD             ; empty

        ; (Lufia Forge) PickSpell: A(8) = the event of a spell to learn (FirstSpellEvent + k), 0 = none. Candidates:
        ; SpellPool entries whose first floor is reached, whose hero is in the party and doesn't know the spell yet
        ; (what op 45 checks: the game's lists, $158F + 32 x min(hero, 2); with the "Up to 128 spells" patch, once
        ; $15EE = B1, one bit per spell, $158F + 16 x hero + spell / 8, bit spell % 8); one of them at random.
        !CANDN = $7FFF73
        !SPID  = $7FFF74
        !SPBIT = $7FFF75
        !SPBYTE = $7FFF76
        !CAND  = $7FFF80            ; up to 64 candidates (pool numbers)
        PickSpell:
            PHP
            REP #$10
            SEP #$20
            PHX
            PHY
            LDA.b #$00
            STA !CANDN
            LDX.w #$0000            ; 3 x k
            LDY.w #$0000            ; k
        .next
            TYA
            CMP.l SpellCount
            BCC +
            BRL .done
        +
            LDA !AC_FLOOR
            CMP.l SpellPool+2,x     ; its first floor
            BCC .skip
            LDA.l SpellPool,x       ; its hero, in the party?
            CMP $7E14D2
            BEQ .member
            CMP $7E14D3
            BEQ .member
            CMP $7E14D4
            BEQ .member
            CMP $7E14D5
            BNE .skip
        .member
            PHX
            PHY
            LDA.l SpellPool+1,x
            STA !SPID
            JSR KnownSpell
            BNE .known
            PLY                     ; not known: a candidate
            PLX
            PHX
            LDA !CANDN
            CMP.b #64
            BCS +
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            TYA
            STA.l !CAND,x
            LDA !CANDN
            INC A
            STA !CANDN
        +   PLX
            BRA .skip
        .known
            PLY
            PLX
        .skip
            INX
            INX
            INX
            INY
            BRL .next
        .done
            LDA !CANDN
            BEQ .out
            JSR RandL
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            LDA.l !CAND,x
            CLC
            ADC.b #!FIRSTSPELLEVENT
        .out
            PLY
            PLX
            PLP
            CMP.b #$00
            RTS

        KnownSpell:                 ; M = 8, X = 16, X = 3 x k: A not 0 = pool entry k's hero knows spell !SPID (X, Y changed)
            LDA $7E15EE
            CMP.b #$B1
            BEQ .bits
            LDA.l SpellPool,x       ; the game's lists
            CMP.b #$03
            BCC +
            LDA.b #$02
        +   REP #$20
            AND.w #$0003
            ASL #5
            TAX
            SEP #$20
            LDY.w #32
        -   LDA.l $7E158F,x
            CMP !SPID
            BEQ .yes
            INX
            DEY
            BNE -
            LDA.b #$00
            RTS
        .yes
            LDA.b #$01
            RTS
        .bits
            LDA.l SpellPool,x       ; (Lufia Forge) bits: byte 16 x hero + spell / 8, bit spell % 8
            AND.b #$03
            ASL #4
            STA !SPBYTE
            LDA !SPID
            AND.b #$07
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            LDA.l SpellBit,x
            STA !SPBIT
            LDA !SPID
            LSR #3
            CLC
            ADC !SPBYTE
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            LDA.l $7E158F,x
            AND !SPBIT
            RTS
        SpellBit:
            db $01, $02, $04, $08, $10, $20, $40, $80

        MsgHook:                    ; A = map 63's message event
            STA $16
            LDA !AC_INCAVE
            BEQ .normal
            LDA !SPELLEVENT
            BEQ .normal
            STA $16                 ; this cave map's spell event
            LDA.b #$00
            STA !SPELLEVENT
            LDA $7E077F
            STA $1A
            RTL
        .normal
            LDA.b #$63
            STA $1A
            RTL
        """;

    private const int ChestSite = 0x086A3, MsgSite = 0x08719;
    private static readonly byte[] ChestOriginal = { 0xA5, 0x04, 0xC9, 0xFF, 0xF0, 0x24 };
    private static readonly byte[] MsgOriginal = { 0x85, 0x16, 0xA9, 0x63, 0x85, 0x1A };

    public sealed record CaveSpell(int Character, int Spell, int Level);

    // the spells spell chests teach: from the heroes' learn lists (the spell patch's, else the game's), the ones
    // learned by levelling up (level 1 and up), sorted by level and 16 of them picked evenly, so chests deeper down
    // teach later spells
    public static List<CaveSpell> Spells(RomBuffer rom) => AllSpells(rom).Take(MaxSpells).ToList();

    // the spell pool for PickSpell: per spell its hero, the spell and the first floor where a chest can
    // teach it (level L: floor 2 x (L - 3), at least 1)
    private static byte[] SpellPool(RomBuffer rom) =>
        Spells(rom).SelectMany(s => new[] { (byte)s.Character, (byte)s.Spell, (byte)Math.Clamp(2 * (s.Level - 3), 1, 100) }).ToArray();

    public static int SpellFirstFloor(int level) => Math.Clamp(2 * (level - 3), 1, 100);

    // every spell the heroes learn by levelling up (level 1 and up), sorted by level
    public static List<CaveSpell> AllSpells(RomBuffer rom)
    {
        var all = new List<CaveSpell>();
        if (SpellMemory.Installed(rom) || SpellMemory.OldBlock(rom) > 0)
        {
            var lists = SpellMemory.ReadLists(rom);
            for (int character = 0; character < 4; character++) all.AddRange(lists[character].Select(e => new CaveSpell(character, e.Spell, e.Level)));
        }
        else
            foreach (var (character, listOffset) in new[] { (0, GameDataOffsets.HeroSpellList), (1, GameDataOffsets.LufiaSpellList), (3, GameDataOffsets.JerinSpellList) })
                for (int entryAt = listOffset, n = 0; n < 64 && rom.ReadByte(entryAt) != 0xFF; entryAt += 2, n++) all.Add(new CaveSpell(character, rom.ReadByte(entryAt + 1), rom.ReadByte(entryAt)));
        return all.Where(s => s.Level > 0).OrderBy(s => s.Level).ThenBy(s => s.Character).ToList();
    }

    private static readonly string[] HeroTags = { "[Hero]", "[Lufia]", "[Aguro]", "[Jerin]" };

    // event for spell chest k: if the hero already knows the spell, a potion, otherwise they learn it
    private static byte[] SpellEvent(RomBuffer rom, CaveSpell caveSpell, int potion)
    {
        byte[] Text(string text)
        {
            var bytes = TextDecoder.Encode(text, rom).ToList();
            if (bytes.Count > 0 && bytes[^1] == 0x00) bytes.RemoveAt(bytes.Count - 1);
            bytes.Add(0x00);   // end of the event
            return bytes.ToArray();
        }
        var learn = new List<byte> { 0x3D, (byte)caveSpell.Character, (byte)caveSpell.Spell, 0x0D };
        string spell = Maps.EventCommands.SpellName(rom, caveSpell.Spell), item = Maps.MapSetupScript.ItemName(rom, potion).Replace('@', ' ').Trim();
        learn.AddRange(Text($"{HeroTags[caveSpell.Character]} learned {spell}!"));
        int known = 5 + learn.Count;
        var script = new List<byte> { 0x45, (byte)caveSpell.Character, (byte)caveSpell.Spell, (byte)known, (byte)(known >> 8) };
        script.AddRange(learn);
        script.AddRange(new byte[] { 0x3E, (byte)potion, 0x01, 0x0D });
        script.AddRange(Text($"{HeroTags[caveSpell.Character]} knows {spell} already.[NL]Found {item} instead."));
        return script.ToArray();
    }
}
