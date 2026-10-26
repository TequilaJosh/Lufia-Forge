namespace LufiaForge.Core.Lufia2;

/// <summary>
/// The spells a hero knows when a new game starts, for any hero and any number of spells. The new-game records
/// ($85:B2A1, <see cref="L2Characters"/>) keep a spell list that can't grow in place (the records follow one another and
/// the loader $81:EC56 reads them with DB = $85, writing WRAM through it, so the block can't simply move to the added
/// space). Instead a small hook runs right after the loader has set every hero up: at $81:ED1A (SEP #$20 / LDA #$FF /
/// STA $0A7F) a JSL to Lufia Forge's block, which does those three and then copies each changed hero's whole list (36
/// bytes, FF = none) over the hero's spell list in RAM (hero block + $96: $7E:0C43 + hero * $BE). Heroes without a
/// change keep the game's list. The block is assembled again on every change, with the lists in it.
/// </summary>
public static class L2StartSpells
{
    public const int Site = 0xED1A, Slots = 36, HeroBlock = 0x7E0BAD, HeroStride = 0xBE, SpellList = 0x96;
    private static readonly byte[] SiteOriginal = { 0xE2, 0x20, 0xA9, 0xFF, 0x8D, 0x7F, 0x0A };
    private const string Magic = "LF2SP";
    private const int BlockSize = 0x400, CodeAt = 0x10;

    /// <summary>File offset of the block, or -1.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= Site + 4 || rom.ReadByte(Site) != 0x22) return -1;
        int at = L2Rom.SnesToFile((int)rom.ReadUInt24Le(Site + 1)) - CodeAt;
        return at >= 0 && at + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(at, Magic.Length)) == Magic ? at : -1;
    }

    public static bool CanChange(RomBuffer rom) =>
        Block(rom) >= 0 || rom.Length > Site + SiteOriginal.Length && rom.ReadBytes(Site, SiteOriginal.Length).SequenceEqual(SiteOriginal);

    /// <summary>The changed lists in the ROM (hero -> spells), read back from the block's data.</summary>
    public static Dictionary<int, List<int>> Changed(RomBuffer rom)
    {
        var d = new Dictionary<int, List<int>>();
        int b = Block(rom);
        if (b < 0) return d;
        int heroes = rom.ReadByte(b + Magic.Length);
        for (int k = 0; k < heroes; k++)
        {
            int e = b + 0x200 + k * (Slots + 1);
            int hero = rom.ReadByte(e);
            d[hero] = rom.ReadBytes(e + 1, Slots).TakeWhile(x => x != 0xFF).Select(x => (int)x).ToList();
        }
        return d;
    }

    /// <summary>The spells a hero starts with: the changed list, or the game's own.</summary>
    public static List<int> Of(RomBuffer rom, int hero) =>
        Changed(rom).TryGetValue(hero, out var l) ? l : L2Characters.ReadStart(rom).Heroes[hero].Spells.ToList();

    /// <summary>Sets a hero's starting spells (the game's own list again: the change is dropped).</summary>
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
        int b = Block(rom);
        if (lists.Count == 0 && b < 0) return;
        // (with no change left the block stays, doing only the game's three instructions, so its space is reused later)
        if (b < 0)
        {
            if (!CanChange(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's where the starting spells hook goes.");
            b = L2Rom.Allocate(rom, BlockSize);
        }
        int code = L2Rom.FileToSnes(b + CodeAt), data = L2Rom.FileToSnes(b + 0x200);
        var c = new List<byte> { 0xE2, 0x20, 0xA9, 0xFF, 0x8D, 0x7F, 0x0A };   // the game's SEP #$20 / LDA #$FF / STA $0A7F
        var heroes = lists.Keys.OrderBy(h => h).ToList();
        var blob = new List<byte>();
        foreach (int h in heroes)
        {
            int src = data + blob.Count + 1, dst = HeroBlock + h * HeroStride + SpellList;
            c.AddRange(new byte[] { 0xA2, 0x00, 0x00 });                                     // LDX #0
            c.AddRange(new byte[] { 0xBF, (byte)src, (byte)(src >> 8), (byte)(src >> 16) });  // LDA list,X
            c.AddRange(new byte[] { 0x9F, (byte)dst, (byte)(dst >> 8), (byte)(dst >> 16) });  // STA $7E:spells,X
            c.AddRange(new byte[] { 0xE8, 0xE0, Slots, 0x00, 0xD0, 0xF2 });                   // INX / CPX #36 / BNE
            blob.Add((byte)h);
            var l = lists[h];
            blob.AddRange(Enumerable.Range(0, Slots).Select(i => i < l.Count ? (byte)l[i] : (byte)0xFF));
        }
        c.Add(0x6B);   // RTL
        if (CodeAt + c.Count > 0x200 || 0x200 + blob.Count > BlockSize) throw new InvalidOperationException("Too many changed heroes for the block.");
        rom.WriteBytes(b, new byte[BlockSize]);
        rom.WriteBytes(b, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(b + Magic.Length, (byte)heroes.Count);
        rom.WriteBytes(b + CodeAt, c.ToArray());
        rom.WriteBytes(b + 0x200, blob.ToArray());
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)code, (byte)(code >> 8), (byte)(code >> 16), 0xEA, 0xEA, 0xEA });
    }
}
