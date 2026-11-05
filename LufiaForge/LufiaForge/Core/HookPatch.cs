namespace LufiaForge.Core;

/// <summary>
/// A patch made of same-size changes at fixed places of the game's code, some of which point into a block of added code
/// (their 24-bit addresses are kept as offsets into the block until the block has its place). Added only when every
/// place still holds the game's bytes; found again from the places (each pointer into the block must agree).
/// </summary>
public sealed class HookPatch
{
    /// <summary>A change at file offset <see cref="At"/>; <see cref="Relocs"/>: positions in <see cref="Patched"/> of
    /// 24-bit addresses into the block (stored as the offset into it).</summary>
    public sealed record Site(int At, byte[] Original, byte[] Patched, int[] Relocs)
    {
        public Site(int at, string original, string patched, params int[] relocs) : this(at, Hex(original), Hex(patched), relocs) { }
    }

    /// <summary>The added code; <see cref="Relocs"/>: positions of 24-bit addresses into the block itself.</summary>
    public sealed record Block(byte[] Bytes, int[] Relocs)
    {
        public Block(string bytes, params int[] relocs) : this(Hex(bytes), relocs) { }
    }

    public IReadOnlyList<Site> Sites { get; }
    public Block Code { get; }

    public HookPatch(Site[] sites, Block code) { Sites = sites; Code = code; }

    public static byte[] Hex(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    private static int U24(byte[] b, int p) => b[p] | b[p + 1] << 8 | b[p + 2] << 16;

    private static bool InReloc(int[] relocs, int k) => relocs.Any(r => k >= r && k < r + 3);

    /// <summary>Every place holds the game's bytes.</summary>
    public bool IsOriginal(RomBuffer rom) =>
        Sites.All(s => s.At + s.Original.Length <= rom.Length && rom.ReadBytes(s.At, s.Original.Length).AsSpan().SequenceEqual(s.Original));

    /// <summary>The file offset of the block when every place holds the patch and points at one block holding its code,
    /// else -1.</summary>
    public int AppliedAt(RomBuffer rom, Func<int, int> snesToFile)
    {
        int block = -1;
        foreach (var s in Sites)
        {
            if (s.At + s.Patched.Length > rom.Length) return -1;
            var b = rom.ReadBytes(s.At, s.Patched.Length);
            for (int k = 0; k < b.Length; k++)
                if (!InReloc(s.Relocs, k) && b[k] != s.Patched[k]) return -1;
            foreach (int r in s.Relocs)
            {
                int start = snesToFile(U24(b, r)) - U24(s.Patched, r);
                if (block < 0) block = start;
                else if (block != start) return -1;
            }
        }
        if (block < 0 || block + Code.Bytes.Length > rom.Length) return -1;
        var c = rom.ReadBytes(block, Code.Bytes.Length);
        for (int k = 0; k < c.Length; k++)
            if (!InReloc(Code.Relocs, k) && c[k] != Code.Bytes[k]) return -1;
        return block;
    }

    /// <summary>Writes the block at file offset <paramref name="blockFile"/> (SNES <paramref name="blockSnes"/>) and the
    /// changes pointing at it.</summary>
    public void Write(RomBuffer rom, int blockFile, int blockSnes)
    {
        rom.WriteBytes(blockFile, Fill(Code.Bytes, Code.Relocs, blockSnes));
        foreach (var s in Sites) rom.WriteBytes(s.At, Fill(s.Patched, s.Relocs, blockSnes));
    }

    private static byte[] Fill(byte[] template, int[] relocs, int blockSnes)
    {
        var b = (byte[])template.Clone();
        foreach (int r in relocs)
        {
            int a = blockSnes + U24(template, r);
            b[r] = (byte)a; b[r + 1] = (byte)(a >> 8); b[r + 2] = (byte)(a >> 16);
        }
        return b;
    }

    /// <summary>Puts the game's bytes back at every place (the block is left to the caller).</summary>
    public void Restore(RomBuffer rom)
    {
        foreach (var s in Sites) rom.WriteBytes(s.At, s.Original);
    }
}
