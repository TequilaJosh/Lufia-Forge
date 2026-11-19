namespace LufiaForge.Core.Maps;

// where each map's setup script is, and moving one to the added space so it can grow.
// the game reads a 16-bit offset from 0x18000 + map*2 (at $01:9884, data bank 03) into the script base ($0D0F-$0D11)
// and position ($0D12-$0D14), with the top bytes $0D11 / $0D14 cleared, so its scripts can only be in banks 03-04.
// the reader itself takes 24-bit positions (event scripts use that), so a small hook at $01:9884 first looks in a
// table of 24-bit offsets (block "LFSU" in the added space, FFFFFF = the map's original entry).
// characters: a map's characters only show up when its setup script switches them on (41+n turns on character n,
// $01:9AB1). 40 does nothing (character FF is past every map's count, $01:9759)
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
        int block = code - Code;
        return block > 0 && block + 4 <= rom.Length && rom.ReadBytes(block, 4).SequenceEqual(Magic) ? block : 0;
    }
    public static bool IsHooked(RomBuffer rom) => Block(rom) > 0;

    // file offset of the map's setup script
    public static int Offset(RomBuffer rom, int mapId)
    {
        int block = Block(rom);
        if (block > 0)
        {
            int entry = block + Table + mapId * 3;
            int relative = rom.ReadByte(entry) | rom.ReadByte(entry + 1) << 8 | rom.ReadByte(entry + 2) << 16;
            if (relative != 0xFFFFFF) return MapSetupScript.PointerTable + relative;
        }
        return MapSetupScript.PointerTable + rom.ReadUInt16Le(MapSetupScript.PointerTable + mapId * 2);
    }

    // whether the map's script got moved to the added space
    public static bool IsMoved(RomBuffer rom, int mapId)
    {
        int block = Block(rom);
        return block > 0 && !(rom.ReadByte(block + Table + mapId * 3) == 0xFF && rom.ReadUInt16Le(block + Table + mapId * 3 + 1) == 0xFFFF);
    }

    // puts the hook in (once)
    public static void Hook(RomBuffer rom)
    {
        if (IsHooked(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The game's setup script loader isn't the original, so it can't be changed safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var freeSpace = ExpansionSpace.Load(rom);
        var code = new List<byte>();
        int size = Code + 80;
        int blockOffset = freeSpace.AllocateInOneBank(size, ExpansionSpace.Kind.Patch, 14);
        freeSpace.Save();
        int table = SnesAddress(blockOffset + Table);
        void Op(params int[] bytes) { foreach (var x in bytes) code.Add((byte)x); }
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
        foreach (var originalByte in Original) code.Add(originalByte);    // STZ $0D11 ... STA $0D12 (ends with M = 16)
        Op(0xFA, 0x6B);                             // PLX; RTL
        if (Code + code.Count > size) throw new InvalidOperationException("Setup hook too long.");
        var block = new byte[size];
        Magic.CopyTo(block, 0);
        Array.Fill(block, (byte)0xFF, Table, 256 * 3);
        code.CopyTo(block, Code);
        rom.WriteBytes(blockOffset, block);
        int codeAddress = SnesAddress(blockOffset + Code);
        // JSL hook, then BRA to the original LDA #$0000 / JSR $C700
        rom.WriteBytes(HookAt, new byte[] { 0x22, (byte)codeAddress, (byte)(codeAddress >> 8), (byte)(codeAddress >> 16), 0x80, (byte)(HookEnd - (HookAt + 6)) });
        rom.FixChecksum();
    }

    // script contents

    // length of a command from its opcode (0 = end), or -1 for an unknown opcode
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

    // the command positions (relative to the script start) of everything reachable, and the script's length
    public static (List<int> Ops, int Length) Parse(byte[] script)
    {
        var ops = new List<int>();
        var targets = new SortedSet<int>();
        var seen = new HashSet<int>();
        int pos = 2, end = 2;
        while (true)
        {
            while (pos < script.Length && seen.Add(pos))
            {
                int op = script[pos], len = Length(op);
                if (len < 0) throw new InvalidOperationException($"Unknown setup command {op:X2} at +{pos:X}.");
                ops.Add(pos);
                if (len == 0) { end = Math.Max(end, pos + 1); break; }
                if (pos + len > script.Length) throw new InvalidOperationException("The setup script runs past its end.");
                if (op == 0x04) targets.Add(script[pos + 1] | script[pos + 2] << 8);
                pos += len;
                end = Math.Max(end, pos);
            }
            int next = targets.FirstOrDefault(t => !seen.Contains(t) && t >= 2, -1);
            if (next < 0) break;
            pos = next;
        }
        ops.Sort();
        return (ops, end);
    }

    // the map's whole setup script
    public static byte[] Read(RomBuffer rom, int mapId)
    {
        int offset = Offset(rom, mapId);
        var script = rom.ReadBytes(offset, Math.Min(0x2000, rom.Length - offset));
        return script.AsSpan(0, Parse(script).Length).ToArray();
    }

    // characters the script switches on (anywhere, also after a story check)
    public static HashSet<int> CharactersOn(byte[] script)
    {
        var (ops, _) = Parse(script);
        return ops.Where(p => script[p] > Nothing && script[p] < 0x80).Select(p => script[p] - CharacterOn).ToHashSet();
    }

    // gives the map its own copy of the script in the added space (the old copy might be shared with other maps)
    public static void Replace(RomBuffer rom, int mapId, byte[] script)
    {
        Hook(rom);
        var freeSpace = ExpansionSpace.Load(rom);
        if (IsMoved(rom, mapId))
        {
            int old = Offset(rom, mapId);
            if (freeSpace.At(old) is { Kind: ExpansionSpace.Kind.Patch } entry && entry.Start == old && entry.Id == 0x200 + mapId) freeSpace.Free(old);
        }
        int scriptOffset = freeSpace.AllocateInOneBank(script.Length, ExpansionSpace.Kind.Patch, 0x200 + mapId, EventScript.ScriptLimit);
        freeSpace.Save();
        rom.WriteBytes(scriptOffset, script);
        int relative = scriptOffset - MapSetupScript.PointerTable, tableEntry = Block(rom) + Table + mapId * 3;
        rom.WriteBytes(tableEntry, new[] { (byte)relative, (byte)(relative >> 8), (byte)(relative >> 16) });
        rom.FixChecksum();
    }

    // forgets a map's moved script (its space gets freed and the map falls back to its original table entry)
    public static void Forget(RomBuffer rom, int mapId)
    {
        if (!IsMoved(rom, mapId)) return;
        var freeSpace = ExpansionSpace.Load(rom);
        int old = Offset(rom, mapId);
        if (freeSpace.At(old) is { Kind: ExpansionSpace.Kind.Patch } entry && entry.Start == old && entry.Id == 0x200 + mapId) { freeSpace.Free(old); freeSpace.Save(); }
        rom.WriteBytes(Block(rom) + Table + mapId * 3, new byte[] { 0xFF, 0xFF, 0xFF });
    }

    // puts commands near the start of the map's setup script so they always run: after its "current map" command
    // (01 mm) if that comes before any story check (step-on areas record the current map when they're switched on,
    // so they have to come after it), otherwise right after the leading word. jumps past the insert move along
    public static void Insert(RomBuffer rom, int mapId, byte[] cmds)
    {
        var script = Read(rom, mapId);
        var (ops, _) = Parse(script);
        int insertAt = 2;
        foreach (int pos in ops)
        {
            if (script[pos] is 0x04 or (>= 0x05 and <= 0x0A) || script[pos] == 0x00) break;
            if (script[pos] == 0x01) { insertAt = pos + 2; break; }
        }
        int inserted = cmds.Length;
        var result = new byte[script.Length + inserted];
        script.AsSpan(0, insertAt).CopyTo(result);
        cmds.CopyTo(result, insertAt);
        script.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + inserted));
        foreach (int pos in ops)
            if (script[pos] == 0x04)
            {
                int moved = pos >= insertAt ? pos + inserted : pos, target = result[moved + 1] | result[moved + 2] << 8;
                if (target >= insertAt) { target += inserted; result[moved + 1] = (byte)target; result[moved + 2] = (byte)(target >> 8); }
            }
        Replace(rom, mapId, result);
    }

    // switches on the map's new characters (added since it loaded, not hidden) that its script doesn't switch on,
    // and switches off hidden ones (sprite FF). commands for new characters go first, so they always show up.
    // returns whether the script changed
    public static bool SyncCharacters(RomBuffer rom, LufiaMap map, bool allowExpand)
    {
        byte[] script;
        try { script = Read(rom, map.MapId); }
        catch (InvalidOperationException) { return false; }   // (a script Lufia Forge can't read is left alone)
        var on = CharactersOn(script);
        // new characters, and hidden ones that got a sprite again (hiding took their 41+n out)
        var add = map.Npcs.Where(n => (n.Index >= map.LoadedNpcCount || map.HiddenAtLoad.Contains(n.Index)) && !n.IsUnused && !on.Contains(n.Index) && n.Index < 0x3F)
                          .Select(n => n.Index).ToList();
        var off = map.Npcs.Where(n => n.IsUnused && on.Contains(n.Index)).Select(n => n.Index).ToHashSet();
        // characters switched on that the map doesn't have at all anymore (the last one taken out, an Undo after a save)
        off.UnionWith(on.Where(i => i >= map.Npcs.Count));
        if (add.Count == 0 && off.Count == 0) { map.MarkNpcsSaved(); return false; }
        // (checked before anything gets made, making the events below expands the rom)
        if (!allowExpand && rom.Length < MapWriter.ExpandedSize)
            throw new InvalidOperationException("Switching characters on or off needs the ROM expanded to 2 MB.");
        // talking to character n runs event n + 1, and one with no script would run whatever the slot points at (a
        // number past the event table grows it)
        foreach (int i in add)
            if (!MapEvents.HasEvent(rom, map.MapId, MapEvents.TalkEvent(i))) MapEvents.Create(rom, map.MapId, MapEvents.TalkEvent(i));
        var (ops, _) = Parse(script);
        var newScript = (byte[])script.Clone();
        foreach (int pos in ops) if (newScript[pos] > Nothing && newScript[pos] < 0x80 && off.Contains(newScript[pos] - CharacterOn)) newScript[pos] = Nothing;
        int inserted = add.Count;
        var result = new byte[newScript.Length + inserted];
        newScript.AsSpan(0, 2).CopyTo(result);
        for (int i = 0; i < inserted; i++) result[2 + i] = (byte)(CharacterOn + add[i]);
        newScript.AsSpan(2).CopyTo(result.AsSpan(2 + inserted));
        // jumps are relative to the script start, so everything after the insert moved by inserted
        foreach (int pos in ops)
            if (newScript[pos] == 0x04)
            {
                int moved = pos + inserted, target = result[moved + 1] | result[moved + 2] << 8;
                if (target >= 2) { target += inserted; result[moved + 1] = (byte)target; result[moved + 2] = (byte)(target >> 8); }
            }
        Replace(rom, map.MapId, result);
        map.MarkNpcsSaved();
        return true;
    }
}
