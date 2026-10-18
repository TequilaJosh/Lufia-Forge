using LufiaForge.Core.Battle;
using LufiaForge.Modules.GameData;
using LufiaForge.Modules.TextEditor;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    public const int FirstMarker = 0xF0, FirstSpellEvent = 4, MaxSpells = 64;

    /// <summary>
    /// Spell chests: in the cave a chest item F0-FF (key items, which the cave never gives) is a spell chest. Opening one
    /// picks a spell (PickSpell: unlocked for the floor, its hero in the party and not knowing it yet), marks the chest
    /// opened as usual, then runs the cave map's event 4 + k (the spell's) instead of the "found" message (map 63's
    /// events); with nothing left to learn it gives a potion, as an item chest.
    /// <code>
    /// $01:86A3  22 ChestHook EA EA      an item chest opens (was LDA $04 / CMP #$FF / BEQ, the empty-chest test)
    /// $01:8719  22 MsgHook EA EA        its message event starts (was STA $16 / LDA #$63 / STA $1A)
    /// </code>
    /// </summary>
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
        ; (the spell list op 45 checks: $158F + 32 x min(hero, 2)); one of them at random.
        !CANDN = $7FFF73
        !SPID  = $7FFF74
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
            LDA.l SpellPool,x
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
            BEQ .known
            INX
            DEY
            BNE -
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

    /// <summary>
    /// The spells spell chests teach: from the heroes' learn lists (the spell patch's, else the game's), the ones
    /// learned by levelling up (level 1 and up), sorted by level and 16 of them picked evenly, so chests deeper down
    /// teach later spells.
    /// </summary>
    public static List<CaveSpell> Spells(RomBuffer rom) => AllSpells(rom).Take(MaxSpells).ToList();

    /// <summary>The spell pool for <c>PickSpell</c>: per spell its hero, the spell and the first floor where a chest can
    /// teach it (level L: floor 2 x (L - 3), at least 1).</summary>
    private static byte[] SpellPool(RomBuffer rom) =>
        Spells(rom).SelectMany(s => new[] { (byte)s.Character, (byte)s.Spell, (byte)Math.Clamp(2 * (s.Level - 3), 1, 100) }).ToArray();

    public static int SpellFirstFloor(int level) => Math.Clamp(2 * (level - 3), 1, 100);

    /// <summary>Every spell the heroes learn by levelling up (level 1 and up), sorted by level.</summary>
    public static List<CaveSpell> AllSpells(RomBuffer rom)
    {
        var all = new List<CaveSpell>();
        if (SpellMemory.Installed(rom) || SpellMemory.OldBlock(rom) > 0)
        {
            var lists = SpellMemory.ReadLists(rom);
            for (int c = 0; c < 4; c++) all.AddRange(lists[c].Select(e => new CaveSpell(c, e.Spell, e.Level)));
        }
        else
            foreach (var (c, at) in new[] { (0, GameDataOffsets.HeroSpellList), (1, GameDataOffsets.LufiaSpellList), (3, GameDataOffsets.JerinSpellList) })
                for (int o = at, n = 0; n < 64 && rom.ReadByte(o) != 0xFF; o += 2, n++) all.Add(new CaveSpell(c, rom.ReadByte(o + 1), rom.ReadByte(o)));
        return all.Where(s => s.Level > 0).OrderBy(s => s.Level).ThenBy(s => s.Character).ToList();
    }

    private static readonly string[] HeroTags = { "[Hero]", "[Lufia]", "[Aguro]", "[Jerin]" };

    /// <summary>Event for spell chest k: if the hero knows the spell, a potion; else they learn it.</summary>
    private static byte[] SpellEvent(RomBuffer rom, CaveSpell s, int potion)
    {
        byte[] Text(string t)
        {
            var b = TextDecoder.Encode(t, rom).ToList();
            if (b.Count > 0 && b[^1] == 0x00) b.RemoveAt(b.Count - 1);
            b.Add(0x00);   // end of the event
            return b.ToArray();
        }
        var learn = new List<byte> { 0x3D, (byte)s.Character, (byte)s.Spell, 0x0D };
        string spell = Maps.EventCommands.SpellName(rom, s.Spell), item = Maps.MapSetupScript.ItemName(rom, potion).Replace('@', ' ').Trim();
        learn.AddRange(Text($"{HeroTags[s.Character]} learned {spell}!"));
        int known = 5 + learn.Count;
        var ev = new List<byte> { 0x45, (byte)s.Character, (byte)s.Spell, (byte)known, (byte)(known >> 8) };
        ev.AddRange(learn);
        ev.AddRange(new byte[] { 0x3E, (byte)potion, 0x01, 0x0D });
        ev.AddRange(Text($"{HeroTags[s.Character]} knows {spell} already.[NL]Found {item} instead."));
        return ev.ToArray();
    }
}
