namespace LufiaForge.Core;

// a patch made of same-size changes at fixed places in the game's code, some of them pointing into a block of added
// code (their 24-bit addresses are kept as offsets into the block until the block has a place).
// only goes in when every place still has the game's bytes. found again from the places (every pointer into the
// block has to agree)
public sealed class HookPatch
{
    // a change at file offset At. Relocs = positions in Patched of 24-bit addresses into the block
    // (stored as the offset into it)
    public sealed record Site(int At, byte[] Original, byte[] Patched, int[] Relocs)
    {
        public Site(int at, string original, string patched, params int[] relocs) : this(at, Hex(original), Hex(patched), relocs) { }
    }

    // the added code. Relocs = positions of 24-bit addresses into the block itself
    public sealed record Block(byte[] Bytes, int[] Relocs)
    {
        public Block(string bytes, params int[] relocs) : this(Hex(bytes), relocs) { }
    }

    public IReadOnlyList<Site> Sites { get; }
    public Block Code { get; }

    public HookPatch(Site[] sites, Block code) { Sites = sites; Code = code; }

    public static byte[] Hex(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    private static int U24(byte[] bytes, int position) => bytes[position] | bytes[position + 1] << 8 | bytes[position + 2] << 16;

    private static bool InReloc(int[] relocs, int index) => relocs.Any(r => index >= r && index < r + 3);

    // every place still has the game's bytes
    public bool IsOriginal(RomBuffer rom) =>
        Sites.All(s => s.At + s.Original.Length <= rom.Length && rom.ReadBytes(s.At, s.Original.Length).AsSpan().SequenceEqual(s.Original));

    // the block's file offset when every place has the patch and they all point at one block with our code in it,
    // else -1
    public int AppliedAt(RomBuffer rom, Func<int, int> snesToFile)
    {
        int blockOffset = -1;
        foreach (var site in Sites)
        {
            if (site.At + site.Patched.Length > rom.Length) return -1;
            var placeBytes = rom.ReadBytes(site.At, site.Patched.Length);
            for (int k = 0; k < placeBytes.Length; k++)
                if (!InReloc(site.Relocs, k) && placeBytes[k] != site.Patched[k]) return -1;
            foreach (int reloc in site.Relocs)
            {
                int start = snesToFile(U24(placeBytes, reloc)) - U24(site.Patched, reloc);
                if (blockOffset < 0) blockOffset = start;
                else if (blockOffset != start) return -1;
            }
        }
        if (blockOffset < 0 || blockOffset + Code.Bytes.Length > rom.Length) return -1;
        var codeBytes = rom.ReadBytes(blockOffset, Code.Bytes.Length);
        for (int k = 0; k < codeBytes.Length; k++)
            if (!InReloc(Code.Relocs, k) && codeBytes[k] != Code.Bytes[k]) return -1;
        return blockOffset;
    }

    // writes the block at file offset blockFile (snes address blockSnes) and the changes pointing at it
    public void Write(RomBuffer rom, int blockFile, int blockSnes)
    {
        rom.WriteBytes(blockFile, Fill(Code.Bytes, Code.Relocs, blockSnes));
        foreach (var site in Sites) rom.WriteBytes(site.At, Fill(site.Patched, site.Relocs, blockSnes));
    }

    private static byte[] Fill(byte[] template, int[] relocs, int blockSnes)
    {
        var filled = (byte[])template.Clone();
        foreach (int reloc in relocs)
        {
            int address = blockSnes + U24(template, reloc);
            filled[reloc] = (byte)address; filled[reloc + 1] = (byte)(address >> 8); filled[reloc + 2] = (byte)(address >> 16);
        }
        return filled;
    }

    // puts the game's bytes back at every place (freeing the block is up to the caller)
    public void Restore(RomBuffer rom)
    {
        foreach (var site in Sites) rom.WriteBytes(site.At, site.Original);
    }
}
