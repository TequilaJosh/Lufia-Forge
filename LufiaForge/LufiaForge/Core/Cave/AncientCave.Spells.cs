using LufiaForge.Core.Battle;
using LufiaForge.Modules.GameData;
using LufiaForge.Modules.TextEditor;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    public const int SpellMarkers = 16, FirstMarker = 0xF0, FirstSpellEvent = 4;

    /// <summary>
    /// Spell chests: in the cave a chest item F0-FF (key items, which the cave never gives) is spell chest k = item - F0.
    /// Opening one marks it opened as usual, then runs the cave map's event 4 + k instead of the "found" message
    /// (map 63's events): that event teaches the spell, or gives a potion when the hero knows it already.
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
            SEC
            SBC.b #$F0-4            ; event 4 + k
            STA !SPELLEVENT
            JML $0186C0             ; opened flag, then the message (MsgHook runs our event instead)
        .normal
            LDA $04
            CMP.b #$FF
            BEQ +
            JML $0186A9             ; an item: as before
        +   JML $0186CD             ; empty

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
    public static List<CaveSpell> Spells(RomBuffer rom)
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
        var learned = all.Where(s => s.Level > 0).OrderBy(s => s.Level).ThenBy(s => s.Character).ToList();
        if (learned.Count == 0) return new();
        return Enumerable.Range(0, SpellMarkers).Select(k => learned[Math.Min(learned.Count - 1, k * learned.Count / SpellMarkers)]).ToList();
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
