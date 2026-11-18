namespace LufiaForge.Core;

// which game a rom is. both are LoROM with the header at 0x7FC0, so the title there tells them apart:
// lufia & the fortress of doom says "LUFIA" (1 MB, 2 MB once we expand it), lufia 2: rise of the sinistrals says
// "Lufia II(Estpolis II)" (2.5 MB)
public enum GameKind
{
    Unknown,
    Lufia1,
    Lufia2,
}

public static class GameDetect
{
    public const int HeaderTitle = 0x7FC0, TitleLength = 21;
    public const int Lufia1MinSize = 0x100000, Lufia2MinSize = 0x280000;

    // the game of a rom image with no copier header
    public static GameKind Detect(byte[] rom)
    {
        if (rom.Length < HeaderTitle + TitleLength) return GameKind.Unknown;
        string title = Title(rom);
        // lufia 2's european release ("LUFIA II", country 02) and the dutch translation built on it ("LUFIA FOR DUTCH",
        // country 07) keep their data in other places than the us game, so we don't open them as it
        if (IsEuropeanLufia2(rom)) return GameKind.Unknown;
        // "LUFIA II" first, both titles start with "LUFIA"
        if (title.StartsWith("LUFIA II", StringComparison.OrdinalIgnoreCase)) return rom.Length >= Lufia2MinSize ? GameKind.Lufia2 : GameKind.Unknown;
        if (title.StartsWith("LUFIA", StringComparison.OrdinalIgnoreCase)) return rom.Length >= Lufia1MinSize ? GameKind.Lufia1 : GameKind.Unknown;
        // a hack can rename the game, its own code still tells which one it is
        var byCode = RomIdentity.ByCode(rom);
        return byCode == GameKind.Lufia2 && rom.Length >= Lufia2MinSize || byCode == GameKind.Lufia1 && rom.Length >= Lufia1MinSize ? byCode : GameKind.Unknown;
    }

    // the header's country byte. 01 for the us games (and hacks built on them)
    public const int CountryByte = 0x7FD9;

    // lufia 2's european release ("LUFIA II", country 02) or a translation built on it (the dutch one, "LUFIA FOR DUTCH",
    // country 07): titled "LUFIA", not from the us (country 01) or japan (00), bigger than lufia 1 ever gets
    // (2 MB once expanded), and not lufia 1's code
    public static bool IsEuropeanLufia2(byte[] rom)
    {
        if (rom.Length < HeaderTitle + TitleLength || rom.Length <= CountryByte) return false;
        return Title(rom).StartsWith("LUFIA", StringComparison.OrdinalIgnoreCase) && rom[CountryByte] > 0x01
            && rom.Length >= Lufia2MinSize && RomIdentity.ByCode(rom) != GameKind.Lufia1;
    }

    // why a rom can't be opened, for the message when Detect says Unknown
    public static string WhyNot(byte[] rom) =>
        Lufia2.L2Dutch.Is(rom)
            ? "This is the Dutch Lufia II, a translation built on the European release, which keeps its data in other places than the " +
              "US game. Start a New project from it: Lufia Forge then puts the Dutch text into a Lufia II (USA) ROM, or a hack built on one."
            : IsEuropeanLufia2(rom)
            ? $"This is the European Lufia II (title \"{Title(rom)}\"). It keeps its maps, events and text in other places than the US " +
              "game, so Lufia Forge can't open it: it edits Lufia II (USA) and the hacks built on it, like Frue Lufia."
            : $"ROM title \"{Title(rom)}\" is neither Lufia 1 nor Lufia II (or the ROM is too small). Wrong game or wrong region?";

    public static string Title(byte[] rom) =>
        rom.Length < HeaderTitle + TitleLength ? "" : System.Text.Encoding.ASCII.GetString(rom, HeaderTitle, TitleLength).TrimEnd();

    public static string Name(GameKind kind) => kind switch
    {
        GameKind.Lufia1 => "Lufia & the Fortress of Doom",
        GameKind.Lufia2 => "Lufia II: Rise of the Sinistrals",
        _ => "an unknown game",
    };
}
