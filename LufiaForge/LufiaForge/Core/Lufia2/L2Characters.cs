namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II characters' starting stats (US ROM, found 2026-10-22): 8 records of 7 u16 at $97:B93C (file 0xBB93C):
/// HP, MP, STR, AGL, INT, GUT, MGR. Record 0 is Maxim's (checked in the game: the shop screen shows STR 12, AGL 9,
/// INT 8, GUT 100, MGR 5 at the start); the others follow the heroes' usual order (Selan, Guy, Artea, Tia, Dekar,
/// Lexis); the 8th (all 20) has no known user.
/// </summary>
public static class L2Characters
{
    public const int Table = 0xBB93C, Count = 8, RecordSize = 14;
    public static readonly string[] Stats = { "HP", "MP", "STR", "AGL", "INT", "GUT", "MGR" };
    public static readonly string[] Names = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis", "Record 8 (unknown)" };

    public static int[] Read(RomBuffer rom, int c) => Enumerable.Range(0, Stats.Length).Select(i => (int)rom.ReadUInt16Le(Table + c * RecordSize + i * 2)).ToArray();

    public static void Write(RomBuffer rom, int c, int[] values)
    {
        for (int i = 0; i < Stats.Length && i < values.Length; i++) rom.WriteUInt16Le(Table + c * RecordSize + i * 2, (ushort)Math.Clamp(values[i], 0, 0xFFFF));
    }
}
