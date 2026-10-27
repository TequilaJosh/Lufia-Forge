using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// New spells, spell animations, new IP attacks, IP attack names of any length and IP attacks with an effect of their
/// own (2026-10-25). The game keeps its 40 spells and their effect scripts in bank $95 ($95:FA5B), the 141 IP effect
/// scripts right before them ($95:F1EF) and the 168 IP attack records in bank $84 ($84:8F10), all packed with no room
/// left; a spell's battle animation is its number + $12 ($81:A9BA and $81:AFA3). Lufia Forge moves them once
/// (<see cref="Install"/>), changing only where the game reads them:
/// <list type="bullet">
/// <item>Block A (the added space, "LF2MG"): the spells' table (256 entries) and records with their scripts, an
/// animation byte per spell, the IP effects' table (256 entries) and scripts. The spell readers keep their code: the
/// table read at $81:F450 (LDA $95FA5B,X / ADC #$FA5B) points at the new table (ADC #0), and the bank of the seven
/// LDA #$95 that go with a spell record becomes the block's. $81:F414 (the spell loader) set DB = $95 before reading
/// $0A0B / $0A0D through it, which only works in a bank with RAM below $2000, so its first 11 bytes are reordered to read
/// those first. The two "number + $12" become a JSL to a routine that reads the spell's animation byte. The IP effects'
/// table read ($81:F46D) and bank ($81:AB59) likewise.</item>
/// <item>Block B ("LF2IP"): the IP attack table (255 attacks) and records, in $97:8000-$97:B0A0, the space the
/// descriptions leave when they move (<see cref="L2Descriptions.Move"/>). The records must stay in a bank with RAM below
/// $2000: the code that uses an attack ($81:AB2A) and the one that shows its name ($85:9585) set DB to the record's bank
/// and reach RAM through it. The table reads ($81:F463, $82:FF15) and the record bank (LDA #$84 at $81:AB2A, $85:9585,
/// $82:FEFE; LDA $84000n,X at $81:C1C1, C1CA, C1D6) change.</item>
/// </list>
/// Every place is checked to hold the game's bytes before anything changes. Scripts that Lufia Forge's script table
/// (<see cref="L2AiScripts"/>) keys by their address follow the move.
/// </summary>
public static class L2Magic
{
    // ── the game's places (file offsets) ─────────────────────────────────────
    private const int SpellTableRead = 0xF450, SpellTableAdc = 0xF455, SpellLoader = 0xF414;
    private static readonly byte[] SpellTableReadOrig = { 0xBF, 0x5B, 0xFA, 0x95 }, SpellAdcOrig = { 0x69, 0x5B, 0xFA };
    private static readonly byte[] SpellLoaderOrig = { 0x8B, 0xA9, 0x95, 0x48, 0xAB, 0x20, 0x46, 0xF4, 0xAC, 0x0D, 0x0A };
    /// <summary>LDA #$95 that set the bank of a spell record or its script ($81:F3EB is in a routine nothing calls).</summary>
    private static readonly int[] SpellBankSites = { 0xA9CA, 0xAFB3, 0xF31E, 0xF394, 0xF3EB, 0xF3FB, 0xF40B };
    private static readonly int[] AnimSites = { 0xA9BA, 0xAFA3 };
    private static readonly byte[] AnimOrig = { 0x18, 0x69, 0x12, 0x8D, 0x62, 0x12 };   // CLC / ADC #$12 / STA $1262
    private const int EffTableRead = 0xF46D, EffTableAdc = 0xF472, EffBankSite = 0xAB59;
    private static readonly byte[] EffTableReadOrig = { 0xBF, 0xEF, 0xF1, 0x95 }, EffAdcOrig = { 0x69, 0xEF, 0xF1 };
    private static readonly int[] IpTableReads = { 0xF463, 0x17F15 };
    private static readonly byte[] IpTableReadOrig = { 0xBF, 0x10, 0x8F, 0x84 };
    private static readonly int[] IpBankSites = { 0xAB2A, 0x29585, 0x17EFE };   // LDA #$84
    private static readonly int[] IpLongSites = { 0xC1C1, 0xC1CA, 0xC1D6 };      // LDA $840003,X / $840005,X / $840006,X

    /// <summary>The game's IP effect scripts (its table's first offset / 2).</summary>
    private const int EffCountOriginal = 141;

    // ── block A ──────────────────────────────────────────────────────────────
    private const string MagicA = "LF2MG", MagicB = "LF2IP";
    private const int Version = 1;
    private const int SizeA = 0x6000, CodeAt = 0x10, AnimAt = 0x100, SpellTableAt = 0x200, EffTableAt = 0x400, HeapA = 0x600;
    // header: +5 version, +6 spell count, +8 effect count (u16), +0A next free byte (u16, from the block)
    // ── block B ($97:8000) ───────────────────────────────────────────────────
    private const int BlockBAt = L2Descriptions.Table, SizeB = L2Descriptions.DataEnd - L2Descriptions.Table;
    private const int IpTableAt = 0x10, HeapB = 0x210;
    // header: +5 version, +6 IP attack count, +8 next free byte (u16)

    public const int MaxSpells = 0xFF;     // 00-FE (FF ends a hero's spell list)
    public const int MaxIp = 0xFF;         // items name them 1-FF
    public const int MaxEffects = 0x100;
    public const int IpNameLength = 14;

    private static byte[] Bytes(int at, RomBuffer rom, int n) => at + n <= rom.Length ? rom.ReadBytes(at, n) : Array.Empty<byte>();

    /// <summary>File offset of block A, or -1.</summary>
    public static int BlockA(RomBuffer rom)
    {
        if (rom.Length <= SpellTableRead + 4 || rom.ReadByte(SpellTableRead) != 0xBF) return -1;
        int snes = (int)rom.ReadUInt24Le(SpellTableRead + 1);
        if (snes == 0x95FA5B) return -1;
        int at = L2Rom.SnesToFile(snes) - SpellTableAt;
        return at >= 0 && at + SizeA <= rom.Length && Encoding.ASCII.GetString(rom.ReadBytes(at, MagicA.Length)) == MagicA ? at : -1;
    }

    /// <summary>File offset of block B, or -1.</summary>
    public static int BlockB(RomBuffer rom)
    {
        if (rom.Length <= IpTableReads[0] + 4 || rom.ReadByte(IpTableReads[0]) != 0xBF) return -1;
        int snes = (int)rom.ReadUInt24Le(IpTableReads[0] + 1);
        if (snes == 0x848F10) return -1;
        int at = L2Rom.SnesToFile(snes) - IpTableAt;
        return at >= 0 && at + 16 <= rom.Length && Encoding.ASCII.GetString(rom.ReadBytes(at, MagicB.Length)) == MagicB ? at : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => BlockA(rom) >= 0 && BlockB(rom) >= 0;

    /// <summary>How many spells: Lufia Forge's count once it moved them, else the length of the table the game reads
    /// (40 in the US game and the fan hacks).</summary>
    public static int SpellCount(RomBuffer rom) => BlockA(rom) is var a && a >= 0 ? rom.ReadByte(a + 6) : L2Battle.SpellTableLength(rom);
    /// <summary>How many IP attacks: Lufia Forge's count once it moved them, else the length of the game's table (its
    /// records start right after it: 168 in the US game and the fan hacks).</summary>
    public static int IpCount(RomBuffer rom)
    {
        if (BlockB(rom) is var b && b >= 0) return rom.ReadByte(b + 6);
        if (rom.ReadByte(IpTableReads[0]) != 0xBF) return L2Battle.IpCountOriginal;
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(IpTableReads[0] + 1));
        int n = L2Battle.TableLength(rom, table, MaxIp, i => L2Battle.IpOffset(rom, i));
        return n > 0 ? n : L2Battle.IpCountOriginal;
    }
    public static int IpEffectCount(RomBuffer rom)
    {
        if (BlockA(rom) is var a && a >= 0) return rom.ReadUInt16Le(a + 8);
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(EffTableRead + 1));
        int n = L2Battle.TableLength(rom, table, MaxEffects, e => L2AiScripts.IpEffectBase(rom, e));
        return n > 0 ? n : EffCountOriginal;
    }

    /// <summary>What the move takes along, read the way the game reads it: each spell's record and its script, each IP
    /// effect script (from its base byte), each IP attack record. Scripts are read as steps and laid out again, so one
    /// that jumps to code elsewhere (the fan hacks do) brings that code along; one that can't be read stops the move.</summary>
    private sealed class Plan
    {
        public List<(int Old, byte[] Record, byte[] Script, int ScriptOld)> Spells = new();
        public List<(int Old, byte Base, byte[] Script)> Effects = new();
        public int[] EffectOf = Array.Empty<int>();     // effect number -> index in Effects (shared bases stay shared)
        public List<byte[]> Ips = new();
    }

    private static Plan MakePlan(RomBuffer rom)
    {
        var p = new Plan();
        for (int i = 0, n = SpellCount(rom); i < n; i++)
        {
            int rec = L2Battle.SpellOffset(rom, i);
            var bytes = rom.ReadBytes(rec, 0x13);
            int w = rom.ReadUInt16Le(rec + 0x11);
            if (w == 0) { p.Spells.Add((rec, bytes, Array.Empty<byte>(), -1)); continue; }
            var steps = L2AiScripts.Parse(rom, L2Rom.InBank(rec, w), rec);
            bytes[0x11] = 0x13; bytes[0x12] = 0;
            p.Spells.Add((rec, bytes, L2AiScripts.BuildCopy(steps, 0x13), L2Rom.InBank(rec, w)));
        }
        // the effects: the table's own length (its first offset), at least as many as the attacks use
        int table = L2Rom.SnesToFile((int)rom.ReadUInt24Le(EffTableRead + 1));
        int count = Math.Max(rom.ReadUInt16Le(table) / 2, Enumerable.Range(0, IpCount(rom)).Max(i => L2Battle.ReadIp(rom, i).Effect) + 1);
        if (count > MaxEffects) throw new InvalidOperationException("This ROM has more IP effects than Lufia Forge's block holds.");
        p.EffectOf = new int[count];
        var byBase = new Dictionary<int, int>();
        for (int e = 0; e < count; e++)
        {
            int b = L2AiScripts.IpEffectBase(rom, e);
            if (!byBase.TryGetValue(b, out int k))
            {
                var steps = L2AiScripts.Parse(rom, b + 1, b);
                k = p.Effects.Count;
                p.Effects.Add((b, rom.ReadByte(b), L2AiScripts.BuildCopy(steps, 1)));
                byBase[b] = k;
            }
            p.EffectOf[e] = k;
        }
        for (int i = 0, n = IpCount(rom); i < n; i++)
        {
            int o = L2Battle.IpOffset(rom, i);
            p.Ips.Add(rom.ReadBytes(o, 6 + rom.ReadUntil(o + 6, 0, 32).Length + 1));
        }
        return p;
    }

    /// <summary>Whether the spells and IP attacks can be moved (every place still has the game's code, every script reads),
    /// and if not why.</summary>
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] b) => Bytes(at, rom, b.Length).SequenceEqual(b);
        bool ok = Same(SpellTableRead, SpellTableReadOrig) && Same(SpellTableAdc, SpellAdcOrig) && Same(SpellLoader, SpellLoaderOrig)
            && SpellBankSites.All(s => Same(s, new byte[] { 0xA9, 0x95 })) && AnimSites.All(s => Same(s, AnimOrig))
            && Same(EffTableRead, EffTableReadOrig) && Same(EffTableAdc, EffAdcOrig) && Same(EffBankSite, new byte[] { 0xA9, 0x95 })
            && IpTableReads.All(s => Same(s, IpTableReadOrig)) && IpBankSites.All(s => Same(s, new byte[] { 0xA9, 0x84 }))
            && Same(IpLongSites[0], new byte[] { 0xBF, 0x03, 0x00, 0x84 }) && Same(IpLongSites[1], new byte[] { 0xBF, 0x05, 0x00, 0x84 })
            && Same(IpLongSites[2], new byte[] { 0xBF, 0x06, 0x00, 0x84 });
        if (!ok) { why = "This ROM's spell or IP attack code isn't the US game's (another patch or hack changed it), so spells and IP attacks can't be added."; return false; }
        if (!L2Descriptions.CanMove(rom) || L2Descriptions.IsMoved(rom) && Encoding.ASCII.GetString(rom.ReadBytes(BlockBAt, L2Descriptions.FreeMark.Length)) != L2Descriptions.FreeMark)
        {
            why = "This ROM's description code isn't the US game's, so there is no room for the IP attacks.";
            return false;
        }
        try { MakePlan(rom); }
        catch (Exception e) { why = "A spell or IP effect script of this ROM can't be read, so they can't be moved: " + e.Message; return false; }
        return true;
    }

    /// <summary>Moves the spells, IP effects and IP attacks (once; see the class). Nothing changes for the game: every
    /// record, script, animation and name stays the same (scripts are laid out again from their steps).</summary>
    public static void Install(RomBuffer rom)
    {
        if (IsInstalled(rom)) return;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        var plan = MakePlan(rom);
        L2Descriptions.Move(rom);   // frees $97:8000-$97:B0A0 for block B

        // block A
        int a = L2Rom.Allocate(rom, SizeA);
        int aSnes = L2Rom.FileToSnes(a), bank = aSnes >> 16;
        if (bank >= 0xFC) throw new InvalidOperationException("The added space left is in the ROM's last banks, which Lufia Forge's script table can't name.");
        rom.WriteBytes(a, new byte[SizeA]);
        rom.WriteBytes(a, Encoding.ASCII.GetBytes(MagicA));
        rom.WriteByte(a + 5, Version);
        int heap = a + HeapA;
        int Addr(int file) => L2Rom.FileToSnes(file) & 0xFFFF;
        var moved = new Dictionary<int, int>();   // old script start -> new (for Lufia Forge's edited scripts)
        var spellNew = new int[plan.Spells.Count];
        for (int i = 0; i < plan.Spells.Count; i++)
        {
            var (_, rec, script, scriptOld) = plan.Spells[i];
            spellNew[i] = heap;
            rom.WriteBytes(heap, rec);
            if (script.Length > 0)
            {
                rom.WriteBytes(heap + 0x13, script);
                moved.TryAdd(scriptOld, heap + 0x13);
            }
            heap += 0x13 + script.Length + 1;
        }
        for (int i = 0; i < 256; i++)
        {
            rom.WriteUInt16Le(a + SpellTableAt + i * 2, (ushort)Addr(spellNew[i < spellNew.Length ? i : 0]));
            rom.WriteByte(a + AnimAt + i, (byte)(i < spellNew.Length ? 0x12 + i : 0x12));
        }
        rom.WriteByte(a + 6, (byte)spellNew.Length);
        var effNew = new int[plan.Effects.Count];
        for (int k = 0; k < plan.Effects.Count; k++)
        {
            var (old, b0, script) = plan.Effects[k];
            effNew[k] = heap;
            rom.WriteByte(heap, b0);
            rom.WriteBytes(heap + 1, script);
            moved.TryAdd(old + 1, heap + 1);
            heap += 1 + script.Length + 1;
        }
        for (int e = 0; e < 256; e++)
            rom.WriteUInt16Le(a + EffTableAt + e * 2, (ushort)Addr(effNew[plan.EffectOf[e < plan.EffectOf.Length ? e : 0]]));
        rom.WriteUInt16Le(a + 8, (ushort)plan.EffectOf.Length);
        if (heap - a > SizeA) throw new InvalidOperationException("The spells don't fit Lufia Forge's block.");
        rom.WriteUInt16Le(a + 0x0A, (ushort)(heap - a));
        // the animation routine: A = the spell (8-bit), X/Y 16-bit
        int animTable = aSnes + AnimAt;
        var code = new byte[]
        {
            0xDA, 0xC2, 0x20, 0x29, 0xFF, 0x00, 0xAA, 0xE2, 0x20,          // PHX / REP #$20 / AND #$00FF / TAX / SEP #$20
            0xBF, (byte)animTable, (byte)(animTable >> 8), (byte)(animTable >> 16),   // LDA anim,X
            0x8D, 0x62, 0x12, 0xFA, 0x6B,                                  // STA $1262 / PLX / RTL
        };
        rom.WriteBytes(a + CodeAt, code);

        // block B: the IP attack records, packed
        int bb = BlockBAt;
        rom.WriteBytes(bb, new byte[SizeB]);
        rom.WriteBytes(bb, Encoding.ASCII.GetBytes(MagicB));
        rom.WriteByte(bb + 5, Version);
        rom.WriteByte(bb + 6, (byte)plan.Ips.Count);
        int at = bb + HeapB;
        var ipNew = new int[plan.Ips.Count];
        for (int i = 0; i < plan.Ips.Count; i++) { ipNew[i] = at; rom.WriteBytes(at, plan.Ips[i]); at += plan.Ips[i].Length; }
        if (at > bb + SizeB) throw new InvalidOperationException("The IP attacks don't fit their space.");
        for (int i = 0; i < 255; i++) rom.WriteUInt16Le(bb + IpTableAt + i * 2, (ushort)Addr(ipNew[i < ipNew.Length ? i : 0]));
        rom.WriteUInt16Le(bb + 8, (ushort)(at - bb));

        // Lufia Forge's edited spell and IP effect scripts follow their scripts
        L2AiScripts.MoveEffectKeys(rom, old => moved.TryGetValue(old, out int n) ? n : null);

        // the code
        byte[] Long(int snes) => new[] { (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) };
        rom.WriteBytes(SpellTableRead + 1, Long(aSnes + SpellTableAt));
        rom.WriteBytes(SpellTableAdc + 1, new byte[] { 0, 0 });
        rom.WriteBytes(SpellLoader, new byte[] { 0x8B, 0x20, 0x46, 0xF4, 0xAC, 0x0D, 0x0A, 0xA9, (byte)bank, 0x48, 0xAB });
        foreach (int s in SpellBankSites) rom.WriteByte(s + 1, (byte)bank);
        int hook = aSnes + CodeAt;
        foreach (int s in AnimSites) rom.WriteBytes(s, new byte[] { 0x22, (byte)hook, (byte)(hook >> 8), (byte)(hook >> 16), 0xEA, 0xEA });
        rom.WriteBytes(EffTableRead + 1, Long(aSnes + EffTableAt));
        rom.WriteBytes(EffTableAdc + 1, new byte[] { 0, 0 });
        rom.WriteByte(EffBankSite + 1, (byte)bank);
        int bSnes = L2Rom.FileToSnes(bb);
        foreach (int s in IpTableReads) rom.WriteBytes(s + 1, Long(bSnes + IpTableAt));
        foreach (int s in IpBankSites) rom.WriteByte(s + 1, (byte)(bSnes >> 16));
        foreach (int s in IpLongSites) rom.WriteByte(s + 3, (byte)(bSnes >> 16));
    }

    private static int HeapTake(RomBuffer rom, int block, int nextAt, int size, int length, string what)
    {
        int next = rom.ReadUInt16Le(block + nextAt);
        if (next + length > size) throw new InvalidOperationException($"There is no room left for {what} in Lufia Forge's block.");
        rom.WriteUInt16Le(block + nextAt, (ushort)(next + length));
        return block + next;
    }

    /// <summary>Free bytes left for new spells and effects (block A) and IP attacks (block B), or -1 before the move.</summary>
    public static (int Spells, int Ip) Room(RomBuffer rom)
    {
        int a = BlockA(rom), b = BlockB(rom);
        return (a < 0 ? -1 : SizeA - rom.ReadUInt16Le(a + 0x0A), b < 0 ? -1 : SizeB - rom.ReadUInt16Le(b + 8));
    }

    // ── spells ───────────────────────────────────────────────────────────────

    /// <summary>The battle animation a spell shows (its number + $12 in the game; a byte of Lufia Forge's after the
    /// move), or -1 for none.</summary>
    public static int SpellAnimation(RomBuffer rom, int spell)
    {
        int a = BlockA(rom);
        if (a >= 0) return rom.ReadByte(a + AnimAt + spell);
        return spell < L2Battle.SpellCountOriginal ? 0x12 + spell : -1;
    }

    public static void SetSpellAnimation(RomBuffer rom, int spell, int animation)
    {
        if (SpellAnimation(rom, spell) == animation) return;
        Install(rom);
        rom.WriteByte(BlockA(rom) + AnimAt + spell, (byte)animation);
    }

    /// <summary>Adds a spell: a copy of <paramref name="from"/> (its record, its effect as it runs now, its animation and
    /// description) with the next number. Returns the new spell's number.</summary>
    public static int AddSpell(RomBuffer rom, int from)
    {
        Install(rom);
        int a = BlockA(rom), id = rom.ReadByte(a + 6);
        if (id >= MaxSpells) throw new InvalidOperationException($"The game can have {MaxSpells} spells at most (FF ends a hero's list of spells).");
        var src = L2Battle.ReadSpell(rom, from);
        var steps = L2AiScripts.Load(rom, L2AiScripts.Kind.Spell, from).Steps;
        var script = L2AiScripts.BuildCopy(steps, 0x13);
        var rec = rom.ReadBytes(src.Offset, 0x13);
        rec[0x11] = 0x13; rec[0x12] = 0;   // its script right after it
        var name = Encoding.ASCII.GetBytes($"New {id:X2}".PadRight(L2Battle.SpellNameLength));
        Array.Copy(name, rec, L2Battle.SpellNameLength);
        int at = HeapTake(rom, a, 0x0A, SizeA, 0x13 + script.Length + 1, "a new spell");
        rom.WriteBytes(at, rec);
        rom.WriteBytes(at + 0x13, script);
        rom.WriteByte(at + 0x13 + script.Length, 0);
        rom.WriteUInt16Le(a + SpellTableAt + id * 2, (ushort)(L2Rom.FileToSnes(at) & 0xFFFF));
        rom.WriteByte(a + AnimAt + id, (byte)Math.Max(0, SpellAnimation(rom, from)));
        rom.WriteByte(a + 6, (byte)(id + 1));
        L2Descriptions.Write(rom, L2Descriptions.SpellEntry(id), L2Descriptions.Read(rom, L2Descriptions.SpellEntry(from)));
        return id;
    }

    // ── IP attacks ───────────────────────────────────────────────────────────

    /// <summary>Adds an IP effect script (<paramref name="steps"/>, laid out as the game's: a byte, then the script run
    /// from offset 1). Returns its number.</summary>
    private static int AddIpEffect(RomBuffer rom, IReadOnlyList<L2AiScripts.Step> steps)
    {
        int a = BlockA(rom), n = rom.ReadUInt16Le(a + 8);
        if (n >= MaxEffects) throw new InvalidOperationException($"Lufia Forge's block has room for {MaxEffects} IP effects.");
        var script = L2AiScripts.BuildCopy(steps, 1);
        int at = HeapTake(rom, a, 0x0A, SizeA, 1 + script.Length + 1, "an IP attack's effect");
        rom.WriteByte(at, 0);
        rom.WriteBytes(at + 1, script);
        rom.WriteByte(at + 1 + script.Length, 0);
        rom.WriteUInt16Le(a + EffTableAt + n * 2, (ushort)(L2Rom.FileToSnes(at) & 0xFFFF));
        rom.WriteUInt16Le(a + 8, (ushort)(n + 1));
        return n;
    }

    /// <summary>Gives IP attack <paramref name="ip"/> an effect of its own: a copy of the one it shares now (as it runs
    /// now, edits included), so a change no longer applies to the others. Returns the new effect number.</summary>
    public static int GiveIpOwnEffect(RomBuffer rom, int ip)
    {
        var steps = L2AiScripts.Load(rom, L2AiScripts.Kind.IpAttack, ip).Steps;
        Install(rom);
        int e = AddIpEffect(rom, steps);
        rom.WriteUInt16Le(L2Battle.IpOffset(rom, ip), (ushort)e);
        return e;
    }

    /// <summary>Adds an IP attack: a copy of <paramref name="from"/> (its numbers, its description and its effect as it
    /// runs now, as an effect of its own). Returns the new attack's index (items name it as index + 1).</summary>
    public static int AddIp(RomBuffer rom, int from)
    {
        var steps = L2AiScripts.Load(rom, L2AiScripts.Kind.IpAttack, from).Steps;
        Install(rom);
        int b = BlockB(rom), id = rom.ReadByte(b + 6);
        if (id >= MaxIp) throw new InvalidOperationException($"The game can have {MaxIp} IP attacks at most.");
        var src = L2Battle.ReadIp(rom, from);
        int e = AddIpEffect(rom, steps);
        var name = Encoding.ASCII.GetBytes("New attack");
        var rec = new byte[6 + name.Length + 1];
        rec[0] = (byte)e; rec[1] = (byte)(e >> 8); rec[2] = src.Animation; rec[3] = src.Cursor; rec[4] = src.Targeting; rec[5] = src.Cost;
        Array.Copy(name, 0, rec, 6, name.Length);
        int at = HeapTake(rom, b, 8, SizeB, rec.Length, "a new IP attack");
        rom.WriteBytes(at, rec);
        rom.WriteUInt16Le(b + IpTableAt + id * 2, (ushort)(L2Rom.FileToSnes(at) & 0xFFFF));
        rom.WriteByte(b + 6, (byte)(id + 1));
        L2Descriptions.Write(rom, L2Descriptions.IpEntry(rom, id), L2Descriptions.Read(rom, L2Descriptions.IpEntry(rom, from)));
        return id;
    }

    /// <summary>Renames an IP attack: in place when the name is no longer than the old one, else (after the move) as a
    /// new record in block B. Letters the game can't show become spaces; at most <see cref="IpNameLength"/> letters (the
    /// battle list shows 13).</summary>
    public static void SetIpName(RomBuffer rom, int ip, string name)
    {
        name = new string(name.Trim().Select(c => c is >= ' ' and < (char)0x7F ? c : ' ').ToArray());
        if (name.Length == 0) throw new InvalidOperationException("The name can't be empty.");
        if (name.Length > IpNameLength) name = name[..IpNameLength];
        var a = L2Battle.ReadIp(rom, ip);
        if (a.Name == name && a.NameLength == name.Length) return;
        var bytes = Encoding.ASCII.GetBytes(name).Append((byte)0).ToArray();
        if (name.Length <= a.NameLength)
        {
            rom.WriteBytes(a.Offset + 6, bytes);
            return;
        }
        Install(rom);
        a = L2Battle.ReadIp(rom, ip);
        int b = BlockB(rom);
        int at = HeapTake(rom, b, 8, SizeB, 6 + bytes.Length, "a longer name");
        rom.WriteBytes(at, rom.ReadBytes(a.Offset, 6));
        rom.WriteBytes(at + 6, bytes);
        rom.WriteUInt16Le(b + IpTableAt + ip * 2, (ushort)(L2Rom.FileToSnes(at) & 0xFFFF));
    }

    // ── words for the editor ─────────────────────────────────────────────────

    /// <summary>Who a spell (record +0B) or an IP attack (+4) targets, read from the target picker $81:D4E0: bit 7 starts
    /// it on the enemies' side, low bits 1 lets L or R pick the whole side, 2 one only; 00 picks nobody (the effect's
    /// "choose the target" does).</summary>
    public static readonly (int Value, string Name)[] Targets =
    {
        (0x82, "One enemy"),
        (0x81, "One enemy, or all of them with L or R"),
        (0x02, "One ally"),
        (0x01, "One ally, or all of them with L or R"),
        (0x00, "Nobody to pick (the effect chooses)"),
    };

    /// <summary>The look of the target pointer (an IP attack's +3, $129E; the battle menus set 1 for attacks, 2 for spells
    /// and 3 for items).</summary>
    public static readonly (int Value, string Name)[] Cursors =
    {
        (0x01, "Attack pointer"), (0x02, "Spell pointer"), (0x03, "Item pointer"), (0x04, "Other pointer"),
    };

    /// <summary>The battle animation table ($95:8000, u16 pointers in bank $95, played by $81:895E): an entry of 0 is no
    /// animation (the game would crash on it).</summary>
    private const int AnimationTable = 0xA8000;

    public static bool IsAnimation(RomBuffer rom, int n) => n is >= 0 and < 0xFF && rom.ReadUInt16Le(AnimationTable + n * 2) != 0;

    /// <summary>Animations the battle code starts itself.</summary>
    private static readonly Dictionary<int, string> BuiltIn = new()
    {
        [0x0E] = "a punch (no weapon)", [0xE0] = "a monster's attack", [0xF0] = "a capsule monster's attack",
    };

    /// <summary>Every animation the game has, named by what uses it (spells first, then IP attacks, items and monsters):
    /// "Firebird (spell)", "Bite (IP attack)", "Short sword (weapon)".</summary>
    public static List<(int Value, string Name)> Animations(RomBuffer rom)
    {
        var users = new Dictionary<int, List<string>>();
        void Add(int anim, string who) { if (anim is >= 0 and < 0x100) (users.TryGetValue(anim, out var l) ? l : users[anim] = new()).Add(who); }
        foreach (var s in L2Battle.Spells(rom)) Add(SpellAnimation(rom, s.Index), $"{s.Name.Trim()} (spell)");
        foreach (var ip in L2Battle.IpAttacks(rom)) Add(ip.Animation, $"{ip.Name} (IP attack)");
        foreach (var it in L2Battle.Items(rom))
            if (it.Values.TryGetValue(0x2000, out var w) && w > 0) Add(w - 1, $"{it.Name.Trim()} ({(it.EquipType == 1 ? "weapon" : "item")})");
        foreach (var kv in BuiltIn) Add(kv.Key, kv.Value);
        var list = new List<(int, string)>();
        for (int n = 0; n < 0xFF; n++)
        {
            if (!IsAnimation(rom, n)) continue;
            if (users.TryGetValue(n, out var who))
            {
                var distinct = who.Distinct().ToList();
                list.Add((n, distinct[0] + (distinct.Count > 1 ? $", also {string.Join(", ", distinct.Skip(1).Take(2))}{(distinct.Count > 3 ? "..." : "")}" : "")));
            }
            else list.Add((n, $"Unused animation {n:X2}"));
        }
        return list;
    }
}
