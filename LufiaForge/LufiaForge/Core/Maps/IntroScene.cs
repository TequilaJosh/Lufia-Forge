namespace LufiaForge.Core.Maps;

// the opening scene's setup routine ($01:C0D4). it loads the intro map (LDA #$4F / JSR $BD70) and then writes over
// the map's palette byte (LDA #$12 / STA $7F0009), so the floating island shows in palette 18 (the sepia one) instead
// of the palette in its header. we read both values out of those instructions
public static class IntroScene
{
    private const int LoadMapAt = 0xC0D4, SetPaletteAt = 0xC0F0;

    // (intro map, palette it's shown in), or null if the routine doesn't look like we expect
    public static (int Map, int Palette)? Read(RomBuffer rom)
    {
        if (rom.Length <= SetPaletteAt + 6) return null;
        bool loadMap = rom.ReadByte(LoadMapAt) == 0xA9 && rom.ReadByte(LoadMapAt + 2) == 0x20
                       && rom.ReadByte(LoadMapAt + 3) == 0x70 && rom.ReadByte(LoadMapAt + 4) == 0xBD;
        bool setPalette = rom.ReadByte(SetPaletteAt) == 0xA9 && rom.ReadByte(SetPaletteAt + 2) == 0x8F
                      && rom.ReadByte(SetPaletteAt + 3) == 0x09 && rom.ReadByte(SetPaletteAt + 4) == 0x00 && rom.ReadByte(SetPaletteAt + 5) == 0x7F;
        if (!loadMap || !setPalette) return null;
        return (rom.ReadByte(LoadMapAt + 1), rom.ReadByte(SetPaletteAt + 1));
    }

    // the palette a map shows in during cutscenes (the intro map's override, otherwise its own)
    public static int PaletteFor(RomBuffer rom, LufiaMap map) =>
        Read(rom) is { } intro && intro.Map == map.MapId ? intro.Palette : map.PaletteIndex;

    // BG2 scroll of a drifting second layer (mode 2, the intro's clouds) after fieldFrames frames of the field
    // running: one pixel right and down per frame from (400, 400), measured in the game.
    // the vertical value is one more because the console shows BG lines one below their scroll value
    public static (int X, int Y) Layer2Scroll(LufiaMap map, double fieldFrames)
    {
        if (map.Layer2 == null || map.Layer2Mode != 2) return (0, 1);
        int frames = (int)Math.Max(0, fieldFrames);
        return (400 + frames, 400 + frames + 1);
    }
}
