namespace LufiaForge.Core.Maps;

/// <summary>A character on the cutscene stage.</summary>
public sealed class StageActor
{
    /// <summary>Actor id (1 = leader, 2-5 followers, 7+n = map character n).</summary>
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Sprite { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>0 right, 1 left, 2 down, 3 up.</summary>
    public int Facing { get; set; } = 2;
    public bool Visible { get; set; } = true;
    public bool Blinking { get; set; }
    public StageActor Clone() => (StageActor)MemberwiseClone();
}

/// <summary>A walk drawn on the stage for one line (tiles in order).</summary>
/// <param name="SegmentSpeeds">Speed index of each leg (Points[k-1] → Points[k]); null = <paramref name="Speed"/> everywhere.</param>
public sealed record StageWalk(int Actor, IReadOnlyList<(int X, int Y)> Points, int PathNumber, bool Waits, int Speed, bool CameraFollows = false,
                               IReadOnlyList<int>? SegmentSpeeds = null)
{
    public int SpeedOf(int leg) => SegmentSpeeds != null && leg < SegmentSpeeds.Count ? SegmentSpeeds[leg] : Speed;

    /// <summary>Frames the whole walk takes.</summary>
    public int Frames()
    {
        int f = 0;
        for (int k = 1; k < Points.Count; k++)
            f += (Math.Abs(Points[k].X - Points[k - 1].X) + Math.Abs(Points[k].Y - Points[k - 1].Y)) * CutsceneStage.FramesPerTile(SpeedOf(k - 1));
        return f;
    }
}

/// <summary>Where everything is after one line of an event.</summary>
public sealed class StageState
{
    public Dictionary<int, StageActor> Actors { get; init; } = new();
    /// <summary>Tile the camera is centred on.</summary>
    public int CameraX { get; set; }
    public int CameraY { get; set; }
    /// <summary>True once a camera command ran (otherwise the camera follows the leader).</summary>
    public bool CameraFixed { get; set; }
    public bool Black { get; set; }
    /// <summary>Where command 6B put the text window (column 20+ = centred), or null for the usual dialogue box.</summary>
    public (int Column, int Row)? TextWindow { get; set; }
    public int Music { get; set; } = -1;
    public bool Shaking { get; set; }
    /// <summary>Walks this line starts (paths run by 14/15, the party gathering by 3C).</summary>
    public List<StageWalk> Walks { get; } = new();
    /// <summary>Text this line shows (dialogue), with its speaker actor or -1.</summary>
    public string? Text { get; set; }
    public int Speaker { get; set; } = -1;
    /// <summary>Text boxes close by themselves (command 69): the last box stays on screen until it's replaced or cleared.</summary>
    public bool AutoText { get; set; }
    /// <summary>Text still on screen from an earlier line (while <see cref="AutoText"/> is on), or null.</summary>
    public string? ShownText { get; set; }
    public int ShownSpeaker { get; set; } = -1;
    /// <summary>Frames this line waits (waits and fades), for playback.</summary>
    public int WaitFrames { get; set; }
    /// <summary>Set when the event leaves the map here (warp / another map's event).</summary>
    public string? Leaves { get; set; }

    public StageState Clone()
    {
        var c = new StageState
        {
            CameraX = CameraX, CameraY = CameraY, CameraFixed = CameraFixed, Black = Black, Music = Music, Shaking = Shaking,
            AutoText = AutoText, ShownText = ShownText, ShownSpeaker = ShownSpeaker, TextWindow = TextWindow,
        };
        foreach (var kv in Actors) c.Actors[kv.Key] = kv.Value.Clone();
        return c;
    }
}

/// <summary>Who is in the party when the cutscene starts.</summary>
public sealed record StageParty(string Name, int[] Characters);

/// <summary>
/// Plays an event's lines in order (without taking branches) and records where the characters, the camera and the
/// screen are after each line. Used by the cutscene stage view and its preview playback.
/// </summary>
public static class CutsceneStage
{
    /// <summary>Field sprite of each character (table $01:E23F, low byte).</summary>
    public static readonly int[] CharacterSprites = { 0x00, 0x01, 0x02, 0x03, 0x27, 0x28, 0x29, 0x2A, 0x0C, 0x10, 0x0C, 0x07, 0x04, 0x1A, 0x1B, 0x08 };

    public static readonly StageParty[] Parties =
    {
        new("Hero", new[] { 0 }),
        new("Hero, Lufia", new[] { 0, 1 }),
        new("Hero, Lufia, Aguro", new[] { 0, 1, 2 }),
        new("Hero, Lufia, Aguro, Jerin", new[] { 0, 1, 2, 3 }),
        new("Prologue: Maxim, Selan, Guy, Artea", new[] { 4, 5, 6, 7 }),
        new("No party (empty screen)", Array.Empty<int>()),
    };

    /// <summary>Commands that move characters, the camera or the screen (what makes an event a cutscene).</summary>
    public static bool IsStageCommand(ScriptOp o) => !o.IsText && o.Bytes.Length > 0 &&
        o.Bytes[0] is >= 0x09 and <= 0x15 or 0x18 or 0x19 or >= 0x20 and <= 0x2F or 0x3C or >= 0x5C and <= 0x68 or >= 0xB0 and <= 0xBF;

    /// <summary>How many stage commands an event has (0 = not a cutscene).</summary>
    public static int StageCommandCount(EventScript s) => s.Ops.Count(IsStageCommand);

        /// <summary>The party a map most likely has (the prologue maps have the four heroes).</summary>
    public static int DefaultParty(int mapId) => mapId is >= 0x93 and <= 0x96 ? 4 : 1;

    /// <summary>Screen size in tiles (256x224 pixels).</summary>
    public const int ScreenTilesX = 16, ScreenTilesY = 14;

    public static List<StageState> Simulate(LufiaMap map, EventScript script, StageParty party, int startX, int startY)
    {
        var st = Initial(map, party, startX, startY);
        st.Black = StartsBlack(script);
        var states = new List<StageState>();
        foreach (var op in script.Ops)
        {
            st = st.Clone();
            try { Apply(map, st, op); } catch { /* malformed command: keep the stage as it was */ }
            if (!st.CameraFixed && st.Actors.TryGetValue(1, out var lead)) { st.CameraX = lead.X; st.CameraY = lead.Y; }
            states.Add(st);
        }
        return states;
    }

    /// <summary>
    /// True when the event fades the screen in before it ever fades it out: it starts on a black screen (the intro,
    /// events run right after a warp that left the screen black).
    /// </summary>
    public static bool StartsBlack(EventScript script)
    {
        foreach (var op in script.Ops)
        {
            if (op.IsText || op.Bytes.Length == 0) continue;
            var b = op.Bytes;
            if (b[0] == 0x18 || (b[0] == 0x50 && b.Length > 1 && b[1] == 0x06)) return true;
            if (b[0] == 0x19 || (b[0] == 0x50 && b.Length > 1 && b[1] is 0x05 or 0x08)) return false;
        }
        return false;
    }

    /// <summary>The stage before the first line: map characters where the map puts them, the party at the start tile.</summary>
    public static StageState Initial(LufiaMap map, StageParty party, int startX, int startY)
    {
        var st = new StageState { CameraX = startX, CameraY = startY };
        foreach (var n in map.Npcs.Where(n => !n.IsUnused))
            // command "character k" = actor 7 + k = the map's record k - 1
            st.Actors[8 + n.Index] = new StageActor { Id = 8 + n.Index, Name = $"Character {n.Index + 1}", Sprite = n.Sprite, X = n.X, Y = n.Y };
        for (int i = 0; i < party.Characters.Length; i++)
        {
            int c = party.Characters[i];
            st.Actors[1 + i] = new StageActor
            {
                Id = 1 + i, Name = EventCommands.CharacterNames[c], Sprite = CharacterSprites[c], X = startX, Y = startY, Facing = 3,
            };   // followers stand behind the leader on the same tile
        }

        return st;
    }

    /// <summary>
    /// Walks the cutscene doesn't wait for keep going while later lines run (the intro's camera pans this way):
    /// place the walking character (and the camera, when it follows) where it is at the end of each later line,
    /// using the line times from the timeline. A later command that moves the same character ends the walk.
    /// </summary>
    public static void ApplyTime(IReadOnlyList<StageState> states, IReadOnlyList<TimelineItem> timeline, EventScript? script = null)
    {
        var slow = script != null ? CutsceneTimeline.SlowSpans(script, timeline) : new();
        var at = timeline.GroupBy(t => t.Line).ToDictionary(g => g.Key, g => g.First());
        int EndOf(int line) => at.TryGetValue(line, out var t) ? t.Start + (t.Blocking ? t.Duration : 0) : 0;
        for (int w = 0; w < states.Count; w++)
        {
            foreach (var walk in states[w].Walks.Where(x => !x.Waits))
            {
                int fpt = FramesPerTile(walk.Speed);
                int start = at.TryGetValue(w, out var tw) ? tw.Start : 0;
                int tiles = 0;
                for (int k = 1; k < walk.Points.Count; k++) tiles += Math.Abs(walk.Points[k].X - walk.Points[k - 1].X) + Math.Abs(walk.Points[k].Y - walk.Points[k - 1].Y);
                for (int j = w; j < states.Count; j++)
                {
                    if (j > w && states[j].Walks.Any(x => x.Actor == walk.Actor)) break;   // a new walk takes over
                    double elapsed = Math.Max(0, CutsceneTimeline.WalkProgress(start, EndOf(j) - start, slow));
                    if (elapsed >= walk.Frames()) break;                                    // finished: the line's final position stands
                    var (x, y) = PointAlongFrames(walk, elapsed);
                    if (states[j].Actors.TryGetValue(walk.Actor, out var a)) { a.X = x; a.Y = y; }
                    if (walk.CameraFollows) { states[j].CameraX = x; states[j].CameraY = y; states[j].CameraFixed = true; }
                }
            }
        }
    }

    /// <summary>The tile reached after <paramref name="frames"/> frames of walking (each leg at its own speed).</summary>
    public static (int X, int Y) PointAlongFrames(StageWalk w, double frames)
    {
        double left = frames;
        for (int k = 1; k < w.Points.Count; k++)
        {
            var (ax, ay) = w.Points[k - 1]; var (bx, by) = w.Points[k];
            int fpt = FramesPerTile(w.SpeedOf(k - 1));
            double legFrames = (Math.Abs(bx - ax) + Math.Abs(by - ay)) * fpt;
            if (left < legFrames)
            {
                double f = legFrames == 0 ? 1 : left / legFrames;
                return ((int)Math.Round(ax + (bx - ax) * f), (int)Math.Round(ay + (by - ay) * f));
            }
            left -= legFrames;
        }
        return w.Points[^1];
    }

    /// <summary>The tile reached after walking <paramref name="tiles"/> tiles along a path.</summary>
    public static (int X, int Y) PointAlong(IReadOnlyList<(int X, int Y)> pts, double tiles)
    {
        double done = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double len = Math.Abs(pts[i].X - pts[i - 1].X) + Math.Abs(pts[i].Y - pts[i - 1].Y);
            if (tiles < done + len)
            {
                double f = len == 0 ? 1 : (tiles - done) / len;
                return ((int)Math.Round(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * f), (int)Math.Round(pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * f));
            }
            done += len;
        }
        return pts[^1];
    }

    /// <summary>Where the party most likely stands when the event starts (next to the character it belongs to).</summary>
    public static (int X, int Y) DefaultStart(LufiaMap map, int ev)
    {
        if (map.Npcs.FirstOrDefault(n => n.Index == ev && !n.IsUnused) is { } npc) return (npc.X, npc.Y + 1);
        if (map.Arrivals.Count > 0) return (map.Arrivals[0].X, map.Arrivals[0].Y);
        return (map.Width / 2, map.Height / 2);
    }

    /// <summary>Frames to walk one tile at a speed index (table $01:E237, $10 = 1 pixel per frame).</summary>
    public static int FramesPerTile(int speedIndex) => Math.Max(1, 16 * 16 / Math.Max(1, EventCommands.ScrollSpeeds[speedIndex & 7]));

    public static int ActorOf(int whoValue) => whoValue >= 0x100 ? 7 + (whoValue - 0x100) : whoValue;

    private static StageActor? Get(StageState st, int id) => st.Actors.TryGetValue(id, out var a) ? a : null;

    private static IEnumerable<StageActor> Followers(StageState st) => st.Actors.Values.Where(a => a.Id is >= 2 and <= 5);

    private static void Apply(LufiaMap map, StageState st, ScriptOp op)
    {
        var b = op.Bytes;
        if (op.IsText)
        {
            st.Text = op.Text;
            st.Speaker = b.Length == 0 ? -1 : b[0] switch
            {
                >= 0x88 and <= 0x8F => b[0] - 0x88,
                >= 0x90 and <= 0xAF => b[0] - 0x90 + 8,
                0x0E or 0x31 or 0x33 or 0x35 or 0x37 when b.Length > 1 => b[1],
                0x0F or 0x30 or 0x32 or 0x34 or 0x36 when b.Length > 1 => b[1] + 7,
                _ => -1,
            };
            if (st.AutoText) { st.ShownText = st.Text; st.ShownSpeaker = st.Speaker; } else st.ShownText = null;
            return;
        }
        if (b.Length == 0) return;
        int o = b[0];
        switch (o)
        {
            case >= 0x20 and <= 0x27:
            {
                int id = ActorOf(ParamDef.Who(1, 0x7F).Get(b));
                var a = Get(st, id);
                if (a == null) break;
                a.X = ((b[1] >> 7) << 8) | b[2]; a.Y = b[3]; a.Facing = (o >> 1) & 3;
                if (id == 1) foreach (var f in Followers(st)) { f.X = a.X; f.Y = a.Y; }
                break;
            }
            case >= 0x28 and <= 0x2F:
                if (Get(st, ActorOf(ParamDef.Who(1).Get(b))) is { } t) t.Facing = (o >> 1) & 3;
                break;
            case >= 0xB0 and <= 0xBF:
                if (Get(st, (o & 3) + 1) is { } p) p.Facing = (o >> 2) & 3;
                break;
            case 0x14 or 0x15:
            {
                int id = ActorOf(ParamDef.Who(2).Get(b));
                var a = Get(st, id);
                int n = b[1];
                if (a == null || n < 1 || n > map.Paths.Count) break;
                var path = map.Paths[n - 1];
                var pts = path.Points();
                var walk = new List<(int X, int Y)>();
                if (path.WalkToStart) walk.Add((a.X, a.Y));
                walk.AddRange(pts);
                var last = pts[^1];
                a.X = last.X; a.Y = last.Y; a.Visible = true;
                if (path.Steps.Count > 0)
                {
                    var s = path.Steps[^1];
                    a.Facing = s.Facing is >= 1 and <= 4 ? s.Facing - 1 : s.Facing == 5 ? a.Facing : s.Direction;
                }
                if (path.CameraFollows) { st.CameraX = a.X; st.CameraY = a.Y; st.CameraFixed = true; }
                var speeds = new List<int>();
                if (path.WalkToStart) speeds.Add(path.Steps.Count > 0 ? path.Steps[0].Speed : 4);
                speeds.AddRange(path.Steps.Select(x => x.Speed));
                st.Walks.Add(new StageWalk(id, walk, n, !path.NoWait, path.Steps.Count > 0 ? path.Steps[0].Speed : 4, path.CameraFollows, speeds));
                break;
            }
            case 0x3C:
                if (Get(st, 1) is { } lead)
                    foreach (var f in Followers(st))
                    {
                        if (f.X != lead.X || f.Y != lead.Y) st.Walks.Add(new StageWalk(f.Id, new[] { (f.X, f.Y), (lead.X, lead.Y) }, 0, true, 4));
                        f.X = lead.X; f.Y = lead.Y;
                    }
                break;
            case 0x5C or 0x5D:
                if (Get(st, ActorOf(ParamDef.Who(1).Get(b))) is { } h) { h.Visible = b[2] != 0; h.Blinking = b[2] != 0; }
                break;
            case 0x5E or 0x5F:
                if (Get(st, ActorOf(ParamDef.Who(1).Get(b))) is { } v) { v.Visible = true; v.Blinking = false; }
                break;
            case 0x1A:
            {
                int c = b[1] & 15;
                int src = b[2] == 0 ? 1 : 7 + b[2];
                int id = Enumerable.Range(2, 4).FirstOrDefault(i => !st.Actors.ContainsKey(i));
                if (id == 0) break;
                var at = Get(st, src) ?? Get(st, 1);
                st.Actors[id] = new StageActor { Id = id, Name = EventCommands.CharacterNames[c], Sprite = CharacterSprites[c], X = at?.X ?? 0, Y = at?.Y ?? 0, Facing = at?.Facing ?? 2 };
                break;
            }
            case 0x1B:
            {
                var last = Followers(st).OrderByDescending(a => a.Id).FirstOrDefault();
                if (last != null) st.Actors.Remove(last.Id);
                break;
            }
            case 0x09:
                st.CameraX = b[1] | (b[2] << 8); st.CameraY = b[3] | (b[4] << 8); st.CameraFixed = true;
                break;
            case >= 0x10 and <= 0x13:
            {
                int tiles = b[1] == 0 ? 256 : b[1];
                (int dx, int dy) = (o & 3) switch { 0 => (1, 0), 1 => (-1, 0), 2 => (0, 1), _ => (0, -1) };
                st.CameraX += dx * tiles; st.CameraY += dy * tiles; st.CameraFixed = true;
                int speed = EventCommands.ScrollSpeeds[b[2] & 7];
                st.WaitFrames = tiles * 16 * 16 / Math.Max(1, speed);
                break;
            }
            case 0x18: st.Black = false; break;   // fade in
            case 0x19: st.Black = true; st.ShownText = null; break;    // fade to black
            case 0x50 when b[1] is 0x05 or 0x08: st.Black = true; st.WaitFrames = 32; st.ShownText = null; break;
            case 0x50 when b[1] is 0x09 or 0x14: st.ShownText = null; break;   // close the text overlay
            case 0x69: st.AutoText = true; break;
            case 0x6B when b.Length > 2: st.TextWindow = (b[1], b[2]); break;
            case 0x6A: st.AutoText = false; st.ShownText = null; break;
            case 0x50 when b[1] == 0x06: st.Black = false; st.WaitFrames = 32; break;
            case 0x5A: st.WaitFrames = 32; break;
            case 0x0A: st.Music = b[1]; break;
            case 0x65 or 0x66: st.Shaking = true; break;
            case 0x67: st.Shaking = false; break;
            case >= 0x80 and <= 0x87: st.WaitFrames = EventCommands.ShortWaitFrames[o & 7]; break;
            case 0x0B: st.WaitFrames = (b[1] * 4) & 0xFF; break;
            case 0x4C or 0x5B: st.Leaves = b[1] == 0 ? $"moves the party to arrival point {b[2]}" : $"warps to map {b[1]:X2}"; break;
            case 0x51: st.Leaves = $"continues with event {EventCommands.ChainedEvent(b)} of map {b[1]:X2}"; break;
        }
    }
}
