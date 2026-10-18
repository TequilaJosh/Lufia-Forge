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
    private static readonly (int Slot, int Count)[] GearMix = { (1, 6), (2, 4), (3, 3), (4, 4), (5, 2), (6, 3) };

    /// <summary>
    /// The 10 tiers x 32 chest items: 22 pieces of gear, every kind in each tier (6 weapons, 4 armors, 3 shields, 4 for
    /// the head, 2 shoes, 3 rings), each kind from its own price band for the depth (tier t: the t/10 to (t+2)/10 share
    /// of that kind sorted by price, different pieces spread over it), then 8 healing items (stronger deeper down) and
    /// 2 spell chests (marker F0: the spell is picked when the chest opens, see <see cref="AncientCave"/> spells).
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
        var data = new byte[Tiers * PerTier];
        for (int t = 0; t < Tiers; t++)
        {
            var row = new List<int>();
            foreach (var (slot, count) in GearMix)
            {
                var kind = gear.Where(g => g.Slot == slot).OrderBy(g => g.Price).ThenBy(g => g.Id).ToList();
                if (kind.Count == 0) continue;
                int lo = Math.Min(kind.Count - 1, kind.Count * t / Tiers);
                int hiIx = Math.Min(kind.Count, Math.Max(lo + count, kind.Count * (t + 2) / Tiers));
                lo = Math.Max(0, Math.Min(lo, hiIx - count));
                for (int k = 0; k < count; k++) row.Add(kind[Math.Min(kind.Count - 1, lo + (hiIx - lo) * k / count)].Id);
            }
            row.AddRange(t switch
            {
                <= 2 => new[] { potion, potion, potion, potion, anti, anti, hi, hi },
                <= 6 => new[] { potion, potion, hi, hi, hi, anti, anti, ex },
                _ => new[] { hi, hi, hi, ex, ex, ex, anti, anti },
            });
            while (row.Count < PerTier - 2) row.Add(potion);
            row = row.Take(PerTier - 2).ToList();
            row.Add(spells ? FirstMarker : potion);
            row.Add(spells ? FirstMarker : potion);
            for (int k = 0; k < PerTier; k++) data[t * PerTier + k] = (byte)row[k];
        }
        return data;
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

    /// <summary>Floor 1-100 -> its tier (row <see cref="PerTier"/> x tier of <see cref="ChestLoot"/>).</summary>
    private static byte[] LootTiers() => Enumerable.Range(0, 100).Select(f => (byte)Math.Min(Tiers - 1, f / 10)).ToArray();

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
