using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II event scripts (US ROM, read from the game's code 2026-10-23; every map's scripts parse with these
/// lengths). The interpreter ($80:9D32) reads a command byte and jumps through the table at $80:CA14 (commands
/// 00-CC). Commands 08, 13 xx, 61-67, 9E xx and 10 (a choice) switch to text: there 10-7F are letters, 80-FF
/// dictionary words, 00-0F still commands (01 back to commands, 00 the end, 0B the choice cursor). Pointers are u16
/// offsets from the map's event block ("PH"), and from $87:8000 (file 0x38000) in a map's start-up script.
/// Flags: 256 bits at $7E:077E; variables: bytes at $7E:079E.
/// </summary>
public static class L2Script
{
    public const int InitBase = 0x38000;

    /// <summary>Argument bytes (-1: worked out from the bytes), meaning, and how sure (H: read from the code,
    /// M: from what it touches, L: a guess).</summary>
    public static readonly Dictionary<int, (int Args, string Name, char Sure)> Commands = new()
    {
        [0x00] = (0, "end (return from 05/06/09 call if active; else close text, restore camera, end script)", 'H'),
        [0x01] = (0, "end text mode, wait for button, continue", 'H'),
        [0x02] = (-1, "crash (BRK)", 'H'),
        [0x03] = (0, "newline", 'H'),
        [0x04] = (1, "text voice sound (xx<80) or pause (xx&7F)*10 frames", 'H'),
        [0x05] = (1, "dictionary word xx (call)", 'H'),
        [0x06] = (1, "dictionary word xx+0x100 (call)", 'H'),
        [0x07] = (1, "glyph 0x300+xx", 'M'),
        [0x08] = (0, "open text mode, current speaker (the character talked to)", 'H'),
        [0x09] = (0, "player name (text)", 'H'),
        [0x0A] = (2, "text back-reference (section 2)", 'H'),
        [0x0B] = (0, "choice cursor (text)", 'H'),
        [0x0C] = (0, "? ! , . + newline", 'H'),
        [0x0D] = (0, "? ! , . + newline", 'H'),
        [0x0E] = (0, "? ! , . + newline", 'H'),
        [0x0F] = (0, "? ! , . + newline", 'H'),
        [0x10] = (-1, "choice: n, n pointers, then text", 'H'),
        [0x11] = (-1, "crash", 'H'),
        [0x12] = (-1, "var v, n, n pointers: jump to p[min(var[v]++, n-1)]", 'H'),
        [0x13] = (1, "open text mode, speaker = actor xx", 'H'),
        [0x14] = (-1, "condition list until FF (section 5)", 'H'),
        [0x15] = (3, "flag, p: jump if flag set", 'H'),
        [0x16] = (3, "change map: map ($05AE), $05B8, $05B0 (script replaced)", 'M'),
        [0x17] = (1, "inn: music/fade/rest sequence, then pay xx gold", 'H'),
        [0x18] = (1, "shop xx", 'H'),
        [0x19] = (0, "church menu (menu 2 via $83:AFEA)", 'H'),
        [0x1A] = (1, "set flag", 'H'),
        [0x1B] = (1, "clear flag", 'H'),
        [0x1C] = (2, "jump p", 'H'),
        [0x1D] = (2, "var[v] = value", 'H'),
        [0x1E] = (2, "var[v] += value", 'H'),
        [0x1F] = (2, "var[v] -= value", 'H'),
        [0x20] = (2, "give item xx (0x000-0x0FF), count yy (with 'got' message per item)", 'H'),
        [0x21] = (2, "give item xx+0x100, count yy", 'H'),
        [0x22] = (2, "add gold u16", 'H'),
        [0x23] = (2, "party member xx learns spell yy ($82:FD3D)", 'M'),
        [0x24] = (2, "remove item xx, count yy ($82:FA57)", 'H'),
        [0x25] = (2, "remove item xx+0x100, count yy", 'H'),
        [0x26] = (2, "remove gold u16", 'H'),
        [0x27] = (2, "no-op (reads 2 bytes)", 'H'),
        [0x28] = (0, "$81:F629/F5ED/F60B (unknown)", 'L'),
        [0x29] = (3, "member xx: raise stat yy by zz (tables $85:9EBA, $80:A596/A5A4)", 'M'),
        [0x2A] = (3, "no-op", 'H'),
        [0x2B] = (1, "party member xx joins ($0926)", 'H'),
        [0x2C] = (1, "party member xx leaves", 'H'),
        [0x2D] = (1, "show actor xx (clear hidden bit; ids 10-4F also persistent)", 'H'),
        [0x2E] = (1, "hide actor xx (set hidden bit; ids 10-4F persistent)", 'H'),
        [0x2F] = (3, "place actor xx at tile x, y ($80:A74D)", 'M'),
        [0x30] = (2, "place actor xx at preset point yy", 'M'),
        [0x31] = (2, "actor xx turns to direction yy (table $80:A7B6)", 'H'),
        [0x32] = (2, "release actor xx (FF = all) with movement mode yy ($070A)", 'M'),
        [0x33] = (2, "path xx (list +0x12), actor yy walks it (waits for a free walker)", 'H'),
        [0x34] = (2, "actor xx map sprite yy", 'H'),
        [0x35] = (3, "persistent NPC xx (10-4F) moves to map yy, spot zz (hidden now if on this map)", 'M'),
        [0x36] = (1, "wait until actor xx stops moving", 'H'),
        [0x37] = (1, "wait xx frames", 'H'),
        [0x38] = (1, "wait xx * 60 frames", 'H'),
        [0x39] = (0, "no-op", 'H'),
        [0x3A] = (2, "scroll screen to tile x, y ($80:B374)", 'M'),
        [0x3B] = (1, "scroll screen to preset point xx", 'M'),
        [0x3C] = (0, "party gathers behind the leader (waits until done)", 'M'),
        [0x3D] = (1, "party gathers instantly (byte unused)", 'M'),
        [0x3E] = (0, "$0583 = 80", 'L'),
        [0x3F] = (0, "$0583 = 0F", 'L'),
        [0x40] = (0, "$83:8E4C", 'L'),
        [0x41] = (-1, "camera: 0n 00 back to party (speed n), dn yy d=1-4 move yy tiles (direction table $80:B5BE), 6n x y to tile, 7n pp to preset, xx FF wait for camera", 'H'),
        [0x42] = (0, "= 00", 'H'),
        [0x43] = (2, "effect between areas xx and yy (list +0x14), $83:8B6A", 'L'),
        [0x44] = (3, "word[$0320 + 2xx] += u16", 'M'),
        [0x45] = (3, "word[$0320 + 2xx] -= u16", 'M'),
        [0x46] = (3, "word[$0320 + 2xx] = u16", 'M'),
        [0x47] = (1, "text box style ($1255; 10 = thought, no voice)", 'M'),
        [0x48] = (2, "text window position: column xx, row yy ($125D)", 'M'),
        [0x49] = (1, "text auto-advance after xx frames (0 = wait for button)", 'M'),
        [0x4A] = (1, "$09AD (text appearance?)", 'L'),
        [0x4B] = (1, "play music xx (restarts)", 'H'),
        [0x4C] = (1, "play sound xx ($84:8775)", 'H'),
        [0x4D] = (1, "wait until APU port 0 >= xx (music sync)", 'M'),
        [0x4E] = (0, "wait while APU status bit 1", 'L'),
        [0x4F] = (1, "no-op", 'H'),
        [0x50] = (0, "text voice off ($1260 = 0)", 'M'),
        [0x51] = (0, "text voice from speaker", 'M'),
        [0x52] = (1, "text voice = xx", 'M'),
        [0x53] = (1, "script battle xx (table $97:C53D)", 'H'),
        [0x54] = (1, "door xx ($83:F559, A = xx<<8)", 'L'),
        [0x55] = (1, "door xx, variant 0x40", 'L'),
        [0x56] = (0, "stop earthquake ($80:B97E)", 'M'),
        [0x57] = (0, "wait until screen effects done ($1261 = 0)", 'M'),
        [0x58] = (1, "fade, target 0F; low nibble -> $7FD08F, high -> $7FD090", 'M'),
        [0x59] = (1, "fade, target 00; same nibbles", 'M'),
        [0x5A] = (3, "earthquake (3 parameters -> $7FD07E-80)", 'M'),
        [0x5B] = (-1, "sub xx (table $80:BA55): 00 y, 01 y (bank $86 sequence), 02 y, 03 = reset (JML $80:8000, 1 arg), 04 y, 05 y, 06 = hang, 07 y, 08 y", 'M'),
        [0x5C] = (-1, "crash", 'H'),
        [0x5D] = (-1, "crash", 'H'),
        [0x5E] = (1, "use exit/warp xx of the map (list +0x06)", 'M'),
        [0x5F] = (1, "no-op", 'H'),
        [0x60] = (2, "write PPU register $2100 + xx = yy", 'H'),
        [0x61] = (0, "speaker 0,1,2,3,5,4,6 and open text mode", 'H'),
        [0x62] = (0, "speaker 0,1,2,3,5,4,6 and open text mode", 'H'),
        [0x63] = (0, "speaker 0,1,2,3,5,4,6 and open text mode", 'H'),
        [0x64] = (0, "speaker 0,1,2,3,5,4,6 and open text mode", 'H'),
        [0x65] = (0, "speaker 0,1,2,3,5,4,6 and open text mode", 'H'),
        [0x66] = (0, "speaker 0,1,2,3,5,4,6 and open text mode", 'H'),
        [0x67] = (0, "speaker 0,1,2,3,5,4,6 and open text mode", 'H'),
        [0x68] = (2, "create actor: id xx, map sprite yy (skipped if $05B3 bit 4)", 'H'),
        [0x69] = (1, "map mode ($09A7)", 'H'),
        [0x6A] = (3, "flag, p: jump if flag clear", 'H'),
        [0x6B] = (0, "refresh leader/camera ($80:ED9C)", 'L'),
        [0x6C] = (3, "actor xx pixel offset x, y (signed; y - 16)", 'H'),
        [0x6D] = (-1, "credits text: u16 tilemap offset, bytes until 00 (table $80:B13E)", 'H'),
        [0x6E] = (-1, "same, 2-row font (table $80:B1D7)", 'H'),
        [0x6F] = (1, "change music xx", 'M'),
        [0x70] = (0, "fade music out (60 frames)", 'M'),
        [0x71] = (0, "wait for music fade", 'M'),
        [0x72] = (0, "stop music", 'M'),
        [0x73] = (2, "animation xx at preset point yy", 'H'),
        [0x74] = (1, "battle background", 'H'),
        [0x75] = (0, "scroll screen to the leader", 'M'),
        [0x76] = (0, "wait for fade/screen effect", 'M'),
        [0x77] = (1, "hold actor xx (flag 01)", 'M'),
        [0x78] = (0, "reset NPCs 5-27 (mode 9)", 'L'),
        [0x79] = (0, "wait until no visible actor moves", 'M'),
        [0x7A] = (2, "actor xx sprite frame yy ($066A)", 'H'),
        [0x7B] = (2, "create actor xx, sprite yy (variant)", 'H'),
        [0x7C] = (1, "ship: mode ($09DF), position mode ($09EE), on map ($09E0), party in ship ($09E1), sprite ($09E2)", 'M'),
        [0x7D] = (1, "ship: mode ($09DF), position mode ($09EE), on map ($09E0), party in ship ($09E1), sprite ($09E2)", 'M'),
        [0x7E] = (1, "ship: mode ($09DF), position mode ($09EE), on map ($09E0), party in ship ($09E1), sprite ($09E2)", 'M'),
        [0x7F] = (1, "ship: mode ($09DF), position mode ($09EE), on map ($09E0), party in ship ($09E1), sprite ($09E2)", 'M'),
        [0x80] = (1, "ship: mode ($09DF), position mode ($09EE), on map ($09E0), party in ship ($09E1), sprite ($09E2)", 'M'),
        [0x81] = (1, "capsule monster xx joins (message $30 = 3)", 'M'),
        [0x82] = (0, "$80:F821 + $83:8E4C", 'L'),
        [0x83] = (1, "use exit xx (variant, sets $05B3 bit 5)", 'M'),
        [0x84] = (0, "halt (never advances)", 'H'),
        [0x85] = (2, "world position = preset xx, vehicle/facing nibble yy ($05B0-$05B2)", 'M'),
        [0x86] = (2, "actor xx action yy relative to facing ($83:D350)", 'M'),
        [0x87] = (2, "effect between areas xx, yy (variant of 43)", 'L'),
        [0x88] = (0, "= 6B", 'L'),
        [0x89] = (1, "hold actor xx when it stops (waits)", 'M'),
        [0x8A] = (0, "stop earthquake immediately", 'M'),
        [0x8B] = (3, "move actor xx by signed dx, dy pixels", 'H'),
        [0x8C] = (3, "actor xx movement params ($7FE5CE, $7FE5A6)", 'L'),
        [0x8D] = (6, "object/projectile: actor, preset, 4 bytes, speed 3C", 'L'),
        [0x8E] = (3, "effect object ($83:C6AA, type 0600)", 'L'),
        [0x8F] = (3, "effect object (type 06)", 'L'),
        [0x90] = (3, "effect object (type 30)", 'L'),
        [0x91] = (2, "flag FF = (u24 $0B55 >= u16)", 'M'),
        [0x92] = (1, "flag FF = $83:8AC9(xx)", 'L'),
        [0x93] = (3, "colour fade (signed step)", 'L'),
        [0x94] = (3, "screen tint (colour math $2131 = 93/53/D3)", 'M'),
        [0x95] = (3, "screen tint (colour math $2131 = 93/53/D3)", 'M'),
        [0x96] = (3, "screen tint (colour math $2131 = 93/53/D3)", 'M'),
        [0x97] = (0, "end tint", 'M'),
        [0x98] = (2, "actor xx to preset yy (action 62)", 'M'),
        [0x99] = (1, "actor action 61", 'L'),
        [0x9A] = (1, "remove effect objects with id xx", 'L'),
        [0x9B] = (5, "colour fade (two BGR15 colours + 1)", 'L'),
        [0x9C] = (3, "area xx + 2 bytes ($80:D533)", 'L'),
        [0x9D] = (1, "actor action 63", 'L'),
        [0x9E] = (1, "open text mode, window at row xx, centred on the text width", 'H'),
        [0x9F] = (3, "effect object (as 8E, flag 04)", 'L'),
        [0xA0] = (6, "as 8D, actor-to-actor", 'L'),
        [0xA1] = (2, "wait for effect object xx, field yy", 'M'),
        [0xA2] = (1, "wait until effect object xx is gone", 'M'),
        [0xA3] = (2, "actor xx action yy ($83:D350)", 'M'),
        [0xA4] = (0, "pixelate screen", 'M'),
        [0xA5] = (0, "reset stack, JML $83:ACEF (back to the field loop)", 'M'),
        [0xA6] = (0, "rebuild actors from $7EF600 (if $05B6 bit 0)", 'L'),
        [0xA7] = (1, "00: set $09A6 bit 0, else clear", 'L'),
        [0xA8] = (1, "01: $84:890B, else $84:878A", 'L'),
        [0xA9] = (2, "xx < 2: $09E6 = yy; else $05C0/$05C1 = xx, yy", 'L'),
        [0xAA] = (0, "clear $7FD100-$7FD103", 'L'),
        [0xAB] = (1, "flag FF = (Dragon egg count >= xx) (item 0x2B)", 'H'),
        [0xAC] = (2, "preset xx, yy -> $05BD-$05BF", 'L'),
        [0xAD] = (0, "$8E:BCAD", 'L'),
        [0xAE] = (1, "FF: clear $05B6 bit 1, else set", 'L'),
        [0xAF] = (2, "actor xx $7FE35E = yy, $0736 bit 7 set", 'L'),
        [0xB0] = (1, "actor xx clear that bit", 'L'),
        [0xB1] = (2, "$80:AD57 on actors xx, yy", 'L'),
        [0xB2] = (2, "same, both orders", 'L'),
        [0xB3] = (1, "$80:AD39", 'L'),
        [0xB4] = (3, "as 43 with mode zz (stores Y, yields)", 'L'),
        [0xB5] = (0, "$089D = 0, $085D = FF", 'L'),
        [0xB6] = (2, "actor xx $7FE316 = yy", 'L'),
        [0xB7] = (1, "actor xx $8E:BAF6/BAE7", 'L'),
        [0xB8] = (0, "next message stays up while the player walks on", 'M'),
        [0xB9] = (1, "party member xx: $BC = FF unless status bit 2", 'L'),
        [0xBA] = (1, "party member xx $82:FD15", 'L'),
        [0xBB] = (0, "menu/message 7 (report)", 'M'),
        [0xBC] = (0, "$83:8C8A (menu), then continue", 'L'),
        [0xBD] = (2, "create effect actor (id - 0x48) xx, target yy", 'L'),
        [0xBE] = (1, "= 77", 'M'),
        [0xBF] = (3, "actor zz action xx toward actor yy", 'L'),
        [0xC0] = (3, "as 8B, then snap tile position", 'M'),
        [0xC1] = (2, "$7FD4F3/F4", 'L'),
        [0xC2] = (6, "as 8D, speed 4F", 'L'),
        [0xC3] = (6, "as A0, speed 4F", 'L'),
        [0xC4] = (2, "scroll to tile x, y (mode 2)", 'M'),
        [0xC5] = (0, "clear tint/effect bits", 'L'),
        [0xC6] = (2, "animation xx at actor yy", 'M'),
        [0xC7] = (0, "screen refresh with NMI off", 'L'),
        [0xC8] = (7, "as 8D with speed byte", 'L'),
        [0xC9] = (7, "as A0 with speed byte", 'L'),
        [0xCA] = (3, "$7FD020 + xx = yy, zz", 'L'),
        [0xCB] = (1, "$0B52", 'L'),
        [0xCC] = (0, "$0B62 = max", 'L'),
    };

    /// <summary>A command or a run of text in a script.</summary>
    public sealed record Op(int At, int Length, string Text, bool IsText = false);

    private static readonly HashSet<int> TextStart = new() { 0x08, 0x13, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x9E, 0x10 };
    // (not B8: it makes the next message stay up while the player walks on, and that message follows it)
    private static readonly HashSet<int> Ends = new() { 0x00, 0x42, 0x84, 0x16, 0x5E, 0x83, 0xA5, 0x02, 0x11, 0x5C, 0x5D, 0x18, 0x19, 0x81, 0xBC };

    private static int Condition14(RomBuffer rom, int p)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            int c = rom.ReadByte(p++);
            if (c == 0xFF) return p;
            if ((c & 0xC0) == 0xC0) p += (c & 0xF8) switch { 0xC0 => 4, 0xC8 or 0xD0 or 0xD8 => 3, _ => 2 };
            else p += (c & 0x30) == 0 ? 1 : 2;
        }
        return p;
    }

    /// <summary>The length of the command at <paramref name="p"/> (in command mode).</summary>
    public static int Length(RomBuffer rom, int p)
    {
        int c = rom.ReadByte(p);
        switch (c)
        {
            case 0x10: return 2 + 2 * rom.ReadByte(p + 1);
            case 0x12: return 3 + 2 * rom.ReadByte(p + 2);
            case 0x14: return Condition14(rom, p + 1) - p;
            case 0x41: return (rom.ReadByte(p + 1) & 0xF0) == 0x60 && rom.ReadByte(p + 2) != 0xFF ? 4 : 3;
            case 0x5B: return rom.ReadByte(p + 1) is 3 or 6 ? 2 : 3;
            case 0x6D or 0x6E: { int q = p + 3; while (q < rom.Length && rom.ReadByte(q) != 0) q++; return q + 1 - p; }
        }
        return Commands.TryGetValue(c, out var d) && d.Args >= 0 ? 1 + d.Args : 1;
    }

    /// <summary>Pointer targets of the command at <paramref name="p"/> (file offsets), and whether running carries on
    /// after it.</summary>
    public static (List<int> Targets, bool FallsThrough) Flow(RomBuffer rom, int p, int jumpBase)
    {
        var t = new List<int>();
        int c = rom.ReadByte(p);
        int Ptr(int at) => jumpBase + rom.ReadUInt16Le(at);
        switch (c)
        {
            case 0x10: for (int i = 0; i < rom.ReadByte(p + 1); i++) t.Add(Ptr(p + 2 + i * 2)); break;
            case 0x12: for (int i = 0; i < rom.ReadByte(p + 2); i++) t.Add(Ptr(p + 3 + i * 2)); return (t, false);
            case 0x15 or 0x6A: t.Add(Ptr(p + 2)); break;
            case 0x1C: t.Add(Ptr(p + 1)); return (t, false);
            case 0x14:
                for (int q = p + 1, guard = 0; guard < 64; guard++)
                {
                    int k = rom.ReadByte(q++);
                    if (k == 0xFF) break;
                    if ((k & 0xC0) == 0xC0) q += (k & 0xF8) switch { 0xC0 => 4, 0xC8 or 0xD0 or 0xD8 => 3, _ => 2 };
                    else if ((k & 0x30) is 0x20 or 0x30) { t.Add(Ptr(q)); q += 2; }
                    else q += (k & 0x30) == 0 ? 1 : 2;
                }
                break;
            case 0x5B when rom.ReadByte(p + 1) is 3 or 6: return (t, false);
        }
        return (t, !Ends.Contains(c) && Commands.ContainsKey(c));
    }

    /// <summary>Reads text from <paramref name="p"/> (text mode) up to the code that leaves it; returns its end.</summary>
    private static int TextEnd(RomBuffer rom, int p, out byte last)
    {
        last = 0;
        for (int guard = 0; guard < 2000 && p < rom.Length; guard++)
        {
            byte b = rom.ReadByte(p);
            if (b >= 0x10) { p++; continue; }
            if (b is 0x00 or 0x01 or 0x0B) { last = b; return p + 1; }
            p += b switch { 0x04 or 0x05 or 0x06 or 0x07 => 2, 0x0A => 3, _ => 1 };
        }
        return p;
    }

    /// <summary>Every command and text reachable from <paramref name="starts"/>, in address order.</summary>
    public static List<Op> Walk(RomBuffer rom, IEnumerable<int> starts, int jumpBase, Func<int, string>? describe = null)
    {
        var ops = new SortedDictionary<int, Op>();
        var todo = new Stack<int>(starts.Where(s => s > 0 && s < rom.Length));
        var seen = new HashSet<int>();
        while (todo.Count > 0)
        {
            int p = todo.Pop();
            for (int guard = 0; guard < 3000 && p > 0 && p < rom.Length && seen.Add(p); guard++)
            {
                int c = rom.ReadByte(p);
                int len = Length(rom, p);
                var (targets, falls) = Flow(rom, p, jumpBase);
                foreach (var t in targets) todo.Push(t);
                ops[p] = new Op(p, len, Describe(rom, p, len, jumpBase));
                p += len;
                if (!falls) break;
                if (!TextStart.Contains(c)) continue;
                int end = TextEnd(rom, p, out byte last);
                ops[p] = new Op(p, end - p, L2Text.Decode(rom, p, out _), true);
                p = end;
                if (last == 0) break;
            }
        }
        return ops.Values.ToList();
    }

    /// <summary>One command as text: its bytes and meaning.</summary>
    public static string Describe(RomBuffer rom, int p, int len, int jumpBase)
    {
        int c = rom.ReadByte(p);
        var a = rom.ReadBytes(p + 1, len - 1);
        string bytes = string.Join(" ", a.Select(b => b.ToString("X2")));
        string name = Commands.TryGetValue(c, out var d) ? d.Name : "(not a command)";
        var n = Names.For(rom);
        string Ptr(int i) => $"{jumpBase + (a[i] | a[i + 1] << 8):X6}";
        int W(int i) => a[i] | a[i + 1] << 8;
        string Item(int id) => id < n.Items.Length ? n.Items[id] : $"item {id:X3}";
        string Hero(int id) => id < L2Characters.Names.Length ? L2Characters.Names[id] : $"hero {id:X2}";
        string Actor(int id) => id < 7 ? $"actor {id:X2} ({L2Characters.Names[id]})" : id == 0xFF ? "all actors" : $"actor {id:X2}";
        string Map(int id) => id < n.Maps.Length ? $"map {id:X2} {n.Maps[id]}" : $"map {id:X2}";
        string what = c switch
        {
            0x08 => "text, spoken by the current speaker (the one talked to in a talk, else the last one named)",
            0x10 => $"choice of {a[0]}: " + string.Join(", ", Enumerable.Range(0, a[0]).Select(i => Ptr(1 + i * 2))),
            0x12 => $"by variable {a[0]:X2} (then +1), jump to one of: " + string.Join(", ", Enumerable.Range(0, a[1]).Select(i => Ptr(2 + i * 2))),
            0x13 => $"text, spoken by {Actor(a[0])}",
            0x14 => "if " + Conditions(a, jumpBase, n),
            0x15 => $"if flag {a[0]:X2} is set, jump to {Ptr(1)}",
            0x16 => $"go to {Map(a[0])} ({a[1]}, {a[2]})",
            0x17 => $"inn ({a[0]} gold)",
            0x18 => $"shop {a[0]:X2}",
            0x1A => $"set flag {a[0]:X2}",
            0x1B => $"clear flag {a[0]:X2}",
            0x1C => $"jump to {Ptr(0)}",
            0x1D => $"variable {a[0]:X2} = {a[1]}",
            0x1E => $"variable {a[0]:X2} += {a[1]}",
            0x1F => $"variable {a[0]:X2} -= {a[1]}",
            0x20 => $"give {Item(a[0])} x{a[1]}",
            0x21 => $"give {Item(0x100 + a[0])} x{a[1]}",
            0x22 => $"give {W(0)} gold",
            0x23 => $"{Hero(a[0])} learns {(a[1] < n.Spells.Length ? n.Spells[a[1]] : $"spell {a[1]:X2}")}",
            0x24 => $"take {Item(a[0])} x{a[1]}",
            0x25 => $"take {Item(0x100 + a[0])} x{a[1]}",
            0x26 => $"take {W(0)} gold",
            0x2B => $"{Hero(a[0])} joins the party",
            0x2C => $"{Hero(a[0])} leaves the party",
            0x2D => $"show {Actor(a[0])}",
            0x2E => $"hide {Actor(a[0])}",
            0x2F => $"place {Actor(a[0])} at ({a[1]}, {a[2]})",
            0x30 => $"place {Actor(a[0])} at preset point {a[1]:X2}",
            0x31 => $"{Actor(a[0])} turns {(a[1] is >= 1 and <= 4 ? new[] { "up", "down", "left", "right" }[a[1] - 1] : $"to {a[1]:X2}")}",
            0x33 => $"{Actor(a[1])} walks path {a[0]:X2}",
            0x34 => $"{Actor(a[0])} uses map sprite {a[1]:X2}",
            0x36 => $"wait until {Actor(a[0])} stops",
            0x37 => $"wait {a[0]} frames",
            0x38 => $"wait {a[0]} seconds",
            0x4B => $"play music {a[0]:X2}{(a[0] < n.Songs.Length ? " " + n.Songs[a[0]] : "")}",
            0x6F => $"change music to {a[0]:X2}{(a[0] < n.Songs.Length ? " " + n.Songs[a[0]] : "")}",
            0x4C => $"sound effect {a[0]:X2}",
            0x53 => $"battle {a[0]:X2}",
            0x5E => $"use exit {a[0]:X2}",
            >= 0x61 and <= 0x67 => $"text, spoken by {Actor(new[] { 0, 1, 2, 3, 5, 4, 6 }[c - 0x61])}",
            0x68 or 0x7B => $"create actor {a[0]:X2} with map sprite {a[1]:X2}",
            0x6A => $"if flag {a[0]:X2} is clear, jump to {Ptr(1)}",
            0x81 => $"capsule monster {a[0]:X2} joins",
            0x9E => $"text (narration), at row {a[0]}",
            _ => name,
        };
        return $"{c:X2} {bytes,-24} {what}";
    }

    /// <summary>Command 14's condition list written out.</summary>
    private static string Conditions(byte[] a, int jumpBase, Names n)
    {
        var parts = new List<string>();
        string[] cmp = { "==", "!=", ">=", "<=", ">", "<", "<", "<" };
        int i = 0;
        while (i < a.Length && a[i] != 0xFF)
        {
            int c = a[i++];
            string join = (c & 0xC0) switch { 0x40 => "or ", 0x80 => "and ", _ => "" };
            if ((c & 0xC0) == 0xC0)
            {
                int g = c & 0xF8;
                string op = cmp[c & 7];
                switch (g)
                {
                    case 0xC0: { int item = a[i] | a[i + 1] << 8, v = a[i + 2] | a[i + 3] << 8; parts.Add($"count of {(item < n.Items.Length ? n.Items[item] : $"item {item:X3}")} {op} {v}"); i += 4; break; }
                    case 0xC8: parts.Add($"{L2Characters.Names.ElementAtOrDefault(a[i]) ?? "hero"} HP {op} {a[i + 1] | a[i + 2] << 8}"); i += 3; break;
                    case 0xD0: parts.Add($"{L2Characters.Names.ElementAtOrDefault(a[i]) ?? "hero"} MP {op} {a[i + 1] | a[i + 2] << 8}"); i += 3; break;
                    case 0xD8: parts.Add("(unused test)"); i += 3; break;
                    case 0xE0: parts.Add($"{L2Characters.Names.ElementAtOrDefault(a[i]) ?? "hero"} knows spell {a[i + 1]:X2}"); i += 2; break;
                    case 0xE8: parts.Add($"{L2Characters.Names.ElementAtOrDefault(a[i]) ?? "hero"} level {op} {a[i + 1]}"); i += 2; break;
                    case 0xF0: parts.Add($"vehicle {op} {a[i + 1]}"); i += 2; break;
                    default: parts.Add($"actor {a[i]:X2} flag 20"); i += 2; break;
                }
                continue;
            }
            switch (c & 0x30)
            {
                case 0x00: parts.Add($"{join}flag {a[i]:X2} is {((c & 1) == 0 ? "set" : "clear")}"); i += 1; break;
                case 0x10: parts.Add($"{join}variable {a[i]:X2} {cmp[c & 7]} {a[i + 1]}"); i += 2; break;
                case 0x20: parts.Add($"then jump to {jumpBase + (a[i] | a[i + 1] << 8):X6}"); i += 2; break;
                default: parts.Add($"else jump to {jumpBase + (a[i] | a[i + 1] << 8):X6}"); i += 2; break;
            }
        }
        return string.Join(" ", parts);
    }

    /// <summary>Songs are packed files 00-3E ($80:941A); each starts "S2C", tempo, then a 12-letter name.</summary>
    public const int SongCount = 0x3F;

    public static string SongName(RomBuffer rom, int song)
    {
        try
        {
            var f = L2Rom.ReadFile(rom, song);
            for (int k = 0; k + 16 <= f.Length && k < 8; k++)
                if (f[k] == 'S' && f[k + 1] == '2' && f[k + 2] == 'C') return Encoding.ASCII.GetString(f, k + 4, 12).Trim(' ', '\0');
        }
        catch (Exception) { }
        return "";
    }

    /// <summary>Names used in listings, read once per ROM.</summary>
    private sealed class Names
    {
        public string[] Items = Array.Empty<string>(), Spells = Array.Empty<string>(), Maps = Array.Empty<string>(), Songs = Array.Empty<string>();
        private static Names? _last;
        private static RomBuffer? _rom;

        public static Names For(RomBuffer rom)
        {
            if (_last != null && ReferenceEquals(_rom, rom)) return _last;
            var n = new Names
            {
                Items = L2Battle.Items(rom).Select(i => i.Name.Trim()).ToArray(),
                Spells = L2Battle.Spells(rom).Select(s => s.Name.Trim()).ToArray(),
                Maps = Enumerable.Range(0, L2Maps.Count).Select(m => L2Maps.Name(rom, m)).ToArray(),
                Songs = Enumerable.Range(0, SongCount).Select(i => SongName(rom, i)).ToArray(),
            };
            _rom = rom; _last = n;
            return n;
        }
    }

    /// <summary>A map's scripts: the start-up script, then the event block's load, talk and trigger lists.</summary>
    public static List<(string Title, List<Op> Ops)> MapScripts(RomBuffer rom, int map)
    {
        var list = new List<(string, List<Op>)>();
        int init = L2EventBlocks.InitScript(rom, map);
        list.Add(("Start-up script", Walk(rom, new[] { init }, L2EventBlocks.InitBase(init))));
        int block = L2Events.Block(rom, map);
        if (block < 0) return list;
        foreach (var (kind, id, off) in L2Events.Entries(rom, block))
            list.Add(($"{kind switch { "talk" => "Talk to", "load" => "On load", "scene" => "Scene", "trigger" => "Trigger", _ => kind }} {id:X2}", Walk(rom, new[] { block + off }, block)));
        return list;
    }
}
