using LufiaForge.Core.Battle;

namespace LufiaForge.Core.Maps;

/// <summary>
/// The field monsters of Lufia II: Rise of the Sinistrals (USA), as map character sprites for Lufia 1 (for visible
/// monsters, <see cref="VisibleMonsters"/>).
/// <para>
/// Lufia II's map sprites: sprite n → u16 at $CF:F000 + 2n → a 5-byte entry in bank CF: [kind][palette row]
/// [LoROM address of the graphics, 4bpp, uncompressed]. Its 8 sprite palette rows are fixed, at $A6:BF00 (file
/// 0x133F00). The monster kinds (seen in the game's dungeons and by drawing every entry):
/// <list type="bullet">
/// <item>10: 16x16, 6 frames: front, front, side, side, back, back (side frames face left); 4 tiles row by row</item>
/// <item>11: 16x32, 6 frames as above; 256 bytes: rows 0-7 at +00/+20, 8-15 at +80/+A0, 16-23 at +40/+60, 24-31 at +C0/+E0</item>
/// <item>13: 32x32, 6 frames as above; 16 tiles row by row</item>
/// <item>08: 16x16, 2 frames (the same every way it faces); 0B: 32x32, 2 frames</item>
/// </list>
/// Lufia 1 draws a map character as 16x24 (two 16x16 objects, $01:A6D6), or 16x16 / 32x32 by its other routine;
/// its frames are side, side (drawn flipped when it faces right), side, side, front, front, back, back. So 16x16
/// monsters get 8 empty rows on top, 16x32 ones lose their top 8 rows when those are empty in every frame (else they
/// go in the middle of a 32x32 frame).
/// </para>
/// </summary>
public static class Lufia2Sprites
{
    private const int PointerTable = 0x27F000, Palettes = 0x133F00, SpriteCount = 251;

    /// <summary>The monsters (Lufia II sprite number, a name: Lufia II gives them none).</summary>
    public static readonly (int Number, string Name)[] Monsters =
    {
        (0x80, "Octopus"), (0x81, "Snail"), (0x82, "Ninja"), (0x83, "Worm"), (0x84, "Mammoth"), (0x85, "Deer"),
        (0x86, "Beetle"), (0x87, "Crab"), (0x88, "Eyeball"), (0x89, "Spider"), (0x8A, "Cat"), (0x8B, "Scorpion"),
        (0x8C, "Mushroom"), (0x8D, "Rooster"), (0x8E, "Stump"), (0x8F, "Bat"), (0x92, "Fly"), (0x93, "Butterfly"),
        (0x94, "Jelly"), (0x96, "Flower"), (0x97, "Blue Flower"), (0x98, "Hornet"), (0x99, "Armor"), (0x9A, "Slime"),
        (0x9B, "Werewolf"), (0x9C, "Dryad"), (0x9D, "Imp"), (0x9E, "Goblin"), (0x9F, "Vampire"), (0xA0, "Skeleton"),
        (0xA1, "Ice Man"), (0xA2, "Mud Man"), (0xA3, "Ghost"), (0xA5, "Knight"), (0xA6, "Orc"), (0xA7, "Troll"),
        (0xA8, "Mummy"), (0xA9, "Witch"), (0xAA, "Ogre"), (0xAB, "Hood"), (0xAC, "Blue Man"), (0xAD, "Wizard"),
        (0xAE, "Siren"), (0xAF, "Pumpkin"), (0xB0, "Demon"), (0xB1, "Wisp"), (0xB2, "Shadow"), (0xB3, "Snake"),
        (0xB6, "Man Eater"), (0xB7, "Wolf"), (0xB8, "Dragon"), (0xB9, "Giant"), (0xBA, "Bone Knight"), (0xBB, "Kraken"),
        (0xBC, "Plant"), (0xBD, "Chomper"), (0xBE, "Frog"), (0xBF, "Hydra"), (0xC0, "Red Dragon"), (0xC1, "Pink Demon"),
        (0xC2, "Big Jelly"), (0xC3, "Wyvern"), (0xC4, "Gargoyle"), (0xC6, "Big Spider"), (0xD0, "Bone Beast"),
        (0xD3, "Blue Beast"), (0xD4, "Mantis"),
    };

    /// <summary>A monster as a Lufia 1 sprite sheet: the 8 frames side by side (ARGB, alpha 0 = transparent),
    /// <paramref name="FrameWidth"/> x <paramref name="Height"/> pixels each (16x24 or 32x32).</summary>
    public sealed record Sheet(uint[] Pixels, int FrameWidth, int Height)
    {
        public int Width => FrameWidth * 8;
    }

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);

    /// <summary>The Lufia II ROM without a copier header, or null (<see cref="Lufia2Backgrounds.Rom"/>).</summary>
    public static byte[]? Rom(byte[] file) => Lufia2Backgrounds.Rom(file);

    /// <summary>Monster <paramref name="number"/> (a Lufia II sprite number) as a Lufia 1 sheet.</summary>
    public static Sheet Read(byte[] r, int number)
    {
        if (number < 0 || number >= SpriteCount) throw new ArgumentOutOfRangeException(nameof(number));
        int e = FileOffset(0xCF0000 | r[PointerTable + number * 2] | r[PointerTable + number * 2 + 1] << 8);
        int kind = r[e], row = r[e + 1] & 7, gfx = FileOffset(r[e + 2] | r[e + 3] << 8 | r[e + 4] << 16);
        var (w, h, frames) = kind switch
        {
            0x10 => (2, 2, 6), 0x11 => (2, 4, 6), 0x13 => (4, 4, 6), 0x08 => (2, 2, 2), 0x0B => (4, 4, 2),
            _ => throw new InvalidOperationException($"Lufia II sprite {number:X2} isn't a monster.")
        };
        var colours = new uint[16];
        for (int i = 1; i < 16; i++)
        {
            int c = r[Palettes + row * 32 + i * 2] | r[Palettes + row * 32 + i * 2 + 1] << 8;
            uint R = (uint)(c & 31), G = (uint)(c >> 5 & 31), B = (uint)(c >> 10 & 31);
            colours[i] = 0xFF000000 | (R << 3 | R >> 2) << 16 | (G << 3 | G >> 2) << 8 | (B << 3 | B >> 2);
        }
        // Lufia II's frames as colour numbers
        int pw = w * 8, ph = h * 8, bytes = w * h * 32;
        var l2 = new byte[frames][];
        for (int f = 0; f < frames; f++)
        {
            var px = new byte[pw * ph];
            for (int t = 0; t < w * h; t++)
            {
                int tx = t % w, ty = t / w, off = (w, h) == (2, 4) ? (ty & 1) * 0x80 + (ty >> 1) * 0x40 + tx * 0x20 : t * 32;
                int o = gfx + f * bytes + off;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int bit = 7 - x, a = o + y * 2;
                        px[(ty * 8 + y) * pw + tx * 8 + x] = (byte)((r[a] >> bit & 1) | (r[a + 1] >> bit & 1) << 1 | (r[a + 16] >> bit & 1) << 2 | (r[a + 17] >> bit & 1) << 3);
                    }
            }
            l2[f] = px;
        }
        // Lufia 1's size: 16x16 → 16x24 (empty top), 16x32 → 16x24 when its top 8 rows are empty, else 32x32
        int fw, fh, dx, dy;
        if (ph == 16) (fw, fh, dx, dy) = pw == 16 ? (16, 24, 0, 8) : (pw, ph, 0, 0);
        else if (pw == 16 && ph == 32 && l2.All(px => px.Take(16 * 8).All(c => c == 0))) (fw, fh, dx, dy) = (16, 24, 0, -8);
        else if (pw == 16 && ph == 32) (fw, fh, dx, dy) = (32, 32, 8, 0);
        else (fw, fh, dx, dy) = (pw, ph, 0, 0);
        // Lufia 1's order: side, side, side, side, front, front, back, back
        int[] order = frames == 6 ? new[] { 2, 3, 2, 3, 0, 1, 4, 5 } : new[] { 0, 1, 0, 1, 0, 1, 0, 1 };
        var sheet = new uint[fw * 8 * fh];
        for (int k = 0; k < 8; k++)
        {
            var src = l2[order[k]];
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    int c = src[y * pw + x], sx = x + dx, sy = y + dy;
                    if (c == 0 || sx < 0 || sy < 0 || sx >= fw || sy >= fh) continue;
                    sheet[sy * fw * 8 + k * fw + sx] = colours[c];
                }
        }
        return new Sheet(sheet, fw, fh);
    }
}
