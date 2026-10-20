using LufiaForge.Core.Battle;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    public const int Tiers = 10, PerTier = 32;
    public const int Potion = 0x94, HiPotion = 0x95, ExPotion = 0x96, Antidote = 0x99;
    private const int RecentCount = 8;

    /// <summary>
    /// Chest contents ($7F object +1) for a floor: tier = (floor - 1) / 10, then one of the tier's 32 items at random
    /// (PostHeader, replacing the project's 8 placeholder potions). (Lufia Forge) Variety: a piece of gear is picked
    /// again (up to 12 tries) when a chest of this floor already has it or it was one of the last 8 pieces given out
    /// (floors before); healing items and spell chests may repeat.
    /// </summary>
    private const string LootSource = """
        !TRIES  = $7FFF70
        !PICK   = $7FFF71
        !RECIDX = $7FFF72
        !RECENT = $7FFF78           ; the last 8 pieces of gear given out (ring)

        ; X = 12 x this chest's number (kept), M = 8, X = 16 -> A(8) = an item for the chest
        ChestItem:
            PHX
            LDA.b #12
            STA !TRIES
        .again
            LDA !AC_FLOOR
            DEC A
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            LDA.l LootTier,x        ; tier
            REP #$20
            AND.w #$00FF
            ASL #5                  ; x 32
            PHA
            SEP #$20
            LDA.b #32
            JSR RandL
            REP #$20
            AND.w #$00FF
            CLC
            ADC $01,s
            TAX
            PLA
            SEP #$20
            LDA.l ChestLoot,x
            STA !PICK
            LDA !TRIES
            DEC A
            STA !TRIES
            BEQ .take               ; out of tries: keep it
            JSR RepeatOk
            BCS .take
            REP #$20                ; this floor's chests so far
            LDA $01,s
            TAX
            SEP #$20
        -   CPX.w #$0000
            BEQ .recent
            REP #$20
            TXA
            SEC
            SBC.w #12
            TAX
            SEP #$20
            LDA.l !OBJS+1,x
            CMP !PICK
            BEQ .again
            BRA -
        .recent
            LDX.w #$0000            ; the pieces given out lately
        -   LDA.l !RECENT,x
            CMP !PICK
            BEQ .again
            INX
            CPX.w #8
            BCC -
        .take
            JSR RepeatOk
            BCS +
            LDA !RECIDX             ; remember it
            AND.b #$07
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            LDA !PICK
            STA.l !RECENT,x
            INX
            TXA
            STA !RECIDX
        +   LDA !PICK
            PLX
            RTS

        RepeatOk:                   ; C = !PICK may repeat (healing items, spell chests); X changed
            LDX.w #$0000
        -   LDA.l RepeatItems,x
            CMP !PICK
            BEQ +
            INX
            CPX.w #5
            BCC -
            CLC
            RTS
        +   SEC
            RTS
        """;

    private sealed record ItemInfo(int Id, string Name, int Type, int Slot, int Equip, int Price);

    private static List<ItemInfo> Items(RomBuffer rom)
    {
        var list = new List<ItemInfo>();
        for (int id = 1; id < 0xF0; id++)
        {
            int o = ItemTable.RecordOffset(rom, id);
            if (o <= 0 || o + 18 > rom.Length) continue;
            var nb = rom.ReadBytes(o, 12);
            string name = new string(nb.TakeWhile(c => c is >= 0x20 and < 0x7F).Select(c => (char)c).ToArray()).Trim();
            list.Add(new ItemInfo(id, name, rom.ReadByte(o + 12), rom.ReadByte(o + 13), rom.ReadByte(o + 14), rom.ReadUInt16Le(o + 16)));
        }
        return list;
    }

    // pieces of each kind of gear per tier: weapons, armor, shields, head (helmets, hair bands...), shoes, rings
    private static readonly (int Slot, int Count)[] GearMix = { (1, 6), (2, 4), (3, 3), (4, 3), (5, 2), (6, 2) };
    private const int BattleItemsPerTier = 3, HealingPerTier = 3, SpellsPerTier = 5;

    /// <summary>How strong a piece of gear is, for its kind: weapons by ATP, armor / shields / head by DFP, shoes and
    /// rings (speed, protections) by price.</summary>
    private static int Strength(RomBuffer rom, ItemInfo g)
    {
        int Prop(int id)
        {
            int o = ItemTable.RecordOffset(rom, g.Id) + 21;
            for (int k = 0; k < 16 && rom.ReadByte(o) != 0; k++, o += 3) if (rom.ReadByte(o) == id) return rom.ReadUInt16Le(o + 1);
            return 0;
        }
        return g.Slot switch { 1 => Prop(5), 2 or 3 or 4 => Prop(6), _ => g.Price };
    }

    // stat potions (Power, Spell, Speed, Mind; Great Potion deeper down), one entry per tier
    private static readonly string[] StatPotionNames = { "Power Potion", "Spell Potion", "Speed Potion", "Mind Potion", "Great Potion" };

    private static List<int> StatPotions(RomBuffer rom)
    {
        var items = Items(rom);
        return StatPotionNames.Select(n => items.FirstOrDefault(i => i.Name.Replace('@', ' ') == n)?.Id ?? 0).ToList();
    }

    // battle items (type 81): arrows, bombs, pins and the like; not the quest odds and ends of the same type
    private static readonly string[] BattleItemWords = { "Arrow", "Bomb", "Pin", "Shriek", "Smoke", "Tooth", "Gourd", "Magic Guard" };

    private static List<ItemInfo> BattleItems(RomBuffer rom) =>
        Items(rom).Where(i => i.Type == 0x81 && i.Price > 0 && BattleItemWords.Any(w => i.Name.Replace('@', ' ').Contains(w, StringComparison.OrdinalIgnoreCase)))
                  .OrderBy(i => i.Price).ThenBy(i => i.Id).ToList();

    /// <summary>
    /// The 10 tiers x 32 chest items: 20 pieces of gear, every kind in each tier (6 weapons, 4 armors, 3 shields, 3 for
    /// the head, 2 shoes, 2 rings). Each kind is a ladder sorted by <see cref="Strength"/>, one for the Hero and Aguro
    /// and one for Lufia and Jerin (rings: one for all), and tier t takes the (t+1)/11 to (t+2)/11 step of it: the
    /// steps don't overlap, so deeper tiers never give weaker gear. Then 3 battle items (arrows, bombs...; the t/10 to
    /// (t+2)/10 share by price), 3 healing items (stronger deeper down), a stat potion (Power, Spell, Speed and Mind in
    /// turn; from tier 7 every other tier a Great Potion) and 5 spell chests (marker F0: the spell is picked when the
    /// chest opens), about 1 chest in 6.
    /// </summary>
    public static byte[] ChestLoot(RomBuffer rom)
    {
        var items = Items(rom);
        var gear = items.Where(i => i.Type is 0x02 or 0x06 && i.Slot is >= 1 and <= 6 && (i.Equip & 0xF0) != 0 && i.Price > 0).ToList();
        bool usable(int id) => items.Any(i => i.Id == id && (i.Type & 0x41) != 0);
        int Pick(int want, int fallback) => usable(want) ? want : fallback;
        int potion = Pick(Potion, items.FirstOrDefault(i => (i.Type & 0x41) != 0)?.Id ?? 1);
        int hi = Pick(HiPotion, potion), ex = Pick(ExPotion, hi), anti = Pick(Antidote, potion);
        bool spells = AllSpells(rom).Count > 0;
        var battle = BattleItems(rom).Select(i => i.Id).ToList();
        var statPotions = StatPotions(rom);
        var data = new byte[Tiers * PerTier];
        for (int t = 0; t < Tiers; t++)
        {
            var row = new List<int>();
            foreach (var (slot, count) in GearMix)
            {
                // two ladders where the kind has gear for both: the Hero and Aguro's, Lufia and Jerin's (gear for all
                // is on both); rings are everyone's
                var kind = gear.Where(g => g.Slot == slot).ToList();
                var ladders = slot == 6 ? new List<List<ItemInfo>> { kind }
                    : new[] { 0x50, 0xA0 }.Select(who => kind.Where(g => (g.Equip & who) != 0).ToList()).Where(l => l.Count > 0).ToList();
                for (int k = 0; k < count && ladders.Count > 0; k++)
                {
                    var ladder = ladders[k % ladders.Count].OrderBy(g => Strength(rom, g)).ThenBy(g => g.Price).ThenBy(g => g.Id).ToList();
                    int n = ladder.Count, per = (count + ladders.Count - 1 - k % ladders.Count) / ladders.Count, j = k / ladders.Count;
                    // tier t: the (t+1)/11 to (t+2)/11 step of the ladder (the weakest step is left out); steps don't
                    // overlap, so a deeper tier never has weaker gear than one before it
                    int lo = Math.Min(n - 1, n * (t + 1) / (Tiers + 1));
                    int top = Math.Max(lo + 1, Math.Min(n, n * (t + 2) / (Tiers + 1)));
                    row.Add(ladder[Math.Min(n - 1, lo + (top - lo) * j / Math.Max(1, per))].Id);
                }
            }
            row.AddRange(Band(battle, t, BattleItemsPerTier));
            row.AddRange((t switch
            {
                <= 2 => new[] { potion, potion, anti },
                <= 6 => new[] { hi, hi, ex },
                _ => new[] { hi, ex, ex },
            }).Take(HealingPerTier));
            int stat = t >= 7 && t % 2 == 1 && statPotions[4] != 0 ? statPotions[4] : statPotions[t % 4];
            row.Add(stat != 0 ? stat : potion);
            while (row.Count < PerTier - SpellsPerTier) row.Add(potion);
            row = row.Take(PerTier - SpellsPerTier).ToList();
            for (int k = 0; k < SpellsPerTier; k++) row.Add(spells ? FirstMarker : potion);
            for (int k = 0; k < PerTier; k++) data[t * PerTier + k] = (byte)row[k];
        }
        return data;
    }

    /// <summary><paramref name="count"/> different entries of a price-sorted list for tier <paramref name="t"/>: spread
    /// over its t/10 to (t+2)/10 share.</summary>
    private static IEnumerable<int> Band(List<int> sorted, int t, int count)
    {
        if (sorted.Count == 0) yield break;
        int lo = Math.Min(sorted.Count - 1, sorted.Count * t / Tiers);
        int hi = Math.Min(sorted.Count, Math.Max(lo + count, sorted.Count * (t + 2) / Tiers));
        lo = Math.Max(0, Math.Min(lo, hi - count));
        for (int k = 0; k < count; k++) yield return sorted[Math.Min(sorted.Count - 1, lo + (hi - lo) * k / count)];
    }

    /// <summary>Items a floor may have more than once (healing items, the spell chest marker).</summary>
    private static byte[] RepeatItems(RomBuffer rom)
    {
        var items = Items(rom);
        bool usable(int id) => items.Any(i => i.Id == id && (i.Type & 0x41) != 0);
        return new[] { Potion, HiPotion, ExPotion, Antidote }.Select(id => (byte)(usable(id) ? id : ChestPotion(rom))).Append((byte)FirstMarker).ToArray();
    }

    /// <summary>The potion a spell chest gives when its hero knows the spell already.</summary>
    private static int ChestPotion(RomBuffer rom) => Items(rom).Any(i => i.Id == Potion && (i.Type & 0x41) != 0) ? Potion : 1;

    /// <summary>Floor 1-100 -> its tier (row <see cref="PerTier"/> x tier of <see cref="ChestLoot"/>): as far through the
    /// tiers as the floor's monsters are through the groups, so the gear keeps up with the monsters.</summary>
    private static byte[] LootTiers(RomBuffer rom)
    {
        var rows = CaveGroups(rom);
        // (never back down: floors that mix in an easier group keep the tier reached)
        int best = 0;
        return GroupIndexByFloor(rom, rows).Select(i => (byte)(best = Math.Max(best, Math.Min(Tiers - 1, (int)Math.Round(i * (double)Tiers / rows.Count))))).ToArray();
    }

    /// <summary>What the chests can hold, tier by tier (for the Patch Manager / tests).</summary>
    public static List<string> LootSummary(RomBuffer rom)
    {
        var names = Items(rom).ToDictionary(i => i.Id, i => $"{i.Name} ({i.Price} GP)");
        names[FirstMarker] = "spell chest";
        var loot = ChestLoot(rom);
        return Enumerable.Range(0, Tiers).Select(t => $"floors {t * 10 + 1}-{t * 10 + 10}: " +
            string.Join(", ", loot.Skip(t * PerTier).Take(PerTier).Distinct().Select(id => names.GetValueOrDefault(id, $"item {id:X2}")))).ToList();
    }
}
