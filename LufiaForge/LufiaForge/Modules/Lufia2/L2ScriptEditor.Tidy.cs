using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>What the game's own events always do and an event made here may forget, done by the editor itself (checked
/// in the game, 2026-11-10, on the user's Jeros' Story scene and against all of the game's events):
/// <list type="bullet">
/// <item>A colour started by 93-96 / 9B is always ended by "End the colour" (97) before the event ends (105 of 105
/// flashes); left on, the flash keeps cycling on the map after the event.</item>
/// <item>The game never makes the leader flicker or vanish (99 / 9D on 00): that frees the leader's place (its tile
/// becomes (0,0)), and when the event ends the leader stands there, the view jumps and the map's characters go. The
/// game's events hide the leader (2E 00) instead; a map character that looks like Maxim flickers away in his place.</item>
/// <item>Shaking is stopped (8A) and rings are removed (9A) the same way.</item>
/// <item>A tile copy (43, 87, B4) made while the screen is off changes the map but doesn't draw it again (the game skips
/// the drawing while $0583 is $80: before a scene's first fade in, after a fade to black, after "screen off"), so the
/// screen keeps the old look until the map scrolls. "Draw the map again" (40) after it draws it (checked in the game on
/// the user's Jeros' Story scene: the copy alone, at the start of the scene or after a fade to black, never showed; with
/// 40 after it, it shows as the screen comes on). The start-up script and the game's own events (list +2) run before
/// the map is drawn and need nothing. The check flags none of the 313 copies in the game's own scenes, talk and step-on
/// events.</item>
/// </list></summary>
public partial class L2ScriptEditor
{
    private static bool StartsColour(int op) => op is 0x93 or 0x94 or 0x95 or 0x96 or 0x9B;
    private static bool IsRing(int op) => op is 0x8D or 0xA0 or 0xC2 or 0xC3 or 0xC8 or 0xC9;
    private static bool CopiesTiles(int op) => op is 0x43 or 0x87 or 0xB4;
    private static bool DrawsMap(int op) => op is 0x40 or 0xC7 or 0xBC;

    /// <summary>For every step: whether the screen is off when it runs, reading the steps top to bottom. A scene whose
    /// first change of the screen is a fade in starts with it off (the party arrives in it); a fade to black, "screen
    /// off" and a map change turn it off, a fade in and "screen on" turn it on. Only for events the player starts (not
    /// the start-up script or the game's own events, which run before the map is drawn).</summary>
    private bool[] ScreenOff()
    {
        var steps = _script!.Steps;
        var off = new bool[steps.Count];
        if (_event?.Entry is not { } e || e.Head == L2EventBlocks.GameList) return off;
        int first = steps.Where(s => !s.IsText && s.Bytes.Length > 0).Select(s => (int)s.Op)
                         .FirstOrDefault(op => op is 0x58 or 0x59 or 0xA4 or 0x3E or 0x3F, -1);
        // only a scene (it plays as the party arrives): talk and step-on events start with the screen on, and the game's
        // use "fade in quickly" there without a fade out first
        bool now = e.Head == L2EventBlocks.SceneList && first is 0x59 or 0xA4 or 0x3F;
        for (int i = 0; i < steps.Count; i++)
        {
            off[i] = now;
            var s = steps[i];
            if (s.IsText || s.Bytes.Length == 0) continue;
            if (s.Op is 0x58 or 0x3E or 0x16 or 0x5E) now = true;
            else if (s.Op is 0x59 or 0xA4 or 0x3F) now = false;
        }
        return off;
    }

    /// <summary>Step <paramref name="i"/> copies map tiles while the screen is off and nothing draws the map after it.</summary>
    private bool CopyNotDrawn(int i, bool[] off)
    {
        var steps = _script!.Steps;
        if (i < 0 || i >= steps.Count || i >= off.Length || !off[i] || steps[i] is not { IsText: false } s || s.Bytes.Length == 0 || !CopiesTiles(s.Op)) return false;
        // the last of copies one after another gets the drawing
        return !(i + 1 < steps.Count && steps[i + 1] is { IsText: false } n && n.Bytes.Length > 0 && (DrawsMap(n.Op) || CopiesTiles(n.Op) && off[i + 1]));
    }

    /// <summary>After every tile copy made while the screen is off: "Draw the map again" (40). Returns how many were
    /// added.</summary>
    private int FitMapDraws()
    {
        if (_script == null) return 0;
        var off = ScreenOff();
        int added = 0;
        for (int i = _script.Steps.Count - 1; i >= 0; i--)
        {
            if (!CopyNotDrawn(i, off)) continue;
            _script.Steps.Insert(i + 1, new L2Step { Bytes = new byte[] { 0x40 } });
            added++;
        }
        return added;
    }

    /// <summary>The colour, shaking and rings still going when step <paramref name="end"/> is reached, reading the steps
    /// top to bottom (a map change starts afresh).</summary>
    private (L2Step? Colour, bool Shake, List<int> Rings) EffectsAt(int end)
    {
        L2Step? colour = null; bool shake = false; var rings = new List<int>();
        for (int i = 0; i < end && i < _script!.Steps.Count; i++)
        {
            var s = _script.Steps[i];
            if (s.IsText || s.Bytes.Length == 0) continue;
            int op = s.Op;
            if (StartsColour(op)) colour = s;
            else if (op is 0x97 or 0xC5) colour = null;
            else if (op == 0x5A) shake = true;
            else if (op is 0x8A or 0x56) shake = false;
            else if (IsRing(op) && s.Bytes.Length > 1) { if (!rings.Contains(s.Bytes[1])) rings.Add(s.Bytes[1]); }
            else if (op is 0x9A or 0x9F or 0xA2 && s.Bytes.Length > 1) rings.Remove(s.Bytes[1]);
            else if (op is 0x16 or 0x5E) { colour = null; shake = false; rings.Clear(); }
        }
        return (colour, shake, rings);
    }

    /// <summary>Before every "End the event" (00) that a colour, shaking or ring is still going at: the steps that end
    /// them (after a wait as long as the colour's change when the colour was started just before). Returns how many
    /// were added.</summary>
    private int FitEffects()
    {
        if (_script == null) return 0;
        int added = 0;
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            var s = _script.Steps[i];
            if (s.IsText || s.Op != 0x00) continue;
            var (colour, shake, rings) = EffectsAt(i);
            var add = new List<L2Step>();
            if (colour != null)
            {
                // the colour started right before: let it play out first
                if (i > 0 && _script.Steps[i - 1] == colour && colour.Bytes.Length >= 4 && colour.Bytes[3] > 0)
                    add.Add(new L2Step { Bytes = new byte[] { 0x37, colour.Bytes[3] } });
                add.Add(new L2Step { Bytes = new byte[] { 0x97 } });
            }
            if (shake) add.Add(new L2Step { Bytes = new byte[] { 0x8A } });
            foreach (int r in rings) add.Add(new L2Step { Bytes = new byte[] { 0x9A, (byte)r } });
            if (add.Count == 0) continue;
            // jumps to the End now go to the first added step, so every way out ends the effects
            foreach (var other in _script.Steps)
                foreach (var j in other.Jumps)
                    if (j.Step == s) j.Step = add[0];
            _script.Steps.InsertRange(i, add);
            i += add.Count;
            added += add.Count;
        }
        return added;
    }

    /// <summary>Step <paramref name="i"/> makes the leader flicker or vanish (99 / 9D on 00).</summary>
    private bool LeaderVanishes(int i) =>
        _script != null && i >= 0 && i < _script.Steps.Count && _script.Steps[i] is { IsText: false } s && s.Op is 0x99 or 0x9D && s.Bytes.Length >= 2 && s.Bytes[1] == 0x00;

    /// <summary>Every step that makes the leader flicker or vanish becomes: a map character that looks like Maxim shown,
    /// put on his tile, facing his way; Maxim hidden (as the game's events do); then the character flickers or
    /// vanishes. One character plays the part for the whole event. Returns how many steps were changed.</summary>
    public int LeaderVanishToStandIn()
    {
        if (_rom == null || _script == null || !CanEdit) return 0;
        int changed = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            int i = Enumerable.Range(0, _script.Steps.Count).FirstOrDefault(LeaderVanishes, -1);
            if (i < 0) break;
            var at = Before(i)?.Actors.GetValueOrDefault(0);
            int x = at is { Placed: true } ? at.X : 0, y = at is { Placed: true } ? at.Y : 0, dir = at?.Dir ?? 1;
            int op = _script.Steps[i].Op;
            // a Maxim look-alike this event already uses plays the part again
            var usedIds = _script.Steps.Where(s => !s.IsText && s.Bytes.Length >= 2).Select(s => (int)s.Bytes[1]).ToHashSet();
            int id = _who.Where(kv => kv.Key >= 0x50 && kv.Value.Sprite == 0 && usedIds.Contains(kv.Key)).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
            if (id < 0)
            {
                id = AddCastMember(0, x, y);
                if (id < 0 || _script == null) return changed;
            }
            Remember();
            // shown first: the game's character commands skip a hidden character and move the last one found instead
            var steps = new[]
            {
                new L2Step { Bytes = new byte[] { 0x2D, (byte)id } },
                new L2Step { Bytes = new byte[] { 0x2F, (byte)id, (byte)x, (byte)y } },
                new L2Step { Bytes = new byte[] { 0x31, (byte)id, (byte)(dir + 1) } },
                new L2Step { Bytes = new byte[] { 0x2E, 0x00 } },
                new L2Step { Bytes = new byte[] { (byte)op, (byte)id } },
            };
            var old = _script.Steps[i];
            foreach (var other in _script.Steps)
                foreach (var j in other.Jumps)
                    if (j.Step == old) j.Step = steps[0];
            _script.Steps.RemoveAt(i);
            _script.Steps.InsertRange(i, steps);
            changed++;
            Changed(true);
        }
        if (changed > 0)
            StageInfo = $"Maxim flickering away is now played by a map character that looks like him ({changed} step{(changed > 1 ? "s" : "")}): " +
                        "it is put on his tile and shown, Maxim is hidden, then it flickers away. The game frees the leader's place when he flickers " +
                        "away himself, and he turns up on another tile when the event ends.";
        return changed;
    }
}
