namespace LufiaForge.Core.Lufia2;

// the spells a hero knows when a new game starts, for any hero and any number of spells. the new-game records
// ($85:B2A1, L2Characters) keep a spell list that can't grow in place (the records follow one another, and the loader
// $81:EC56 reads them with DB = $85 and writes WRAM through it, so the block can't just move to the added space).
// so a small hook runs right after the loader has set every hero up: at $81:ED1A (SEP #$20 / LDA #$FF / STA $0A7F) a
// JSL to our block, which does those three and then copies each changed hero's whole list (36 bytes, FF = none) over
// the hero's spell list in RAM (hero block + $96 = $7E:0C43 + hero * $BE). heroes without a change keep the game's
// list. the block gets assembled again on every change, with the lists in it
public static class L2StartSpells
{
    public const int Site = 0xED1A, Slots = 36, HeroBlock = 0x7E0BAD, HeroStride = 0xBE, SpellList = 0x96;
    private static readonly byte[] SiteOriginal = { 0xE2, 0x20, 0xA9, 0xFF, 0x8D, 0x7F, 0x0A };
    private const string Magic = "LF2SP";
    private const int BlockSize = 0x400, CodeAt = 0x10;

    // file offset of the block, or -1
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int blockAt = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return blockAt >= 0 && blockAt + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(blockAt, Magic.Length)) == Magic ? blockAt : -1;
    }

    public static bool CanChange(RomBuffer rom) =>
        Block(rom) >= 0 || rom.Length > Site + SiteOriginal.Length && rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal);

    // the changed lists in the rom (hero -> spells), read back from the block's data
    public static Dictionary<int, List<int>> Changed(RomBuffer rom)
    {
        var changed = new Dictionary<int, List<int>>();
        int block = Block(rom);
        if (block < 0) return changed;
        int heroes = rom.ReadByte(block + Magic.Length);
        for (int k = 0; k < heroes; k++)
        {
            int entry = block + 0x200 + k * (Slots + 1);
            int hero = rom.ReadByte(entry);
            changed[hero] = rom.ReadBytes(entry + 1, Slots).TakeWhile(x => x != 0xFF).Select(x => (int)x).ToList();
        }
        return changed;
    }

    // the spells a hero starts with: the changed list, or the game's own
    public static List<int> Of(RomBuffer rom, int hero) =>
        Changed(rom).TryGetValue(hero, out var spells) ? spells : L2Characters.ReadStart(rom).Heroes[hero].Spells.ToList();

    // sets a hero's starting spells (setting the game's own list again drops the change)
    public static void Set(RomBuffer rom, int hero, IReadOnlyList<int> spells)
    {
        if (spells.Count > Slots) throw new ArgumentException($"A hero knows at most {Slots} spells.");
        var all = Changed(rom);
        var own = L2Characters.ReadStart(rom).Heroes[hero].Spells;
        if (spells.SequenceEqual(own)) all.Remove(hero); else all[hero] = spells.ToList();
        Write(rom, all);
    }

    private static void Write(RomBuffer rom, Dictionary<int, List<int>> lists)
    {
        int block = Block(rom);
        if (lists.Count == 0 && block < 0) return;
        // with no change left the block stays, only doing the game's three instructions, so its space gets reused later
        if (block < 0)
        {
            if (!CanChange(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's where the starting spells hook goes.");
            block = L2Rom.Allocate(rom, BlockSize);
        }
        int code = L2Rom.FileToSnes(block + CodeAt), data = L2Rom.FileToSnes(block + 0x200);
        var hookCode = new List<byte> { 0xE2, 0x20, 0xA9, 0xFF, 0x8D, 0x7F, 0x0A };   // the game's SEP #$20 / LDA #$FF / STA $0A7F
        var heroes = lists.Keys.OrderBy(h => h).ToList();
        var blob = new List<byte>();
        foreach (int hero in heroes)
        {
            int source = data + blob.Count + 1, dest = HeroBlock + hero * HeroStride + SpellList;
            hookCode.AddRange(new byte[] { 0xA2, 0x00, 0x00 });                                     // LDX #0
            hookCode.AddRange(new byte[] { 0xBF, (byte)source, (byte)(source >> 8), (byte)(source >> 16) });  // LDA list,X
            hookCode.AddRange(new byte[] { 0x9F, (byte)dest, (byte)(dest >> 8), (byte)(dest >> 16) });  // STA $7E:spells,X
            hookCode.AddRange(new byte[] { 0xE8, 0xE0, Slots, 0x00, 0xD0, 0xF2 });                   // INX / CPX #36 / BNE
            blob.Add((byte)hero);
            var spells = lists[hero];
            blob.AddRange(Enumerable.Range(0, Slots).Select(i => i < spells.Count ? (byte)spells[i] : (byte)0xFF));
        }
        hookCode.Add(0x6B);   // RTL
        if (CodeAt + hookCode.Count > 0x200 || 0x200 + blob.Count > BlockSize) throw new InvalidOperationException("Too many changed heroes for the block.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, (byte)heroes.Count);
        rom.WriteBytes(block + CodeAt, hookCode.ToArray());
        rom.WriteBytes(block + 0x200, blob.ToArray());
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16), 0xEA, 0xEA, 0xEA });
    }
}
