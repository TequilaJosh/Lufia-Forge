using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

/// <summary>
/// Lufia 1: hold R to turn without walking, as in Lufia II (Colmines92's patch, shared on the Lufia Forge Discord). The
/// field step code stores the pressed direction as the leader's facing at <c>$01:8BFF</c> (<c>REP #$10 / LDX $06 /
/// STA $0B,X</c>) and then walks; the patch puts a <c>JSL</c> to its routine there (which does those three and sets
/// carry while R, bit 10 of the held buttons $30, is down) and a <c>BCS</c> to the end of the step (<c>$01:8C84</c>), so
/// the leader turns but doesn't move, and no step is counted (no random battle).
/// </summary>
public static class TurnInPlacePatch
{
    public const int SpaceId = 42;

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);

    // JSL routine / BCS $8C84 (from $8C05: +7F)
    private static readonly HookPatch.Site[] Sites = { new(0x8BFF, "C2 10 A6 06 95 0B", "22 00 00 00 B0 7F", 1) };
    // REP #$10 / PHA / CLC / LDA $30 / BIT #$10 / BEQ +1 / SEC / PLA / LDX $06 / STA $0B,X / RTL
    private static readonly HookPatch.Block Code = new("C2 10 48 18 A5 30 89 10 F0 01 38 68 A6 06 95 0B 6B");

    private static HookPatch? _patch;
    private static HookPatch Patch => _patch ??= new(Sites, Code);

    public static bool IsApplied(RomBuffer rom) => !rom.IsLufia2 && Patch.AppliedAt(rom, FileOffset) >= 0;
    /// <summary>The place the patch changes still holds the game's bytes.</summary>
    public static bool IsOriginal(RomBuffer rom) => !rom.IsLufia2 && Patch.IsOriginal(rom);

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The field step code at $01:8BFF isn't the game's (another patch or hack changed it), so hold R to turn can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);
        var space = ExpansionSpace.Load(rom);
        int at = space.AllocateInOneBank(Patch.Code.Bytes.Length, ExpansionSpace.Kind.Patch, SpaceId);
        Patch.Write(rom, at, SnesAddress(at));
        space.Save();
        rom.FixChecksum();
    }

    public static void Remove(RomBuffer rom)
    {
        int at = Patch.AppliedAt(rom, FileOffset);
        if (at < 0) return;
        Patch.Restore(rom);
        var space = ExpansionSpace.Load(rom);
        space.Free(at);
        space.Save();
        rom.FixChecksum();
    }
}
