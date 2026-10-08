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
    public const int Subgroups = 0x45BE2;       // $08:DBE2
    public const int Bosses = 0x45CEE;          // $08:DCEE
    public const int FirstBoss = 0xE6, LastBoss = 0xFA;
    public const int ZoneResource = 0xB0, ZoneWidth = 80, ZoneHeight = 64;
    public const int RateOffset = 0x8877;       // LDA #$0A in $01:8876
    public const int SafeStepsOffset = 0x8858;  // CMP #$04 in $01:8857

    public static int GroupCount(RomBuffer rom) => (rom.ReadUInt16Le(GroupPointers) - 0xDA60) / 2;
    /// <summary>Subgroups fill the space between the subgroup table and the boss table.</summary>
    public static int SubgroupCount => (Bosses - Subgroups) / 4;

    private static int Bank8(int addr) => 0x40000 + (addr & 0x7FFF);

    public static List<int> GroupSubgroups(RomBuffer rom, int group)
    {
        int p = Bank8(rom.ReadUInt16Le(GroupPointers + 2 * (group - 1)));
        int n = rom.ReadByte(p);
        return Enumerable.Range(0, n).Select(i => (int)rom.ReadByte(p + 1 + i)).ToList();
    }

    public static int[] SubgroupMonsters(RomBuffer rom, int sub) =>
        Enumerable.Range(0, 4).Select(i => (int)rom.ReadByte(Subgroups + 4 * (sub - 1) + i)).ToArray();

    public static void SetSubgroupMonsters(RomBuffer rom, int sub, int[] monsters)
    {
        for (int i = 0; i < 4; i++) rom.WriteByte(Subgroups + 4 * (sub - 1) + i, (byte)monsters[i]);
    }

    public static int[] BossMonsters(RomBuffer rom, int formation) =>
        Enumerable.Range(0, 4).Select(i => (int)rom.ReadByte(Bosses + 4 * (formation - FirstBoss) + i)).ToArray();

    public static void SetBossMonsters(RomBuffer rom, int formation, int[] monsters)
    {
        for (int i = 0; i < 4; i++) rom.WriteByte(Bosses + 4 * (formation - FirstBoss) + i, (byte)monsters[i]);
    }

    /// <summary>
    /// Rewrites every group record, packed in the space the game's records use (from the first record to the
    /// subgroup table), with the pointers updated. Fails if they no longer fit.
    /// </summary>
    public static void WriteGroups(RomBuffer rom, IReadOnlyList<List<int>> groups)
    {
        int n = GroupCount(rom);
        if (groups.Count != n) throw new ArgumentException("Every group must be given.");
        int start = Bank8(rom.ReadUInt16Le(GroupPointers));
        int room = Subgroups - start;
        int need = groups.Sum(g => 1 + g.Count);
        if (need > room) throw new InvalidOperationException($"The groups need {need} bytes but only {room} are available; remove some entries.");
        int at = start;
        for (int g = 0; g < n; g++)
        {
            rom.WriteUInt16Le(GroupPointers + 2 * g, (ushort)(0x8000 | ((at - 0x40000) & 0x7FFF)));
            rom.WriteByte(at++, (byte)groups[g].Count);
            foreach (int s in groups[g]) rom.WriteByte(at++, (byte)s);
        }
        while (at < Subgroups) rom.WriteByte(at++, 0);
    }

    public static int RoomForGroups(RomBuffer rom) => Subgroups - Bank8(rom.ReadUInt16Le(GroupPointers));

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
