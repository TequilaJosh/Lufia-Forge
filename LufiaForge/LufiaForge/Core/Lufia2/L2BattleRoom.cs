namespace LufiaForge.Core.Lufia2;

/// <summary>
/// How much of the battle sprite buffer (64 squares of 16x16, sprite tiles 0x100-0x1FF) the kinds of monster of one
/// battle need (read from the game's code 2026-10-27; docs/lufia2/graphics.md section 8.4).
/// <list type="bullet">
/// <item>A monster group's battle ($81:839F): the monster in place 1 or 2 (picked at random when both are filled) is the
/// first kind; the other places are shuffled and the first 2 filled ones are the other kinds (so any 2 of them). Monsters
/// are then dealt to the kinds ($81:84FC) while their widths fit: each adds its width in 8x8 tiles + 1 and the sum must
/// stay under 25, so a kind that can't fit beside the others never comes with them. Place 1 empty with place 2 filled is
/// a fixed battle (not counted here).</item>
/// <item>A story battle: every different monster of its six is a kind; $85:DF44 keeps 3 kinds and runs into a BRK at
/// $85:DF73 on a 4th (checked: the game drops back to its title screen).</item>
/// <item>$85:EB91 loads each kind's sprite into the buffer one after the other; a sprite another kind already loaded is
/// used again, so kinds that share a sprite count once. Past 64 squares the tiles run off the sprite tiles the battle
/// has.</item>
/// </list>
/// </summary>
public static class L2BattleRoom
{
    public const int Squares = L2MonsterSprites.MaxSquares, MaxKinds = 3, MaxWidth = 24;

    /// <summary>What a battle's kinds need: the most squares one battle can take, the monsters of that battle, how many
    /// kinds it can have; <see cref="Fixed"/> for a fixed group (not counted).</summary>
    public sealed record Need(int Squares, IReadOnlyList<int> Monsters, int Kinds, bool Fixed = false)
    {
        public bool TooBig => Squares > L2BattleRoom.Squares;
        public bool TooManyKinds => Kinds > MaxKinds;
    }

    /// <summary>A monster's sprite, its squares and its width in tiles (for the sums), with an optional sprite to try
    /// instead for one monster.</summary>
    public sealed class Sizes
    {
        private readonly RomBuffer _rom;
        private readonly List<L2Battle.Monster> _monsters;
        private readonly Dictionary<int, (int Squares, int Across)> _sprites = new();
        public int TryMonster { get; set; } = -1;
        public int TrySprite { get; set; }

        public Sizes(RomBuffer rom) { _rom = rom; _monsters = L2Battle.Monsters(rom); }

        public int SpriteOf(int monster) => monster == TryMonster ? TrySprite : monster < _monsters.Count ? _monsters[monster].Sprite : 0;

        public (int Squares, int Across) Sprite(int sprite)
        {
            if (_sprites.TryGetValue(sprite, out var v)) return v;
            if (sprite < 1 || sprite > L2MonsterSprites.Count(_rom)) v = (0, 0);
            else
            {
                var (tx, ty, _) = L2MonsterSprites.Size(_rom, sprite);
                var (sx, sy) = L2MonsterSprites.Squares(tx, ty);
                v = (sx * sy, tx);
            }
            return _sprites[sprite] = v;
        }

        /// <summary>Squares the monsters' sprites take together (a shared sprite once).</summary>
        public int SquaresOf(IEnumerable<int> monsters) => monsters.Select(SpriteOf).Distinct().Sum(s => Sprite(s).Squares);

        public bool FitsAcross(IEnumerable<int> monsters) => monsters.Sum(m => Sprite(SpriteOf(m)).Across + 1) <= MaxWidth;
    }

    /// <summary>The most squares a battle of group <paramref name="g"/> can need.</summary>
    public static Need Group(RomBuffer rom, int g, Sizes? sizes = null) => Group(L2Encounters.Group(rom, g), sizes ?? new Sizes(rom));

    public static Need Group(int[] e, Sizes sizes)
    {
        if (L2Encounters.IsFixed(e)) return new Need(sizes.SquaresOf(new[] { e[1] }), new[] { e[1] }, 1, Fixed: true);
        var filled = Enumerable.Range(0, e.Length).Where(i => e[i] != 0xFF).ToList();
        var firsts = e[0] != 0xFF && e[1] != 0xFF ? new List<int> { 0, 1 } : e[0] != 0xFF ? new List<int> { 0 } : new List<int>();
        var combos = new List<List<int>>();
        if (firsts.Count == 0)
            combos.AddRange(Choose(filled, Math.Min(MaxKinds, filled.Count)));
        else
            foreach (int f in firsts)
            {
                var rest = filled.Where(i => i != f).ToList();
                foreach (var c in Choose(rest, Math.Min(MaxKinds - 1, rest.Count))) combos.Add(new List<int> { f }.Concat(c).ToList());
            }
        Need best = new(0, Array.Empty<int>(), 0);
        foreach (var c in combos)
        {
            var ms = c.Select(i => e[i]).ToList();
            // the kinds that can come together: the first always (when it fits alone), the others while the widths fit
            foreach (var sub in Subsets(ms, firsts.Count > 0))
            {
                if (!sizes.FitsAcross(sub)) continue;
                int sq = sizes.SquaresOf(sub);
                if (sq > best.Squares) best = new Need(sq, sub, sub.Distinct().Count());
            }
        }
        return best;
    }

    /// <summary>The squares a story battle's kinds need (every different monster of its six is a kind).</summary>
    public static Need Story(RomBuffer rom, int b, Sizes? sizes = null)
    {
        sizes ??= new Sizes(rom);
        var kinds = L2Encounters.StoryBattle(rom, b).Select(s => s.Monster).Where(m => m != 0xFF).Distinct().ToList();
        return new Need(sizes.SquaresOf(kinds), kinds, kinds.Count);
    }

    /// <summary>The groups and story battles that have monster <paramref name="monster"/> and would need more than 64
    /// squares if it showed sprite <paramref name="sprite"/>.</summary>
    public static List<(string What, Need Need)> Overflows(RomBuffer rom, int monster, int sprite)
    {
        var sizes = new Sizes(rom) { TryMonster = monster, TrySprite = sprite };
        var r = new List<(string, Need)>();
        for (int g = 0; g < L2Encounters.GroupCount; g++)
        {
            var e = L2Encounters.Group(rom, g);
            if (!e.Contains(monster)) continue;
            var n = Group(e, sizes);
            if (n.TooBig && n.Monsters.Contains(monster)) r.Add(($"group {g:X2}", n));
        }
        var seen = new HashSet<int>();
        for (int b = 0; b < L2Encounters.StoryBattleCount; b++)
        {
            if (!seen.Add(L2Encounters.StoryBattleAt(rom, b))) continue;   // battles sharing a record: the first
            var n = Story(rom, b, sizes);
            if (n.Monsters.Contains(monster) && n.TooBig) r.Add(($"story battle {b:X2}", n));
        }
        return r;
    }

    /// <summary>The most squares any battle with monster <paramref name="monster"/> needs (with its sprite as
    /// <paramref name="sprite"/>), or 0 when no group or story battle has it.</summary>
    public static int MostWith(RomBuffer rom, int monster, int sprite)
    {
        var sizes = new Sizes(rom) { TryMonster = monster, TrySprite = sprite };
        int most = 0;
        for (int g = 0; g < L2Encounters.GroupCount; g++)
        {
            var e = L2Encounters.Group(rom, g);
            if (e.Contains(monster)) most = Math.Max(most, Group(e, sizes).Squares);
        }
        for (int b = 0; b < L2Encounters.StoryBattleCount; b++)
        {
            var n = Story(rom, b, sizes);
            if (n.Monsters.Contains(monster)) most = Math.Max(most, n.Squares);
        }
        return most;
    }

    /// <summary>A need in words: "up to 70 squares with 1A Goblin, 2B Bat" (and the kinds past 3).</summary>
    public static string Describe(Need n, IReadOnlyList<L2Battle.Monster>? monsters = null)
    {
        string Name(int m) => monsters != null && m < monsters.Count ? $"{m:X2} {monsters[m].Name.Trim()}" : m.ToString("X2");
        string s = $"up to {n.Squares} of {Squares} squares";
        if (n.Monsters.Count > 0) s += " with " + string.Join(", ", n.Monsters.Distinct().Select(Name));
        if (n.TooManyKinds) s += $"; {n.Kinds} kinds, and the game drops back to its title screen on a 4th";
        return s;
    }

    private static IEnumerable<List<int>> Choose(List<int> items, int k)
    {
        if (k == 0) { yield return new List<int>(); yield break; }
        for (int i = 0; i <= items.Count - k; i++)
            foreach (var rest in Choose(items.Skip(i + 1).ToList(), k - 1))
                yield return new List<int> { items[i] }.Concat(rest).ToList();
    }

    /// <summary>Non-empty subsets (keeping the first item when <paramref name="keepFirst"/>).</summary>
    private static IEnumerable<List<int>> Subsets(List<int> items, bool keepFirst)
    {
        for (int mask = 1; mask < 1 << items.Count; mask++)
        {
            if (keepFirst && (mask & 1) == 0) continue;
            yield return Enumerable.Range(0, items.Count).Where(i => (mask >> i & 1) != 0).Select(i => items[i]).ToList();
        }
    }
}
