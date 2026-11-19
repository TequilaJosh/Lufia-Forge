namespace LufiaForge.Core.Maps;

// a character on the cutscene stage
public sealed class StageActor
{
    // actor id (1 = leader, 2-5 followers, 7+n = map character n)
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Sprite { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    // 0 right, 1 left, 2 down, 3 up
    public int Facing { get; set; } = 2;
    public bool Visible { get; set; } = true;
    public bool Blinking { get; set; }
    public StageActor Clone() => (StageActor)MemberwiseClone();
}

// a walk drawn on the stage for one line (tiles in order).
// SegmentSpeeds = speed index of each leg (Points[k-1] -> Points[k]), null = Speed everywhere
public sealed record StageWalk(int Actor, IReadOnlyList<(int X, int Y)> Points, int PathNumber, bool Waits, int Speed, bool CameraFollows = false,
                               IReadOnlyList<int>? SegmentSpeeds = null)
{
    public int SpeedOf(int leg) => SegmentSpeeds != null && leg < SegmentSpeeds.Count ? SegmentSpeeds[leg] : Speed;

    // frames the whole walk takes
    public int Frames()
    {
        int total = 0;
        for (int k = 1; k < Points.Count; k++)
            total += (Math.Abs(Points[k].X - Points[k - 1].X) + Math.Abs(Points[k].Y - Points[k - 1].Y)) * CutsceneStage.FramesPerTile(SpeedOf(k - 1));
        return total;
    }
}

// where everything is after one line of an event
public sealed class StageState
{
    public Dictionary<int, StageActor> Actors { get; init; } = new();
    // tile the camera is centred on
    public int CameraX { get; set; }
    public int CameraY { get; set; }
    // true once a camera command ran (otherwise the camera follows the leader)
    public bool CameraFixed { get; set; }
    public bool Black { get; set; }
    // where command 6B put the text window (column 20+ = centred), or null for the usual dialogue box
    public (int Column, int Row)? TextWindow { get; set; }
    public int Music { get; set; } = -1;
    public bool Shaking { get; set; }
    // walks this line starts (paths run by 14/15, the party gathering by 3C)
    public List<StageWalk> Walks { get; } = new();
    // text this line shows (dialogue), with its speaker actor or -1
    public string? Text { get; set; }
    public int Speaker { get; set; } = -1;
    // text boxes close by themselves (command 69), the last box stays on screen until it's replaced or cleared
    public bool AutoText { get; set; }
    // text still on screen from an earlier line (while AutoText is on), or null
    public string? ShownText { get; set; }
    public int ShownSpeaker { get; set; } = -1;
    // frames this line waits (waits and fades), for playback
    public int WaitFrames { get; set; }
    // the emote bubble on screen (command 6F): over which actor, which emote, frames left (int.MaxValue = until hidden)
    public (int Actor, int Icon, int Frames)? Emote { get; set; }
    // set when the event leaves the map here (warp / another map's event)
    public string? Leaves { get; set; }

    public StageState Clone()
    {
        var copy = new StageState
        {
            CameraX = CameraX, CameraY = CameraY, CameraFixed = CameraFixed, Black = Black, Music = Music, Shaking = Shaking,
            AutoText = AutoText, ShownText = ShownText, ShownSpeaker = ShownSpeaker, TextWindow = TextWindow, Emote = Emote,
        };
        foreach (var kv in Actors) copy.Actors[kv.Key] = kv.Value.Clone();
        return copy;
    }
}

// who's in the party when the cutscene starts
public sealed record StageParty(string Name, int[] Characters);

// plays an event's lines in order (no branches taken) and records where the characters, the camera and the screen
// are after each line. the cutscene stage view and its preview playback use it
public static class CutsceneStage
{
    // field sprite of each character (table $01:E23F, low byte)
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

    // commands that move characters, the camera or the screen (what makes an event a cutscene)
    public static bool IsStageCommand(ScriptOp op) => !op.IsText && op.Bytes.Length > 0 &&
        op.Bytes[0] is >= 0x09 and <= 0x15 or 0x18 or 0x19 or >= 0x20 and <= 0x2F or 0x3C or >= 0x5C and <= 0x68 or 0x6F or >= 0xB0 and <= 0xBF;

    // how many stage commands an event has (0 = not a cutscene)
    public static int StageCommandCount(EventScript script) => script.Ops.Count(IsStageCommand);

    // the party a map most likely has (the prologue maps have the four heroes)
    public static int DefaultParty(int mapId) => mapId is >= 0x93 and <= 0x96 ? 4 : 1;

    // screen size in tiles (256x224 pixels)
    public const int ScreenTilesX = 16, ScreenTilesY = 14;

    public static List<StageState> Simulate(LufiaMap map, EventScript script, StageParty party, int startX, int startY)
    {
        var state = Initial(map, party, startX, startY);
        state.Black = StartsBlack(script);
        var states = new List<StageState>();
        foreach (var op in script.Ops)
        {
            state = state.Clone();
            try { Apply(map, state, op); } catch { /* broken command, keep the stage as it was */ }
            // a timed bubble goes away once the lines after it have waited long enough
            if (state.Emote is { } emote && emote.Frames != int.MaxValue && !(!op.IsText && op.Bytes.Length > 0 && op.Bytes[0] == 0x6F))
                state.Emote = emote.Frames - state.WaitFrames > 0 ? emote with { Frames = emote.Frames - state.WaitFrames } : null;
            if (!state.CameraFixed && state.Actors.TryGetValue(1, out var lead)) { state.CameraX = lead.X; state.CameraY = lead.Y; }
            states.Add(state);
        }
        return states;
    }

    // true when the event fades the screen in before it ever fades it out, so it starts on a black screen (the intro,
    // events that run right after a warp left the screen black)
    public static bool StartsBlack(EventScript script)
    {
        foreach (var op in script.Ops)
        {
            if (op.IsText || op.Bytes.Length == 0) continue;
            var bytes = op.Bytes;
            if (bytes[0] == 0x18 || (bytes[0] == 0x50 && bytes.Length > 1 && bytes[1] == 0x06)) return true;
            if (bytes[0] == 0x19 || (bytes[0] == 0x50 && bytes.Length > 1 && bytes[1] is 0x05 or 0x08)) return false;
        }
        return false;
    }

    // the stage before the first line: map characters where the map puts them, the party on the start tile
    public static StageState Initial(LufiaMap map, StageParty party, int startX, int startY)
    {
        var state = new StageState { CameraX = startX, CameraY = startY };
        foreach (var npc in map.Npcs.Where(n => !n.IsUnused))
            // command "character k" = actor 7 + k = the map's record k - 1
            state.Actors[8 + npc.Index] = new StageActor { Id = 8 + npc.Index, Name = $"Character {npc.Index + 1}", Sprite = npc.Sprite, X = npc.X, Y = npc.Y };
        for (int i = 0; i < party.Characters.Length; i++)
        {
            int character = party.Characters[i];
            state.Actors[1 + i] = new StageActor
            {
                Id = 1 + i, Name = EventCommands.CharacterNames[character], Sprite = CharacterSprites[character], X = startX, Y = startY, Facing = 3,
            };   // followers stand behind the leader on the same tile
        }

        return state;
    }

    // walks the cutscene doesn't wait for keep going while later lines run (that's how the intro's camera pans).
    // so we put the walking character (and the camera, if it follows) where it is at the end of each later line,
    // going by the line times from the timeline. a later command that moves the same character ends the walk
    public static void ApplyTime(IReadOnlyList<StageState> states, IReadOnlyList<TimelineItem> timeline, EventScript? script = null)
    {
        var slow = script != null ? CutsceneTimeline.SlowSpans(script, timeline) : new();
        var itemOfLine = timeline.GroupBy(t => t.Line).ToDictionary(g => g.Key, g => g.First());
        int EndOf(int line) => itemOfLine.TryGetValue(line, out var item) ? item.Start + (item.Blocking ? item.Duration : 0) : 0;
        for (int line = 0; line < states.Count; line++)
        {
            foreach (var walk in states[line].Walks.Where(x => !x.Waits))
            {
                int framesPerTile = FramesPerTile(walk.Speed);
                int start = itemOfLine.TryGetValue(line, out var walkItem) ? walkItem.Start : 0;
                int tiles = 0;
                for (int k = 1; k < walk.Points.Count; k++) tiles += Math.Abs(walk.Points[k].X - walk.Points[k - 1].X) + Math.Abs(walk.Points[k].Y - walk.Points[k - 1].Y);
                for (int j = line; j < states.Count; j++)
                {
                    if (j > line && states[j].Walks.Any(x => x.Actor == walk.Actor)) break;   // a new walk takes over
                    double elapsed = Math.Max(0, CutsceneTimeline.WalkProgress(start, EndOf(j) - start, slow));
                    if (elapsed >= walk.Frames()) break;                                    // done, the line's final position stands
                    var (x, y) = PointAlongFrames(walk, elapsed);
                    if (states[j].Actors.TryGetValue(walk.Actor, out var actor)) { actor.X = x; actor.Y = y; }
                    if (walk.CameraFollows) { states[j].CameraX = x; states[j].CameraY = y; states[j].CameraFixed = true; }
                }
            }
        }
    }

    // the tile you reach after frames frames of walking (each leg at its own speed)
    public static (int X, int Y) PointAlongFrames(StageWalk walk, double frames)
    {
        double left = frames;
        for (int k = 1; k < walk.Points.Count; k++)
        {
            var (ax, ay) = walk.Points[k - 1]; var (bx, by) = walk.Points[k];
            int framesPerTile = FramesPerTile(walk.SpeedOf(k - 1));
            double legFrames = (Math.Abs(bx - ax) + Math.Abs(by - ay)) * framesPerTile;
            if (left < legFrames)
            {
                double fraction = legFrames == 0 ? 1 : left / legFrames;
                return ((int)Math.Round(ax + (bx - ax) * fraction), (int)Math.Round(ay + (by - ay) * fraction));
            }
            left -= legFrames;
        }
        return walk.Points[^1];
    }

    // the tile you reach after walking tiles tiles along a path
    public static (int X, int Y) PointAlong(IReadOnlyList<(int X, int Y)> points, double tiles)
    {
        double walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double legLength = Math.Abs(points[i].X - points[i - 1].X) + Math.Abs(points[i].Y - points[i - 1].Y);
            if (tiles < walked + legLength)
            {
                double fraction = legLength == 0 ? 1 : (tiles - walked) / legLength;
                return ((int)Math.Round(points[i - 1].X + (points[i].X - points[i - 1].X) * fraction), (int)Math.Round(points[i - 1].Y + (points[i].Y - points[i - 1].Y) * fraction));
            }
            walked += legLength;
        }
        return points[^1];
    }

    // where the party most likely stands when the event starts (next to the character it belongs to)
    public static (int X, int Y) DefaultStart(LufiaMap map, int ev)
    {
        if (map.Npcs.FirstOrDefault(n => MapEvents.TalkEvent(n.Index) == ev && !n.IsUnused) is { } npc) return (npc.X, npc.Y + 1);
        if (map.Arrivals.Count > 0) return (map.Arrivals[0].X, map.Arrivals[0].Y);
        return (map.Width / 2, map.Height / 2);
    }

    // frames to walk one tile at a speed index (table $01:E237, $10 = 1 pixel per frame)
    public static int FramesPerTile(int speedIndex) => Math.Max(1, 16 * 16 / Math.Max(1, EventCommands.ScrollSpeeds[speedIndex & 7]));

    public static int ActorOf(int whoValue) => whoValue >= 0x100 ? 7 + (whoValue - 0x100) : whoValue;

    private static StageActor? Get(StageState state, int id) => state.Actors.TryGetValue(id, out var actor) ? actor : null;

    private static IEnumerable<StageActor> Followers(StageState state) => state.Actors.Values.Where(a => a.Id is >= 2 and <= 5);

    private static void Apply(LufiaMap map, StageState state, ScriptOp op)
    {
        var bytes = op.Bytes;
        if (op.IsText)
        {
            state.Text = op.Text;
            state.Speaker = bytes.Length == 0 ? -1 : bytes[0] switch
            {
                >= 0x88 and <= 0x8F => bytes[0] - 0x88,
                >= 0x90 and <= 0xAF => bytes[0] - 0x90 + 8,
                0x0E or 0x31 or 0x33 or 0x35 or 0x37 when bytes.Length > 1 => bytes[1],
                0x0F or 0x30 or 0x32 or 0x34 or 0x36 when bytes.Length > 1 => bytes[1] + 7,
                _ => -1,
            };
            if (state.AutoText) { state.ShownText = state.Text; state.ShownSpeaker = state.Speaker; } else state.ShownText = null;
            return;
        }
        if (bytes.Length == 0) return;
        int command = bytes[0];
        switch (command)
        {
            case >= 0x20 and <= 0x27:
            {
                int id = ActorOf(ParamDef.Who(1, 0x7F).Get(bytes));
                var actor = Get(state, id);
                if (actor == null) break;
                actor.X = ((bytes[1] >> 7) << 8) | bytes[2]; actor.Y = bytes[3]; actor.Facing = (command >> 1) & 3;
                if (id == 1) foreach (var follower in Followers(state)) { follower.X = actor.X; follower.Y = actor.Y; }
                break;
            }
            case >= 0x28 and <= 0x2F:
                if (Get(state, ActorOf(ParamDef.Who(1).Get(bytes))) is { } turner) turner.Facing = (command >> 1) & 3;
                break;
            case >= 0xB0 and <= 0xBF:
                if (Get(state, (command & 3) + 1) is { } partyMember) partyMember.Facing = (command >> 2) & 3;
                break;
            case 0x14 or 0x15:
            {
                int id = ActorOf(ParamDef.Who(2).Get(bytes));
                var actor = Get(state, id);
                int pathNumber = bytes[1];
                if (actor == null || pathNumber < 1 || pathNumber > map.Paths.Count) break;
                var path = map.Paths[pathNumber - 1];
                var points = path.Points();
                var walk = new List<(int X, int Y)>();
                if (path.WalkToStart) walk.Add((actor.X, actor.Y));
                walk.AddRange(points);
                var last = points[^1];
                actor.X = last.X; actor.Y = last.Y; actor.Visible = true;
                if (path.Steps.Count > 0)
                {
                    var lastStep = path.Steps[^1];
                    actor.Facing = lastStep.Facing is >= 1 and <= 4 ? lastStep.Facing - 1 : lastStep.Facing == 5 ? actor.Facing : lastStep.Direction;
                }
                if (path.CameraFollows) { state.CameraX = actor.X; state.CameraY = actor.Y; state.CameraFixed = true; }
                var speeds = new List<int>();
                if (path.WalkToStart) speeds.Add(path.Steps.Count > 0 ? path.Steps[0].Speed : 4);
                speeds.AddRange(path.Steps.Select(x => x.Speed));
                state.Walks.Add(new StageWalk(id, walk, pathNumber, !path.NoWait, path.Steps.Count > 0 ? path.Steps[0].Speed : 4, path.CameraFollows, speeds));
                break;
            }
            case 0x3C:
                if (Get(state, 1) is { } lead)
                    foreach (var follower in Followers(state))
                    {
                        if (follower.X != lead.X || follower.Y != lead.Y) state.Walks.Add(new StageWalk(follower.Id, new[] { (follower.X, follower.Y), (lead.X, lead.Y) }, 0, true, 4));
                        follower.X = lead.X; follower.Y = lead.Y;
                    }
                break;
            case 0x5C or 0x5D:
                if (Get(state, ActorOf(ParamDef.Who(1).Get(bytes))) is { } hidden) { hidden.Visible = bytes[2] != 0; hidden.Blinking = bytes[2] != 0; }
                break;
            case 0x6F when bytes.Length >= 4:
                state.Emote = bytes[3] == 0 ? null : (bytes[1], bytes[2], bytes[3] == 0xFF ? int.MaxValue : bytes[3]);
                break;
            case 0x5E or 0x5F:
                if (Get(state, ActorOf(ParamDef.Who(1).Get(bytes))) is { } shown) { shown.Visible = true; shown.Blinking = false; }
                break;
            case 0x1A:
            {
                int character = bytes[1] & 15;
                int source = bytes[2] == 0 ? 1 : 7 + bytes[2];
                int id = Enumerable.Range(2, 4).FirstOrDefault(i => !state.Actors.ContainsKey(i));
                if (id == 0) break;
                var placeNear = Get(state, source) ?? Get(state, 1);
                state.Actors[id] = new StageActor { Id = id, Name = EventCommands.CharacterNames[character], Sprite = CharacterSprites[character], X = placeNear?.X ?? 0, Y = placeNear?.Y ?? 0, Facing = placeNear?.Facing ?? 2 };
                break;
            }
            case 0x1B:
            {
                var last = Followers(state).OrderByDescending(a => a.Id).FirstOrDefault();
                if (last != null) state.Actors.Remove(last.Id);
                break;
            }
            case 0x09:
                state.CameraX = bytes[1] | (bytes[2] << 8); state.CameraY = bytes[3] | (bytes[4] << 8); state.CameraFixed = true;
                break;
            case >= 0x10 and <= 0x13:
            {
                int tiles = bytes[1] == 0 ? 256 : bytes[1];
                (int dx, int dy) = (command & 3) switch { 0 => (1, 0), 1 => (-1, 0), 2 => (0, 1), _ => (0, -1) };
                state.CameraX += dx * tiles; state.CameraY += dy * tiles; state.CameraFixed = true;
                int speed = EventCommands.ScrollSpeeds[bytes[2] & 7];
                state.WaitFrames = tiles * 16 * 16 / Math.Max(1, speed);
                break;
            }
            case 0x18: state.Black = false; break;   // fade in
            case 0x19: state.Black = true; state.ShownText = null; break;    // fade to black
            case 0x50 when bytes[1] is 0x05 or 0x08: state.Black = true; state.WaitFrames = 32; state.ShownText = null; break;
            case 0x50 when bytes[1] is 0x09 or 0x14: state.ShownText = null; break;   // close the text overlay
            case 0x69: state.AutoText = true; break;
            case 0x6B when bytes.Length > 2: state.TextWindow = (bytes[1], bytes[2]); break;
            case 0x6A: state.AutoText = false; state.ShownText = null; break;
            case 0x50 when bytes[1] == 0x06: state.Black = false; state.WaitFrames = 32; break;
            case 0x5A: state.WaitFrames = 32; break;
            case 0x0A: state.Music = bytes[1]; break;
            case 0x65 or 0x66: state.Shaking = true; break;
            case 0x67: state.Shaking = false; break;
            case >= 0x80 and <= 0x87: state.WaitFrames = EventCommands.ShortWaitFrames[command & 7]; break;
            case 0x0B: state.WaitFrames = (bytes[1] * 4) & 0xFF; break;
            case 0x4C or 0x5B: state.Leaves = bytes[1] == 0 ? $"moves the party to arrival point {bytes[2]}" : $"warps to map {bytes[1]:X2}"; break;
            case 0x51: state.Leaves = $"continues with event {EventCommands.ChainedEvent(bytes)} of map {bytes[1]:X2}"; break;
        }
    }
}
