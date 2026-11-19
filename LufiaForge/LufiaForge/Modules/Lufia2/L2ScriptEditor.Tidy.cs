using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// what the game's own events always do and an event made here might forget, so the editor does it itself (checked
// in the game, 2026-11-10, on the user's Jeros' Story scene and against all of the game's events):
// - a colour started by 93-96 / 9B always gets ended by "End the colour" (97) before the event ends (105 of 105
//   flashes). left on, the flash keeps cycling on the map after the event
// - the game never makes the leader flicker or vanish (99 / 9D on 00). that frees the leader's place (its tile
//   becomes (0,0)), and when the event ends the leader stands there, the view jumps and the map's characters go. the
//   game's events hide the leader (2E 00) instead, and a map character that looks like Maxim flickers away in his place
// - shaking gets stopped (8A) and rings removed (9A) the same way
// - a tile copy (43, 87, B4) made while the screen is off changes the map but doesn't draw it again (the game skips
//   the drawing while $0583 is $80: before a scene's first fade in, after a fade to black, after "screen off"), so the
//   screen keeps the old look until the map scrolls. "Draw the map again" (40) after it draws it (checked in the game on
//   the user's Jeros' Story scene: the copy alone, at the start of the scene or after a fade to black, never showed, with
//   40 after it it shows as the screen comes on). the start-up script and the game's own events (list +2) run before
//   the map is drawn and need nothing. the check flags none of the 313 copies in the game's own scenes, talk and step-on
//   events
public partial class L2ScriptEditor
{
    private static bool StartsColour(int op) => op is 0x93 or 0x94 or 0x95 or 0x96 or 0x9B;
    private static bool IsRing(int op) => op is 0x8D or 0xA0 or 0xC2 or 0xC3 or 0xC8 or 0xC9;
    private static bool CopiesTiles(int op) => op is 0x43 or 0x87 or 0xB4;
    private static bool DrawsMap(int op) => op is 0x40 or 0xC7 or 0xBC;

    // for every step, whether the screen is off when it runs, reading the steps top to bottom. a scene whose
    // first change of the screen is a fade in starts with it off (the party arrives in it). a fade to black, "screen
    // off" and a map change turn it off, a fade in and "screen on" turn it on. only for events the player starts (not
    // the start-up script or the game's own events, which run before the map is drawn)
    private bool[] ScreenOff()
    {
        var steps = _script!.Steps;
        var off = new bool[steps.Count];
        if (_event?.Entry is not { } entry || entry.Head == L2EventBlocks.GameList) return off;
        int first = steps.Where(s => !s.IsText && s.Bytes.Length > 0).Select(s => (int)s.Op)
                         .FirstOrDefault(op => op is 0x58 or 0x59 or 0xA4 or 0x3E or 0x3F, -1);
        // only a scene (it plays as the party arrives). talk and step-on events start with the screen on, and the game's
        // use "fade in quickly" there without a fade out first
        bool now = entry.Head == L2EventBlocks.SceneList && first is 0x59 or 0xA4 or 0x3F;
        for (int i = 0; i < steps.Count; i++)
        {
            off[i] = now;
            var step = steps[i];
            if (step.IsText || step.Bytes.Length == 0) continue;
            if (step.Op is 0x58 or 0x3E or 0x16 or 0x5E) now = true;
            else if (step.Op is 0x59 or 0xA4 or 0x3F) now = false;
        }
        return off;
    }

    // true if step index copies map tiles while the screen is off and nothing draws the map after it
    private bool CopyNotDrawn(int index, bool[] off)
    {
        var steps = _script!.Steps;
        if (index < 0 || index >= steps.Count || index >= off.Length || !off[index] || steps[index] is not { IsText: false } step || step.Bytes.Length == 0 || !CopiesTiles(step.Op)) return false;
        // the last of copies one after another gets the drawing
        return !(index + 1 < steps.Count && steps[index + 1] is { IsText: false } nextStep && nextStep.Bytes.Length > 0 && (DrawsMap(nextStep.Op) || CopiesTiles(nextStep.Op) && off[index + 1]));
    }

    // puts "Draw the map again" (40) after every tile copy made while the screen is off. returns how many were added
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

    // the colour, shaking and rings still going when step end is reached, reading the steps
    // top to bottom (a map change starts afresh)
    private (L2Step? Colour, bool Shake, List<int> Rings) EffectsAt(int end)
    {
        L2Step? colour = null; bool shake = false; var rings = new List<int>();
        for (int i = 0; i < end && i < _script!.Steps.Count; i++)
        {
            var step = _script.Steps[i];
            if (step.IsText || step.Bytes.Length == 0) continue;
            int op = step.Op;
            if (StartsColour(op)) colour = step;
            else if (op is 0x97 or 0xC5) colour = null;
            else if (op == 0x5A) shake = true;
            else if (op is 0x8A or 0x56) shake = false;
            else if (IsRing(op) && step.Bytes.Length > 1) { if (!rings.Contains(step.Bytes[1])) rings.Add(step.Bytes[1]); }
            else if (op is 0x9A or 0x9F or 0xA2 && step.Bytes.Length > 1) rings.Remove(step.Bytes[1]);
            else if (op is 0x16 or 0x5E) { colour = null; shake = false; rings.Clear(); }
        }
        return (colour, shake, rings);
    }

    // before every "End the event" (00) that a colour, shaking or ring is still going at, adds the steps that end
    // them (after a wait as long as the colour's change if the colour was started just before). returns how many
    // were added
    private int FitEffects()
    {
        if (_script == null) return 0;
        int added = 0;
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            var step = _script.Steps[i];
            if (step.IsText || step.Op != 0x00) continue;
            var (colour, shake, rings) = EffectsAt(i);
            var add = new List<L2Step>();
            if (colour != null)
            {
                // the colour started right before, let it play out first
                if (i > 0 && _script.Steps[i - 1] == colour && colour.Bytes.Length >= 4 && colour.Bytes[3] > 0)
                    add.Add(new L2Step { Bytes = new byte[] { 0x37, colour.Bytes[3] } });
                add.Add(new L2Step { Bytes = new byte[] { 0x97 } });
            }
            if (shake) add.Add(new L2Step { Bytes = new byte[] { 0x8A } });
            foreach (int ring in rings) add.Add(new L2Step { Bytes = new byte[] { 0x9A, (byte)ring } });
            if (add.Count == 0) continue;
            // jumps to the End now go to the first added step, so every way out ends the effects
            foreach (var other in _script.Steps)
                foreach (var j in other.Jumps)
                    if (j.Step == step) j.Step = add[0];
            _script.Steps.InsertRange(i, add);
            i += add.Count;
            added += add.Count;
        }
        return added;
    }

    // a ring and what the game's warps always do with it ($80:AA20, Shrine of Daos, Gades at the Shrine, every
    // "appears in a flash"): it keeps turning (8F 00 41), closes in by 2 a frame to 8 pixels and goes (9F 84 E1), and the
    // event waits until it's gone (A2)
    public static L2Step[] RingSteps(byte[] start)
    {
        byte ring = start[1];
        return new[]
        {
            new L2Step { Bytes = (byte[])start.Clone() },
            new L2Step { Bytes = new byte[] { 0x8F, ring, 0x00, 0x41 } },
            new L2Step { Bytes = new byte[] { 0x9F, ring, 0x84, 0xE1 } },
            new L2Step { Bytes = new byte[] { 0xA2, ring } },
        };
    }

    // a ring number no ring of this event uses
    private int FreeRing()
    {
        var used = _script?.Steps.Where(s => !s.IsText && s.Bytes.Length >= 2 && IsRing(s.Op)).Select(s => (int)s.Bytes[1]).ToHashSet() ?? new HashSet<int>();
        return Enumerable.Range(0, 8).FirstOrDefault(r => !used.Contains(r), 0);
    }

    private bool StepOnRing(int index, int ring, params int[] ops) =>
        _script!.Steps[index] is { IsText: false } step && step.Bytes.Length >= 2 && step.Bytes[1] == ring && ops.Contains(step.Op);

    // the step that started the ring the step at index works on (the last start of that ring
    // above it), or -1
    private int RingStart(int index, int ring)
    {
        for (int j = index - 1; j >= 0; j--)
            if (_script!.Steps[j] is { IsText: false } step && step.Bytes.Length >= 2 && IsRing(step.Op) && step.Bytes[1] == ring) return j;
        return -1;
    }

    // between steps from and to (not included): a change that makes the ring turn (8F / 90 with a step),
    // and one that takes it away (9F that stops, or 9A)
    private (bool Turns, bool Goes) RingMoves(int from, int to, int ring)
    {
        bool turns = false, goes = false;
        for (int j = from + 1; j < to && j < _script!.Steps.Count; j++)
        {
            var step = _script.Steps[j];
            if (step.IsText || step.Bytes.Length < 2 || step.Bytes[1] != ring) continue;
            if (step.Op is 0x8F or 0x90 && step.Bytes.Length >= 4 && L2EventCommands.RingRate(step.Bytes[3]).Step != 0) turns = true;
            if (step.Op == 0x9F && step.Bytes.Length >= 4 && L2EventCommands.RingStops(step.Bytes[2]) && L2EventCommands.RingRate(step.Bytes[3]).Step != 0) goes = true;
            if (step.Op == 0x9A) goes = true;
        }
        return (turns, goes);
    }

    // true if step index waits until a ring is gone (A2) that nothing takes away. the game waits there
    // forever (the ring stands where it is)
    private bool RingWaitsForever(int index)
    {
        if (_script == null || index < 0 || index >= _script.Steps.Count || _script.Steps[index] is not { IsText: false, Op: 0xA2 } step || step.Bytes.Length < 2) return false;
        int start = RingStart(index, step.Bytes[1]);
        return start >= 0 && !RingMoves(start, index, step.Bytes[1]).Goes;
    }

    // true if step index starts a ring that nothing makes turn or change before the ring is started
    // again or the event ends. it stands still (one dot if it has one sprite)
    private bool RingStandsStill(int index)
    {
        if (_script == null || index < 0 || index >= _script.Steps.Count || _script.Steps[index] is not { IsText: false } step || step.Bytes.Length < 2 || !IsRing(step.Op)) return false;
        int ring = step.Bytes[1], end = index + 1;
        while (end < _script.Steps.Count && !(_script.Steps[end] is { IsText: false } other && other.Bytes.Length >= 2 && IsRing(other.Op) && other.Bytes[1] == ring)) end++;
        for (int j = index + 1; j < end; j++)
            if (StepOnRing(j, ring, 0x8E, 0x8F, 0x90, 0x9F) && _script.Steps[j].Bytes.Length >= 4 && L2EventCommands.RingRate(_script.Steps[j].Bytes[3]).Step != 0) return false;
        return true;
    }

    // before every "wait until a ring is gone" that would wait forever, adds the game's closing steps (it turns if
    // nothing turns it yet, then closes in and goes). returns how many waits were fixed
    private int FitRings()
    {
        if (_script == null) return 0;
        int fixedCount = 0;
        for (int i = 0; i < _script.Steps.Count; i++)
        {
            if (!RingWaitsForever(i)) continue;
            int ring = _script.Steps[i].Bytes[1], start = RingStart(i, ring);
            var add = new List<L2Step>();
            if (!RingMoves(start, i, ring).Turns) add.Add(new L2Step { Bytes = new byte[] { 0x8F, (byte)ring, 0x00, 0x41 } });
            add.Add(new L2Step { Bytes = new byte[] { 0x9F, (byte)ring, 0x84, 0xE1 } });
            var wait = _script.Steps[i];
            foreach (var other in _script.Steps)
                foreach (var j in other.Jumps)
                    if (j.Step == wait) j.Step = add[0];
            _script.Steps.InsertRange(i, add);
            i += add.Count;
            fixedCount++;
        }
        return fixedCount;
    }

    // the card's button: makes the ring at step index the way the game's warps make it (10 sparkles
    // 120 pixels out, a quarter turn. it turns, closes in and goes, and the event waits for it)
    public void RingLikeGame(int index)
    {
        if (_script == null || !CanEdit || index < 0 || index >= _script.Steps.Count || _script.Steps[index] is not { IsText: false } step || !IsRing(step.Op)) return;
        Remember();
        int ring = step.Bytes[1];
        if (step.Op is 0x8D or 0xA0 or 0xC2 or 0xC3 && step.Bytes.Length >= 7)
        {
            var bytes = (byte[])step.Bytes.Clone();
            bytes[3] = 0x0A; bytes[4] = 0x78; bytes[5] = 0x40; bytes[6] = 0x00;
            step.Bytes = bytes;
        }
        // the steps after it that it's missing (up to where the ring is started again)
        int end = index + 1;
        while (end < _script.Steps.Count && !(_script.Steps[end] is { IsText: false } other && other.Bytes.Length >= 2 && IsRing(other.Op) && other.Bytes[1] == ring)) end++;
        // up to the first wait for it. it has to turn and close in (9F) before that, a "Remove a ring" later doesn't count as closing
        int wait = Enumerable.Range(index + 1, end - index - 1).FirstOrDefault(j => StepOnRing(j, ring, 0xA2), -1);
        int limit = wait >= 0 ? wait : end;
        bool turns = RingMoves(index, limit, ring).Turns;
        bool closes = Enumerable.Range(index + 1, limit - index - 1).Any(j => StepOnRing(j, ring, 0x9F) && _script.Steps[j].Bytes.Length >= 4
                                                                        && L2EventCommands.RingStops(_script.Steps[j].Bytes[2]) && L2EventCommands.RingRate(_script.Steps[j].Bytes[3]).Step != 0);
        var add = new List<L2Step>();
        if (!turns) add.Add(new L2Step { Bytes = new byte[] { 0x8F, (byte)ring, 0x00, 0x41 } });
        if (!closes) add.Add(new L2Step { Bytes = new byte[] { 0x9F, (byte)ring, 0x84, 0xE1 } });
        if (wait < 0 && !closes) add.Add(new L2Step { Bytes = new byte[] { 0xA2, (byte)ring } });
        _script.Steps.InsertRange(index + 1, add);
        Changed(true);
        SelectedLine = index;
        StageInfo = "The ring is now as in the game's warps: 10 sparkles 120 pixels out that turn, close in and go" + (add.Count > 0 ? $" ({add.Count} step{(add.Count > 1 ? "s" : "")} added after it)." : ".");
    }

    // true if step index makes the leader flicker or vanish (99 / 9D on 00)
    private bool LeaderVanishes(int index) =>
        _script != null && index >= 0 && index < _script.Steps.Count && _script.Steps[index] is { IsText: false } step && step.Op is 0x99 or 0x9D && step.Bytes.Length >= 2 && step.Bytes[1] == 0x00;

    // every step that makes the leader flicker or vanish becomes: a map character that looks like Maxim shown,
    // put on his tile, facing his way, then Maxim hidden (like the game's events do), then the character flickers or
    // vanishes. one character plays the part for the whole event. returns how many steps were changed
    public int LeaderVanishToStandIn()
    {
        if (_rom == null || _script == null || !CanEdit) return 0;
        int changed = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            int i = Enumerable.Range(0, _script.Steps.Count).FirstOrDefault(LeaderVanishes, -1);
            if (i < 0) break;
            var leader = Before(i)?.Actors.GetValueOrDefault(0);
            int x = leader is { Placed: true } ? leader.X : 0, y = leader is { Placed: true } ? leader.Y : 0, dir = leader?.Dir ?? 1;
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
            // shown first, the game's character commands skip a hidden character and move the last one found instead
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
