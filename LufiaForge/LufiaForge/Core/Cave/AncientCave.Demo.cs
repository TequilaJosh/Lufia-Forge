using LufiaForge.Core.Battle;
using LufiaForge.Modules.GameData;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    /// <summary>
    /// The Ancient Cave demo in one go: the cave with New Game at the Old Cave elevator, faster walking (the Game Data
    /// setting) and party members retargeting instead of missing (<see cref="RetargetPatch"/>).
    /// </summary>
    public static void ApplyDemo(RomBuffer rom)
    {
        Apply(rom, CaveMode.ElevatorStart);
        if (!RetargetPatch.IsApplied(rom)) RetargetPatch.Apply(rom);
        SetFastWalk(rom, true);
        rom.FixChecksum();
    }

    public static bool IsDemo(RomBuffer rom) => IsAppliedAs(rom, CaveMode.ElevatorStart) && RetargetPatch.IsApplied(rom) && IsFastWalk(rom);

    /// <summary>The demo can be added: the cave's code is the game's (or the cave is in), and so is the battle code the
    /// retarget fix changes (or the fix is in).</summary>
    public static bool CanApplyDemo(RomBuffer rom) =>
        (IsOriginal(rom) || IsApplied(rom)) && (RetargetPatch.IsOriginal(rom) || RetargetPatch.IsApplied(rom));

    /// <summary>Takes the demo out again: the cave, the retarget fix and the faster walking.</summary>
    public static void RemoveDemo(RomBuffer rom)
    {
        Remove(rom);
        if (RetargetPatch.IsApplied(rom)) RetargetPatch.Remove(rom);
        SetFastWalk(rom, false);
        rom.FixChecksum();
    }

    private static bool IsFastWalk(RomBuffer rom) => rom.ReadByte(GameDataOffsets.WalkSpeed) == 0x20;

    private static void SetFastWalk(RomBuffer rom, bool on)
    {
        rom.WriteByte(GameDataOffsets.WalkSpeed, (byte)(on ? 0x20 : 0x10));
        rom.WriteBytes(GameDataOffsets.WalkSpeedPatch, on ? GameDataOffsets.WalkPatchFast : GameDataOffsets.WalkPatchNormal);
    }
}
