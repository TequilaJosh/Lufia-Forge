using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

/// <summary>
/// Random and scripted battles, from the game's code:
///
/// Steps ($01:881D): after 4 safe steps, each step starts a battle when random(128) &lt; danger x rate / 128,
/// where danger ($16B5) is normally 128 and rate is the constant at <see cref="RateOffset"/> (10); the chance
/// is halved away from the world map. So 10 = 1 in 12.8 steps on the world map, 1 in 25.6 elsewhere.
///
/// Which battle ($01:88AB): on the world map the encounter group comes from the zone grid (resource B0, one
/// byte per 4x4 tiles, 80 x 64); elsewhere from the map's setup script (command 0D gg). Formation $142D = group.
///
/// Monsters ($08:D9D6): group g has a record [count][subgroup...] (pointers at $08:DA60); one subgroup is picked
/// at random; a subgroup is 4 monster ids at $08:DBE2 + 4 x (s - 1) (FF = empty), and the battle draws its
/// monsters from those. Scripted boss formations E6-FA have their own 4 monsters at $08:DCEE.
/// </summary>
public static class Encounters
{
    public const int GroupPointers = 0x45A60;   // $08:DA60
    /// <summary>The game's subgroup table ($08:DBE2, 67 entries); a patched ROM reads <see cref="SubgroupPatch"/>'s table instead.</summary>
    public const int OldSubgroups = 0x45BE2;    // $08:DBE2
    public const int OldSubgroupCount = (0x45CEE - OldSubgroups) / 4;
    public const int Bosses = 0x45CEE;          // $08:DCEE
    public const int FirstBoss = 0xE6, LastBoss = 0xFA;
    public const int ZoneResource = 0xB0, ZoneWidth = 80, ZoneHeight = 64;
    public const int RateOffset = 0x8877;       // LDA #$0A in $01:8876
    /// <summary>The safe-steps number (CMP #$04 in $01:8857), or inside <see cref="EncounterPatch"/>'s routine once applied.</summary>
    public static int SafeStepsOffset(RomBuffer rom) => EncounterPatch.SafeStepsOffset(rom);

    public static int GroupCount(RomBuffer rom) => (rom.ReadUInt16Le(GroupPointers) - 0xDA60) / 2;
    /// <summary>How many subgroups the ROM can hold: 67, or 255 with <see cref="SubgroupPatch"/>.</summary>
    public static int SubgroupCount(RomBuffer rom) => SubgroupPatch.IsApplied(rom) ? SubgroupPatch.MaxSubgroups : OldSubgroupCount;

    /// <summary>File offset of subgroup s's 4 monster ids.</summary>
    public static int SubgroupOffset(RomBuffer rom, int s) =>
        (SubgroupPatch.IsApplied(rom) ? SubgroupPatch.TableOffset(rom) : OldSubgroups) + 4 * (s - 1);

    private static int Bank8(int addr) => 0x40000 + (addr & 0x7FFF);

    public static List<int> GroupSubgroups(RomBuffer rom, int group)
    {
        int p = Bank8(rom.ReadUInt16Le(GroupPointers + 2 * (group - 1)));
        int n = rom.ReadByte(p);
        return Enumerable.Range(0, n).Select(i => (int)rom.ReadByte(p + 1 + i)).ToList();
    }

    public static int[] SubgroupMonsters(RomBuffer rom, int sub) =>
        Enumerable.Range(0, 4).Select(i => (int)rom.ReadByte(SubgroupOffset(rom, sub) + i)).ToArray();

    public static void SetSubgroupMonsters(RomBuffer rom, int sub, int[] monsters)
    {
        for (int i = 0; i < 4; i++) rom.WriteByte(SubgroupOffset(rom, sub) + i, (byte)monsters[i]);
    }

    public static int[] BossMonsters(RomBuffer rom, int formation) =>
        Enumerable.Range(0, 4).Select(i => (int)rom.ReadByte(Bosses + 4 * (formation - FirstBoss) + i)).ToArray();

    public static void SetBossMonsters(RomBuffer rom, int formation, int[] monsters)
    {
        for (int i = 0; i < 4; i++) rom.WriteByte(Bosses + 4 * (formation - FirstBoss) + i, (byte)monsters[i]);
    }

    /// <summary>Groups above this would read as boss formations (the battle code treats $142D >= E6 as one).</summary>
    public const int MaxGroups = FirstBoss - 1;

    /// <summary>Bytes the group pointers and lists need: 2 per pointer, then [count][subgroups] per group.</summary>
    public static int GroupBytes(IReadOnlyList<IReadOnlyCollection<int>> groups) => groups.Sum(g => 3 + g.Count);

    /// <summary>
    /// Where the group pointers and lists must end: at the game's subgroup table, or, once the subgroups are read
    /// from the patch's table, at the boss formations (the old subgroup table's bytes are free then).
    /// </summary>
    public static int GroupAreaEnd(RomBuffer rom) => SubgroupPatch.IsApplied(rom) ? Bosses : OldSubgroups;

    /// <summary>Bytes from the pointer table to <see cref="GroupAreaEnd"/>.</summary>
    public static int GroupRoom(RomBuffer rom) => GroupAreaEnd(rom) - GroupPointers;

    /// <summary>
    /// Rewrites the group pointer table and every group list, packed in the game's space ($08:DA60 up to the
    /// subgroup table): the pointers first (their count is the number of groups, as the game and
    /// <see cref="GroupCount"/> read it), then the lists. Fails if they don't fit.
    /// </summary>
    public static void WriteGroups(RomBuffer rom, IReadOnlyList<List<int>> groups)
    {
        int n = groups.Count;
        if (n < 1 || n > MaxGroups) throw new ArgumentException($"There must be 1-{MaxGroups} groups.");
        if (groups.Any(g => g.Count is < 1 or > 255)) throw new ArgumentException("Every group needs 1-255 subgroups.");
        int max = SubgroupCount(rom), room = GroupRoom(rom), end = GroupAreaEnd(rom);
        if (groups.Any(g => g.Any(s => s < 1 || s > max))) throw new ArgumentException($"Subgroups are 01-{max:X2}.");
        int need = GroupBytes(groups);
        if (need > room) throw new InvalidOperationException($"The groups need {need} bytes but only {room} are available; remove some entries (e.g. a subgroup listed twice).");
        int at = GroupPointers + 2 * n;
        for (int g = 0; g < n; g++)
        {
            rom.WriteUInt16Le(GroupPointers + 2 * g, (ushort)(0x8000 | ((at - 0x40000) & 0x7FFF)));
            rom.WriteByte(at++, (byte)groups[g].Count);
            foreach (int s in groups[g]) rom.WriteByte(at++, (byte)s);
        }
        while (at < end) rom.WriteByte(at++, 0);
    }

    public static int RoomForGroups(RomBuffer rom) => GroupAreaEnd(rom) - Bank8(rom.ReadUInt16Le(GroupPointers));

    /// <summary>
    /// A subgroup with no monster at all would hang the game: the battle code keeps looking for a monster
    /// in it ($08:DA38 loop), so every subgroup needs at least one.
    /// </summary>
    public static bool HasMonster(int[] monsters) => monsters.Any(m => m != 0xFF);

    // ── maps ────────────────────────────────────────────────────────────────

    /// <summary>The encounter-group commands (0D gg) in a map's setup script: (file offset of gg, group, conditional).</summary>
    public static List<(int Offset, int Group, bool Conditional)> MapGroups(RomBuffer rom, int mapId)
    {
        var list = new List<(int, int, bool)>();
        int p = MapSetupScript.Offset(rom, mapId) + 2;
        bool conditional = false;
        for (int steps = 0; steps < 1000 && p < rom.Length - 4; steps++)
        {
            int op = rom.ReadByte(p);
            if (op == 0x00) break;
            if (op < 0x20)
            {
                if (op is 0x04 or (>= 0x05 and <= 0x0A)) conditional = true;
                if (op == 0x0D) list.Add((p + 1, rom.ReadByte(p + 1), conditional));
                if (op > 0x0D) break;
                p += op is 0x04 or 0x0B ? 3 : 2;
            }
            else if (op < 0x40) p += 4;
            else if (op < 0x80) p += 1;
            else if (op < 0xC0) p += 2;
            else p += 4;
        }
        return list;
    }

    // ── world map zones ─────────────────────────────────────────────────────

    public static byte[] ReadZones(RomBuffer rom) => LufiaCompression.DecompressResource(rom, ZoneResource, out _);

    public static ResourceWriter.Result WriteZones(RomBuffer rom, byte[] zones, bool allowExpand) =>
        ResourceWriter.Save(rom, ZoneResource, zones, allowExpand);
}
