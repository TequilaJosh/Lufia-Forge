namespace LufiaForge.Core.Maps;

// the ending's staff roll. there's no separate credits table: each card is narration text in a centred window
// (command 6B 20 rr, then the text) inside the ending's scene events, and every one of those events closes its
// window with 50 14. the US game has 14 cards in 9 events across 8 maps (2D/12 "< STAFF >" ... 2D/13 "THE END").
// the scenes run one after another through warps and map setup scripts, so the order is the game's own
// (GameOrder), cards added to other events come after
public static class CreditsRoll
{
    public sealed record Card(int Map, int Event, int Op, string Text, int Row)
    {
        public string Title => Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(empty)";
    }

    // (map, event) of the scenes in the order the ending shows them
    private static readonly (int Map, int Event)[] GameOrder =
    {
        (0x2D, 12), (0x04, 38), (0x0C, 26), (0x0E, 15), (0x28, 22), (0x31, 14), (0x3C, 21), (0x45, 20), (0x2D, 13),
    };

    // the cards found in the rom, in the ending's order
    public static List<Card> Find(RomBuffer rom)
    {
        var cards = new List<Card>();
        foreach (var mapInfo in MapCatalog.Scan(rom))
            foreach (int eventId in EventScript.PlausibleEvents(rom, mapInfo.MapId))
            {
                var script = EventScript.Load(rom, mapInfo.MapId, eventId);
                if (script == null || !script.Ops.Any(o => !o.IsText && o.Bytes is [0x50, 0x14, ..])) continue;
                for (int k = 1; k < script.Ops.Count; k++)
                    if (script.Ops[k].IsText && !script.Ops[k - 1].IsText && script.Ops[k - 1].Bytes is [0x6B, 0x20, var row, ..])
                        cards.Add(new Card(mapInfo.MapId, eventId, k, script.Ops[k].Text, row));
            }
        int Rank(Card card)
        {
            int i = Array.IndexOf(GameOrder, (card.Map, card.Event));
            return i < 0 ? 1000 + card.Map : i;
        }
        return cards.OrderBy(Rank).ThenBy(c => c.Op).ToList();
    }

    // centres every line under the longest one with leading spaces (the game centres the whole window on screen,
    // and it's as wide as its longest line)
    public static string Centre(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        int longest = lines.Max(l => l.Length);
        return string.Join("\n", lines.Select(l => l.Length == 0 ? "" : new string(' ', (longest - l.Length) / 2) + l));
    }

    // widest line the screen can show (256 pixels, 8 per character)
    public const int MaxLineLength = 32;

    // the card the way the game draws it: white letters with a dark outline on black, 256 x 224 (BGRA)
    public static uint[] Render(RomBuffer rom, string text, int row)
    {
        const int ScreenWidth = 256, ScreenHeight = 224;
        var pixels = new uint[ScreenWidth * ScreenHeight];
        Array.Fill(pixels, 0xFF000000u);
        string printed = CutsceneText.Printed(text.Replace("\r", ""));
        foreach (var (line, x0, y0) in CutsceneText.Layout(printed, printed, 0x20, row))
        {
            int lineWidth = line.Length * 8;
            if (lineWidth == 0) continue;
            var lit = new bool[16, lineWidth];
            for (int k = 0; k < line.Length; k++)
            {
                var glyph = CutsceneText.Glyph(rom, line[k]);
                for (int r = 0; r < 16; r++)
                    for (int bit = 0; bit < 8; bit++) lit[r, k * 8 + bit] = ((glyph[r] >> (7 - bit)) & 1) != 0;
            }
            bool IsLit(int r, int x) => r >= 0 && r < 16 && x >= 0 && x < lineWidth && lit[r, x];
            for (int r = 0; r < 16; r++)
                for (int x = 0; x < lineWidth; x++)
                {
                    int screenX = x0 + x, screenY = y0 - 1 + r;
                    if (screenX < 0 || screenX >= ScreenWidth || screenY < 0 || screenY >= ScreenHeight) continue;
                    if (lit[r, x]) pixels[screenY * ScreenWidth + screenX] = 0xFFF8F8F8u;
                    else if (IsLit(r, x - 1) || IsLit(r, x + 1) || IsLit(r - 1, x - 1) || IsLit(r - 1, x) || IsLit(r - 1, x + 1) || IsLit(r + 1, x) || IsLit(r + 1, x - 1))
                        pixels[screenY * ScreenWidth + screenX] = 0xFF404858u;
                }
        }
        return pixels;
    }
}
