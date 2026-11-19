using LufiaForge.Core.Maps;

namespace LufiaForge.Core;

// lufia 1: hold R to turn without walking like lufia 2 does. colmines92's patch from the discord
// game saves the direction as the leader's facing at $01:8BFF (REP #$10 / LDX $06 / STA $0B,X) then walks.
// we JSL to our routine there, it does the same thing but sets carry if R is held ($30 bit $10),
// then the BCS after it skips to the end of the step ($01:8C84)
// so you turn but don't move, no step counted = no random battle
public static class TurnInPlacePatch
{
    public const int SpaceId = 42;

    private static int ToFileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int ToSnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);

    // JSL to the routine, then BCS $8C84 (+7F from $8C05)
    private static readonly HookPatch.Site[] HookSites = { new(0x8BFF, "C2 10 A6 06 95 0B", "22 00 00 00 B0 7F", 1) };

    // REP #$10 / PHA / CLC / LDA $30 / BIT #$10 / BEQ +1 / SEC / PLA / LDX $06 / STA $0B,X / RTL
    private static readonly HookPatch.Block RoutineCode = new("C2 10 48 18 A5 30 89 10 F0 01 38 68 A6 06 95 0B 6B");

    private static HookPatch? _patch;
    private static HookPatch Patch => _patch ??= new(HookSites, RoutineCode);

    public static bool IsApplied(RomBuffer rom) => !rom.IsLufia2 && Patch.AppliedAt(rom, ToFileOffset) >= 0;

    // true if the bytes we replace are still the game's (nothing else patched over them)
    public static bool IsOriginal(RomBuffer rom) => !rom.IsLufia2 && Patch.IsOriginal(rom);

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        if (!IsOriginal(rom)) throw new InvalidOperationException("The field step code at $01:8BFF isn't the game's (another patch or hack changed it), so hold R to turn can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);

        var freeSpace = ExpansionSpace.Load(rom);
        int routineOffset = freeSpace.AllocateInOneBank(Patch.Code.Bytes.Length, ExpansionSpace.Kind.Patch, SpaceId);
        Patch.Write(rom, routineOffset, ToSnesAddress(routineOffset));
        freeSpace.Save();
        rom.FixChecksum();
    }

    public static void Remove(RomBuffer rom)
    {
        int routineOffset = Patch.AppliedAt(rom, ToFileOffset);
        if (routineOffset < 0) return;

        Patch.Restore(rom);
        var freeSpace = ExpansionSpace.Load(rom);
        freeSpace.Free(routineOffset);
        freeSpace.Save();
        rom.FixChecksum();
    }
}
