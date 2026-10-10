namespace LufiaForge.Core.Maps;

/// <summary>
/// The ending's staff roll. There is no separate credits table: each card is narration text in a centred
/// window (command 6B 20 rr, then the text) inside the ending's scene events, and every such event closes its
/// window with 50 14. The US game has 14 cards in 9 events across 8 maps (2D/12 "&lt; STAFF &gt;" ... 2D/13 "THE END").
/// The scenes run one after another through warps and map setup scripts, so the order is the game's own
/// (<see cref="GameOrder"/>); cards added to other events come after.
/// </summary>
public static class CreditsRoll
{
    public sealed record Card(int Map, int Event, int Op, string Text, int Row)
    {
        public string Title => Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(empty)";
    }

    /// <summary>(map, event) of the scenes in the order the ending shows them.</summary>
    private static readonly (int Map, int Event)[] GameOrder =
    {
        (0x2D, 12), (0x04, 38), (0x0C, 26), (0x0E, 15), (0x28, 22), (0x31, 14), (0x3C, 21), (0x45, 20), (0x2D, 13),
    };

    /// <summary>The cards found in the ROM, in the ending's order.</summary>
    public static List<Card> Find(RomBuffer rom)
    {
        var cards = new List<Card>();
        foreach (var m in MapCatalog.Scan(rom))
            foreach (int e in EventScript.PlausibleEvents(rom, m.MapId))
            {
                var s = EventScript.Load(rom, m.MapId, e);
                if (s == null || !s.Ops.Any(o => !o.IsText && o.Bytes is [0x50, 0x14, ..])) continue;
                for (int k = 1; k < s.Ops.Count; k++)
                    if (s.Ops[k].IsText && !s.Ops[k - 1].IsText && s.Ops[k - 1].Bytes is [0x6B, 0x20, var row, ..])
                        cards.Add(new Card(m.MapId, e, k, s.Ops[k].Text, row));
            }
        int Rank(Card c)
        {
            int i = Array.IndexOf(GameOrder, (c.Map, c.Event));
            return i < 0 ? 1000 + c.Map : i;
        }
        return cards.OrderBy(Rank).ThenBy(c => c.Op).ToList();
    }

    /// <summary>
    /// Centres every line under the longest one with leading spaces (the game centres the whole window on screen,
    /// as wide as its longest line).
    /// </summary>
    public static string Centre(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        int max = lines.Max(l => l.Length);
        return string.Join("\n", lines.Select(l => l.Length == 0 ? "" : new string(' ', (max - l.Length) / 2) + l));
    }

    /// <summary>Widest line the screen can show (256 pixels, 8 per character).</summary>
    public const int MaxLineLength = 32;

    /// <summary>The card as the game draws it: white letters with a dark outline on black, 256 x 224 (BGRA).</summary>
    public static uint[] Render(RomBuffer rom, string text, int row)
    {
        const int W = 256, H = 224;
        var px = new uint[W * H];
        Array.Fill(px, 0xFF000000u);
        string printed = CutsceneText.Printed(text.Replace("\r", ""));
        foreach (var (line, x0, y0) in CutsceneText.Layout(printed, printed, 0x20, row))
        {
            int w = line.Length * 8;
            if (w == 0) continue;
            var on = new bool[16, w];
            for (int k = 0; k < line.Length; k++)
            {
                var g = CutsceneText.Glyph(rom, line[k]);
                for (int r = 0; r < 16; r++)
                    for (int b = 0; b < 8; b++) on[r, k * 8 + b] = ((g[r] >> (7 - b)) & 1) != 0;
            }
            bool P(int r, int x) => r >= 0 && r < 16 && x >= 0 && x < w && on[r, x];
            for (int r = 0; r < 16; r++)
                for (int x = 0; x < w; x++)
                {
                    int sx = x0 + x, sy = y0 - 1 + r;
                    if (sx < 0 || sx >= W || sy < 0 || sy >= H) continue;
                    if (on[r, x]) px[sy * W + sx] = 0xFFF8F8F8u;
                    else if (P(r, x - 1) || P(r, x + 1) || P(r - 1, x - 1) || P(r - 1, x) || P(r - 1, x + 1) || P(r + 1, x) || P(r + 1, x - 1))
                        px[sy * W + sx] = 0xFF404858u;
                }
        }
        return px;
    }
}
