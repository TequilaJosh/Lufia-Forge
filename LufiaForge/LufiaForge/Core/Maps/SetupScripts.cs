namespace LufiaForge.Core.Maps;

/// <summary>
/// Where each map's setup script is, and moving one to the added space so it can grow.
/// <para>The game reads a 16-bit offset from 0x18000 + map*2 (at $01:9884, data bank 03) into the script base
/// ($0D0F-$0D11) and position ($0D12-$0D14), with the top bytes $0D11 / $0D14 cleared, so its scripts can only be
/// in banks 03-04. The reader itself takes 24-bit positions (event scripts use that), so a small hook at $01:9884
/// first looks in a table of 24-bit offsets (block "LFSU" in the added space, FFFFFF = the map's original entry).</para>
/// <para>Characters: a map's characters appear only when its setup script switches them on (41+n turns on
/// character n, $01:9AB1). 40 does nothing (character FF is past every map's count, $01:9759).</para>
/// </summary>
public static class SetupScripts
{
    private const int HookAt = 0x9884, HookEnd = 0x98A0;
    private static readonly byte[] Original = Convert.FromHexString("9C110D9C140DAD7F07C22029FF000A69008085 00B2008D0F0D8D120D".Replace(" ", ""));
    private static readonly byte[] Magic = "LFSU"u8.ToArray();
    private const int Header = 8, Table = Header, Code = Header + 256 * 3;
    public const int CharacterOn = 0x41, Nothing = 0x40;

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    public static bool IsOriginal(RomBuffer rom) => rom.ReadBytes(HookAt, Original.Length).SequenceEqual(Original);

    private static int Block(RomBuffer rom)
    {
        if (rom.ReadByte(HookAt) != 0x22) return 0;
        int code = FileOffset(rom.ReadByte(HookAt + 1) | rom.ReadByte(HookAt + 2) << 8 | rom.ReadByte(HookAt + 3) << 16);
        int b = code - Code;
        return b > 0 && b + 4 <= rom.Length && rom.ReadBytes(b, 4).SequenceEqual(Magic) ? b : 0;
    }
    public static bool IsHooked(RomBuffer rom) => Block(rom) > 0;

    /// <summary>File offset of the map's setup script.</summary>
    public static int Offset(RomBuffer rom, int mapId)
    {
        int b = Block(rom);
        if (b > 0)
        {
            int p = b + Table + mapId * 3;
            int t = rom.ReadByte(p) | rom.ReadByte(p + 1) << 8 | rom.ReadByte(p + 2) << 16;
            if (t != 0xFFFFFF) return MapSetupScript.PointerTable + t;
        }
        return MapSetupScript.PointerTable + rom.ReadUInt16Le(MapSetupScript.PointerTable + mapId * 2);
    }

    /// <summary>Whether the map's script was moved to the added space.</summary>
    public static bool IsMoved(RomBuffer rom, int mapId)
    {
        int b = Block(rom);
        return b > 0 && !(rom.ReadByte(b + Table + mapId * 3) == 0xFF && rom.ReadUInt16Le(b + Table + mapId * 3 + 1) == 0xFFFF);
    }

    /// <summary>Puts the hook in (once).</summary>
    public static void Hook(RomBuffer rom)
    {
        if (IsHooked(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's setup script loader isn't the original, so it can't be changed safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        var code = new List<byte>();
        int size = Code + 80;
        int at = space.AllocateInOneBank(size, ExpansionSpace.Kind.Patch, 14);
        space.Save();
        int table = SnesAddress(at + Table);
        void Op(params int[] b) { foreach (var x in b) code.Add((byte)x); }
        // entered with M = 8, X = 16, data bank 03 (banks 00-3F see work RAM at $0000-$1FFF)
        Op(0xDA);                                   // PHX
        Op(0xC2, 0x20);                             // REP #$20
        Op(0xAD, 0x7F, 0x07);                       // LDA $077F
        Op(0x29, 0xFF, 0x00);                       // AND #$00FF
        Op(0x85, 0x00);                             // STA $00
        Op(0x0A, 0x18, 0x65, 0x00);                 // ASL; CLC; ADC $00  (map * 3)
        Op(0xAA);                                   // TAX
        Op(0xE2, 0x20);                             // SEP #$20
        Op(0xBF, (table + 2) & 0xFF, (table + 2) >> 8 & 0xFF, (table + 2) >> 16);   // LDA table+2,X
        Op(0xC9, 0xFF);                             // CMP #$FF
        Op(0xF0, 0x14);                             // BEQ original
        Op(0x8D, 0x11, 0x0D, 0x8D, 0x14, 0x0D);     // STA $0D11; STA $0D14
        Op(0xC2, 0x20);                             // REP #$20
        Op(0xBF, table & 0xFF, table >> 8 & 0xFF, table >> 16);                     // LDA table,X
        Op(0x8D, 0x0F, 0x0D, 0x8D, 0x12, 0x0D);     // STA $0D0F; STA $0D12
        Op(0xFA, 0x6B);                             // PLX; RTL
        // original:
        foreach (var b in Original) code.Add(b);    // STZ $0D11 ... STA $0D12 (ends with M = 16)
        Op(0xFA, 0x6B);                             // PLX; RTL
        if (Code + code.Count > size) throw new InvalidOperationException("Setup hook too long.");
        var block = new byte[size];
        Magic.CopyTo(block, 0);
        Array.Fill(block, (byte)0xFF, Table, 256 * 3);
        code.CopyTo(block, Code);
        rom.WriteBytes(at, block);
        int c = SnesAddress(at + Code);
        // JSL hook; BRA to the original LDA #$0000 / JSR $C700
        rom.WriteBytes(HookAt, new byte[] { 0x22, (byte)c, (byte)(c >> 8), (byte)(c >> 16), 0x80, (byte)(HookEnd - (HookAt + 6)) });
        rom.FixChecksum();
    }

    // ── script contents ───────────────────────────────────────────────────

    /// <summary>Length of each command at <paramref name="p"/> (0 = end), or -1 for an unknown opcode.</summary>
    private static int Length(int op) => op switch
    {
        0x00 => 0,
        0x04 or 0x0B => 3,
        <= 0x0D => 2,
        < 0x20 => -1,
        < 0x40 => 4,
        < 0x80 => 1,
        < 0xC0 => 2,
        _ => 4,
    };

    /// <summary>The command positions (relative to the script start) of everything reachable, and the script's length.</summary>
    public static (List<int> Ops, int Length) Parse(byte[] s)
    {
        var ops = new List<int>();
        var targets = new SortedSet<int>();
        var seen = new HashSet<int>();
        int p = 2, end = 2;
        while (true)
        {
            while (p < s.Length && seen.Add(p))
            {
                int op = s[p], len = Length(op);
                if (len < 0) throw new InvalidOperationException($"Unknown setup command {op:X2} at +{p:X}.");
                ops.Add(p);
                if (len == 0) { end = Math.Max(end, p + 1); break; }
                if (p + len > s.Length) throw new InvalidOperationException("The setup script runs past its end.");
                if (op == 0x04) targets.Add(s[p + 1] | s[p + 2] << 8);
                p += len;
                end = Math.Max(end, p);
            }
            int next = targets.FirstOrDefault(t => !seen.Contains(t) && t >= 2, -1);
            if (next < 0) break;
            p = next;
        }
        ops.Sort();
        return (ops, end);
    }

    /// <summary>The map's whole setup script.</summary>
    public static byte[] Read(RomBuffer rom, int mapId)
    {
        int at = Offset(rom, mapId);
        var s = rom.ReadBytes(at, Math.Min(0x2000, rom.Length - at));
        return s.AsSpan(0, Parse(s).Length).ToArray();
    }

    /// <summary>Characters the script switches on (anywhere, also after a story check).</summary>
    public static HashSet<int> CharactersOn(byte[] s)
    {
        var (ops, _) = Parse(s);
        return ops.Where(p => s[p] > Nothing && s[p] < 0x80).Select(p => s[p] - CharacterOn).ToHashSet();
    }

    /// <summary>Gives the map its own copy of the script in the added space (the old copy may be shared with other maps).</summary>
    public static void Replace(RomBuffer rom, int mapId, byte[] script)
    {
        Hook(rom);
        var space = ExpansionSpace.Load(rom);
        if (IsMoved(rom, mapId))
        {
            int old = Offset(rom, mapId);
            if (space.At(old) is { Kind: ExpansionSpace.Kind.Patch } e && e.Start == old && e.Id == 0x200 + mapId) space.Free(old);
        }
        int at = space.AllocateInOneBank(script.Length, ExpansionSpace.Kind.Patch, 0x200 + mapId, EventScript.ScriptLimit);
        space.Save();
        rom.WriteBytes(at, script);
        int t = at - MapSetupScript.PointerTable, b = Block(rom) + Table + mapId * 3;
        rom.WriteBytes(b, new[] { (byte)t, (byte)(t >> 8), (byte)(t >> 16) });
        rom.FixChecksum();
    }

    /// <summary>
    /// Switches on the map's new characters (added since it was loaded; not hidden) that its script doesn't switch on,
    /// and switches off hidden ones (sprite FF). Commands for new characters go first, so they always appear.
    /// Returns whether the script changed.
    /// </summary>
    public static bool SyncCharacters(RomBuffer rom, LufiaMap map, bool allowExpand)
    {
        byte[] s;
        try { s = Read(rom, map.MapId); }
        catch (InvalidOperationException) { return false; }   // (a script Lufia Forge can't read is left alone)
        var on = CharactersOn(s);
        var add = map.Npcs.Where(n => n.Index >= map.LoadedNpcCount && !n.IsUnused && !on.Contains(n.Index) && n.Index < 0x3F)
                          .Select(n => n.Index).ToList();
        var off = map.Npcs.Where(n => n.IsUnused && on.Contains(n.Index)).Select(n => n.Index).ToHashSet();
        if (add.Count == 0 && off.Count == 0) { map.MarkNpcsSaved(); return false; }
        if (!allowExpand && rom.Length < MapWriter.ExpandedSize)
            throw new InvalidOperationException("Switching characters on or off needs the ROM expanded to 2 MB.");
        var (ops, _) = Parse(s);
        var n2 = (byte[])s.Clone();
        foreach (int p in ops) if (n2[p] > Nothing && n2[p] < 0x80 && off.Contains(n2[p] - CharacterOn)) n2[p] = Nothing;
        int k = add.Count;
        var result = new byte[n2.Length + k];
        n2.AsSpan(0, 2).CopyTo(result);
        for (int i = 0; i < k; i++) result[2 + i] = (byte)(CharacterOn + add[i]);
        n2.AsSpan(2).CopyTo(result.AsSpan(2 + k));
        // jumps are relative to the script start: everything after the insert moved by k
        foreach (int p in ops)
            if (n2[p] == 0x04)
            {
                int q = p + k, w = result[q + 1] | result[q + 2] << 8;
                if (w >= 2) { w += k; result[q + 1] = (byte)w; result[q + 2] = (byte)(w >> 8); }
            }
        Replace(rom, map.MapId, result);
        map.MarkNpcsSaved();
        return true;
    }
}
