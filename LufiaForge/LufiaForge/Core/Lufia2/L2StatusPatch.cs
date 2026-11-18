namespace LufiaForge.Core.Lufia2;

// lufia 2: 16 new statuses, as new battle code (source: tools/statuses/l2status.asm, the data in L2StatusPatch.Data.cs
// is generated from it by tools/statuses/gen.py)
// - each fighter has a second status word (bits: Names) in RAM at $7F:FC04 (heroes 0-6 by record, monster slots, the
//   capsule monster). Zombie, Decay, Burnt and Blind stay on a hero after a battle and get saved (in the save
//   buffer's unused bytes $7E:3BBB-3BC3, behind a marker, so an old save loads with none). the rest end with the battle
// - battle script commands: 5D s c causes status s with chance c (%), 5E s c cures it, 5F s p resists it by p% (in a
//   "when hit" or armor script, for that target only), 60 s wards the targets against it for the battle. for 5E, 5F
//   and 60, s = 10 means all 16 (5F 10 64 = immune to every one, like the game's shared script 11 is to its own eight)
// - hooks in the game's code: a fighter's turn ($81:A7DA), the heroes' commands ($81:CA9B, $85:9288, $85:925A), the
//   confused action ($81:AF3F, $81:AF64: berserk), the damage ($85:D2C5), DFP ($85:D18E), each target ($85:D0CD,
//   $85:D454), the action's end ($85:CE43, $81:A82C: messages), the round's end ($81:8943: burns, bleeding, regen,
//   mana leak, doom, decay, timers), poison ($81:C6BB, it heals a zombie), the battle's end and start, saving and
//   loading, IP ($85:D508, none when cursed)
// - from the 4th version (2026-11-15): Bounce, the game's status bit 6 (physical hits go back at the attacker, plain
//   weapon attacks too. a monster or a worn item's wearer can start every battle with it, an item can give or cure it),
//   and focus (the Tag ring and Tag candy: each weapon attack in a row on one foe does more, FocusPercents, command 61
//   gives it for the battle). the 5th (after 0.4.0-beta.28) only changes the messages to "<name> bounces it back!" and
//   "<name>'s focus: 25% more" (the old ones didn't fit the box, and the battle font has no +)
public static partial class L2StatusPatch
{
    private static HookPatch? _patch;
    private static HookPatch Patch => _patch ??= new(Sites, Code);

    // the 16 statuses, by bit
    public static readonly string[] Names =
    {
        "Zombie", "Frozen", "Decay", "Burnt", "Berserk", "Spooked", "Amnesia", "Piety",
        "Regen", "Doom", "Blind", "Stone", "Bleed", "Curse", "Stop", "Mana leak",
    };

    // the status number 5E, 5F and 60 read as all 16 at once
    public const int Every = 0x10;
    public const string EveryName = "Every new status";

    // a status's name (or EveryName)
    public static string NameOf(int status) => status == Every ? EveryName : status >= 0 && status < Names.Length ? Names[status] : $"new status {status}";

    // what each one does, for the editor
    public static readonly string[] Help =
    {
        "Healing hurts it, poison heals it, light does 3x damage. Stays after the battle.",
        "Can't act. The next physical hit does 2.5x and shatters the ice; fire melts it.",
        "Each round a random stat drops 1-15%. Stays after the battle.",
        "Takes damage each round (as poison does) and its physical attacks do half; water or ice magic cures it. Stays after the battle.",
        "Attacks on its own, friend or foe (foes 70% of the time), with double damage, for 3-10 rounds.",
        "Too scared to act 1 time in 4; calms down by itself.",
        "Its magic does 25% less, its DFP is 50% higher.",
        "Its healing (HP and MP) and its light attacks do 25% more, but it takes 50% more damage from dark.",
        "Gets back 1/12 of its max HP each round.",
        "Dies after 3 rounds unless cured.",
        "Half of its physical attacks miss. Stays after the battle.",
        "Can't act and takes no damage or healing; every hero stone loses the battle.",
        "Loses 1/16 of its max HP each round it acts.",
        "Healing does half, and a cursed hero gets no IP.",
        "Skips its turns for 2-4 rounds.",
        "Loses 1/12 of its max MP each round.",
    };

    // true if the newest version is in
    public static bool IsApplied(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile) >= 0;

    // some version with the element table is in (the newest, or the second, which Apply upgrades), so its data can be read
    public static bool IsIn(RomBuffer rom) => DataAt(rom) >= 0;

    // file offset of the patch's block, or -1
    public static int BlockAt(RomBuffer rom) => Patch.AppliedAt(rom, L2Rom.SnesToFile);

    public static bool IsOriginal(RomBuffer rom) => Patch.IsOriginal(rom);

    private static HookPatch? _v1, _v2, _v3, _v4;
    private static HookPatch V1 => _v1 ??= new(SitesV1, CodeV1);
    private static HookPatch V2 => _v2 ??= new(SitesV2, CodeV2);
    private static HookPatch V3 => _v3 ??= new(SitesV3, CodeV3);
    private static HookPatch V4 => _v4 ??= new(SitesV4, CodeV4);

    // file offset of the data after the code (the monsters' starting statuses, the resistance records, the element
    // table's address), for the newest version or the second. -1 if neither is in
    internal static int DataAt(RomBuffer rom)
    {
        int at = Patch.AppliedAt(rom, L2Rom.SnesToFile);
        if (at >= 0) return at + Patch.Code.Bytes.Length;
        at = V4.AppliedAt(rom, L2Rom.SnesToFile);
        if (at >= 0) return at + CodeV4.Bytes.Length;
        at = V3.AppliedAt(rom, L2Rom.SnesToFile);
        if (at >= 0) return at + CodeV3.Bytes.Length;
        at = V2.AppliedAt(rom, L2Rom.SnesToFile);
        return at >= 0 ? at + CodeV2.Bytes.Length : -1;
    }

    // file offset of the element table's address (4 bytes), or -1
    internal static int ElemPtrFile(RomBuffer rom) => DataAt(rom) is var data and >= 0 ? data + MonsterTableSize + ResistTableSize : -1;

    // true if the rom has an older version (Apply upgrades it)
    public static bool IsOldVersion(RomBuffer rom) =>
        !IsApplied(rom) && (V1.AppliedAt(rom, L2Rom.SnesToFile) >= 0 || V2.AppliedAt(rom, L2Rom.SnesToFile) >= 0 ||
                            V3.AppliedAt(rom, L2Rom.SnesToFile) >= 0 || V4.AppliedAt(rom, L2Rom.SnesToFile) >= 0);

    // the 4th version (0.4.0-beta.28) is in: its Bounce and focus messages need the 5th (opening a project upgrades
    // it and keeps all its data)
    public static bool HasFourthVersion(RomBuffer rom) => !IsApplied(rom) && V4.AppliedAt(rom, L2Rom.SnesToFile) >= 0;

    public static void Apply(RomBuffer rom) =>
        // all or nothing: an older version's code comes out first, and moving the game's weaknesses saves scripts that
        // can fail. a failure has to leave the rom as it was, not with the battle code half in
        L2Rom.AllOrNothing(rom, () => { ApplyCore(rom); return 0; });

    private static void ApplyCore(RomBuffer rom)
    {
        if (IsApplied(rom)) return;
        byte[]? starts = null, elemPtr = null, all = null;
        if (V4.AppliedAt(rom, L2Rom.SnesToFile) is var v4 and >= 0)
        {
            // the fourth version (0.4.0-beta.28): the same data, all of it kept (only its messages changed)
            all = rom.ReadBytes(v4 + CodeV4.Bytes.Length, DataSize);
            elemPtr = all.AsSpan(MonsterTableSize + ResistTableSize, 4).ToArray();
            V4.Restore(rom);
        }
        else if (V3.AppliedAt(rom, L2Rom.SnesToFile) is var v3 and >= 0)
        {
            // the third version: same as the second
            int data = v3 + CodeV3.Bytes.Length;
            starts = rom.ReadBytes(data, MonsterTableSize);
            elemPtr = rom.ReadBytes(data + MonsterTableSize + ResistTableSize, 4);
            V3.Restore(rom);
        }
        else if (V2.AppliedAt(rom, L2Rom.SnesToFile) is var v2 and >= 0)
        {
            // the second version: its starting statuses and its element table (already filled in) come along
            int data = v2 + CodeV2.Bytes.Length;
            starts = rom.ReadBytes(data, MonsterTableSize);
            elemPtr = rom.ReadBytes(data + MonsterTableSize + ResistTableSize, 4);
            V2.Restore(rom);
        }
        else if (V1.AppliedAt(rom, L2Rom.SnesToFile) is var v1 and >= 0)
        {
            // the first version: its monsters' starting statuses come along, its code comes out
            starts = rom.ReadBytes(v1 + CodeV1.Bytes.Length, MonsterTableSize);
            V1.Restore(rom);
        }
        if (!IsOriginal(rom)) throw new InvalidOperationException("Some of the battle code the new statuses change isn't the US game's (another patch or a fan hack changed it), so they can't be added safely.");
        L2Rom.AllOrNothing(rom, () =>
        {
            // the block, then the monsters' starting statuses (256 words, none to start with), the resistance records and
            // the element table's address. the element table goes on its own (it's read with the data bank set to its bank)
            int at = L2Rom.Allocate(rom, Patch.Code.Bytes.Length + DataSize);
            rom.WriteBytes(at + Patch.Code.Bytes.Length, new byte[DataSize]);
            if (starts != null) rom.WriteBytes(at + Patch.Code.Bytes.Length, starts);
            rom.WriteBytes(at + Patch.Code.Bytes.Length + FocusPctAt, DefaultFocus);
            if (all != null) rom.WriteBytes(at + Patch.Code.Bytes.Length, all);
            Patch.Write(rom, at, L2Rom.FileToSnes(at));
            if (elemPtr != null) rom.WriteBytes(at + ElemPtrAt, elemPtr);
            else
            {
                int table = L2Rom.Allocate(rom, L2Elements.TableSize);
                rom.WriteBytes(table, new byte[L2Elements.TableSize]);
                int tableSnes = L2Rom.FileToSnes(table);
                rom.WriteBytes(at + ElemPtrAt, new[] { (byte)tableSnes, (byte)(tableSnes >> 8), (byte)(tableSnes >> 16), (byte)0 });
            }
            return at;
        });
        // the game's weaknesses and armor guards move into the table (an upgrade keeps the table it had)
        if (elemPtr == null) L2Elements.MoveAll(rom);
        WriteResistTable(rom);
    }

    private const int MonsterTableSize = 0x200, ResistRecord = 10, ResistTableSize = 0x200 * ResistRecord;

    // the data after the code (from the 4th version): the monsters' new statuses at the start, the resistance records,
    // the element table's address, then the game's statuses each monster starts with (a byte per id), a word per item
    // (low byte = the game's statuses its wearer starts each battle with, bit 8 = focus when worn) and the 5 focus percents
    private const int GameStartAt = MonsterTableSize + ResistTableSize + 4, ItemXAt = GameStartAt + 0x100,
        FocusPctAt = ItemXAt + 0x400, DataSize = FocusPctAt + 8;

    // the focus bonus of the 2nd-6th attack in a row on one foe, to start with (Mewlies' idea)
    public static readonly byte[] DefaultFocus = { 10, 25, 50, 75, 100 };

    // the game's status bits a starting status can be: Bounce (bit 6) and Mirror (bit 7)
    public const int Bounce = 0x40, Mirror = 0x80;

    // file offset of this version's own data (the 4th version's tables), or -1 if it isn't in
    private static int NewDataAt(RomBuffer rom) =>
        Patch.AppliedAt(rom, L2Rom.SnesToFile) is var at and >= 0 ? at + Patch.Code.Bytes.Length :
        V4.AppliedAt(rom, L2Rom.SnesToFile) is var v4At and >= 0 ? v4At + CodeV4.Bytes.Length : -1;   // the 4th: the same tables

    // the game's statuses (bits as in the status byte: Bounce, Mirror) the monster has from the moment it's in a battle
    public static int MonsterGameStart(RomBuffer rom, int monster) =>
        NewDataAt(rom) is var data and >= 0 && monster is >= 0 and <= 0xFF ? rom.ReadByte(data + GameStartAt + monster) : 0;

    public static void SetMonsterGameStart(RomBuffer rom, int monster, int bits)
    {
        if (!IsApplied(rom)) Apply(rom);
        if (monster is >= 0 and <= 0xFF) rom.WriteByte(NewDataAt(rom) + GameStartAt + monster, (byte)bits);
    }

    // the game's statuses the item gives its wearer at the start of every battle
    public static int ItemGameStart(RomBuffer rom, int item) =>
        NewDataAt(rom) is var data and >= 0 && item is >= 0 and < 0x200 ? rom.ReadByte(data + ItemXAt + item * 2) : 0;

    public static void SetItemGameStart(RomBuffer rom, int item, int bits)
    {
        if (!IsApplied(rom)) Apply(rom);
        if (item is >= 0 and < 0x200) rom.WriteByte(NewDataAt(rom) + ItemXAt + item * 2, (byte)bits);
    }

    // true if wearing the item gives focus (like the Tag ring does): each attack in a row on one foe does more
    // (FocusPercents)
    public static bool ItemFocus(RomBuffer rom, int item) =>
        NewDataAt(rom) is var data and >= 0 && item is >= 0 and < 0x200 && (rom.ReadByte(data + ItemXAt + item * 2 + 1) & 1) != 0;

    public static void SetItemFocus(RomBuffer rom, int item, bool on)
    {
        if (!IsApplied(rom)) Apply(rom);
        if (item is < 0 or >= 0x200) return;
        int at = NewDataAt(rom) + ItemXAt + item * 2 + 1;
        rom.WriteByte(at, (byte)(on ? rom.ReadByte(at) | 1 : rom.ReadByte(at) & ~1));
    }

    // the focus bonus (percent) of the 2nd, 3rd, 4th, 5th and 6th-and-later attack in a row on one foe
    public static byte[] FocusPercents(RomBuffer rom) =>
        NewDataAt(rom) is var data and >= 0 ? rom.ReadBytes(data + FocusPctAt, 5) : (byte[])DefaultFocus.Clone();

    public static void SetFocusPercents(RomBuffer rom, IReadOnlyList<int> percents)
    {
        if (!IsApplied(rom)) Apply(rom);
        int data = NewDataAt(rom);
        for (int i = 0; i < 5 && i < percents.Count; i++) rom.WriteByte(data + FocusPctAt + i, (byte)Math.Clamp(percents[i], 0, 255));
    }

    // what an item gives its wearer, for the STATUS screen's resistance pages (Y). read from its armor script ("when
    // its wearer is hit"): elements halved / nullified (shared scripts 06-0E, 18 and 19, or "if the element is n,
    // divide the damage"), the game's statuses made impossible (chance set to 0) or halved, the new statuses resisted (5F)
    public sealed record Resist(int ElementHalf, int ElementNone, int OldImmune, int OldHalf, int NewImmune, int NewPartial)
    {
        public bool Any => (ElementHalf | ElementNone | OldImmune | OldHalf | NewImmune | NewPartial) != 0;
    }

    // the shared scripts that halve an element (read from them: 06 fire... 0E dark, 0B = attacks with no element)
    private static readonly Dictionary<int, int> LibraryHalves = new() { [0x06] = 0x0002, [0x07] = 0x0800, [0x08] = 0x0004, [0x09] = 0x0400, [0x0A] = 0x0010, [0x0B] = 0x0001, [0x0E] = 0x0020 };

    public static Resist ResistOf(RomBuffer rom, int item)
    {
        List<L2AiScripts.Step> steps;
        try { steps = L2AiScripts.Load(rom, L2AiScripts.Kind.Armor, item).Steps; } catch (Exception) { return new(0, 0, 0, 0, 0, 0); }
        int half = 0, none = 0, oldImm = 0, oldHalf = 0, newImm = 0, newPart = 0, counter2 = 0, counter1 = -1;
        int V(L2AiScripts.Step step, int i) => L2AiScripts.ArgValue(step, i);
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
                    if (LibraryHalves.TryGetValue(lib, out var element)) half |= element;
                    if (lib == 0x18 && (counter2 >> 8) != 0x80) half |= counter2;
                    if (lib == 0x19 && (counter2 >> 8) != 0x80) none |= counter2;
                    break;
                case 0x06 when counter1 == 0 && V(st, 0) == 0x80 && st.Targets.Length > 0 && st.Targets[0] is { } target:
                    // "if its counter 1 (the element) is n, go to" a step that divides the damage by 2 or sets it to 0
                    if (target.Op == 0x10 && V(target, 0) == 0x2A && V(target, 1) == 2) half |= V(st, 1);
                    if (target.Op == 0x0C && V(target, 0) == 0x2A && V(target, 1) == 0) none |= V(st, 1);
                    break;
                case 0x07 when V(st, 0) == 0x27 && V(st, 1) == 0 && steps.ElementAtOrDefault(i + 1) is { Op: 0x10 } divide && V(divide, 0) == 0x2A && V(divide, 1) == 2:
                    half |= 0x0001;                                     // "if the element is not 0, skip. halve": no element
                    break;
                case 0x5F:
                    int bits = st.Args[0] >= 0x10 ? 0xFFFF : 1 << (st.Args[0] & 0x0F), percent = st.Args[1];   // 10 = every one
                    if (percent >= 100) newImm |= bits; else if (percent > 0) newPart |= bits;
                    break;
            }
        }
        return new(half, none, oldImm, oldHalf & ~oldImm, newImm, newPart & ~newImm);
    }

    // writes every item's record (call after an armor script changes, does nothing without the patch)
    public static void WriteResistTable(RomBuffer rom)
    {
        int at = DataAt(rom);
        if (at < 0) return;
        int table = at + MonsterTableSize;
        int count = Math.Min(L2Battle.ItemCountIn(rom), 0x200);
        var data = new byte[ResistTableSize];
        for (int i = 0; i < count; i++)
        {
            var resist = ResistOf(rom, i);
            if (!resist.Any) continue;
            int record = i * ResistRecord;
            data[record] = (byte)resist.ElementHalf; data[record + 1] = (byte)(resist.ElementHalf >> 8);
            data[record + 2] = (byte)resist.ElementNone; data[record + 3] = (byte)(resist.ElementNone >> 8);
            data[record + 4] = (byte)resist.OldImmune; data[record + 5] = (byte)resist.OldHalf;
            data[record + 6] = (byte)resist.NewImmune; data[record + 7] = (byte)(resist.NewImmune >> 8);
            data[record + 8] = (byte)resist.NewPartial; data[record + 9] = (byte)(resist.NewPartial >> 8);
        }
        if (!rom.ReadBytes(table, data.Length).AsSpan().SequenceEqual(data)) rom.WriteBytes(table, data);
    }

    // file offset of the monster's starting statuses, or -1 without the patch
    private static int MonsterEntry(RomBuffer rom, int monster)
    {
        int data = DataAt(rom);
        return data < 0 || monster is < 0 or > 0xFF ? -1 : data + monster * 2;
    }

    // the statuses the monster has from the moment it's in a battle (bits as in Names, Zombie makes an undead monster)
    public static int MonsterStart(RomBuffer rom, int monster) => MonsterEntry(rom, monster) is var entry and >= 0 ? rom.ReadUInt16Le(entry) : 0;

    // sets them (puts the patch in first if it isn't)
    public static void SetMonsterStart(RomBuffer rom, int monster, int bits)
    {
        if (!IsApplied(rom)) Apply(rom);
        int entry = MonsterEntry(rom, monster);
        if (entry >= 0) rom.WriteUInt16Le(entry, (ushort)bits);
    }

    // the game's Tag rings (1CE, 1CF in the US game) and Tag candy (1D1): items no code in the game uses (nothing reads
    // them, the candy has an animation but no script). found by name
    public static (List<int> Rings, List<int> Candies) TagItems(RomBuffer rom)
    {
        var rings = new List<int>(); var candies = new List<int>();
        int count = Math.Min(L2Battle.ItemCountIn(rom), 0x200);
        for (int i = 0; i < count; i++)
        {
            string name = L2Battle.ReadItem(rom, i).Name.Trim();
            if (name.Equals("Tag ring", StringComparison.OrdinalIgnoreCase)) rings.Add(i);
            else if (name.Equals("Tag candy", StringComparison.OrdinalIgnoreCase)) candies.Add(i);
        }
        return (rings, candies);
    }

    // true if the Tag rings give focus when worn and the Tag candy gives it in battle (MakeTagItems)
    public static bool TagItemsDone(RomBuffer rom)
    {
        var (rings, candies) = TagItems(rom);
        return rings.Count + candies.Count > 0 && rings.All(r => ItemFocus(rom, r)) &&
               candies.All(c => L2StatusSteps.HasStep(rom, L2AiScripts.Kind.ItemBattle, c, 0x61));
    }

    // Mewlies' idea for the game's unused Tag ring and Tag candy: the ring gives focus to its wearer, the candy gives a
    // hero focus for the rest of the battle, so each weapon attack in a row on the same foe does more (FocusPercents).
    // returns what changed
    public static List<string> MakeTagItems(RomBuffer rom)
    {
        var done = new List<string>();
        var (rings, candies) = TagItems(rom);
        if (rings.Count + candies.Count == 0) return done;
        if (!IsApplied(rom)) Apply(rom);
        foreach (int ring in rings.Where(r => !ItemFocus(rom, r))) { SetItemFocus(rom, ring, true); done.Add($"Tag ring ({ring:X3}): focus when worn"); }
        foreach (int candy in candies)
            if (L2StatusSteps.SetStep(rom, L2AiScripts.Kind.ItemBattle, candy, true, 0x61)) done.Add($"Tag candy ({candy:X3}): focus for the battle when used");
        return done;
    }

    public static void Remove(RomBuffer rom)
    {
        if (IsApplied(rom)) Patch.Restore(rom);
    }
}
