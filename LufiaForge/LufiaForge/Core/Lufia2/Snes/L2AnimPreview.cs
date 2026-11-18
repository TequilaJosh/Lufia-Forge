using System.IO.Compression;

namespace LufiaForge.Core.Lufia2.Snes;

// a battle animation shown the way the game shows it. a battle gets set up on SnesMachine (the work RAM of a real one,
// L2AnimSeed, the battle's screen registers, a battle background on BG1 and a monster as the sprite the spell lands on,
// both from the rom) and the game's own animation code plays the animation (L2AnimPlayer). no sound
public static class L2AnimPreview
{
    // where the monster stands (its feet, centre) in the battle the seed comes from
    public const int MonsterX = 144, MonsterFeetY = 104;

    // Hero = the character attacking (0 Maxim ... 6 Lexis), whose script a weapon swing's table (op 0F) picks. the
    // seed's attacker is Maxim in party slot 0
    public sealed record Options(int Background = 0, int MonsterSprite = -1, int MonsterPalette = 0, int MaxFrames = 900, int Hero = 0);

    // plays animation id: the frames (256 x 224, 0xAARRGGBB) and why it stopped early, if it did
    public static (List<uint[]> Frames, string? Problem) Play(RomBuffer rom, int id, Options? o = null)
    {
        o ??= new Options();
        var player = Setup(rom.ToArray(), rom, o);
        player.Play(id, o.MaxFrames);
        // the first picture is drawn before the battle's registers are all set, so it's left out
        if (player.Frames.Count > 1) player.Frames.RemoveAt(0);
        return (player.Frames, player.Problem);
    }

    public static L2AnimPlayer Setup(byte[] romBytes, RomBuffer rom, Options o)
    {
        var player = new L2AnimPlayer(romBytes);
        var machine = player.Machine;
        // work RAM
        using (var inflate = new DeflateStream(new System.IO.MemoryStream(Convert.FromBase64String(L2AnimSeed.Wram)), CompressionMode.Decompress))
        {
            var blob = new byte[0x2000 + 0x700 + 0x100];
            int got = 0, n;
            while (got < blob.Length && (n = inflate.Read(blob, got, blob.Length - got)) > 0) got += n;
            Array.Copy(blob, 0, machine.Wram, 0, 0x2000);
            Array.Copy(blob, 0x2000, machine.Wram, 0x2800, 0x700);
            Array.Copy(blob, 0x2700, machine.Wram, 0x4F00, 0x100);
        }
        // the attacker's character ($153D + party slot, read by op 0F)
        if (o.Hero is > 0 and < L2SpellAnims.CharacterTable) machine.Wram[0x153D] = (byte)o.Hero;
        player.BattleScreen();
        // the battle background on BG1: map at VRAM word 0, tiles from tile $100 of the characters at word $2000
        if (o.Background >= 0 && o.Background < L2Gfx.BgCount)
        {
            var (map, tiles, pal) = L2Gfx.BgRecord(rom, o.Background);
            var tileData = L2Rom.ReadFile(rom, L2Gfx.BgTileFile + tiles);
            Array.Copy(tileData, 0, machine.Vram, 0x6000, Math.Min(tileData.Length, 0x10000 - 0x6000 - 0x2000));
            var mapData = L2Rom.ReadFile(rom, L2Gfx.BgMapFile + map);
            int prev = 0;
            for (int i = 0; i < 0x400; i++)
            {
                int raw = 2 * i + 1 < mapData.Length ? mapData[2 * i] | mapData[2 * i + 1] << 8 : 0;
                int entry = i == 0 ? raw : (prev - raw) & 0xFFFF;
                prev = entry;
                machine.Vram[i * 2] = (byte)entry; machine.Vram[i * 2 + 1] = (byte)(entry >> 8);
            }
            for (int c = 0; c < 32; c++)
            {
                int colour = rom.ReadUInt16Le(L2Gfx.BgPalettes + pal * 0x40 + c * 2);
                machine.Cgram[(32 + c) * 2] = (byte)colour; machine.Cgram[(32 + c) * 2 + 1] = (byte)(colour >> 8);
            }
        }
        // OAM buffer ($0100, high table $0300): only the monster
        for (int i = 0; i < 128; i++) { machine.Wram[0x100 + i * 4] = 0; machine.Wram[0x101 + i * 4] = 0xE0; machine.Wram[0x102 + i * 4] = 0; machine.Wram[0x103 + i * 4] = 0; }
        for (int i = 0; i < 32; i++) machine.Wram[0x300 + i] = 0;
        if (o.MonsterSprite > 0 && o.MonsterSprite <= L2MonsterSprites.Count(rom))   // sprites are 1 .. Count
        {
            var data = L2MonsterSprites.Read(rom, o.MonsterSprite);
            var (w, h, px) = L2MonsterSprites.Pixels(rom, o.MonsterSprite, data);
            var colours = L2MonsterSprites.Palette(data, o.MonsterPalette);
            // we draw it behind the animation's sprites (the battle's own character sprites are left out)
            var under = new uint[SnesRenderer.Width * SnesRenderer.Height];
            int left = MonsterX - w / 2, top = MonsterFeetY - h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = left + x, sy = top + y, colourIndex = px[y * w + x];
                    if (colourIndex == 0 || sx < 0 || sy < 0 || sx >= SnesRenderer.Width || sy >= SnesRenderer.Height) continue;
                    under[sy * SnesRenderer.Width + sx] = L2Gfx.Argb(colours[colourIndex]);
                }
            player.Underlay = under;
        }
        // colours: the CGRAM buffer ($0320) and its copy ($7F:F1DB) the fades start from
        Array.Copy(machine.Cgram, 0, machine.Wram, 0x320, 0x200);
        Array.Copy(machine.Cgram, 0, machine.Wram, 0x1F1DB, 0x200);
        Array.Copy(machine.Wram, 0x100, machine.Oam, 0, 0x220);
        player.HideTable1 = true;
        return player;
    }
}
