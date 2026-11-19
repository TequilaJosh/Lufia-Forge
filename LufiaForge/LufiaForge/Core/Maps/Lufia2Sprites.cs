using LufiaForge.Core.Battle;

namespace LufiaForge.Core.Maps;

// the field monsters of Lufia II: Rise of the Sinistrals (USA), as map character sprites for Lufia 1 (for visible
// monsters, VisibleMonsters).
// Lufia II's map sprites: sprite n -> u16 at $CF:F000 + 2n -> a 5-byte entry in bank CF: [kind][palette row]
// [LoROM address of the graphics, 4bpp, uncompressed]. its 8 sprite palette rows are fixed, at $A6:BF00 (file
// 0x133F00). the monster kinds (seen in the game's dungeons and by drawing every entry):
// - 10: 16x16, 6 frames: front, front, side, side, back, back (side frames face left), 4 tiles row by row
// - 11: 16x32, 6 frames like above, 256 bytes: rows 0-7 at +00/+20, 8-15 at +80/+A0, 16-23 at +40/+60, 24-31 at +C0/+E0
// - 13: 32x32, 6 frames like above, 16 tiles row by row
// - 08: 16x16, 2 frames (the same whichever way it faces). 0B: 32x32, 2 frames
// Lufia 1 draws a map character as 16x24 (two 16x16 objects, $01:A6D6), or 16x16 / 32x32 by its other routine.
// its frames are side, side (drawn flipped when it faces right), side, side, front, front, back, back. so 16x16
// monsters get 8 empty rows on top, and 16x32 ones lose their top 8 rows if those are empty in every frame (if not
// they go on the left of a 32x32 frame, since the game draws 32x32 characters from their tile's left edge, $01:A487)
public static class Lufia2Sprites
{
    private const int PointerTable = 0x27F000, PaletteTable = 0x133F00, SpriteCount = 251;

    // the monsters (Lufia II sprite number, and a name since Lufia II gives them none)
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

    // Lufia II's 8 sprite palette rows (colours 1-15, BGR555, file 0x133F00), so a monster gets its whole row
    // and monsters that share a row in Lufia II share one in Lufia 1 too
    public static readonly ushort[][] Palettes =
    {
        new ushort[] { 0x0001, 0x0D30, 0x15F5, 0x473C, 0x08C2, 0x4549, 0x6230, 0x088A, 0x08F6, 0x2DAB, 0x56F5, 0x29F9, 0x5B1F, 0x7B17, 0x7FFF },
        new ushort[] { 0x0042, 0x3864, 0x5109, 0x794B, 0x0902, 0x0008, 0x0096, 0x25C5, 0x4B0D, 0x45AD, 0x7318, 0x21F9, 0x531F, 0x499E, 0x7FFF },
        new ushort[] { 0x0C22, 0x004C, 0x1617, 0x337F, 0x0902, 0x01A0, 0x02C0, 0x39C6, 0x6F72, 0x41D2, 0x5ADB, 0x361B, 0x531F, 0x37E0, 0x7FFF },
        new ushort[] { 0x0842, 0x0C69, 0x0871, 0x24DA, 0x51BC, 0x3409, 0x58B1, 0x7156, 0x054B, 0x0E11, 0x0F1A, 0x2529, 0x4210, 0x5EF7, 0x739C },
        new ushort[] { 0x0842, 0x3081, 0x4102, 0x5989, 0x6A50, 0x1922, 0x21E4, 0x32E7, 0x048B, 0x1531, 0x21DA, 0x150A, 0x25D2, 0x2ABA, 0x739C },
        new ushort[] { 0x0842, 0x08E2, 0x1165, 0x1648, 0x3355, 0x108D, 0x1915, 0x2E3C, 0x3526, 0x51EC, 0x72D3, 0x1064, 0x20C9, 0x312D, 0x739C },
        new ushort[] { 0x0421, 0x2108, 0x3DEF, 0x5EF7, 0x088C, 0x0CFA, 0x31DD, 0x0DC0, 0x3C21, 0x7C62, 0x7EAF, 0x0D30, 0x1637, 0x3B7E, 0x7FFF },
        new ushort[] { 0x0421, 0x0429, 0x049B, 0x1C61, 0x2101, 0x3241, 0x19C6, 0x4B72, 0x00CC, 0x023A, 0x379F, 0x2927, 0x4E0D, 0x6F16, 0x7FFF },
    };

    private static readonly Dictionary<string, int> Rows = new()
    {
        ["Octopus"] = 1, ["Snail"] = 1, ["Ninja"] = 5, ["Worm"] = 5, ["Mammoth"] = 4, ["Deer"] = 1, ["Beetle"] = 4, ["Crab"] = 3,
        ["Eyeball"] = 3, ["Spider"] = 3, ["Cat"] = 3, ["Scorpion"] = 3, ["Mushroom"] = 3, ["Rooster"] = 3, ["Stump"] = 4, ["Bat"] = 3,
        ["Fly"] = 1, ["Butterfly"] = 4, ["Jelly"] = 3, ["Flower"] = 5, ["Blue Flower"] = 4, ["Hornet"] = 4, ["Armor"] = 4, ["Slime"] = 4,
        ["Werewolf"] = 0, ["Dryad"] = 2, ["Imp"] = 1, ["Goblin"] = 1, ["Vampire"] = 3, ["Skeleton"] = 3, ["Ice Man"] = 5, ["Mud Man"] = 4,
        ["Ghost"] = 5, ["Knight"] = 1, ["Orc"] = 1, ["Troll"] = 5, ["Mummy"] = 3, ["Witch"] = 5, ["Ogre"] = 4, ["Hood"] = 5,
        ["Blue Man"] = 4, ["Wizard"] = 4, ["Siren"] = 1, ["Pumpkin"] = 4, ["Demon"] = 3, ["Wisp"] = 3, ["Shadow"] = 3, ["Snake"] = 2,
        ["Man Eater"] = 5, ["Wolf"] = 4, ["Dragon"] = 4, ["Giant"] = 4, ["Bone Knight"] = 3, ["Kraken"] = 3, ["Plant"] = 5, ["Chomper"] = 2,
        ["Frog"] = 1, ["Hydra"] = 2, ["Red Dragon"] = 3, ["Pink Demon"] = 3, ["Big Jelly"] = 4, ["Wyvern"] = 5, ["Gargoyle"] = 5,
        ["Big Spider"] = 0, ["Bone Beast"] = 0, ["Blue Beast"] = 4, ["Mantis"] = 4,
    };

    // the whole Lufia II palette row of monster name (15 colours), or null
    public static ushort[]? PaletteOf(string name) => Rows.TryGetValue(name, out int row) ? Palettes[row] : null;

    // a monster as a Lufia 1 sprite sheet: the 8 frames side by side (ARGB, alpha 0 = transparent),
    // FrameWidth x Height pixels each (16x24 or 32x32)
    public sealed record Sheet(uint[] Pixels, int FrameWidth, int Height)
    {
        public int Width => FrameWidth * 8;
    }

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);

    // the Lufia II rom without a copier header, or null (Lufia2Backgrounds.Rom)
    public static byte[]? Rom(byte[] file) => Lufia2Backgrounds.Rom(file);

    // monster number (a Lufia II sprite number) as a Lufia 1 sheet
    public static Sheet Read(byte[] rom, int number)
    {
        if (number < 0 || number >= SpriteCount) throw new ArgumentOutOfRangeException(nameof(number));
        int entry = FileOffset(0xCF0000 | rom[PointerTable + number * 2] | rom[PointerTable + number * 2 + 1] << 8);
        int kind = rom[entry], row = rom[entry + 1] & 7, graphics = FileOffset(rom[entry + 2] | rom[entry + 3] << 8 | rom[entry + 4] << 16);
        var (tilesWide, tilesHigh, frames) = kind switch
        {
            0x10 => (2, 2, 6), 0x11 => (2, 4, 6), 0x13 => (4, 4, 6), 0x08 => (2, 2, 2), 0x0B => (4, 4, 2),
            _ => throw new InvalidOperationException($"Lufia II sprite {number:X2} isn't a monster.")
        };
        var colours = new uint[16];
        for (int i = 1; i < 16; i++)
        {
            int colour = rom[PaletteTable + row * 32 + i * 2] | rom[PaletteTable + row * 32 + i * 2 + 1] << 8;
            uint red = (uint)(colour & 31), green = (uint)(colour >> 5 & 31), blue = (uint)(colour >> 10 & 31);
            colours[i] = 0xFF000000 | (red << 3 | red >> 2) << 16 | (green << 3 | green >> 2) << 8 | (blue << 3 | blue >> 2);
        }
        // Lufia II's frames as colour numbers
        int pixelWidth = tilesWide * 8, pixelHeight = tilesHigh * 8, frameBytes = tilesWide * tilesHigh * 32;
        var l2Frames = new byte[frames][];
        for (int frame = 0; frame < frames; frame++)
        {
            var pixels = new byte[pixelWidth * pixelHeight];
            for (int tile = 0; tile < tilesWide * tilesHigh; tile++)
            {
                int tx = tile % tilesWide, ty = tile / tilesWide, tileStart = (tilesWide, tilesHigh) == (2, 4) ? (ty & 1) * 0x80 + (ty >> 1) * 0x40 + tx * 0x20 : tile * 32;
                int tileOffset = graphics + frame * frameBytes + tileStart;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int bit = 7 - x, rowOffset = tileOffset + y * 2;
                        pixels[(ty * 8 + y) * pixelWidth + tx * 8 + x] = (byte)((rom[rowOffset] >> bit & 1) | (rom[rowOffset + 1] >> bit & 1) << 1 | (rom[rowOffset + 16] >> bit & 1) << 2 | (rom[rowOffset + 17] >> bit & 1) << 3);
                    }
            }
            l2Frames[frame] = pixels;
        }
        // Lufia 1's size: 16x16 -> 16x24 (empty top), 16x32 -> 16x24 if its top 8 rows are empty, otherwise 32x32
        int frameWidth, frameHeight, dx, dy;
        if (pixelHeight == 16) (frameWidth, frameHeight, dx, dy) = pixelWidth == 16 ? (16, 24, 0, 8) : (pixelWidth, pixelHeight, 0, 0);
        else if (pixelWidth == 16 && pixelHeight == 32 && l2Frames.All(px => px.Take(16 * 8).All(c => c == 0))) (frameWidth, frameHeight, dx, dy) = (16, 24, 0, -8);
        else if (pixelWidth == 16 && pixelHeight == 32) (frameWidth, frameHeight, dx, dy) = (32, 32, 0, 0);   // (on the left, the game draws a 32x32 character from its tile's left edge)
        else (frameWidth, frameHeight, dx, dy) = (pixelWidth, pixelHeight, 0, 0);
        // Lufia 1's order: side, side, side, side, front, front, back, back
        int[] order = frames == 6 ? new[] { 2, 3, 2, 3, 0, 1, 4, 5 } : new[] { 0, 1, 0, 1, 0, 1, 0, 1 };
        var sheet = new uint[frameWidth * 8 * frameHeight];
        for (int k = 0; k < 8; k++)
        {
            var source = l2Frames[order[k]];
            for (int y = 0; y < pixelHeight; y++)
                for (int x = 0; x < pixelWidth; x++)
                {
                    int colourIndex = source[y * pixelWidth + x], destX = x + dx, destY = y + dy;
                    if (colourIndex == 0 || destX < 0 || destY < 0 || destX >= frameWidth || destY >= frameHeight) continue;
                    sheet[destY * frameWidth * 8 + k * frameWidth + destX] = colours[colourIndex];
                }
        }
        return new Sheet(sheet, frameWidth, frameHeight);
    }
}
