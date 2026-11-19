namespace LufiaForge.Core.Lufia2;

// how much of the battle sprite buffer (64 squares of 16x16, sprite tiles 0x100-0x1FF) the kinds of monster in one
// battle need (read from the game's code 2026-10-27, docs/lufia2/graphics.md section 8.4)
// - a monster group's battle ($81:839F): the monster in place 1 or 2 (random pick when both are filled) is the first
//   kind. the other places get shuffled and the first 2 filled ones are the other kinds (so any 2 of them). then
//   monsters get dealt to the kinds ($81:84FC) while their widths fit: each adds its width in 8x8 tiles + 1 and the
//   sum has to stay under 25, so a kind that can't fit beside the others never comes with them. place 1 empty with
//   place 2 filled is a fixed battle (not counted here)
// - a story battle: every different monster of its six is a kind. $85:DF44 keeps 3 kinds and runs into a BRK at
//   $85:DF73 on a 4th (checked: the game drops back to its title screen)
// - $85:EB91 loads each kind's sprite into the buffer one after the other. a sprite another kind already loaded gets
//   used again, so kinds sharing a sprite count once. past 64 squares the tiles run off the sprite tiles the battle has
public static class L2BattleRoom
{
    public const int Squares = L2MonsterSprites.MaxSquares, MaxKinds = 3, MaxWidth = 24;

    // what a battle's kinds need: the most squares one battle can take, the monsters of that battle, how many kinds
    // it can have. Fixed for a fixed group (not counted)
    public sealed record Need(int Squares, IReadOnlyList<int> Monsters, int Kinds, bool Fixed = false)
    {
        public bool TooBig => Squares > L2BattleRoom.Squares;
        public bool TooManyKinds => Kinds > MaxKinds;
    }

    // a monster's sprite, its squares and its width in tiles (for the sums), with an optional sprite to try instead
    // for one monster
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
            if (_sprites.TryGetValue(sprite, out var size)) return size;
            if (sprite < 1 || sprite > L2MonsterSprites.Count(_rom)) size = (0, 0);
            else
            {
                var (tilesAcross, tilesDown, _) = L2MonsterSprites.Size(_rom, sprite);
                var (squaresAcross, squaresDown) = L2MonsterSprites.Squares(tilesAcross, tilesDown);
                size = (squaresAcross * squaresDown, tilesAcross);
            }
            return _sprites[sprite] = size;
        }

        // squares the monsters' sprites take together (a shared sprite once)
        public int SquaresOf(IEnumerable<int> monsters) => monsters.Select(SpriteOf).Distinct().Sum(s => Sprite(s).Squares);

        public bool FitsAcross(IEnumerable<int> monsters) => monsters.Sum(m => Sprite(SpriteOf(m)).Across + 1) <= MaxWidth;
    }

    // the most squares a battle of group group can need
    public static Need Group(RomBuffer rom, int group, Sizes? sizes = null) => Group(L2Encounters.Group(rom, group), sizes ?? new Sizes(rom));

    public static Need Group(int[] places, Sizes sizes)
    {
        if (L2Encounters.IsFixed(places)) return new Need(sizes.SquaresOf(new[] { places[1] }), new[] { places[1] }, 1, Fixed: true);
        var filled = Enumerable.Range(0, places.Length).Where(i => places[i] != 0xFF).ToList();
        var firsts = places[0] != 0xFF && places[1] != 0xFF ? new List<int> { 0, 1 } : places[0] != 0xFF ? new List<int> { 0 } : new List<int>();
        var combos = new List<List<int>>();
        if (firsts.Count == 0)
            combos.AddRange(Choose(filled, Math.Min(MaxKinds, filled.Count)));
        else
            foreach (int first in firsts)
            {
                var rest = filled.Where(i => i != first).ToList();
                foreach (var combo in Choose(rest, Math.Min(MaxKinds - 1, rest.Count))) combos.Add(new List<int> { first }.Concat(combo).ToList());
            }
        Need best = new(0, Array.Empty<int>(), 0);
        foreach (var combo in combos)
        {
            var monsters = combo.Select(i => places[i]).ToList();
            // the kinds that can come together: the first always (when it fits alone), the others while the widths fit
            foreach (var subset in Subsets(monsters, firsts.Count > 0))
            {
                if (!sizes.FitsAcross(subset)) continue;
                int squares = sizes.SquaresOf(subset);
                if (squares > best.Squares) best = new Need(squares, subset, subset.Distinct().Count());
            }
        }
        return best;
    }

    // the squares a story battle's kinds need (every different monster of its six is a kind)
    public static Need Story(RomBuffer rom, int battle, Sizes? sizes = null)
    {
        sizes ??= new Sizes(rom);
        var kinds = L2Encounters.StoryBattle(rom, battle).Select(s => s.Monster).Where(m => m != 0xFF).Distinct().ToList();
        return new Need(sizes.SquaresOf(kinds), kinds, kinds.Count);
    }

    // the groups and story battles that have monster monster and would need more than 64 squares if it showed
    // sprite sprite
    public static List<(string What, Need Need)> Overflows(RomBuffer rom, int monster, int sprite)
    {
        var sizes = new Sizes(rom) { TryMonster = monster, TrySprite = sprite };
        var overflows = new List<(string, Need)>();
        for (int group = 0; group < L2Encounters.GroupCount; group++)
        {
            var places = L2Encounters.Group(rom, group);
            if (!places.Contains(monster)) continue;
            var need = Group(places, sizes);
            if (need.TooBig && need.Monsters.Contains(monster)) overflows.Add(($"group {group:X2}", need));
        }
        var seen = new HashSet<int>();
        for (int battle = 0; battle < L2Encounters.StoryBattleCount(rom); battle++)
        {
            if (!seen.Add(L2Encounters.StoryBattleAt(rom, battle))) continue;   // battles sharing a record: only the first
            var need = Story(rom, battle, sizes);
            if (need.Monsters.Contains(monster) && need.TooBig) overflows.Add(($"story battle {battle:X2}", need));
        }
        return overflows;
    }

    // the most squares any battle with monster monster needs (with its sprite as sprite), 0 if no group or story
    // battle has it
    public static int MostWith(RomBuffer rom, int monster, int sprite)
    {
        var sizes = new Sizes(rom) { TryMonster = monster, TrySprite = sprite };
        int most = 0;
        for (int group = 0; group < L2Encounters.GroupCount; group++)
        {
            var places = L2Encounters.Group(rom, group);
            if (places.Contains(monster)) most = Math.Max(most, Group(places, sizes).Squares);
        }
        for (int battle = 0; battle < L2Encounters.StoryBattleCount(rom); battle++)
        {
            var need = Story(rom, battle, sizes);
            if (need.Monsters.Contains(monster)) most = Math.Max(most, need.Squares);
        }
        return most;
    }

    // a need in words: "up to 70 squares with 1A Goblin, 2B Bat" (and the kinds past 3)
    public static string Describe(Need need, IReadOnlyList<L2Battle.Monster>? monsters = null)
    {
        string Name(int m) => monsters != null && m < monsters.Count ? $"{m:X2} {monsters[m].Name.Trim()}" : m.ToString("X2");
        string text = $"up to {need.Squares} of {Squares} squares";
        if (need.Monsters.Count > 0) text += " with " + string.Join(", ", need.Monsters.Distinct().Select(Name));
        if (need.TooManyKinds) text += $"; {need.Kinds} kinds, and the game drops back to its title screen on a 4th";
        return text;
    }

    private static IEnumerable<List<int>> Choose(List<int> items, int k)
    {
        if (k == 0) { yield return new List<int>(); yield break; }
        for (int i = 0; i <= items.Count - k; i++)
            foreach (var rest in Choose(items.Skip(i + 1).ToList(), k - 1))
                yield return new List<int> { items[i] }.Concat(rest).ToList();
    }

    // non-empty subsets (keeping the first item if keepFirst)
    private static IEnumerable<List<int>> Subsets(List<int> items, bool keepFirst)
    {
        for (int mask = 1; mask < 1 << items.Count; mask++)
        {
            if (keepFirst && (mask & 1) == 0) continue;
            yield return Enumerable.Range(0, items.Count).Where(i => (mask >> i & 1) != 0).Select(i => items[i]).ToList();
        }
    }
}
