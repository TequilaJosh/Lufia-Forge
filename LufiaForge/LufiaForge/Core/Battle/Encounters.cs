using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// random and scripted battles, worked out from the game's code.
//
// steps ($01:881D): after 4 safe steps, each step starts a battle when random(128) < danger x rate / 128.
// danger ($16B5) is normally 128 and rate is the constant at RateOffset (10). the chance gets halved
// away from the world map, so 10 = 1 in 12.8 steps on the world map, 1 in 25.6 everywhere else.
//
// which battle ($01:88AB): on the world map the encounter group comes from the zone grid (resource B0, one
// byte per 4x4 tiles, 80 x 64), anywhere else from the map's setup script (command 0D gg). formation $142D = group.
//
// monsters ($08:D9D6): group g has a record [count][subgroup...] (pointers at $08:DA60) and one subgroup gets
// picked at random. a subgroup is 4 monster ids at $08:DBE2 + 4 x (s - 1) (FF = empty) and the battle draws its
// monsters from those. scripted boss formations E6-FA have their own 4 monsters at $08:DCEE
public static class Encounters
{
    public const int GroupPointers = 0x45A60;   // $08:DA60
    // the game's subgroup table ($08:DBE2, 67 entries). a patched rom reads SubgroupPatch's table instead
    public const int OldSubgroups = 0x45BE2;    // $08:DBE2
    public const int OldSubgroupCount = (0x45CEE - OldSubgroups) / 4;
    public const int Bosses = 0x45CEE;          // $08:DCEE
    public const int FirstBoss = 0xE6, LastBoss = 0xFA;
    public const int ZoneResource = 0xB0, ZoneWidth = 80, ZoneHeight = 64;
    public const int RateOffset = 0x8877;       // LDA #$0A in $01:8876
    // the safe steps number (CMP #$04 in $01:8857), or inside EncounterPatch's routine once that's applied
    public static int SafeStepsOffset(RomBuffer rom) => EncounterPatch.SafeStepsOffset(rom);

    public static int GroupCount(RomBuffer rom) => (rom.ReadUInt16Le(GroupPointers) - 0xDA60) / 2;
    // how many subgroups the rom can hold: 67, or 255 with SubgroupPatch
    public static int SubgroupCount(RomBuffer rom) => SubgroupPatch.IsApplied(rom) ? SubgroupPatch.MaxSubgroups : OldSubgroupCount;

    // file offset of subgroup's 4 monster ids
    public static int SubgroupOffset(RomBuffer rom, int subgroup) =>
        (SubgroupPatch.IsApplied(rom) ? SubgroupPatch.TableOffset(rom) : OldSubgroups) + 4 * (subgroup - 1);

    private static int Bank8(int addr) => 0x40000 + (addr & 0x7FFF);

    public static List<int> GroupSubgroups(RomBuffer rom, int group)
    {
        int recordOffset = Bank8(rom.ReadUInt16Le(GroupPointers + 2 * (group - 1)));
        int count = rom.ReadByte(recordOffset);
        return Enumerable.Range(0, count).Select(i => (int)rom.ReadByte(recordOffset + 1 + i)).ToList();
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

    // groups above this would read as boss formations (the battle code treats $142D >= E6 as one)
    public const int MaxGroups = FirstBoss - 1;

    // bytes the group pointers and lists need: 2 per pointer, then [count][subgroups] per group
    public static int GroupBytes(IReadOnlyList<IReadOnlyCollection<int>> groups) => groups.Sum(g => 3 + g.Count);

    // where the group pointers and lists have to end: at the game's subgroup table, or once the subgroups get read
    // from the patch's table, at the boss formations (the old subgroup table's bytes are free then)
    public static int GroupAreaEnd(RomBuffer rom) => SubgroupPatch.IsApplied(rom) ? Bosses : OldSubgroups;

    // bytes from the pointer table to GroupAreaEnd
    public static int GroupRoom(RomBuffer rom) => GroupAreaEnd(rom) - GroupPointers;

    // rewrites the group pointer table and every group list, packed in the game's space ($08:DA60 up to the
    // subgroup table). pointers first (their count is the number of groups, that's how the game and
    // GroupCount read it), then the lists. fails if they don't fit
    public static void WriteGroups(RomBuffer rom, IReadOnlyList<List<int>> groups)
    {
        int count = groups.Count;
        if (count < 1 || count > MaxGroups) throw new ArgumentException($"There must be 1-{MaxGroups} groups.");
        if (groups.Any(g => g.Count is < 1 or > 255)) throw new ArgumentException("Every group needs 1-255 subgroups.");
        int maxSubgroup = SubgroupCount(rom), room = GroupRoom(rom), end = GroupAreaEnd(rom);
        if (groups.Any(g => g.Any(s => s < 1 || s > maxSubgroup))) throw new ArgumentException($"Subgroups are 01-{maxSubgroup:X2}.");
        // nothing changed: leave the game's own layout as it is (it shares some lists)
        var current = Enumerable.Range(1, GroupCount(rom)).Select(g => GroupSubgroups(rom, g)).ToList();
        if (current.Count == count && current.Zip(groups).All(p => p.First.SequenceEqual(p.Second))) return;
        int need = GroupBytes(groups);
        if (need > room) throw new InvalidOperationException($"The groups need {need} bytes but only {room} are available; remove some entries (e.g. a subgroup listed twice).");
        int writeAt = GroupPointers + 2 * count;
        for (int g = 0; g < count; g++)
        {
            rom.WriteUInt16Le(GroupPointers + 2 * g, (ushort)(0x8000 | ((writeAt - 0x40000) & 0x7FFF)));
            rom.WriteByte(writeAt++, (byte)groups[g].Count);
            foreach (int subgroup in groups[g]) rom.WriteByte(writeAt++, (byte)subgroup);
        }
        while (writeAt < end) rom.WriteByte(writeAt++, 0);
    }

    public static int RoomForGroups(RomBuffer rom) => GroupAreaEnd(rom) - Bank8(rom.ReadUInt16Le(GroupPointers));

    // a subgroup with no monster at all would hang the game: the battle code keeps looking for a monster
    // in it ($08:DA38 loop), so every subgroup needs at least one
    public static bool HasMonster(int[] monsters) => monsters.Any(m => m != 0xFF);

    // maps

    // the encounter group commands (0D gg) in a map's setup script: (file offset of gg, group, conditional)
    public static List<(int Offset, int Group, bool Conditional)> MapGroups(RomBuffer rom, int mapId)
    {
        var list = new List<(int, int, bool)>();
        int position = MapSetupScript.Offset(rom, mapId) + 2;
        bool conditional = false;
        for (int steps = 0; steps < 1000 && position < rom.Length - 4; steps++)
        {
            int op = rom.ReadByte(position);
            if (op == 0x00) break;
            if (op < 0x20)
            {
                if (op is 0x04 or (>= 0x05 and <= 0x0A)) conditional = true;
                if (op == 0x0D) list.Add((position + 1, rom.ReadByte(position + 1), conditional));
                if (op > 0x0D) break;
                position += op is 0x04 or 0x0B ? 3 : 2;
            }
            else if (op < 0x40) position += 4;
            else if (op < 0x80) position += 1;
            else if (op < 0xC0) position += 2;
            else position += 4;
        }
        return list;
    }

    // world map zones

    public static byte[] ReadZones(RomBuffer rom) => LufiaCompression.DecompressResource(rom, ZoneResource, out _);

    public static ResourceWriter.Result WriteZones(RomBuffer rom, byte[] zones, bool allowExpand) =>
        ResourceWriter.Save(rom, ZoneResource, zones, allowExpand);
}
