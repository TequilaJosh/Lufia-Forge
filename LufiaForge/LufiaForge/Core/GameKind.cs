namespace LufiaForge.Core;

/// <summary>Which game a ROM is. Both are LoROM with the header at 0x7FC0, so the title there tells them apart:
/// Lufia &amp; the Fortress of Doom says "LUFIA" (1 MB, 2 MB once Lufia Forge expands it), Lufia II: Rise of the
/// Sinistrals says "Lufia II(Estpolis II)" (2.5 MB).</summary>
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

    /// <summary>The game of a ROM image without a copier header.</summary>
    public static GameKind Detect(byte[] rom)
    {
        if (rom.Length < HeaderTitle + TitleLength) return GameKind.Unknown;
        string title = Title(rom);
        // ("LUFIA II" first: both titles start with "LUFIA")
        if (title.StartsWith("LUFIA II", StringComparison.OrdinalIgnoreCase)) return rom.Length >= Lufia2MinSize ? GameKind.Lufia2 : GameKind.Unknown;
        if (title.StartsWith("LUFIA", StringComparison.OrdinalIgnoreCase)) return rom.Length >= Lufia1MinSize ? GameKind.Lufia1 : GameKind.Unknown;
        // a hack may rename the game: its own code still tells which it is
        var byCode = RomIdentity.ByCode(rom);
        return byCode == GameKind.Lufia2 && rom.Length >= Lufia2MinSize || byCode == GameKind.Lufia1 && rom.Length >= Lufia1MinSize ? byCode : GameKind.Unknown;
    }

    public static string Title(byte[] rom) =>
        rom.Length < HeaderTitle + TitleLength ? "" : System.Text.Encoding.ASCII.GetString(rom, HeaderTitle, TitleLength).TrimEnd();

    public static string Name(GameKind kind) => kind switch
    {
        GameKind.Lufia1 => "Lufia & the Fortress of Doom",
        GameKind.Lufia2 => "Lufia II: Rise of the Sinistrals",
        _ => "an unknown game",
    };
}
