namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II: 16 new statuses, as new battle code (source: tools/statuses/l2status.asm; the data in
/// L2StatusPatch.Data.cs is generated from it by tools/statuses/gen.py).
/// <list type="bullet">
/// <item>Each fighter has a second status word (bits: <see cref="Names"/>) in RAM at $7F:FC04 (heroes 0-6 by record,
/// monster slots, the capsule monster). Zombie, Decay, Burnt and Blind stay on a hero after a battle and are saved (in
/// the save buffer's unused bytes $7E:3BBB-3BC3, behind a marker: an old save loads with none); the rest end with it.</item>
/// <item>Battle script commands: 5D s c causes status s with chance c (%), 5E s c cures it, 5F s p resists it by p% (in a
/// "when hit" or armor script: for that target only), 60 s wards the targets against it for the battle.</item>
/// <item>Hooks in the game's code: a fighter's turn ($81:A7DA), the heroes' commands ($81:CA9B, $85:9288, $85:925A), the
/// confused action ($81:AF3F, $81:AF64: berserk), the damage ($85:D2C5), DFP ($85:D18E), each target ($85:D0CD,
/// $85:D454), the action's end ($85:CE43, $81:A82C: messages), the round's end ($81:8943: burns, bleeding, regen, mana
/// leak, doom, decay, timers), poison ($81:C6BB: it heals a zombie), the battle's end and start, saving and loading,
/// IP ($85:D508: none when cursed).</item>
/// </list>
/// </summary>
public static partial class L2StatusPatch
{
    private static HookPatch? _patch;
    private static HookPatch Patch => _patch ??= new(Sites, Code);

    /// <summary>The 16 statuses, by bit.</summary>
    public static readonly string[] Names =
    {
        "Zombie", "Frozen", "Decay", "Burnt", "Berserk", "Spooked", "Amnesia", "Piety",
        "Regen", "Doom", "Blind", "Stone", "Bleed", "Curse", "Stop", "Mana leak",
    };

    /// <summary>What each does, for the editor.</summary>
    public static readonly string[] Help =
    {
        "Healing hurts it, poison heals it, light does 3x damage. Stays after the battle.",
        "Can't act. The next physical hit does 2.5x and shatters the ice; fire melts it.",
        "Each round a random stat drops 1-15%. Stays after the battle.",
        "Takes damage each round (as poison does) and its physical attacks do half; water or ice magic cures it. Stays after the battle.",
        "Attacks on its own, friend or foe (foes 70% of the time), with double damage, for 3-10 rounds.",
        "Too scared to act 1 time in 4; calms down by itself.",
        "Its magic does 25% less, its DFP is 50% higher.",
        "Takes 50% more damage from dark.",
        "Gets back 1/12 of its max HP each round.",
        "Dies after 3 rounds unless cured.",
        "Half of its physical attacks miss. Stays after the battle.",
        "Can't act and takes no damage or healing; every hero stone loses the battle.",
        "Loses 1/16 of its max HP each round it acts.",
        "Healing does half, and a cursed hero gets no IP.",
        "Skips its turns for 2-4 rounds.",
        "Loses 1/12 of its max MP each round.",
    };

    public static bool IsApplied(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile) >= 0;

    /// <summary>File offset of the patch's block, or -1.</summary>
    public static int BlockAt(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile);

    public static bool IsOriginal(RomBuffer rom) => Patch.IsOriginal(rom);

    private static HookPatch? _v1;
    private static HookPatch V1 => _v1 ??= new(SitesV1, CodeV1);

    /// <summary>Whether the ROM has the first version (no element table): <see cref="Apply"/> upgrades it.</summary>
    public static bool IsOldVersion(RomBuffer rom) => V1.AppliedAt(rom, L2Rom.SnesToFile) >= 0;

    public static void Apply(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        byte[]? starts = null;
        if (IsOldVersion(rom))
        {
            // the first version: its monsters' starting statuses come along, its code comes out
            int old = V1.AppliedAt(rom, L2Rom.SnesToFile);
            starts = rom.ReadBytes(old + CodeV1.Bytes.Length, MonsterTableSize);
            V1.Restore(rom);
        }
        if (!IsOriginal(rom)) throw new InvalidOperationException("Some of the battle code the new statuses change isn't the US game's (another patch or a fan hack changed it), so they can't be added safely.");
        L2Rom.AllOrNothing(rom, () =>
        {
            // the block, then the monsters' starting statuses (256 words, none to start with), the resistance records and
            // the element table's address; the element table on its own (it is read with the data bank set to its bank)
            int at = L2Rom.Allocate(rom, Patch.Code.Bytes.Length + MonsterTableSize + ResistTableSize + 4);
            rom.WriteBytes(at + Patch.Code.Bytes.Length, new byte[MonsterTableSize + ResistTableSize + 4]);
            if (starts != null) rom.WriteBytes(at + Patch.Code.Bytes.Length, starts);
            int table = L2Rom.Allocate(rom, L2Elements.TableSize);
            rom.WriteBytes(table, new byte[L2Elements.TableSize]);
            Patch.Write(rom, at, L2Rom.FileToSnes(at));
            int ts = L2Rom.FileToSnes(table);
            rom.WriteBytes(at + ElemPtrAt, new[] { (byte)ts, (byte)(ts >> 8), (byte)(ts >> 16), (byte)0 });
            return at;
        });
        // the game's weaknesses and armor guards move into the table
        L2Elements.MoveAll(rom);
        WriteResistTable(rom);
    }

    private const int MonsterTableSize = 0x200, ResistRecord = 10, ResistTableSize = 0x200 * ResistRecord;

    /// <summary>What an item gives its wearer, for the STATUS screen's resistance pages (Y): read from its armor script
    /// ("when its wearer is hit"): elements halved / nullified (shared scripts 06-0E, 18 and 19, or "if the element is n,
    /// divide the damage"), the game's statuses made impossible (chance set to 0) or halved, the new statuses resisted (5F).</summary>
    public sealed record Resist(int ElementHalf, int ElementNone, int OldImmune, int OldHalf, int NewImmune, int NewPartial)
    {
        public bool Any => (ElementHalf | ElementNone | OldImmune | OldHalf | NewImmune | NewPartial) != 0;
    }

    /// <summary>The shared scripts that halve an element (read from them: 06 fire... 0E dark; 0B: attacks with no element).</summary>
    private static readonly Dictionary<int, int> LibraryHalves = new() { [0x06] = 0x0002, [0x07] = 0x0800, [0x08] = 0x0004, [0x09] = 0x0400, [0x0A] = 0x0010, [0x0B] = 0x0001, [0x0E] = 0x0020 };

    public static Resist ResistOf(RomBuffer rom, int item)
    {
        List<L2AiScripts.Step> steps;
        try { steps = L2AiScripts.Load(rom, L2AiScripts.Kind.Armor, item).Steps; } catch (Exception) { return new(0, 0, 0, 0, 0, 0); }
        int half = 0, none = 0, oldImm = 0, oldHalf = 0, newImm = 0, newPart = 0, counter2 = 0, counter1 = -1;
        int V(L2AiScripts.Step st, int i) => L2AiScripts.ArgValue(st, i);
        for (int i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            switch (st.Op)
            {
                case 0x0C:
                    int reg = V(st, 0), val = V(st, 1);
                    if (reg is >= 0x4E and <= 0x58 && (reg - 0x4E) % 2 == 0)
                    {
                        int bit = 1 << (reg - 0x4E) / 2;
                        if (val == 0) oldImm |= bit;
                        else if ((val >> 8) == 0x80) oldHalf |= bit;   // set from a counter (halved before)
                    }
                    if (reg == 0x81) counter2 = val;                    // the element bits shared scripts 18 / 19 check
                    if (reg == 0x80 && val == 0x8027) counter1 = 0;     // its counter 1 = the attack's element
                    break;
                case 0x42:
                    int lib = st.Args[0];
                    if (LibraryHalves.TryGetValue(lib, out var e)) half |= e;
                    if (lib == 0x18 && (counter2 >> 8) != 0x80) half |= counter2;
                    if (lib == 0x19 && (counter2 >> 8) != 0x80) none |= counter2;
                    break;
                case 0x06 when counter1 == 0 && V(st, 0) == 0x80 && st.Targets.Length > 0 && st.Targets[0] is { } t:
                    // "if its counter 1 (the element) is n, go to" a step that divides the damage by 2 or sets it to 0
                    if (t.Op == 0x10 && V(t, 0) == 0x2A && V(t, 1) == 2) half |= V(st, 1);
                    if (t.Op == 0x0C && V(t, 0) == 0x2A && V(t, 1) == 0) none |= V(st, 1);
                    break;
                case 0x07 when V(st, 0) == 0x27 && V(st, 1) == 0 && steps.ElementAtOrDefault(i + 1) is { Op: 0x10 } d && V(d, 0) == 0x2A && V(d, 1) == 2:
                    half |= 0x0001;                                     // "if the element is not 0, skip; halve": no element
                    break;
                case 0x5F:
                    int s = st.Args[0] & 0x0F, p = st.Args[1];
                    if (p >= 100) newImm |= 1 << s; else if (p > 0) newPart |= 1 << s;
                    break;
            }
        }
        return new(half, none, oldImm, oldHalf & ~oldImm, newImm, newPart & ~newImm);
    }

    /// <summary>Writes every item's record (call after an armor script changes; nothing without the patch).</summary>
    public static void WriteResistTable(RomBuffer rom)
    {
        int at = Patch.AppliedAt(rom, L2Rom.SnesToFile);
        if (at < 0) return;
        int table = at + Patch.Code.Bytes.Length + MonsterTableSize;
        int n = Math.Min(L2Battle.ItemCountIn(rom), 0x200);
        var data = new byte[ResistTableSize];
        for (int i = 0; i < n; i++)
        {
            var r = ResistOf(rom, i);
            if (!r.Any) continue;
            int o = i * ResistRecord;
            data[o] = (byte)r.ElementHalf; data[o + 1] = (byte)(r.ElementHalf >> 8);
            data[o + 2] = (byte)r.ElementNone; data[o + 3] = (byte)(r.ElementNone >> 8);
            data[o + 4] = (byte)r.OldImmune; data[o + 5] = (byte)r.OldHalf;
            data[o + 6] = (byte)r.NewImmune; data[o + 7] = (byte)(r.NewImmune >> 8);
            data[o + 8] = (byte)r.NewPartial; data[o + 9] = (byte)(r.NewPartial >> 8);
        }
        if (!rom.ReadBytes(table, data.Length).AsSpan().SequenceEqual(data)) rom.WriteBytes(table, data);
    }

    /// <summary>File offset of monster <paramref name="monster"/>'s starting statuses, or -1 without the patch.</summary>
    private static int MonsterEntry(RomBuffer rom, int monster)
    {
        int at = Patch.AppliedAt(rom, L2Rom.SnesToFile);
        return at < 0 || monster is < 0 or > 0xFF ? -1 : at + Patch.Code.Bytes.Length + monster * 2;
    }

    /// <summary>The statuses monster <paramref name="monster"/> has from the moment it is in a battle (bits as
    /// <see cref="Names"/>; Zombie makes an undead monster).</summary>
    public static int MonsterStart(RomBuffer rom, int monster) => MonsterEntry(rom, monster) is var e and >= 0 ? rom.ReadUInt16Le(e) : 0;

    /// <summary>Sets them (puts the patch in first when it isn't).</summary>
    public static void SetMonsterStart(RomBuffer rom, int monster, int bits)
    {
        if (!IsApplied(rom)) Apply(rom);
        int e = MonsterEntry(rom, monster);
        if (e >= 0) rom.WriteUInt16Le(e, (ushort)bits);
    }

    public static void Remove(RomBuffer rom)
    {
        if (IsApplied(rom)) Patch.Restore(rom);
    }
}
