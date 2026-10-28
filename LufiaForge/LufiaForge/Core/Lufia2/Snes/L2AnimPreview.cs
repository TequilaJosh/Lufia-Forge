using System.IO.Compression;

namespace LufiaForge.Core.Lufia2.Snes;

/// <summary>
/// A battle animation shown as the game shows it: a battle is set up on <see cref="SnesMachine"/> (the work RAM of a real
/// one, <see cref="L2AnimSeed"/>; the battle's screen registers; a battle background on BG1 and a monster as the sprite
/// the spell lands on, both from the ROM) and the game's own animation code plays the animation
/// (<see cref="L2AnimPlayer"/>). Sound isn't played.
/// </summary>
public static class L2AnimPreview
{
    /// <summary>Where the monster stands (its feet, centre) in the battle the seed comes from.</summary>
    public const int MonsterX = 144, MonsterFeetY = 104;

    /// <summary><paramref name="Hero"/>: the character attacking (0 Maxim ... 6 Lexis), whose script a weapon swing's
    /// table (op 0F) picks; the seed's attacker is Maxim in party slot 0.</summary>
    public sealed record Options(int Background = 0, int MonsterSprite = -1, int MonsterPalette = 0, int MaxFrames = 900, int Hero = 0);

    /// <summary>Plays animation <paramref name="id"/>: the frames (256 x 224, 0xAARRGGBB) and why it stopped early, if it did.</summary>
    public static (List<uint[]> Frames, string? Problem) Play(RomBuffer rom, int id, Options? o = null)
    {
        o ??= new Options();
        var p = Setup(rom.ToArray(), rom, o);
        p.Play(id, o.MaxFrames);
        // the first picture is drawn before the battle's registers are all set: left out
        if (p.Frames.Count > 1) p.Frames.RemoveAt(0);
        return (p.Frames, p.Problem);
    }

    public static L2AnimPlayer Setup(byte[] romBytes, RomBuffer rom, Options o)
    {
        var p = new L2AnimPlayer(romBytes);
        var m = p.Machine;
        // work RAM
        using (var z = new DeflateStream(new System.IO.MemoryStream(Convert.FromBase64String(L2AnimSeed.Wram)), CompressionMode.Decompress))
        {
            var blob = new byte[0x2000 + 0x700 + 0x100];
            int got = 0, n;
            while (got < blob.Length && (n = z.Read(blob, got, blob.Length - got)) > 0) got += n;
            Array.Copy(blob, 0, m.Wram, 0, 0x2000);
            Array.Copy(blob, 0x2000, m.Wram, 0x2800, 0x700);
            Array.Copy(blob, 0x2700, m.Wram, 0x4F00, 0x100);
        }
        // the attacker's character ($153D + party slot, read by op 0F)
        if (o.Hero is > 0 and < L2SpellAnims.CharacterTable) m.Wram[0x153D] = (byte)o.Hero;
        p.BattleScreen();
        // the battle background on BG1: map at VRAM word 0, tiles from tile $100 of the characters at word $2000
        if (o.Background >= 0 && o.Background < L2Gfx.BgCount)
        {
            var (map, tiles, pal) = L2Gfx.BgRecord(rom, o.Background);
            var t = L2Rom.ReadFile(rom, L2Gfx.BgTileFile + tiles);
            Array.Copy(t, 0, m.Vram, 0x6000, Math.Min(t.Length, 0x10000 - 0x6000 - 0x2000));
            var md = L2Rom.ReadFile(rom, L2Gfx.BgMapFile + map);
            int prev = 0;
            for (int i = 0; i < 0x400; i++)
            {
                int raw = 2 * i + 1 < md.Length ? md[2 * i] | md[2 * i + 1] << 8 : 0;
                int v = i == 0 ? raw : (prev - raw) & 0xFFFF;
                prev = v;
                m.Vram[i * 2] = (byte)v; m.Vram[i * 2 + 1] = (byte)(v >> 8);
            }
            for (int c = 0; c < 32; c++)
            {
                int v = rom.ReadUInt16Le(L2Gfx.BgPalettes + pal * 0x40 + c * 2);
                m.Cgram[(32 + c) * 2] = (byte)v; m.Cgram[(32 + c) * 2 + 1] = (byte)(v >> 8);
            }
        }
        // OAM buffer ($0100, high table $0300): only the monster
        for (int i = 0; i < 128; i++) { m.Wram[0x100 + i * 4] = 0; m.Wram[0x101 + i * 4] = 0xE0; m.Wram[0x102 + i * 4] = 0; m.Wram[0x103 + i * 4] = 0; }
        for (int i = 0; i < 32; i++) m.Wram[0x300 + i] = 0;
        if (o.MonsterSprite > 0 && o.MonsterSprite < L2MonsterSprites.Count(rom))
        {
            var data = L2MonsterSprites.Read(rom, o.MonsterSprite);
            var (w, h, px) = L2MonsterSprites.Pixels(rom, o.MonsterSprite, data);
            var colours = L2MonsterSprites.Palette(data, o.MonsterPalette);
            // drawn by Lufia Forge behind the animation's sprites (the battle's own character sprites are left out)
            var under = new uint[SnesRenderer.Width * SnesRenderer.Height];
            int left = MonsterX - w / 2, top = MonsterFeetY - h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = left + x, sy = top + y, ci = px[y * w + x];
                    if (ci == 0 || sx < 0 || sy < 0 || sx >= SnesRenderer.Width || sy >= SnesRenderer.Height) continue;
                    under[sy * SnesRenderer.Width + sx] = L2Gfx.Argb(colours[ci]);
                }
            p.Underlay = under;
        }
        // colours: the CGRAM buffer ($0320) and its copy ($7F:F1DB) the fades start from
        Array.Copy(m.Cgram, 0, m.Wram, 0x320, 0x200);
        Array.Copy(m.Cgram, 0, m.Wram, 0x1F1DB, 0x200);
        Array.Copy(m.Wram, 0x100, m.Oam, 0, 0x220);
        p.HideTable1 = true;
        return p;
    }
}
