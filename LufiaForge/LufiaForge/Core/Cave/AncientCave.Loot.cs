using LufiaForge.Core.Battle;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    public const int Tiers = 10, PerTier = 16;
    public const int Potion = 0x94, HiPotion = 0x95, ExPotion = 0x96, Antidote = 0x99;

    /// <summary>
    /// Chest contents ($7F object +1) for a floor: tier = (floor - 1) / 10, then one of the tier's 16 items at random
    /// (PostHeader, replacing the project's 8 placeholder potions).
    /// </summary>
    private const string LootSource = """
        ; A(8) = (in) nothing -> A(8) = an item for a chest on this floor (X kept)
        ChestItem:
            PHX
            LDA !AC_FLOOR
            DEC A
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            LDA.l LootTier,x        ; tier x 16
            PHA
            LDA.b #16
            JSR RandL
            CLC
            ADC $01,s
            REP #$20
            AND.w #$00FF
            TAX
            SEP #$20
            PLA
            LDA.l ChestLoot,x
            PLX
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

    /// <summary>
    /// The 10 tiers x 16 chest items: 10 pieces of equipment chosen by price (tier t takes the pieces from the
    /// (t-1)/10 to (t+1)/10 share of all equipment sorted by price, spread evenly, so deeper floors give better
    /// gear) and 6 reusable healing items (potions and antidotes, stronger potions deeper down).
    /// </summary>
    public static byte[] ChestLoot(RomBuffer rom)
    {
        var items = Items(rom);
        var gear = items.Where(i => i.Type is 0x02 or 0x06 && i.Slot is >= 1 and <= 6 && (i.Equip & 0xF0) != 0 && i.Price > 0)
                        .OrderBy(i => i.Price).ThenBy(i => i.Id).ToList();
        bool usable(int id) => items.Any(i => i.Id == id && (i.Type & 0x41) != 0);
        int Pick(int want, int fallback) => usable(want) ? want : fallback;
        int potion = Pick(Potion, items.FirstOrDefault(i => (i.Type & 0x41) != 0)?.Id ?? 1);
        int hi = Pick(HiPotion, potion), ex = Pick(ExPotion, hi), anti = Pick(Antidote, potion);
        var data = new byte[Tiers * PerTier];
        for (int t = 0; t < Tiers; t++)
        {
            var row = new List<int>();
            if (gear.Count > 0)
            {
                int lo = gear.Count * Math.Max(0, t - 1) / Tiers, top = Math.Max(lo + 1, (gear.Count * (t + 1) + Tiers - 1) / Tiers);
                for (int k = 0; k < 10; k++) row.Add(gear[Math.Min(gear.Count - 1, lo + (top - lo) * k / 10)].Id);
            }
            row.AddRange(t switch
            {
                <= 2 => new[] { potion, potion, potion, anti, anti, hi },
                <= 6 => new[] { potion, hi, hi, hi, anti, ex },
                _ => new[] { hi, hi, ex, ex, ex, anti },
            });
            while (row.Count < PerTier) row.Add(potion);
            for (int k = 0; k < PerTier; k++) data[t * PerTier + k] = (byte)row[k];
        }
        return data;
    }

    /// <summary>Floor 1-100 -> its tier x 16 (the start of its row in <see cref="ChestLoot"/>).</summary>
    private static byte[] LootTiers() => Enumerable.Range(0, 100).Select(f => (byte)(Math.Min(Tiers - 1, f / 10) * PerTier)).ToArray();

    /// <summary>What the chests can hold, tier by tier (for the Patch Manager / tests).</summary>
    public static List<string> LootSummary(RomBuffer rom)
    {
        var names = Items(rom).ToDictionary(i => i.Id, i => $"{i.Name} ({i.Price} GP)");
        var loot = ChestLoot(rom);
        return Enumerable.Range(0, Tiers).Select(t => $"floors {t * 10 + 1}-{t * 10 + 10}: " +
            string.Join(", ", loot.Skip(t * PerTier).Take(PerTier).Distinct().Select(id => names.GetValueOrDefault(id, $"item {id:X2}")))).ToList();
    }
}
