using LufiaForge.Core.Battle;
using LufiaForge.Modules.GameData;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    /// <summary>The demo's record in the cave block (+10): what was there before the demo added it, so taking the demo
    /// out leaves those alone. Bit 7: the record is there (a demo from before it took both out).</summary>
    private const byte DemoRecorded = 0x80, RetargetBefore = 0x01, FastWalkBefore = 0x02;

    /// <summary>
    /// The Ancient Cave demo in one go: the cave with New Game at the Old Cave elevator and its priest and merchants,
    /// faster walking (the Game Data setting) and party members retargeting instead of missing (<see cref="RetargetPatch"/>).
    /// </summary>
    public static void ApplyDemo(RomBuffer rom) => Atomic(rom, () =>
    {
        // (what was there before, kept from an earlier demo when it is in already; a demo from before the record: unknown)
        byte record = Read(rom) is { } was && (rom.ReadByte(was.Block + DemoRecord) & DemoRecorded) != 0 ? rom.ReadByte(was.Block + DemoRecord)
            : IsDemo(rom) ? (byte)0
            : (byte)(DemoRecorded | (RetargetPatch.IsApplied(rom) ? RetargetBefore : 0) | (IsFastWalk(rom) ? FastWalkBefore : 0));
        ApplyCore(rom, CaveMode.ElevatorStart, people: true, force: false);
        if (!RetargetPatch.IsApplied(rom)) RetargetPatch.Apply(rom);
        SetFastWalk(rom, true);
        rom.WriteByte(Read(rom)!.Block + DemoRecord, record);
        rom.FixChecksum();
    });

    public static bool IsDemo(RomBuffer rom) => IsAppliedAs(rom, CaveMode.ElevatorStart) && HasPeople(rom) && RetargetPatch.IsApplied(rom) && IsFastWalk(rom);

    /// <summary>The demo can be added: the cave's code is the game's (or the cave is in), and so is the battle code the
    /// retarget fix changes (or the fix is in).</summary>
    public static bool CanApplyDemo(RomBuffer rom) =>
        (IsOriginal(rom) || IsApplied(rom)) && (RetargetPatch.IsOriginal(rom) || RetargetPatch.IsApplied(rom));

    /// <summary>Takes the demo out again: the cave, and the retarget fix and the faster walking unless they were in
    /// before the demo (a demo added by an older Lufia Forge has no record: both go).</summary>
    public static void RemoveDemo(RomBuffer rom) => Atomic(rom, () =>
    {
        byte record = Read(rom) is { } info ? rom.ReadByte(info.Block + DemoRecord) : (byte)0;
        bool known = (record & DemoRecorded) != 0;
        RemoveCore(rom);
        if (RetargetPatch.IsApplied(rom) && !(known && (record & RetargetBefore) != 0)) RetargetPatch.Remove(rom);
        if (!(known && (record & FastWalkBefore) != 0)) SetFastWalk(rom, false);
        rom.FixChecksum();
    });

    private static bool IsFastWalk(RomBuffer rom) => rom.ReadByte(GameDataOffsets.WalkSpeed) == 0x20;

    private static void SetFastWalk(RomBuffer rom, bool on)
    {
        rom.WriteByte(GameDataOffsets.WalkSpeed, (byte)(on ? 0x20 : 0x10));
        rom.WriteBytes(GameDataOffsets.WalkSpeedPatch, on ? GameDataOffsets.WalkPatchFast : GameDataOffsets.WalkPatchNormal);
    }
}
