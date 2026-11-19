using LufiaForge.Core.Battle;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    public const int Tiers = 10, PerTier = 32;
    public const int Potion = 0x94, HiPotion = 0x95, ExPotion = 0x96, Antidote = 0x99;
    private const int RecentCount = 8;

    // chest contents ($7F object +1) for a floor: tier = (floor - 1) / 10, then one of the tier's 32 items at random
    // (PostHeader, replacing the project's 8 placeholder potions). (ours) variety: a piece of gear gets picked
    // again (up to 12 tries) when a chest on this floor already has it or it was one of the last 8 pieces given out
    // (on floors before). healing items and spell chests can repeat
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
            int recordOffset = ItemTable.RecordOffset(rom, id);
            if (recordOffset <= 0 || recordOffset + 18 > rom.Length) continue;
            var nameBytes = rom.ReadBytes(recordOffset, 12);
            string name = new string(nameBytes.TakeWhile(c => c is >= 0x20 and < 0x7F).Select(c => (char)c).ToArray()).Trim();
            list.Add(new ItemInfo(id, name, rom.ReadByte(recordOffset + 12), rom.ReadByte(recordOffset + 13), rom.ReadByte(recordOffset + 14), rom.ReadUInt16Le(recordOffset + 16)));
        }
        return list;
    }

    // pieces of each kind of gear per tier: weapons, armor, shields, head (helmets, hair bands...), shoes, rings
    private static readonly (int Slot, int Count)[] GearMix = { (1, 6), (2, 4), (3, 3), (4, 3), (5, 2), (6, 2) };
    private const int BattleItemsPerTier = 3, HealingPerTier = 3, SpellsPerTier = 5;

    // how strong a piece of gear is for its kind: weapons by ATP, armor / shields / head by DFP, shoes and
    // rings (speed, protections) by price
    private static int Strength(RomBuffer rom, ItemInfo gear)
    {
        int Prop(int property)
        {
            int propertyAt = ItemTable.RecordOffset(rom, gear.Id) + 21;
            for (int k = 0; k < 16 && rom.ReadByte(propertyAt) != 0; k++, propertyAt += 3) if (rom.ReadByte(propertyAt) == property) return rom.ReadUInt16Le(propertyAt + 1);
            return 0;
        }
        return gear.Slot switch { 1 => Prop(5), 2 or 3 or 4 => Prop(6), _ => gear.Price };
    }

    // stat potions (Power, Spell, Speed, Mind, and Great Potion deeper down), one entry per tier
    private static readonly string[] StatPotionNames = { "Power Potion", "Spell Potion", "Speed Potion", "Mind Potion", "Great Potion" };

    private static List<int> StatPotions(RomBuffer rom)
    {
        var items = Items(rom);
        return StatPotionNames.Select(name => items.FirstOrDefault(i => i.Name.Replace('@', ' ') == name)?.Id ?? 0).ToList();
    }

    // battle items (type 81): arrows, bombs, pins and the like, but not the quest odds and ends of the same type
    private static readonly string[] BattleItemWords = { "Arrow", "Bomb", "Pin", "Shriek", "Smoke", "Tooth", "Gourd", "Magic Guard" };

    private static List<ItemInfo> BattleItems(RomBuffer rom) =>
        Items(rom).Where(i => i.Type == 0x81 && i.Price > 0 && BattleItemWords.Any(w => i.Name.Replace('@', ' ').Contains(w, StringComparison.OrdinalIgnoreCase)))
                  .OrderBy(i => i.Price).ThenBy(i => i.Id).ToList();

    // the 10 tiers x 32 chest items: 20 pieces of gear, every kind in each tier (6 weapons, 4 armors, 3 shields, 3 for
    // the head, 2 shoes, 2 rings). each kind is a ladder sorted by Strength, one for the Hero and Aguro
    // and one for Lufia and Jerin (rings: one for everyone), and tier t takes the (t+1)/11 to (t+2)/11 step of it. the
    // steps don't overlap, so deeper tiers never give weaker gear. then 3 battle items (arrows, bombs... the t/10 to
    // (t+2)/10 share by price), 3 healing items (stronger deeper down), a stat potion (Power, Spell, Speed and Mind in
    // turn, from tier 7 every other tier a Great Potion) and 5 spell chests (marker F0, the spell gets picked when the
    // chest opens), about 1 chest in 6
    public static byte[] ChestLoot(RomBuffer rom)
    {
        var items = Items(rom);
        var gear = items.Where(i => i.Type is 0x02 or 0x06 && i.Slot is >= 1 and <= 6 && (i.Equip & 0xF0) != 0 && i.Price > 0).ToList();
        bool usable(int id) => items.Any(i => i.Id == id && (i.Type & 0x41) != 0);
        int Pick(int wanted, int fallback) => usable(wanted) ? wanted : fallback;
        int potion = Pick(Potion, items.FirstOrDefault(i => (i.Type & 0x41) != 0)?.Id ?? 1);
        int hiPotion = Pick(HiPotion, potion), exPotion = Pick(ExPotion, hiPotion), antidote = Pick(Antidote, potion);
        bool spells = AllSpells(rom).Count > 0;
        var battle = BattleItems(rom).Select(i => i.Id).ToList();
        var statPotions = StatPotions(rom);
        var data = new byte[Tiers * PerTier];
        for (int tier = 0; tier < Tiers; tier++)
        {
            var row = new List<int>();
            foreach (var (slot, count) in GearMix)
            {
                // two ladders where the kind has gear for both: the Hero and Aguro's, Lufia and Jerin's (gear for everyone
                // is on both). rings are everyone's
                var kind = gear.Where(g => g.Slot == slot).ToList();
                var ladders = slot == 6 ? new List<List<ItemInfo>> { kind }
                    : new[] { 0x50, 0xA0 }.Select(who => kind.Where(g => (g.Equip & who) != 0).ToList()).Where(l => l.Count > 0).ToList();
                for (int k = 0; k < count && ladders.Count > 0; k++)
                {
                    var ladder = ladders[k % ladders.Count].OrderBy(g => Strength(rom, g)).ThenBy(g => g.Price).ThenBy(g => g.Id).ToList();
                    int ladderSize = ladder.Count, perLadder = (count + ladders.Count - 1 - k % ladders.Count) / ladders.Count, step = k / ladders.Count;
                    // tier t: the (t+1)/11 to (t+2)/11 step of the ladder (the weakest step is left out). steps don't
                    // overlap, so a deeper tier never has weaker gear than one before it
                    int lo = Math.Min(ladderSize - 1, ladderSize * (tier + 1) / (Tiers + 1));
                    int top = Math.Max(lo + 1, Math.Min(ladderSize, ladderSize * (tier + 2) / (Tiers + 1)));
                    row.Add(ladder[Math.Min(ladderSize - 1, lo + (top - lo) * step / Math.Max(1, perLadder))].Id);
                }
            }
            row.AddRange(Band(battle, tier, BattleItemsPerTier));
            row.AddRange((tier switch
            {
                <= 2 => new[] { potion, potion, antidote },
                <= 6 => new[] { hiPotion, hiPotion, exPotion },
                _ => new[] { hiPotion, exPotion, exPotion },
            }).Take(HealingPerTier));
            int stat = tier >= 7 && tier % 2 == 1 && statPotions[4] != 0 ? statPotions[4] : statPotions[tier % 4];
            row.Add(stat != 0 ? stat : potion);
            while (row.Count < PerTier - SpellsPerTier) row.Add(potion);
            row = row.Take(PerTier - SpellsPerTier).ToList();
            for (int k = 0; k < SpellsPerTier; k++) row.Add(spells ? FirstMarker : potion);
            for (int k = 0; k < PerTier; k++) data[tier * PerTier + k] = (byte)row[k];
        }
        return data;
    }

    // count different entries of a price-sorted list for tier, spread
    // over its tier/10 to (tier+2)/10 share
    private static IEnumerable<int> Band(List<int> sorted, int tier, int count)
    {
        if (sorted.Count == 0) yield break;
        int lo = Math.Min(sorted.Count - 1, sorted.Count * tier / Tiers);
        int hi = Math.Min(sorted.Count, Math.Max(lo + count, sorted.Count * (tier + 2) / Tiers));
        lo = Math.Max(0, Math.Min(lo, hi - count));
        for (int k = 0; k < count; k++) yield return sorted[Math.Min(sorted.Count - 1, lo + (hi - lo) * k / count)];
    }

    // items a floor can have more than once (healing items, the spell chest marker)
    private static byte[] RepeatItems(RomBuffer rom)
    {
        var items = Items(rom);
        bool usable(int id) => items.Any(i => i.Id == id && (i.Type & 0x41) != 0);
        return new[] { Potion, HiPotion, ExPotion, Antidote }.Select(id => (byte)(usable(id) ? id : ChestPotion(rom))).Append((byte)FirstMarker).ToArray();
    }

    // the potion a spell chest gives when its hero already knows the spell
    private static int ChestPotion(RomBuffer rom) => Items(rom).Any(i => i.Id == Potion && (i.Type & 0x41) != 0) ? Potion : 1;

    // floor 1-100 -> its tier (row PerTier x tier of ChestLoot): as far through the
    // tiers as the floor's monsters are through the groups, so the gear keeps up with the monsters
    private static byte[] LootTiers(RomBuffer rom)
    {
        var rows = CaveGroups(rom);
        // (never goes back down, floors that mix in an easier group keep the tier they reached)
        int best = 0;
        return GroupIndexByFloor(rom, rows).Select(i => (byte)(best = Math.Max(best, Math.Min(Tiers - 1, (int)Math.Round(i * (double)Tiers / rows.Count))))).ToArray();
    }

    // what the chests can hold, tier by tier (for the Patch Manager / tests)
    public static List<string> LootSummary(RomBuffer rom)
    {
        var names = Items(rom).ToDictionary(i => i.Id, i => $"{i.Name} ({i.Price} GP)");
        names[FirstMarker] = "spell chest";
        var loot = ChestLoot(rom);
        return Enumerable.Range(0, Tiers).Select(t => $"floors {t * 10 + 1}-{t * 10 + 10}: " +
            string.Join(", ", loot.Skip(t * PerTier).Take(PerTier).Distinct().Select(id => names.GetValueOrDefault(id, $"item {id:X2}")))).ToList();
    }
}
