namespace LufiaForge.Core.Lufia2;

// what a cutscene step does (one or a few commands of the script, seen as one thing)
public enum L2BeatKind
{
    Appear, Leave, Walk, Face, Act, Say, Camera, CameraBack, FadeOut, FadeIn, Music, Sound, Wait, Shake, StopShake,
    FlagOn, FlagOff, GoTo, End, Other,
}

// one step of a cutscene in plain words: a few script commands that belong together ("Guy walks 3 tiles left" is
// three times "A3 Guy step-left" + "89 Guy"). Build makes the commands from the fields, L2Cutscene.Beats reads them
// back. steps the cutscene editor doesn't know are L2BeatKind.Other and stay as they are
public sealed class L2Beat
{
    public L2BeatKind Kind { get; set; }
    // the script steps it's made of (as read, or as last built)
    public List<L2Step> Steps { get; } = new();
    // the character (actor id), and a second one walking along (-1 = none)
    public int Who { get; set; }
    public int Who2 { get; set; } = -1;
    // 0 up, 1 down, 2 left, 3 right
    public int Dir { get; set; } = 1;
    // tiles walked
    public int Count { get; set; } = 1;
    // a tile (appear, camera), -1 = where it is
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    // action, song, sound, frames, story flag, fade speed, camera speed, map, shake strength
    public int Value { get; set; }
    // go to: scene. shake: how often
    public int Value2 { get; set; }
    // go to: arrival point
    public int Value3 { get; set; }
    // appear / leave in a flash
    public bool Flash { get; set; }
    // camera: jumps there at once instead of scrolling
    public bool AtOnce { get; set; }
    // say: the opening command (61-67 a hero, 13 xx a character, 9E xx narration, 08 the one talked to)
    public byte[] Opener { get; set; } = { 0x61 };
    public string Text { get; set; } = "";

    public static readonly string[] DirNames = { "up", "down", "left", "right" };
    // the frame 7A shows for a facing (0 up, 1 down, 2 left, 3 right)
    public static readonly int[] FaceFrame = { 4, 0, 2, 6 };

    // the commands for the fields
    public List<L2Step> Build()
    {
        var built = new List<L2Step>();
        void C(params int[] b) => built.Add(new L2Step { Bytes = b.Select(x => (byte)x).ToArray() });
        switch (Kind)
        {
            case L2BeatKind.Appear:
                // shown first: the game's commands that find a character skip hidden ones ($80:BF6F), so "put" on a
                // hidden character would move a different one (both run in the same frame, so no flicker)
                C(0x2D, Who);
                if (X >= 0 && Y >= 0) C(0x2F, Who, X, Y);
                if (Flash) { C(0xA3, Who, 0x86); C(0x89, Who); }
                break;
            case L2BeatKind.Leave:
                if (Flash) C(0x99, Who); else C(0x2E, Who);
                break;
            case L2BeatKind.Walk:
                for (int i = 0; i < Math.Max(1, Count); i++)
                {
                    C(0xA3, Who, Dir);
                    if (Who2 >= 0) C(0xA3, Who2, Dir);
                    C(0x89, Who);
                    if (Who2 >= 0) C(0x89, Who2);
                }
                break;
            case L2BeatKind.Face: C(0x7A, Who, FaceFrame[Dir & 3]); break;
            case L2BeatKind.Act: C(0xA3, Who, Value); C(0x89, Who); break;
            case L2BeatKind.Say:
                // a line rebuilt (another speaker) keeps how it ends: 00 ends the event, 0B asks, 01 goes on
                built.Add(new L2Step { Bytes = (byte[])Opener.Clone(), IsText = true, Text = Text, End = Steps.FirstOrDefault(x => x.IsText)?.End ?? 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" });
                break;
            case L2BeatKind.Camera:
                if (AtOnce) C(0x3A, X, Y); else { C(0xC4, X, Y); C(0x41, 0x00, 0xFF); }
                break;
            case L2BeatKind.CameraBack: C(0x41, Value & 15, 0x00); break;
            case L2BeatKind.FadeOut: C(0x58, Value); C(0x76); break;
            case L2BeatKind.FadeIn: C(0x59, Value); C(0x76); break;
            case L2BeatKind.Music: C(0x6F, Value); break;
            case L2BeatKind.Sound: C(0x4C, Value); break;
            case L2BeatKind.Wait: C(0x37, Math.Clamp(Value, 1, 255)); break;
            case L2BeatKind.Shake: C(0x5A, 0x00, Value, Value2); break;
            case L2BeatKind.StopShake: C(0x8A); break;
            case L2BeatKind.FlagOn: built.Add(new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.On, Value) }); break;
            case L2BeatKind.FlagOff: built.Add(new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.Off, Value) }); break;
            case L2BeatKind.GoTo: C(0x16, Value, Value2, Value3); break;
            case L2BeatKind.End: C(0x00); break;
            default: return Steps.Select(s => s).ToList();
        }
        return built;
    }

    // a new step of a kind with sensible values
    public static L2Beat New(L2BeatKind kind, int who = 0) => kind switch
    {
        L2BeatKind.Act => new L2Beat { Kind = kind, Who = who, Value = 0x64 },
        L2BeatKind.Say => new L2Beat { Kind = kind, Who = who, Opener = new byte[] { 0x61 }, Text = "..." },
        L2BeatKind.CameraBack => new L2Beat { Kind = kind, Value = 2 },
        L2BeatKind.FadeOut or L2BeatKind.FadeIn => new L2Beat { Kind = kind, Value = 0x13 },
        L2BeatKind.Wait => new L2Beat { Kind = kind, Value = 60 },
        L2BeatKind.Shake => new L2Beat { Kind = kind, Value = 2, Value2 = 0x60 },
        L2BeatKind.Music => new L2Beat { Kind = kind, Value = 0x11 },
        L2BeatKind.Sound => new L2Beat { Kind = kind, Value = 0x0E },
        L2BeatKind.GoTo => new L2Beat { Kind = kind, Value = 0x03, Value2 = 0x01, Value3 = 0x01 },
        _ => new L2Beat { Kind = kind, Who = who },
    };
}

// where everyone is at one moment of a cutscene (for the stage)
public sealed class L2StageState
{
    public sealed class Actor
    {
        public int Id { get; init; }
        public int X { get; set; } = -1;
        public int Y { get; set; } = -1;
        // 0 up, 1 down, 2 left, 3 right
        public int Dir { get; set; } = 1;
        public bool Visible { get; set; }
        public int Sprite { get; set; } = -1;
        // pixels the picture is drawn off its place ("Draw a character shifted", 6C, set not added)
        public int ShiftX { get; set; }
        public int ShiftY { get; set; }
        // pixels the character stands off its tile ("Nudge a character", 8B / C0, added up, gone once it's put on a
        // tile or point)
        public int NudgeX { get; set; }
        public int NudgeY { get; set; }
        public bool Placed => X >= 0 && Y >= 0;
        public Actor Copy() => new() { Id = Id, X = X, Y = Y, Dir = Dir, Visible = Visible, Sprite = Sprite, ShiftX = ShiftX, ShiftY = ShiftY, NudgeX = NudgeX, NudgeY = NudgeY };
    }

    public Dictionary<int, Actor> Actors { get; } = new();
    // the tile the camera is centred on
    public int CameraX { get; set; }
    public int CameraY { get; set; }
    public bool CameraOnLeader { get; set; } = true;
    // the text box shown at this step (null = none)
    public string? Speaker { get; set; }
    public string? Text { get; set; }
    public bool Dark { get; set; }
    public bool Shaking { get; set; }
    public int Song { get; set; } = -1;
    // what this step does in a few words (for the stage's caption)
    public string Caption { get; set; } = "";
    // roughly how long the step takes in the game, in frames (for playback)
    public int Frames { get; set; }
    // the actor that moved in this step (-1 = none) and where it came from
    public int MovedId { get; set; } = -1;
    // the tiles the moved actor went over in this step, in order (a path walk), for playback. null = a straight move
    public List<(int X, int Y)>? MovedRoute { get; set; }
    // map tiles copied so far (43, 87, B4): the "from" area (X, Y, W, H) put with its top-left on (DX, DY)
    public List<(int DX, int DY, int SX, int SY, int W, int H)> TileCopies { get; } = new();
    // the story flags that are on (the ones set at the start, then turned on and off by the steps)
    public HashSet<int> Flags { get; } = new();
    // event counters (variables) that aren't 0
    public Dictionary<int, int> Counters { get; } = new();
    // a walk in this step that the game stops (a wall, another character), and the tile it hits. null = none
    public string? Warning { get; set; }
    public int WarnX { get; set; } = -1;
    public int WarnY { get; set; } = -1;
    // a check or a jump, the way playback followed it: why it went where it went (null = not a check or jump)
    public string? BranchWhy { get; set; }
    // the step index it went to (-1 = the event ended, -2 = a step outside the event)
    public int BranchTo { get; set; } = -1;
    // it jumped (false = went on to the next step)
    public bool Jumped { get; set; }
    // the game's current speaker ($09AB, 08 and 10 speak as it) and the one a text ending 01 goes back to ($09AC,
    // the one talked to in a talk event). FF = none (L2Speakers)
    public int Talker { get; set; } = 0xFF;
    public int TalkSave { get; set; } = 0xFF;
    // a 48 placed the next text box (over nobody). the party is lined up (a dungeon only shows the leader before
    // 3D), null = not known yet (the first step reads the map)
    public bool BoxFixed { get; set; }
    public bool? LinedUp { get; set; }

    public L2StageState Copy()
    {
        var copy = new L2StageState { CameraX = CameraX, CameraY = CameraY, CameraOnLeader = CameraOnLeader, Dark = Dark, Shaking = Shaking, Song = Song, Talker = Talker, TalkSave = TalkSave, BoxFixed = BoxFixed, LinedUp = LinedUp };
        foreach (var kv in Actors) copy.Actors[kv.Key] = kv.Value.Copy();
        copy.Flags.UnionWith(Flags);
        foreach (var kv in Counters) copy.Counters[kv.Key] = kv.Value;
        copy.TileCopies.AddRange(TileCopies);
        return copy;
    }
}

// what the stage knows of the map: its size, collision (the upper layer's cell flags) and preset points
public sealed class L2StageMap
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    private byte[] _flags = Array.Empty<byte>();
    public Dictionary<int, (int X, int Y)> Points { get; private init; } = new();
    // the walk area of each character with a character start (map info +10: x1, y1 to x2, y2, the right and bottom
    // edges left out). the game refuses a step out of it
    public Dictionary<int, (int X1, int Y1, int X2, int Y2)> Areas { get; private init; } = new();
    // the map's paths (what "A character walks a path" walks), by id
    public Dictionary<int, L2Paths.Path> Paths { get; private init; } = new();
    // the map's areas for tile copies (map info list +14: id, x1, y1, x2, y2, x2 and y2 past the end)
    public Dictionary<int, (int X1, int Y1, int X2, int Y2)> TileAreas { get; private init; } = new();

    public static L2StageMap Read(RomBuffer rom, int map)
    {
        var paths = new Dictionary<int, L2Paths.Path>();
        try { foreach (var path in L2Paths.Read(rom, map)) paths.TryAdd(path.Id, path); } catch (Exception) { }
        var points = new Dictionary<int, (int, int)>();
        try { foreach (var point in L2StoryClear.PresetPoints(rom, map)) points.TryAdd(point.Id, (point.X, point.Y)); } catch (Exception) { }
        var tileAreas = new Dictionary<int, (int, int, int, int)>();
        try { foreach (var (_, area) in L2MapInfo.Records(rom, map, L2MapInfo.Areas, 5)) tileAreas.TryAdd(area[0], (area[1], area[2], area[3], area[4])); } catch (Exception) { }
        var areas = new Dictionary<int, (int, int, int, int)>();
        try
        {
            // characters hidden when the map is shown (cutscene characters) walked out of a one-tile area in the game,
            // so only the ones standing on the map from the start are kept in
            var hidden = L2Cutscene.HiddenWhenShown(rom, map);
            foreach (var (_, start) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
                if (!hidden.Contains(0x4F + start[0])) areas.TryAdd(0x4F + start[0], (start[3], start[4], start[5], start[6]));
        }
        catch (Exception) { }
        if (map < 2) return new L2StageMap { Points = points, Areas = areas, Paths = paths, TileAreas = tileAreas };   // the world maps: no cell flags
        try
        {
            var mapData = L2Maps.Read(rom, map);
            var layer = mapData.Layers.Length > 1 ? mapData.Layers[1] : mapData.Layers[0];
            return new L2StageMap { Width = layer.Width, Height = layer.Height, _flags = layer.Flags, Points = points, Areas = areas, Paths = paths, TileAreas = tileAreas };
        }
        catch (Exception) { return new L2StageMap { Points = points, Areas = areas, Paths = paths, TileAreas = tileAreas }; }
    }

    // whether a character with a character start can step onto (x, y): inside its walk area (or it has none)
    public bool InArea(int id, int x, int y) =>
        !Areas.TryGetValue(id, out var area) || x >= area.X1 && x < area.X2 && y >= area.Y1 && y < area.Y2;

    private static readonly int[] Dx = { 0, 0, -1, 1 }, Dy = { -1, 1, 0, 0 };

    // what stops a step from (x, y) one tile in direction dir (0 up, 1 down, 2 left, 3 right): the edge of the map,
    // a block that can't be walked on (flag 80), a wall on the top (04) or left (08) edge of a block. null if the way
    // is clear (or the map has no collision here)
    public string? Wall(int x, int y, int dir)
    {
        if (Width == 0 || _flags.Length < Width * Height) return null;
        int nx = x + Dx[dir & 3], ny = y + Dy[dir & 3];
        if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) return "the edge of the map";
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        byte from = _flags[y * Width + x], to = _flags[ny * Width + nx];
        if ((to & 0x80) != 0) return "a wall";
        if ((dir & 3) == 0 && (from & 0x04) != 0 || (dir & 3) == 1 && (to & 0x04) != 0 || (dir & 3) == 2 && (from & 0x08) != 0 || (dir & 3) == 3 && (to & 0x08) != 0)
            return "a wall";
        return null;
    }
}

// lufia 2 cutscenes: a map's event seen as a scene on a stage. groups script steps into L2Beats, works out where
// everyone stands after each step (Simulate), makes new cutscenes along with how they start (Create), adds
// characters that only show up in a cutscene (AddCast) and points a new game at a cutscene for testing (SetTest)
public static class L2Cutscene
{
    // how a new cutscene starts
    public enum StartKind
    {
        // a scene of the map (+4 list): runs when arriving through "Go to another map" with its number (from another
        // cutscene), or as a new game's start
        Arrive,
        // a scene plus a few steps in the map's "on entering" event (+2 id 01) that go to it once, when a story flag
        // allows
        MapLoads,
        // stepping onto an area (map info +0C, event list +8)
        StepOn,
        // talking to a character (event list +6)
        Talk,
    }

    // reading steps as beats

    private static bool Is(L2Step step, int op, int length) => !step.IsText && step.Bytes.Length == length && step.Op == op;

    // the script's steps grouped into cutscene steps. a step another step jumps to always starts a group
    public static List<L2Beat> Beats(L2EventScript script)
    {
        var targets = new HashSet<L2Step>(script.Steps.SelectMany(step => step.Jumps).Where(j => j.Step != null).Select(j => j.Step!));
        var steps = script.Steps;
        var beats = new List<L2Beat>();
        int i = 0;
        bool Free(int k) => k < steps.Count && !targets.Contains(steps[k]);
        while (i < steps.Count)
        {
            var step = steps[i];
            L2Beat? beat = null;
            int used = 1;
            if (step.IsText)
                beat = new L2Beat { Kind = L2BeatKind.Say, Opener = (byte[])step.Bytes.Clone(), Text = step.Text, Who = SpeakerActor(step.Bytes) };
            else if (Is(step, 0x2F, 4) && Free(i + 1) && Is(steps[i + 1], 0x2D, 2) && steps[i + 1].Bytes[1] == step.Bytes[1])
            {
                beat = new L2Beat { Kind = L2BeatKind.Appear, Who = step.Bytes[1], X = step.Bytes[2], Y = step.Bytes[3] };
                used = 2;
                if (Free(i + 2) && Free(i + 3) && Is(steps[i + 2], 0xA3, 3) && steps[i + 2].Bytes[1] == step.Bytes[1] && steps[i + 2].Bytes[2] == 0x86 && Is(steps[i + 3], 0x89, 2) && steps[i + 3].Bytes[1] == step.Bytes[1])
                { beat.Flash = true; used = 4; }
            }
            else if (Is(step, 0x2D, 2))
            {
                beat = new L2Beat { Kind = L2BeatKind.Appear, Who = step.Bytes[1] };
                if (Free(i + 1) && Is(steps[i + 1], 0x2F, 4) && steps[i + 1].Bytes[1] == step.Bytes[1]) { beat.X = steps[i + 1].Bytes[2]; beat.Y = steps[i + 1].Bytes[3]; used = 2; }
                if (Free(i + used) && Free(i + used + 1) && Is(steps[i + used], 0xA3, 3) && steps[i + used].Bytes[1] == step.Bytes[1] && steps[i + used].Bytes[2] == 0x86
                    && Is(steps[i + used + 1], 0x89, 2) && steps[i + used + 1].Bytes[1] == step.Bytes[1])
                { beat.Flash = true; used += 2; }
            }
            else if (Is(step, 0x2E, 2)) beat = new L2Beat { Kind = L2BeatKind.Leave, Who = step.Bytes[1] };
            else if (Is(step, 0x99, 2)) beat = new L2Beat { Kind = L2BeatKind.Leave, Who = step.Bytes[1], Flash = true };
            else if (Is(step, 0xA3, 3) && step.Bytes[2] <= 3) (beat, used) = ReadWalk(steps, i, Free);
            else if (Is(step, 0xA3, 3) && Free(i + 1) && Is(steps[i + 1], 0x89, 2) && steps[i + 1].Bytes[1] == step.Bytes[1])
            { beat = new L2Beat { Kind = L2BeatKind.Act, Who = step.Bytes[1], Value = step.Bytes[2] }; used = 2; }
            else if (Is(step, 0x7A, 3) && Array.IndexOf(L2Beat.FaceFrame, (int)step.Bytes[2]) is var faceDir && faceDir >= 0)
                beat = new L2Beat { Kind = L2BeatKind.Face, Who = step.Bytes[1], Dir = faceDir };
            else if (Is(step, 0x3A, 3)) beat = new L2Beat { Kind = L2BeatKind.Camera, X = step.Bytes[1], Y = step.Bytes[2], AtOnce = true };
            else if (Is(step, 0xC4, 3) && Free(i + 1) && Is(steps[i + 1], 0x41, 3) && steps[i + 1].Bytes[2] == 0xFF)
            { beat = new L2Beat { Kind = L2BeatKind.Camera, X = step.Bytes[1], Y = step.Bytes[2] }; used = 2; }
            else if (Is(step, 0x41, 3) && step.Bytes[2] == 0x00 && step.Bytes[1] < 0x10) beat = new L2Beat { Kind = L2BeatKind.CameraBack, Value = step.Bytes[1] };
            else if ((Is(step, 0x58, 2) || Is(step, 0x59, 2)) && Free(i + 1) && Is(steps[i + 1], 0x76, 1))
            { beat = new L2Beat { Kind = step.Op == 0x58 ? L2BeatKind.FadeOut : L2BeatKind.FadeIn, Value = step.Bytes[1] }; used = 2; }
            else if (Is(step, 0x6F, 2)) beat = new L2Beat { Kind = L2BeatKind.Music, Value = step.Bytes[1] };
            else if (Is(step, 0x4C, 2)) beat = new L2Beat { Kind = L2BeatKind.Sound, Value = step.Bytes[1] };
            else if (Is(step, 0x37, 2)) beat = new L2Beat { Kind = L2BeatKind.Wait, Value = step.Bytes[1] };
            else if (Is(step, 0x5A, 4)) beat = new L2Beat { Kind = L2BeatKind.Shake, Value = step.Bytes[2], Value2 = step.Bytes[3] };
            else if (Is(step, 0x8A, 1)) beat = new L2Beat { Kind = L2BeatKind.StopShake };
            else if ((Is(step, 0x1A, 2) || Is(step, L2ExtraFlags.OpOn, 2)) && !step.IsText) beat = new L2Beat { Kind = L2BeatKind.FlagOn, Value = L2ExtraFlags.FlagOf(step.Bytes) };
            else if ((Is(step, 0x1B, 2) || Is(step, L2ExtraFlags.OpOff, 2)) && !step.IsText) beat = new L2Beat { Kind = L2BeatKind.FlagOff, Value = L2ExtraFlags.FlagOf(step.Bytes) };
            else if (Is(step, 0x16, 4)) beat = new L2Beat { Kind = L2BeatKind.GoTo, Value = step.Bytes[1], Value2 = step.Bytes[2], Value3 = step.Bytes[3] };
            else if (Is(step, 0x00, 1)) beat = new L2Beat { Kind = L2BeatKind.End };
            beat ??= new L2Beat { Kind = L2BeatKind.Other };
            beat.Steps.AddRange(steps.Skip(i).Take(used));
            beats.Add(beat);
            i += used;
        }
        return beats;
    }

    // "A3 a d (A3 b d) 89 a (89 b)" repeated with the same characters and direction
    private static (L2Beat, int) ReadWalk(List<L2Step> steps, int i, Func<int, bool> free)
    {
        int walker = steps[i].Bytes[1], dir = steps[i].Bytes[2];
        bool Cmd(int k, int op, int who, int act = -1) => k < steps.Count && Is(steps[k], op, op == 0x89 ? 2 : 3) && steps[k].Bytes[1] == who && (act < 0 || steps[k].Bytes[2] == act);
        // is there a second walker?
        int walker2 = -1;
        if (free(i + 1) && i + 1 < steps.Count && Is(steps[i + 1], 0xA3, 3) && steps[i + 1].Bytes[2] == dir && steps[i + 1].Bytes[1] != walker)
        {
            int other = steps[i + 1].Bytes[1];
            if (free(i + 2) && Cmd(i + 2, 0x89, walker) && free(i + 3) && Cmd(i + 3, 0x89, other)) walker2 = other;
        }
        int per = walker2 >= 0 ? 4 : 2;
        int count = 0, k0 = i;
        while (true)
        {
            int k = k0 + count * per;
            if (count > 0 && !free(k)) break;
            bool ok = walker2 >= 0
                ? Cmd(k, 0xA3, walker, dir) && free(k + 1) && Cmd(k + 1, 0xA3, walker2, dir) && free(k + 2) && Cmd(k + 2, 0x89, walker) && free(k + 3) && Cmd(k + 3, 0x89, walker2)
                : Cmd(k, 0xA3, walker, dir) && free(k + 1) && Cmd(k + 1, 0x89, walker);
            if (!ok) break;
            count++;
        }
        if (count == 0) return (new L2Beat { Kind = L2BeatKind.Other }, 1);   // no wait after it, so shown as is
        return (new L2Beat { Kind = L2BeatKind.Walk, Who = walker, Who2 = walker2, Dir = dir, Count = count }, count * per);
    }

    // the actor a text step's speaker is (-1 = narration or the one talked to)
    public static int SpeakerActor(byte[] opener) => opener.Length == 0 ? -1 : opener[0] switch
    {
        >= 0x61 and <= 0x67 => L2EventCommands.SpeakerOf61[opener[0] - 0x61],
        0x13 when opener.Length > 1 => opener[1],
        _ => -1,
    };

    // the text opener for an actor: a hero's own (61-67) or "a character speaks" (13 id)
    public static byte[] OpenerFor(int actor) => actor switch
    {
        >= 0 and < 7 => new[] { (byte)(0x61 + Array.IndexOf(L2EventCommands.SpeakerOf61, actor)) },
        _ => new byte[] { 0x13, (byte)actor },
    };

    // puts a beat's newly built steps in place of its old ones, jumps that went to any of the old steps go to the
    // first new one. steps keep their place in the rom when the commands keep their kind and size
    public static void Replace(L2EventScript script, L2Beat beat)
    {
        var fresh = beat.Build();
        var old = beat.Steps.ToList();
        if (fresh.Count == 0) return;
        if (old.Count == fresh.Count && old.Zip(fresh).All(p => p.First.IsText == p.Second.IsText && p.First.Bytes.Length == p.Second.Bytes.Length && (!p.First.IsText || p.First.Op == p.Second.Op)))
        {
            foreach (var (oldStep, newStep) in old.Zip(fresh)) { oldStep.Bytes = newStep.Bytes; if (oldStep.IsText) { oldStep.Text = newStep.Text; } }
            return;
        }
        int index = old.Count > 0 ? script.Steps.IndexOf(old[0]) : script.Steps.Count;
        foreach (var oldStep in old) script.Steps.Remove(oldStep);
        script.Steps.InsertRange(index, fresh);
        foreach (var step in script.Steps) foreach (var j in step.Jumps) if (j.Step != null && old.Contains(j.Step)) j.Step = fresh[0];
        beat.Steps.Clear();
        beat.Steps.AddRange(fresh);
    }

    // puts a new beat's steps before step index at of the script
    public static void Insert(L2EventScript script, int at, L2Beat beat)
    {
        var fresh = beat.Build();
        script.Steps.InsertRange(Math.Clamp(at, 0, script.Steps.Count), fresh);
        beat.Steps.Clear();
        beat.Steps.AddRange(fresh);
    }

    // takes a beat's steps out, jumps to them go to the step after it
    public static void Remove(L2EventScript script, L2Beat beat)
    {
        if (beat.Steps.Count == 0) return;
        int last = script.Steps.IndexOf(beat.Steps[^1]);
        var next = script.Steps.Skip(last + 1).FirstOrDefault(s => !beat.Steps.Contains(s));
        // the last step got taken out while a jump leads to it (the End a "played once" check jumps to), so put a new End there
        if (next == null && script.Steps.Any(s => s.Jumps.Any(j => j.Step != null && beat.Steps.Contains(j.Step))))
        {
            next = new L2Step { Bytes = new byte[] { 0x00 } };
            script.Steps.Insert(last + 1, next);
        }
        foreach (var oldStep in beat.Steps) script.Steps.Remove(oldStep);
        foreach (var s in script.Steps) foreach (var j in s.Jumps) if (j.Step != null && beat.Steps.Contains(j.Step)) j.Step = next;
    }

    // the stage

    // the map's characters as the map loads: character starts (with the sprite their create gives), characters the
    // start-up script makes without a start (not placed), and the ones it hides (cutscene characters)
    public static L2StageState Initial(RomBuffer rom, int map, int leaderX, int leaderY, IEnumerable<L2Persistent.Place>? persistent = null)
    {
        var state = new L2StageState();
        var sprites = Creates(rom, map);
        foreach (var (_, record) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
        {
            int id = 0x4F + record[0];
            if (!sprites.TryGetValue(id, out int sprite)) continue;   // a start no create makes: not on the map
            state.Actors[id] = new L2StageState.Actor { Id = id, X = record[1], Y = record[2], Visible = true, Sprite = sprite };
        }
        foreach (var (id, sprite) in sprites) if (!state.Actors.ContainsKey(id)) state.Actors[id] = new L2StageState.Actor { Id = id, Visible = false, Sprite = sprite };
        foreach (int id in HiddenWhenShown(rom, map)) if (state.Actors.TryGetValue(id, out var actor)) actor.Visible = false;
        // persistent characters (10-4F) that stand on this map, on their character start (the first place listed)
        if (persistent != null)
        {
            var starts = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Select(r => r.Bytes).ToList();
            foreach (var place in persistent.Where(place => place.Map == map))
                if (!state.Actors.ContainsKey(place.Id) && starts.FirstOrDefault(record => record[0] == place.Spot) is { } record)
                    state.Actors[place.Id] = new L2StageState.Actor { Id = place.Id, X = record[1], Y = record[2], Visible = true, Sprite = place.Sprite };
        }
        state.Actors[0] = new L2StageState.Actor { Id = 0, X = leaderX, Y = leaderY, Visible = true, Sprite = 0 };
        state.CameraX = leaderX; state.CameraY = leaderY;
        return state;
    }

    // the stage the way the game sets the map up with story flags flags on: only the characters the start-up script
    // makes with those flags (its checks followed like the game does: Treasure Sword Shrine 4F only makes its two
    // characters with flag 28 on and 2C off), then the "When the map is shown" event (+2 00) played with them (its
    // puts and hides). the others the script can make are on the stage hidden and nowhere, same as in the game.
    // checked against the game's own scenes and step-on events, 2026-11-10
    public static L2StageState Initial(RomBuffer rom, int map, int leaderX, int leaderY, IEnumerable<L2Persistent.Place>? persistent,
        IEnumerable<int> flags, IReadOnlyDictionary<int, int>? counters = null)
    {
        var state = Initial(rom, map, leaderX, leaderY, persistent);
        state.Flags.UnionWith(flags);
        if (counters != null) foreach (var kv in counters) state.Counters[kv.Key] = kv.Value;
        try
        {
            int init = L2EventBlocks.InitScript(rom, map);
            var initScript = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
            var made = Run(rom, map, state, initScript.Steps, _ => "", 0, 600).Select(r => initScript.Steps[r.Step])
                .Where(step => Is(step, 0x68, 3) || Is(step, 0x7B, 3)).Select(step => (int)step.Bytes[1]).ToHashSet();
            foreach (int id in Creates(rom, map).Keys)
                if (!made.Contains(id) && state.Actors.TryGetValue(id, out var actor)) { actor.Visible = false; actor.X = actor.Y = -1; }
            // HiddenWhenShown hid these whatever the flags: show them again, the event below hides them if it does
            foreach (int id in HiddenWhenShown(rom, map))
                if (made.Contains(id) && state.Actors.TryGetValue(id, out var actor) && actor.Placed) actor.Visible = true;
            int block = L2Events.Block(rom, map);
            if (block >= 0 && L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 0) is { } shown)
            {
                var shownScript = L2EventScript.Read(rom, block + shown.Offset, block);
                var run = Run(rom, map, state, shownScript.Steps, _ => "", 0, 600);
                if (run.Count > 0)
                {
                    var after = run[^1].After;
                    after.Caption = ""; after.BranchWhy = null; after.BranchTo = -1; after.Jumped = false;
                    after.Text = null; after.Speaker = null; after.Warning = null; after.MovedId = -1; after.MovedRoute = null; after.Frames = 0;
                    after.CameraX = leaderX; after.CameraY = leaderY; after.CameraOnLeader = true;
                    return after;
                }
            }
        }
        catch (Exception) { }
        return state;
    }

    // the characters the map's start-up script makes (68 / 7B, every step it can reach) and their sprites (our
    // numbers). the first create of an id wins
    public static Dictionary<int, int> Creates(RomBuffer rom, int map)
    {
        var sprites = new Dictionary<int, int>();
        try
        {
            int init = L2EventBlocks.InitScript(rom, map);
            foreach (var step in L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init)).Steps)
                if ((Is(step, 0x68, 3) || Is(step, 0x7B, 3)) && step.Bytes[1] >= 0x10)
                    sprites.TryAdd(step.Bytes[1], L2Sprites.ToEditor(rom, map, step.Bytes[2]));
        }
        catch (Exception) { }
        return sprites;
    }

    private static readonly int[] Dx = { 0, 0, -1, 1 }, Dy = { -1, 1, 0, 0 };

    // the stage after each step of the script, read top to bottom (jumps aren't followed, Run follows them).
    // element 0 is the stage before the first step, element i + 1 after step i
    public static List<L2StageState> Simulate(RomBuffer rom, int map, L2StageState start, IReadOnlyList<L2Step> steps, Func<int, string> name)
    {
        var stageMap = L2StageMap.Read(rom, map);
        var speakers = new L2Speakers(rom, map);
        var list = new List<L2StageState> { start.Copy() };
        var current = start.Copy();
        current.LinedUp ??= !speakers.Dungeon;
        for (int i = 0; i < steps.Count; i++)
        {
            current = Apply(current, steps, i, stageMap, name, speakers);
            list.Add(current);
        }
        return list;
    }

    // one step of the script the way playback runs it: the step index and the stage after it (with
    // L2StageState.BranchWhy for checks and jumps)
    public sealed record RunStep(int Step, L2StageState After);

    // plays the script the way the game would: from the first step, following jumps and checks with the story flags
    // and counters of start (and the ones the steps set on the way). questions take answer answer (0-based). stops at
    // the end of the event, a step that leaves it, or after limit steps (a loop)
    public static List<RunStep> Run(RomBuffer rom, int map, L2StageState start, IReadOnlyList<L2Step> steps, Func<int, string> name, int answer = 0, int limit = 3000)
    {
        var stageMap = L2StageMap.Read(rom, map);
        var speakers = new L2Speakers(rom, map);
        var run = new List<RunStep>();
        var current = start.Copy();
        current.LinedUp ??= !speakers.Dungeon;
        int i = 0;
        while (i >= 0 && i < steps.Count && run.Count < limit)
        {
            current = Apply(current, steps, i, stageMap, name, speakers);
            int next = Next(steps, i, current, answer);
            run.Add(new RunStep(i, current));
            i = next;
        }
        return run;
    }

    // where the event goes after step i (given the stage after it: flags and counters). sets the state's branch fields
    // for a check or a jump. -1 = it ends
    private static int Next(IReadOnlyList<L2Step> steps, int i, L2StageState state, int answer)
    {
        var step = steps[i];
        var bytes = step.Bytes;
        int Target(int k)
        {
            if (k >= step.Jumps.Count || step.Jumps[k].Step is not { } t) return -2;
            for (int q = 0; q < steps.Count; q++) if (ReferenceEquals(steps[q], t)) return q;
            return -2;
        }
        int Go(int k, string why) { state.BranchWhy = why; state.BranchTo = Target(k); state.Jumped = true; return state.BranchTo; }
        int On(string why) { state.BranchWhy = why; state.BranchTo = i + 1 < steps.Count ? i + 1 : -1; state.Jumped = false; return state.BranchTo; }
        bool Flag(int f) => state.Flags.Contains(f);
        int Counter(int c) => state.Counters.TryGetValue(c, out int v) ? v : 0;
        switch (step.Op)
        {
            case 0x10 when step.IsText && bytes.Length > 1 && bytes[1] > 0:
            {
                int k = Math.Clamp(answer, 0, bytes[1] - 1);
                return Go(k, $"answer {k + 1} is picked");
            }
            case 0x1C when !step.IsText: return Go(0, "always");
            case 0x15 or L2ExtraFlags.OpIfOn when !step.IsText && bytes.Length >= 2:
            {
                int f = L2ExtraFlags.FlagOf(bytes);
                return Flag(f) ? Go(0, $"story flag {L2ExtraFlags.Number(f)} is on") : On($"story flag {L2ExtraFlags.Number(f)} is off");
            }
            case 0x6A or L2ExtraFlags.OpIfOff when !step.IsText && bytes.Length >= 2:
            {
                int f = L2ExtraFlags.FlagOf(bytes);
                return !Flag(f) ? Go(0, $"story flag {L2ExtraFlags.Number(f)} is off") : On($"story flag {L2ExtraFlags.Number(f)} is on");
            }
            case 0x12 when !step.IsText && bytes.Length >= 3 && bytes[2] > 0:
            {
                int c = Counter(bytes[1]), k = Math.Min(c, bytes[2] - 1);
                state.Counters[bytes[1]] = Math.Min(255, c + 1);
                return Go(k, $"counter {bytes[1]:X2} is {c}: choice {k + 1}");
            }
            case 0x14 when !step.IsText:
            {
                bool result = false;
                int jump = 0;
                var why = new List<string>();
                foreach (var c in L2Conditions.Parse(bytes))
                {
                    int at = c.At;
                    int join = c.Code & 0xC0;
                    bool v;
                    switch (c.Kind)
                    {
                        case L2Conditions.KFlag when at + 1 < bytes.Length:
                        {
                            int f = L2ExtraFlags.CheckFlag(c.Code, bytes[at + 1]);
                            v = Flag(f) == ((c.Code & 1) == 0);
                            why.Add($"story flag {L2ExtraFlags.Number(f)} is {(Flag(f) ? "on" : "off")}");
                            break;
                        }
                        case L2Conditions.KCounter when at + 2 < bytes.Length:
                        {
                            int val = Counter(bytes[at + 1]), w = bytes[at + 2];
                            v = (c.Code & 7) switch { 0 => val == w, 1 => val != w, 2 => val >= w, 3 => val <= w, 4 => val > w, _ => val < w };
                            why.Add($"counter {bytes[at + 1]:X2} is {val}");
                            break;
                        }
                        case L2Conditions.KIfTrue:
                            if (result) return Go(jump, string.Join(", ", why) + ": so");
                            jump++;
                            continue;
                        case L2Conditions.KIfFalse:
                            if (!result) return Go(jump, string.Join(", ", why) + ": not so");
                            jump++;
                            continue;
                        default:
                            // items, HP, spells, levels...: the stage doesn't know those
                            v = false; join = 0;
                            why.Add("a check of the party (taken as not so)");
                            break;
                    }
                    result = join switch { 0x40 => result || v, 0x80 => result && v, _ => v };
                }
                return On((why.Count > 0 ? string.Join(", ", why) : "the check") + ": goes on");
            }
        }
        if (step.EndsFlow) return -1;
        return i + 1;
    }

    // the stage after step i of steps, starting from current
    private static L2StageState Apply(L2StageState current, IReadOnlyList<L2Step> steps, int i, L2StageMap stageMap, Func<int, string> name, L2Speakers speakers)
    {
        var step = steps[i];
        var points = stageMap.Points;
        var state = current.Copy();
        state.Speaker = null; state.Text = null; state.MovedId = -1; state.MovedRoute = null;
        state.Frames = 6;
        L2StageState.Actor A(int id)
        {
            if (!state.Actors.TryGetValue(id, out var actor)) state.Actors[id] = actor = new L2StageState.Actor { Id = id, Sprite = id < 7 ? id : -1 };
            return actor;
        }
        var bytes = step.Bytes;
        int op = step.Op;
        if (step.IsText)
        {
            // same way the game picks the speaker (L2Speakers): 13 / 61-67 name one, 08 and 10 speak as the current one
            if (op is 0x13 or (>= 0x61 and <= 0x67)) state.Talker = SpeakerActor(bytes);
            int who = state.Talker;
            int drawnAs = who is >= 0 and < 7 && state.Actors.TryGetValue(who, out var spk) && spk.Sprite >= 0 && spk.Sprite != who ? spk.Sprite : -1;
            string label = who == 0xFF ? "" : speakers.Label(who, drawnAs, sprite: false);
            if (who is >= 1 and < 7 && state.LinedUp == false && op != 0x9E) label = L2Speakers.OverLeader(label);
            if (state.BoxFixed && op == 0x08) label = L2Speakers.FixedBox;
            else if (state.BoxFixed && op != 0x9E && label.Length > 0) label += " (off-screen: the box sits where the step before puts it)";
            state.Speaker = op switch { 0x9E => "", 0x10 => label.Length > 0 ? label + " asks" : "question", _ => label.Length > 0 ? label : "no speaker set" };
            state.Text = step.Text;
            state.Caption = op == 0x9E ? "Narration" : label.Length == 0 ? (op == 0x10 ? "A question" : "Text with no speaker set (the box goes over the last character the game found)") : $"{label} speaks";
            state.Frames = 60 + step.Text.Length * 2;
            if (step.End == 0x01 && state.TalkSave != 0xFF) state.Talker = state.TalkSave;
            state.BoxFixed = false;
        }
        else switch (op)
        {
            case 0x48: state.BoxFixed = true; state.Caption = "The next text box is put in a fixed place (over nobody)"; break;
            case 0x3D: state.LinedUp = true; state.Caption = "The party lines up behind the leader"; break;
            case 0x34 when bytes.Length >= 3: { var actor = A(bytes[1]); actor.Sprite = L2Sprites.ToEditor(speakers.Rom, speakers.Map, bytes[2]); state.Caption = $"{name(bytes[1])} is shown as sprite {L2Sprites.Name(actor.Sprite)}"; break; }
            case 0x2F when bytes.Length >= 4: { var actor = A(bytes[1]); actor.X = bytes[2]; actor.Y = bytes[3]; actor.NudgeX = actor.NudgeY = 0; state.Caption = $"{name(bytes[1])} is put on ({bytes[2]}, {bytes[3]})"; break; }
            case 0x30 when bytes.Length >= 3 && points.TryGetValue(bytes[2], out var p): { var actor = A(bytes[1]); actor.X = p.X; actor.Y = p.Y; actor.NudgeX = actor.NudgeY = 0; state.Caption = $"{name(bytes[1])} is put on point {bytes[2]}"; break; }
            case 0x98 when bytes.Length >= 3: { var actor = A(bytes[1]); if (points.TryGetValue(bytes[2], out var p2)) { actor.X = p2.X; actor.Y = p2.Y; actor.NudgeX = actor.NudgeY = 0; } actor.Visible = true; state.Caption = $"{name(bytes[1])} appears"; state.Frames = 30; break; }
            case 0x6C when bytes.Length >= 4:
            {
                var actor = A(bytes[1]); actor.ShiftX = (sbyte)bytes[2]; actor.ShiftY = (sbyte)bytes[3];
                state.Caption = actor.ShiftX == 0 && actor.ShiftY == 0 ? $"{name(bytes[1])} is drawn on its place again" : $"{name(bytes[1])} is drawn {Pixels(actor.ShiftX, actor.ShiftY)} off its place";
                break;
            }
            case 0x8B or 0xC0 when bytes.Length >= 4:
            {
                var actor = A(bytes[1]); actor.NudgeX += (sbyte)bytes[2]; actor.NudgeY += (sbyte)bytes[3];
                if (op == 0xC0 && actor.Placed)
                {
                    // the game settles it on the tile its pixel place is in
                    int px = actor.X * 16 + actor.NudgeX, py = actor.Y * 16 + actor.NudgeY;
                    actor.X = (int)Math.Floor(px / 16.0); actor.Y = (int)Math.Floor(py / 16.0);
                    actor.NudgeX = px - actor.X * 16; actor.NudgeY = py - actor.Y * 16;
                }
                state.Caption = $"{name(bytes[1])} is nudged {Pixels((sbyte)bytes[2], (sbyte)bytes[3])}";
                break;
            }
            case 0x2D when bytes.Length >= 2: A(bytes[1]).Visible = true; state.Caption = $"{name(bytes[1])} appears"; break;
            case 0x2E or 0x99 or 0x9D when bytes.Length >= 2: A(bytes[1]).Visible = false; state.Caption = $"{name(bytes[1])} leaves"; state.Frames = op == 0x2E ? 6 : 30; break;
            // a story character sent to a map: gone from this one at once (it stands on the new map from the next
            // time that map loads, this one too: $80:B2B1)
            case 0x35 when bytes.Length >= 4 && state.Actors.TryGetValue(bytes[1], out var sent):
                sent.Visible = false;
                state.Caption = L2Persistent.IsAway(bytes[2], bytes[3]) ? $"{name(bytes[1])} is sent away" : $"{name(bytes[1])} is sent to map {bytes[2]:X2} (there from the next time it loads)";
                break;
            case 0x31 when bytes.Length >= 3 && bytes[2] is >= 1 and <= 4: A(bytes[1]).Dir = new[] { 0, 0, 1, 2, 3 }[bytes[2]]; state.Caption = $"{name(bytes[1])} turns"; break;
            case 0x7A when bytes.Length >= 3 && bytes[2] < 8: A(bytes[1]).Dir = new[] { 1, 2, 0, 3 }[bytes[2] / 2]; state.Caption = $"{name(bytes[1])} turns"; break;
            case 0xA3 when bytes.Length >= 3:
            {
                var actor = A(bytes[1]);
                int act = bytes[2], dir = act & 3;
                int move = act switch
                {
                    <= 0x03 or (>= 0x94 and <= 0x97) or (>= 0x74 and <= 0x77) or (>= 0x54 and <= 0x5B) or (>= 0x88 and <= 0x8B) or (>= 0x68 and <= 0x6B) => 1,
                    >= 0x81 and <= 0x84 => 1,
                    >= 0x3C and <= 0x3F => 2,
                    >= 0x50 and <= 0x53 => 6,
                    _ => 0,
                };
                if (act is >= 0x81 and <= 0x84) dir = act - 0x81;
                if (act is <= 0x07 or (>= 0x18 and <= 0x1B) or (>= 0x94 and <= 0x97)) actor.Dir = dir;
                if (act is >= 0x81 and <= 0x84) actor.Dir = dir ^ 1;
                // a plain step (A3 00-03) stops at walls and other characters, same as in the game
                if (move > 0 && actor.Placed && act <= 0x03 && actor.Visible)
                {
                    string? hit = stageMap.Wall(actor.X, actor.Y, dir);
                    int nx = actor.X + Dx[dir], ny = actor.Y + Dy[dir];
                    if (hit == null && !stageMap.InArea(actor.Id, nx, ny) && stageMap.Areas.TryGetValue(actor.Id, out var ar))
                        hit = $"the edge of its walk area ({ar.X1}, {ar.Y1})-({ar.X2 - 1}, {ar.Y2 - 1}) (its character start on the Maps page)";
                    if (hit == null)
                    {
                        var other = state.Actors.Values.FirstOrDefault(o => o.Id != actor.Id && o.Visible && o.X == nx && o.Y == ny && !MovesAway(steps, i, o.Id, dir));
                        if (other != null) hit = name(other.Id);
                    }
                    if (hit != null)
                    {
                        state.Warning = $"{Cap(name(bytes[1]))} can't walk {L2Beat.DirNames[dir]} from ({actor.X}, {actor.Y}): {hit} is in the way, so the game stops the walk there.";
                        state.WarnX = nx; state.WarnY = ny;
                        move = 0;
                    }
                }
                if (move > 0 && actor.Placed) { actor.X += Dx[dir] * move; actor.Y += Dy[dir] * move; state.MovedId = actor.Id; }
                if (act is 0x85 or 0x61 or 0x5F or 0x5D or 0x63) actor.Visible = false;
                if (act is 0x86 or 0x62 or 0x60) actor.Visible = true;
                state.Caption = state.Warning != null ? $"{name(bytes[1])} is stopped" : move > 0 ? $"{name(bytes[1])} walks {L2Beat.DirNames[dir]}" : $"{name(bytes[1])}: {L2EventCommands.Actions.FirstOrDefault(x => x.Item1 == act).Item2 ?? $"action {act:X2}"}";
                state.Frames = move > 0 ? 16 * move : 24;
                break;
            }
            case 0x89: state.Frames = 0; state.Caption = ""; break;
            case 0x33 when bytes.Length >= 3:
            {
                var actor = A(bytes[2]);
                if (!stageMap.Paths.TryGetValue(bytes[1], out var path)) { state.Caption = $"{name(bytes[2])} walks path {bytes[1]} (the map has no such path)"; break; }
                if (!actor.Placed) { state.Caption = $"{name(bytes[2])} walks path {bytes[1]} (but isn't on the map)"; break; }
                var (px, py, pd, tiles, frames) = L2Paths.Play(path.Program, actor.X, actor.Y, actor.Dir);
                // a step whose way is blocked waits in the game until it's free. a wall never frees up, so the walk hangs there
                for (int k = 0; k < tiles.Count; k++)
                {
                    var (fx, fy) = k == 0 ? (actor.X, actor.Y) : (tiles[k - 1].X, tiles[k - 1].Y);
                    if (Math.Abs(tiles[k].X - fx) + Math.Abs(tiles[k].Y - fy) != 1) continue;   // put somewhere (18)
                    if (stageMap.Wall(fx, fy, tiles[k].Dir) is { } hit)
                    {
                        state.Warning = $"{Cap(name(bytes[2]))} walks path {bytes[1]} into {hit} at ({tiles[k].X}, {tiles[k].Y}): in the game it waits there for good (a path doesn't go round walls).";
                        state.WarnX = tiles[k].X; state.WarnY = tiles[k].Y;
                        break;
                    }
                }
                if (px != actor.X || py != actor.Y) { state.MovedId = actor.Id; state.MovedRoute = new[] { (actor.X, actor.Y) }.Concat(tiles.Select(t => (t.X, t.Y))).ToList(); }
                actor.X = px; actor.Y = py; actor.Dir = pd; actor.NudgeX = actor.NudgeY = 0;
                bool goesOn = (path.Flags & L2Paths.FlagEventGoesOn) != 0;
                state.Caption = $"{name(bytes[2])} walks path {bytes[1]}" + (goesOn ? " (the event goes on meanwhile)" : "");
                state.Frames = goesOn ? 6 : Math.Max(6, frames);
                if ((path.Flags & L2Paths.FlagCameraStays) == 0) state.CameraOnLeader = false;
                if ((path.Flags & L2Paths.FlagCameraStays) == 0) { state.CameraX = px; state.CameraY = py; }
                break;
            }
            case 0x3A when bytes.Length >= 3: state.CameraX = bytes[1]; state.CameraY = bytes[2]; state.CameraOnLeader = false; state.Caption = "The camera jumps"; break;
            case 0xC4 when bytes.Length >= 3: state.CameraX = bytes[1]; state.CameraY = bytes[2]; state.CameraOnLeader = false; state.Caption = "The camera scrolls"; state.Frames = 40; break;
            case 0x3B when bytes.Length >= 2 && points.TryGetValue(bytes[1], out var p3): state.CameraX = p3.X; state.CameraY = p3.Y; state.CameraOnLeader = false; break;
            case 0x41 when bytes.Length >= 3:
            {
                int form = L2EventCommands.CameraForm(bytes);
                if (form == 0) { state.CameraOnLeader = true; state.Caption = "The camera goes back to the leader"; state.Frames = 30; }
                else if (form == 1) { int dir = (bytes[1] >> 4) - 1; if (dir is >= 0 and <= 3) { state.CameraX += Dx[dir] * bytes[2]; state.CameraY += Dy[dir] * bytes[2]; state.CameraOnLeader = false; state.Frames = 30; } }
                else if (form == 3 && bytes.Length >= 4) { state.CameraX = bytes[2] + 1 + 8; state.CameraY = bytes[3] + 7; state.CameraOnLeader = false; }
                else if (form == 2 && points.TryGetValue(bytes[2], out var p4)) { state.CameraX = p4.X; state.CameraY = p4.Y; state.CameraOnLeader = false; }
                break;
            }
            case 0x75: state.CameraOnLeader = true; break;
            case 0x58: state.Dark = true; state.Caption = "Fade to black"; state.Frames = 30; break;
            case 0x59 or 0xA4: state.Dark = false; state.Caption = "Fade in"; state.Frames = 30; break;
            case 0x3E: state.Dark = true; break;
            case 0x3F: state.Dark = false; break;
            case 0x6F or 0x4B when bytes.Length >= 2: state.Song = bytes[1]; state.Caption = "Music"; break;
            case 0x72: state.Song = -1; break;
            case 0x5A: state.Shaking = true; state.Caption = "The screen shakes"; break;
            // the "from" area's tiles, at its size, go with their top-left on the "onto" area's first tile ($80:B76A)
            case 0x43 or 0x87 or 0xB4 when bytes.Length >= 3:
                if (stageMap.TileAreas.TryGetValue(bytes[1], out var onto) && stageMap.TileAreas.TryGetValue(bytes[2], out var from))
                {
                    state.TileCopies.Add((onto.X1, onto.Y1, from.X1, from.Y1, from.X2 - from.X1, from.Y2 - from.Y1));
                    state.Caption = $"The map changes at ({onto.X1}, {onto.Y1})";
                }
                else state.Caption = "The map changes (an area isn't on this map)";
                break;
            case 0x8A or 0x56: state.Shaking = false; break;
            case 0x37 when bytes.Length >= 2: state.Frames = bytes[1]; state.Caption = "Wait"; break;
            case 0x38 when bytes.Length >= 2: state.Frames = bytes[1] * 60; state.Caption = "Wait"; break;
            case 0x1A or L2ExtraFlags.OpOn when bytes.Length >= 2: state.Flags.Add(L2ExtraFlags.FlagOf(bytes)); state.Caption = $"Story flag {L2ExtraFlags.Number(L2ExtraFlags.FlagOf(bytes))} on"; break;
            case 0x1B or L2ExtraFlags.OpOff when bytes.Length >= 2: state.Flags.Remove(L2ExtraFlags.FlagOf(bytes)); state.Caption = $"Story flag {L2ExtraFlags.Number(L2ExtraFlags.FlagOf(bytes))} off"; break;
            case 0x1D when bytes.Length >= 3: state.Counters[bytes[1]] = bytes[2]; state.Caption = $"Counter {bytes[1]:X2} = {bytes[2]}"; break;
            case 0x1E when bytes.Length >= 3: state.Counters[bytes[1]] = Math.Min(255, (state.Counters.TryGetValue(bytes[1], out int c1) ? c1 : 0) + bytes[2]); state.Caption = $"Counter {bytes[1]:X2} + {bytes[2]}"; break;
            case 0x1F when bytes.Length >= 3: state.Counters[bytes[1]] = Math.Max(0, (state.Counters.TryGetValue(bytes[1], out int c2) ? c2 : 0) - bytes[2]); state.Caption = $"Counter {bytes[1]:X2} - {bytes[2]}"; break;
            case 0x16: state.Caption = "Go to another map"; state.Dark = true; break;
            case 0x00 or 0x42: state.Caption = "End"; state.Dark = false; state.CameraOnLeader = true; break;
        }
        if (state.CameraOnLeader && state.Actors.TryGetValue(0, out var lead) && lead.Placed) { state.CameraX = lead.X; state.CameraY = lead.Y; }
        return state;
    }

    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    // "8 pixels left and 4 down" for a pixel shift
    private static string Pixels(int x, int y)
    {
        var parts = new List<string>();
        if (x != 0) parts.Add($"{Math.Abs(x)} pixel{(Math.Abs(x) == 1 ? "" : "s")} {(x < 0 ? "left" : "right")}");
        if (y != 0) parts.Add($"{Math.Abs(y)} pixel{(Math.Abs(y) == 1 ? "" : "s")} {(y < 0 ? "up" : "down")}");
        return parts.Count == 0 ? "by 0 pixels" : string.Join(" and ", parts);
    }

    // whether actor id steps in direction dir in the same round as step i (the A3 commands next to it, before the
    // waits): walking together in a line, the one in front moves out of the way
    private static bool MovesAway(IReadOnlyList<L2Step> steps, int i, int id, int dir)
    {
        bool Step(L2Step x) => !x.IsText && x.Op == 0xA3 && x.Bytes.Length >= 3 && x.Bytes[2] <= 0x03;
        for (int k = i + 1; k < steps.Count && Step(steps[k]); k++) if (steps[k].Bytes[1] == id && steps[k].Bytes[2] == dir) return true;
        for (int k = i - 1; k >= 0 && Step(steps[k]); k--) if (steps[k].Bytes[1] == id && steps[k].Bytes[2] == dir) return true;
        return false;
    }

    // the story flags and counters the script checks (15, 6A, 14 and 12), for the stage's starting state
    public static (SortedSet<int> Flags, SortedSet<int> Counters) Checked(IEnumerable<L2Step> steps)
    {
        var flags = new SortedSet<int>();
        var counters = new SortedSet<int>();
        foreach (var step in steps.Where(x => !x.IsText))
        {
            var bytes = step.Bytes;
            if (step.Op is 0x15 or 0x6A or L2ExtraFlags.OpIfOn or L2ExtraFlags.OpIfOff && bytes.Length >= 2) flags.Add(L2ExtraFlags.FlagOf(bytes));
            if (step.Op == 0x12 && bytes.Length >= 2) counters.Add(bytes[1]);
            if (step.Op == 0x14)
                foreach (var check in L2Conditions.Parse(bytes))
                {
                    if (check.Kind == L2Conditions.KFlag && check.At + 1 < bytes.Length) flags.Add(L2ExtraFlags.CheckFlag(check.Code, bytes[check.At + 1]));
                    if (check.Kind == L2Conditions.KCounter && check.At + 1 < bytes.Length) counters.Add(bytes[check.At + 1]);
                }
        }
        return (flags, counters);
    }

    // the frame of a map sprite for a facing (0 up, 1 down, 2 left, 3 right) and walking phase (0 or 1). sprites with
    // six frames have two each for down (0, 1), left (2, 3) and up (4, 5), right is left mirrored (like "Face" 7A's
    // frames 00 down, 02 left, 04 up, 06 right). sprites with two frames only step in place, others have one picture
    public static (int Frame, bool Flip) FacingFrame(RomBuffer rom, int sprite, int dir, int phase)
    {
        int flags = 0;
        try { if (sprite >= 0 && sprite < L2Sprites.Count(rom)) flags = rom.ReadByte(L2Sprites.EntryAt(rom, sprite)); } catch (Exception) { }
        phase &= 1;
        if ((flags & 0x10) != 0) return ((dir & 3) switch { 0 => 4, 1 => 0, _ => 2 } + phase, (dir & 3) == 3);
        if ((flags & 0x08) != 0) return (phase, false);
        return (0, false);
    }

    // whether the sprite has pictures for each facing
    public static bool HasFacings(RomBuffer rom, int sprite)
    {
        try { return sprite >= 0 && sprite < L2Sprites.Count(rom) && (rom.ReadByte(L2Sprites.EntryAt(rom, sprite)) & 0x10) != 0; }
        catch (Exception) { return false; }
    }

    // making cutscenes

    // a story flag no event and no game code uses (from EF down), not in avoid. -1 if none
    public static int FreeFlag(RomBuffer rom, IEnumerable<int>? avoid = null) => L2FlagIndex.FreeStory(rom, false, avoid);

    // a scene number the map doesn't use yet (from 0x20 up, below the F0 the test start uses)
    public static int FreeScene(RomBuffer rom, int map)
    {
        int block = L2Events.Block(rom, map);
        var used = block < 0 ? new HashSet<int>() : L2EventBlocks.ReadLists(rom, block).Where(e => e.Head == L2EventBlocks.SceneList).Select(e => e.Id).ToHashSet();
        for (int id = 0x20; id < 0xF0; id++) if (!used.Contains(id)) return id;
        throw new InvalidOperationException("The map has no free scene number.");
    }

    // what's wrong with where a "Go to another map" step (16 map scene point) sends the party, if anything.
    // the game fades to black, loads the map with the leader hidden and runs that map's scene with that number.
    // if the map has no such scene, or the scene never fades the screen back in, the screen stays black for good
    // (checked in the game 2026-11-18 on map F3: a missing scene, an empty one and one with only a text box all stay black)
    public enum ArrivalTrouble { None, NoEvents, NoScene, NoFadeIn }

    // steps that bring the screen back, or leave it to another map change that does
    private static bool BringsScreenBack(L2Step step) => !step.IsText && step.Op is 0x59 or 0xA4 or 0x3F or 0x16 or 0x5E;

    public static ArrivalTrouble CheckArrival(RomBuffer rom, int map, int scene)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) return ArrivalTrouble.NoEvents;
        List<L2EventBlocks.Entry> lists;
        try { lists = L2EventBlocks.ReadLists(rom, block); } catch (Exception) { return ArrivalTrouble.None; }
        var entry = lists.FirstOrDefault(e => e.Head == L2EventBlocks.SceneList && e.Id == scene);
        if (entry == null) return ArrivalTrouble.NoScene;
        if (!entry.IsScript) return ArrivalTrouble.None;
        try { return L2EventScript.Read(rom, block + entry.Offset, block).Steps.Any(BringsScreenBack) ? ArrivalTrouble.None : ArrivalTrouble.NoFadeIn; }
        catch (Exception) { return ArrivalTrouble.None; }
    }

    // makes a scene on map for a "Go to another map" step to arrive at: it shows the leader, fades in and has a "..."
    // box to start writing from (the same as a new "arrive" cutscene). returns its number
    public static int MakeArrivalScene(RomBuffer rom, int map) => Create(rom, map, new NewOptions { Start = StartKind.Arrive }).Id;

    // puts "show the leader, fade in, wait for the fade" at the top of a scene that never fades the screen back in
    public static void AddFadeIn(RomBuffer rom, int map, int scene)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("That map has no events.");
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.SceneList && e.Id == scene)
                    ?? throw new InvalidOperationException($"That map has no scene {scene:X2}.");
        var script = L2EventScript.Read(rom, block + entry.Offset, block);
        if (script.Problem != null) throw new InvalidOperationException($"Scene {scene:X2} can't be changed: {script.Problem}");
        script.Steps.InsertRange(0, new[]
        {
            new L2Step { Bytes = new byte[] { 0x2D, 0x00 } },
            new L2Step { Bytes = new byte[] { 0x59, 0x13 } },
            new L2Step { Bytes = new byte[] { 0x76 } },
        });
        L2Rom.AllOrNothing(rom, () => L2EventBlocks.Save(rom, map, entry, script));
    }

    // what a new cutscene gets made with
    public sealed class NewOptions
    {
        public StartKind Start { get; init; }
        // plays only once (a free story flag is checked first and turned on)
        public bool Once { get; init; } = true;
        // map loads: a story flag that has to be on (or off, FlagMustBeOn) for it to play (-1 = none)
        public int Flag { get; init; } = -1;
        public bool FlagMustBeOn { get; init; } = true;
        // map loads / arrive: the preset point the party stands on
        public int Point { get; init; } = -1;
        // step on: the area (inclusive tiles)
        public int X1 { get; init; }
        public int Y1 { get; init; }
        public int X2 { get; init; }
        public int Y2 { get; init; }
        // talk: the character
        public int Character { get; init; } = -1;
    }

    // what Create returns: the event it made and what it did
    public sealed record Made(int Head, int Id, int Flag, string What);

    // makes a new cutscene on a map: its event (a first line of text to start from) and how it starts
    public static Made Create(RomBuffer rom, int map, NewOptions options) => L2Rom.AllOrNothing(rom, () => CreateCore(rom, map, options));

    // Create: the event and what starts it get written all or none
    private static Made CreateCore(RomBuffer rom, int map, NewOptions options)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        switch (options.Start)
        {
            case StartKind.Arrive:
            case StartKind.MapLoads:
            {
                int scene = FreeScene(rom, map);
                var script = L2EventScript.New(block);
                // arriving through "Go to another map" or a new game, the screen is black and the leader hidden
                var steps = new List<L2Step>
                {
                    new() { Bytes = new byte[] { 0x2D, 0x00 } },
                    new() { Bytes = new byte[] { 0x59, 0x13 } }, new() { Bytes = new byte[] { 0x76 } },
                    new() { Bytes = new byte[] { 0x61 }, IsText = true, Text = "...", End = 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" },
                };
                script.Steps.InsertRange(0, steps);
                int flag = -1;
                string how = "";
                if (options.Start == StartKind.MapLoads)
                {
                    if (options.Point < 0) throw new InvalidOperationException("Pick the point where the party stands.");
                    flag = FreeFlag(rom, options.Flag >= 0 ? new[] { options.Flag } : null);
                    if (flag < 0) throw new InvalidOperationException("No story flag is free.");
                    // the "on entering" event has to take the starter before the scene gets written
                    if (L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1) is { } enter &&
                        L2EventScript.Read(rom, block + enter.Offset, block).Problem is { } why)
                        throw new InvalidOperationException("The map's \"on entering\" event can't be changed: " + why);
                    L2EventBlocks.Add(rom, map, L2EventBlocks.SceneList, scene, script);
                    how = " " + AddStarter(rom, map, scene, options.Point, flag, options.Flag, options.FlagMustBeOn);
                }
                else L2EventBlocks.Add(rom, map, L2EventBlocks.SceneList, scene, script);
                return new Made(L2EventBlocks.SceneList, scene, flag, $"New cutscene: scene {scene:X2} of map {map:X2}.{how}");
            }
            case StartKind.StepOn:
            {
                int flag = options.Once ? FreeFlag(rom) : -1;
                if (options.Once && flag < 0) throw new InvalidOperationException("No story flag is free.");
                var script = L2EventBlocks.NewScript(block, new byte[] { 0x61 }, "...", flag);
                int id = L2EventBlocks.AddTrigger(rom, map, Math.Min(options.X1, options.X2), Math.Min(options.Y1, options.Y2), Math.Max(options.X1, options.X2), Math.Max(options.Y1, options.Y2), script);
                return new Made(L2EventBlocks.TriggerList, id, flag, $"New cutscene: stepping onto ({options.X1},{options.Y1})-({options.X2},{options.Y2}) of map {map:X2} starts it{(flag >= 0 ? $", once (story flag {flag:X2})" : "")}.");
            }
            default:
            {
                if (options.Character < 0) throw new InvalidOperationException("Pick the character the party talks to.");
                if (L2EventBlocks.ReadLists(rom, block).Any(e => e.Head == L2EventBlocks.TalkList && e.Id == options.Character))
                    throw new InvalidOperationException("Talking to this character already starts an event: pick it in the list to make it a cutscene.");
                int flag = options.Once ? FreeFlag(rom) : -1;
                if (options.Once && flag < 0) throw new InvalidOperationException("No story flag is free.");
                var script = L2EventBlocks.NewScript(block, new byte[] { 0x08 }, "...", flag);
                L2EventBlocks.Add(rom, map, L2EventBlocks.TalkList, options.Character, script);
                return new Made(L2EventBlocks.TalkList, options.Character, flag, $"New cutscene: talking to character {options.Character - 0x4F} of map {map:X2} starts it{(flag >= 0 ? $", once (story flag {flag:X2})" : "")}.");
            }
        }
    }

    // steps at the top of the map's "on entering" event (+2 id 01): if played is off (and the condition flag allows),
    // turn it on and go to the scene on this map. the scene then runs when the map is shown again
    public static string AddStarter(RomBuffer rom, int map, int scene, int point, int played, int flag, bool mustBeOn)
    {
        int block = L2Events.Block(rom, map);
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1);
        var script = entry != null ? L2EventScript.Read(rom, block + entry.Offset, block) : L2EventScript.New(block);
        if (script.Problem != null) throw new InvalidOperationException("The map's \"on entering\" event can't be changed: " + script.Problem);
        var rest = script.Steps[0];
        var add = new List<L2Step>();
        // (story flags 00-FF or the extra 100-17F: L2ExtraFlags picks the command)
        L2Step Skip(L2ExtraFlags.Use use, int flagNumber) { var step = new L2Step { Bytes = L2ExtraFlags.Step(use, flagNumber) }; step.Jumps.Add(new L2Jump { Step = rest }); return step; }
        add.Add(Skip(L2ExtraFlags.Use.IfOn, played));
        if (flag >= 0) add.Add(Skip(mustBeOn ? L2ExtraFlags.Use.IfOff : L2ExtraFlags.Use.IfOn, flag));
        add.Add(new L2Step { Bytes = L2ExtraFlags.Step(L2ExtraFlags.Use.On, played) });
        add.Add(new L2Step { Bytes = new byte[] { 0x16, (byte)map, (byte)scene, (byte)point } });
        script.Steps.InsertRange(0, add);
        if (entry != null) L2EventBlocks.Save(rom, map, entry, script); else L2EventBlocks.Add(rom, map, L2EventBlocks.GameList, 1, script);
        return $"It plays when the party enters the map{(flag >= 0 ? $" while story flag {L2ExtraFlags.Number(flag)} is {(mustBeOn ? "on" : "off")}" : "")}, once (story flag {L2ExtraFlags.Number(played)}), with the party on point {point}.";
    }

    // the "on entering" steps that start scene scene of this map: (played flag, condition flag, must be on, point),
    // or null
    public static (int Played, int Flag, bool MustBeOn, int Point)? Starter(RomBuffer rom, int map, int scene)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) return null;
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1);
        if (entry == null) return null;
        var steps = L2EventScript.Read(rom, block + entry.Offset, block).Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            if (!Is(steps[i], 0x16, 4) || steps[i].Bytes[1] != map || steps[i].Bytes[2] != scene) continue;
            int played = i >= 1 && !steps[i - 1].IsText && L2ExtraFlags.Of(steps[i - 1].Bytes) is (L2ExtraFlags.Use.On, var playedFlag) ? playedFlag : -1;
            int flag = -1; bool on = true;
            if (i >= 3 && !steps[i - 2].IsText && L2ExtraFlags.Of(steps[i - 2].Bytes) is (L2ExtraFlags.Use.IfOn or L2ExtraFlags.Use.IfOff, var checkFlag) use && checkFlag != played)
            { flag = checkFlag; on = use.Use == L2ExtraFlags.Use.IfOff; }
            return (played, flag, on, steps[i].Bytes[3]);
        }
        return null;
    }

    // the story flag a cutscene checks first to play only once ("if it's on, go to the end"), or -1
    public static int OnceFlag(L2EventScript script)
    {
        // (story flags 00-FF or the extra 100-17F: 15 / CF, then 1A / CD of the same flag)
        if (script.Steps.Count < 2 || script.Steps[0].IsText || L2ExtraFlags.Of(script.Steps[0].Bytes) is not (L2ExtraFlags.Use.IfOn, var flag)) return -1;
        return script.Steps.Skip(1).Take(3).Any(s => !s.IsText && L2ExtraFlags.Of(s.Bytes) == (L2ExtraFlags.Use.On, flag)) ? flag : -1;
    }

    // the story flags an event needs on to play, when with none on it only checks and ends (the game's own cutscenes
    // check the story so far: North Lighthouse 6F's step-on area 1 only plays with flag 37 on and 39 off). ticked one
    // at a time while playback then reaches more steps, never its plays-once flag. empty = it plays without any (or no
    // flag makes it play). start makes the starting stage with the given flags on
    public static List<int> FlagsToPlay(RomBuffer rom, int map, L2EventScript script, Func<IEnumerable<int>, L2StageState> start, Func<int, string> name)
    {
        var steps = script.Steps;
        List<int> Reached(IEnumerable<int> on)
        {
            try { return Run(rom, map, start(on), steps, name).Select(r => r.Step).Distinct().ToList(); }
            catch (Exception) { return new(); }
        }
        // does something: a step that isn't a check, a jump or the end
        bool Plays(List<int> reached) => reached.Any(i => i < steps.Count && steps[i].Jumps.Count == 0 && steps[i].Op is not (0x00 or 0x42));
        var none = Reached(Array.Empty<int>());
        var on = new List<int>();
        if (Plays(none)) return on;
        int once = OnceFlag(script);
        var choices = Checked(steps).Flags.Where(f => f != once).ToList();
        int best = none.Count;
        while (true)
        {
            int pick = -1, most = best;
            foreach (int f in choices.Except(on))
            {
                int reachedCount = Reached(on.Append(f)).Count;
                if (reachedCount > most) { most = reachedCount; pick = f; }
            }
            if (pick < 0) break;
            on.Add(pick); best = most;
        }
        return on.Count > 0 && Plays(Reached(on)) ? on : new List<int>();
    }

    // FlagsToPlay(RomBuffer, int, L2EventScript, Func<IEnumerable<int>, L2StageState>, Func<int, string>) for an event
    // of a map's lists, the party on the map's first preset point
    public static List<int> FlagsToPlay(RomBuffer rom, int map, int head, int id)
    {
        int block = L2Events.Block(rom, map);
        var entry = block < 0 ? null : L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == head && x.Id == id && x.IsScript);
        if (entry == null) return new();
        L2EventScript script;
        try { script = L2EventScript.Read(rom, block + entry.Offset, block); } catch (Exception) { return new(); }
        if (script.Problem != null) return new();
        var points = L2StoryClear.PresetPoints(rom, map);
        var (x, y) = points.Count > 0 ? (points[0].X, points[0].Y) : (8, 8);
        return FlagsToPlay(rom, map, script, on =>
        {
            var state = Initial(rom, map, x, y);
            state.Flags.UnionWith(on);
            state.Talker = state.TalkSave = head == L2EventBlocks.TalkList ? id : 0xFF;
            return state;
        }, _ => "");
    }

    // a character that only shows up in cutscenes: "Add a map character" at the top of the start-up script (skipped
    // once flag is on, if there is one), hidden right after the map's characters are made. cutscene steps put it on a
    // tile and show it. with hidden false it's a character that stands on its tile from the start (one to talk to).
    // returns its actor id
    public static int AddCast(RomBuffer rom, int map, int sprite, int flag, int x = 0, int y = 0, bool hidden = true) =>
        L2Rom.AllOrNothing(rom, () => AddCastCore(rom, map, sprite, flag, x, y, hidden));

    // AddCast: its character start, create and hide get written all or none
    private static int AddCastCore(RomBuffer rom, int map, int sprite, int flag, int x, int y, bool hidden)
    {
        var used = L2EventBlocks.CharacterIds(rom, map);
        int id = 0x50;
        while (used.Contains(id)) id++;
        if (id > 0x6F) throw new InvalidOperationException("The map has 32 characters already.");
        int init = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
        if (script.Problem != null) throw new InvalidOperationException("The map's start-up script can't be changed: " + script.Problem);
        if (script.Steps.Count(s => s.Op is 0x68 or 0x7B) >= 31) throw new InvalidOperationException("The map has as many characters as the game can show.");
        // check what a hidden one needs before writing anything (a half-made character would stay in the rom)
        int block0 = L2Events.Block(rom, map);
        if (hidden && block0 < 0) throw new InvalidOperationException("This map has no event block to hide the character in.");
        if (hidden && L2EventBlocks.ReadLists(rom, block0).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 0) is { } shown0 &&
            L2EventScript.Read(rom, block0 + shown0.Offset, block0).Problem is { } why0)
            throw new InvalidOperationException("The map's \"when shown\" event can't be changed: " + why0);
        int spriteByte = L2Sprites.ToGame(rom, map, sprite, L2MapInfo.NewSpriteBytesUsed(rom, map, true));
        // a character start of its own (the game finds a character's actor slot through it, without one the game's
        // own characters can take the slot): where it waits, standing still. its walk area is the whole map, since a
        // step (A3 00-03) out of the area gets refused (checked in the game: a character with a one-tile area didn't walk)
        int width = 255, height = 255;
        try { var mapData = L2Maps.Read(rom, map); width = Math.Min(255, mapData.Layers.Max(l => l.Width)); height = Math.Min(255, mapData.Layers.Max(l => l.Height)); } catch (Exception) { }
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Characters, 8, new[] { (byte)(id - 0x4F), (byte)x, (byte)y, (byte)0, (byte)0, (byte)width, (byte)height, (byte)0x04 }))
            throw new InvalidOperationException("The map's info block would be too big.");
        var first = script.Steps[0];
        var create = new L2Step { Bytes = new byte[] { 0x68, (byte)id, (byte)spriteByte } };
        script.Steps.Insert(0, create);
        if (flag >= 0)
        {
            var guard = new L2Step { Bytes = new byte[] { 0x15, (byte)flag, 0, 0 } };
            guard.Jumps.Add(new L2Jump { Step = first });
            script.Steps.Insert(0, guard);
        }
        L2EventBlocks.SaveInit(rom, map, script);
        if (!hidden) return id;   // a character to talk to: it stands there from the start
        // hidden when the map is shown (event +2 id 00). not in the start-up script: every command there that finds a
        // character ($80:BF6F / BF92) leaves its slot in $A7, the counter 68 makes characters with, and then the story
        // characters the game adds after the script would take the slots of characters already made
        int block = L2Events.Block(rom, map);
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 0);
        var shown = entry != null ? L2EventScript.Read(rom, block + entry.Offset, block) : L2EventScript.New(block);
        if (shown.Problem != null) throw new InvalidOperationException("The map's \"when shown\" event can't be changed: " + shown.Problem);
        shown.Steps.Insert(0, new L2Step { Bytes = new byte[] { 0x2E, (byte)id } });
        if (entry != null) L2EventBlocks.Save(rom, map, entry, shown); else L2EventBlocks.Add(rom, map, L2EventBlocks.GameList, 0, shown);
        return id;
    }

    // the characters the map's "when shown" event (+2 id 00) hides: cutscene characters
    internal static HashSet<int> HiddenWhenShown(RomBuffer rom, int map)
    {
        var ids = new HashSet<int>();
        try
        {
            int block = L2Events.Block(rom, map);
            var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 0);
            if (entry == null) return ids;
            foreach (var step in L2EventScript.Read(rom, block + entry.Offset, block).Steps) if (Is(step, 0x2E, 2)) ids.Add(step.Bytes[1]);
        }
        catch (Exception) { }
        return ids;
    }

    // characters the start-up script makes and hides right away (made for cutscenes)
    public static HashSet<int> CastIds(RomBuffer rom, int map)
    {
        var made = Creates(rom, map).Keys.ToHashSet();
        var ids = HiddenWhenShown(rom, map);
        ids.IntersectWith(made);
        return ids;
    }

    // a new game starts with this cutscene: a scene runs on arrival (the party at point). a stepping-on cutscene, or
    // a talk one that doesn't speak as "the one talked to", plays right after the test start (the party on point),
    // others wait for the talk. the story flags and counters get set as the new game starts (the game's own cutscenes
    // check the story so far and skip themselves without them), and so does what the cutscene's characters need (Needs)
    public static string SetTest(RomBuffer rom, int map, int head, int id, int point, IReadOnlyCollection<int>? flags = null, IReadOnlyDictionary<int, int>? counters = null,
        IReadOnlyList<int>? party = null)
    {
        if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
        if (!L2StoryClear.PresetPoints(rom, map).Any(p => p.Id == point)) throw new InvalidOperationException($"Map {map:X2} has no preset point {point}: add one on the Maps page.");
        L2StartParty.Keep(rom);   // the whole starting party on the map, for the cutscene's walks
        var (need, place) = Needs(rom, map, head, id);
        var all = (flags ?? Array.Empty<int>()).Union(need).OrderBy(f => f).ToList();
        // a flag ticked on that keeps the map from making one of the cutscene's characters (only made while it's off)
        string clash = "";
        try
        {
            int eventBlock = L2Events.Block(rom, map);
            var entry = eventBlock < 0 ? null : L2EventBlocks.ReadLists(rom, eventBlock).FirstOrDefault(x => x.Head == head && x.Id == id);
            if (entry != null)
            {
                var used = ActorsUsed(L2EventScript.Read(rom, eventBlock + entry.Offset, eventBlock), head, id);
                var off = L2MapInfo.Stages(rom, map).Where(c => used.Contains(c.Id) && c.Flags.Any(f => !f.On && all.Contains(f.Flag)) && !L2MapInfo.Stages(rom, map).Any(o => o.Id == c.Id && !o.Flags.Any(f => !f.On && all.Contains(f.Flag))))
                    .Select(c => c.Id).Distinct().ToList();
                if (off.Count > 0) clash = $" Careful: with the flags ticked the map doesn't make character{(off.Count > 1 ? "s" : "")} {string.Join(", ", off.Select(x => (x - 0x4F).ToString()))}, which the cutscene uses.";
            }
        }
        catch (Exception) { }
        string extra = clash + (need.Count == 0 ? "" : $" Story flag{(need.Count > 1 ? "s" : "")} {string.Join(" ", need.Select(f => f.ToString("X2")))} {(need.Count > 1 ? "are" : "is")} on too: the map makes the cutscene's characters only then.") +
                       (place.Count == 0 ? "" : $" Persistent character{(place.Count > 1 ? "s" : "")} {string.Join(" ", place.Select(p => p.Id.ToString("X2")))} {(place.Count > 1 ? "are" : "is")} sent here first, as the story does.");
        if (head == L2EventBlocks.SceneList && Starter(rom, map, id) is { } starter)
        {
            // the "on entering" check: its flag on when it must be, the played flag and a must-be-off flag off
            all.Remove(starter.Played);
            if (starter.Flag >= 0) { if (starter.MustBeOn) { if (!all.Contains(starter.Flag)) all.Add(starter.Flag); } else all.Remove(starter.Flag); }
            return L2MapEdit.SetTestStart(rom, map, point, all, counters, null, place, party) + $" On entering the map the cutscene starts (with the party on point {starter.Point})." + extra;
        }
        if (head == L2EventBlocks.SceneList && all.Count + (counters?.Count ?? 0) + place.Count + L2MapEdit.PartyChanges(rom, party).Count > 0)
            return L2MapEdit.StartWithFlags(rom, map, id, point, all, counters, place, party) + $" Then the cutscene (scene {id:X2}) plays." + extra;
        if (head == L2EventBlocks.SceneList)
        {
            rom.WriteByte(L2StoryClear.NewGameMapAt, (byte)map);
            rom.WriteByte(L2StoryClear.NewGameSceneAt, (byte)id);
            rom.WriteByte(L2StoryClear.NewGamePresetAt, (byte)point);
            return $"A new game now starts with this cutscene (map {map:X2}, scene {id:X2}, the party on point {point}).";
        }
        if (head == L2EventBlocks.TriggerList || head == L2EventBlocks.TalkList && !SpeaksAsTalkedTo(rom, map, head, id))
            return L2MapEdit.SetTestStart(rom, map, point, all, counters, (head, id), place, party) + " The cutscene plays as the game starts" +
                   (head == L2EventBlocks.TriggerList ? " (pick a point on its area: its walks start from there)." : ".") + extra;
        return L2MapEdit.SetTestStart(rom, map, point, all, counters, null, place, party) +
               (head == L2EventBlocks.TalkList ? " Talk to the character to start the cutscene (it speaks as the one talked to, so it needs the talk)." : "") + extra;
    }

    // the preset point the game puts the party on for each scene of map: the point of a "Go to another map" (16 map
    // scene point) of any event that starts it (Elcid's scene 01 gets started from map A2 with point 4, on another
    // point the camera is elsewhere and a box over a character off to the side runs off the screen and wraps round).
    // test starts (scenes F0 up) don't count
    public static Dictionary<int, int> GoToPoints(RomBuffer rom, int map) =>
        GoTos(rom, map).ToDictionary(kv => kv.Key, kv => kv.Value[0].Point);

    // the events that start each scene of map with "Go to another map" (16 map scene point): their map, list, id and
    // the point (Elcid's scene 01 is the ending's, started from map A2 scene 03). test starts (scenes F0 up) don't count
    public static Dictionary<int, List<(int Map, int Head, int Id, int Point)>> GoTos(RomBuffer rom, int map)
    {
        var found = new Dictionary<int, List<(int, int, int, int)>>();
        for (int m = 0; m < L2Maps.Count(rom); m++)
        {
            int block = L2Events.Block(rom, m);
            if (block < 0) continue;
            List<L2EventBlocks.Entry> lists;
            try { lists = L2EventBlocks.ReadLists(rom, block); } catch (Exception) { continue; }
            foreach (var e in lists.Where(e => e.IsScript && !(e.Head == L2EventBlocks.SceneList && e.Id >= 0xF0)))
            {
                L2EventScript script;
                try { script = L2EventScript.Read(rom, block + e.Offset, block); } catch (Exception) { continue; }
                foreach (var step in script.Steps)
                    if (step.Op == 0x16 && step.Bytes.Length >= 4 && step.Bytes[1] == map)
                    {
                        if (!found.TryGetValue(step.Bytes[2], out var list)) found[step.Bytes[2]] = list = new();
                        if (!list.Any(x => x.Item1 == m && x.Item2 == e.Head && x.Item3 == e.Id)) list.Add((m, e.Head, e.Id, step.Bytes[3]));
                    }
            }
        }
        return found;
    }

    // a new game starts on the map (its start-up script runs as it loads), the party on point, with the story flags
    // and counters set
    public static string SetMapTest(RomBuffer rom, int map, int point, IReadOnlyCollection<int>? flags = null, IReadOnlyDictionary<int, int>? counters = null,
        IReadOnlyList<int>? party = null)
    {
        if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
        L2StartParty.Keep(rom);
        return L2MapEdit.SetTestStart(rom, map, point, flags, counters, null, null, party);
    }

    // commands whose first operand is an actor (33, "walks path", has it second)
    private static readonly HashSet<int> ActorFirst = new() { 0x13, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x34, 0x6C, 0x7A, 0x89, 0x98, 0x99, 0x9D, 0xA3 };

    // the actors a cutscene's steps name (the one talked to included)
    public static HashSet<int> ActorsUsed(L2EventScript script, int head, int id)
    {
        var ids = new HashSet<int>();
        if (head == L2EventBlocks.TalkList) ids.Add(id);
        foreach (var step in script.Steps)
        {
            var bytes = step.Bytes;
            if (ActorFirst.Contains(step.Op) && bytes.Length >= 2) ids.Add(bytes[1]);
            else if (step.Op == 0x33 && bytes.Length >= 3) ids.Add(bytes[2]);
        }
        return ids;
    }

    // what a test start needs for the cutscene's characters to be there: the story flags under which the map's
    // start-up script makes them (only the ones that have to be on, a new game has every flag off), and the persistent
    // characters (10-4F) it uses that stand on this map at some point but don't start a new game here, with their
    // place (sent there with 35 id map spot, like the story's own event does)
    public static (List<int> Flags, List<(int Id, int Map, int Spot)> Place) Needs(RomBuffer rom, int map, int head, int id)
    {
        var flags = new List<int>();
        var place = new List<(int, int, int)>();
        int block = L2Events.Block(rom, map);
        var entry = block < 0 ? null : L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == head && x.Id == id);
        if (entry == null) return (flags, place);
        var script = L2EventScript.Read(rom, block + entry.Offset, block);
        if (script.Problem != null) return (flags, place);
        var used = ActorsUsed(script, head, id);
        var stages = L2MapInfo.Stages(rom, map);
        foreach (int actor in used.Where(actor => actor >= 0x50))
        {
            var creates = stages.Where(c => c.Id == actor).ToList();
            if (creates.Count == 0 || creates.Any(c => c.Flags.Count == 0)) continue;   // always made, or never by the map
            foreach (var (f, on) in creates[0].Flags) if (on && !flags.Contains(f)) flags.Add(f);
        }
        // every persistent character the cutscene uses stands where the story puts it on this map: the game only draws
        // one on the map it was sent to, and a step that puts it on a point, shows it or walks it does nothing otherwise
        // (checked in the game: Parcelyte 2C scene 01's Tia, put on a point, and 2D scene 01's Selan, walked, never
        // showed up unsent). one the cutscene sends here itself (35) before using it is left to the cutscene
        var sentByCutscene = new HashSet<int>();
        var usedFirst = new HashSet<int>();
        foreach (var step in script.Steps)
        {
            var bytes = step.Bytes;
            if (step.Op == 0x35 && bytes.Length >= 4 && bytes[2] == map && !usedFirst.Contains(bytes[1])) sentByCutscene.Add(bytes[1]);
            int who = ActorFirst.Contains(step.Op) && bytes.Length >= 2 ? bytes[1] : step.Op == 0x33 && bytes.Length >= 3 ? bytes[2] : -1;
            if (who >= 0) usedFirst.Add(who);
        }
        var start = L2Persistent.Start(rom);
        var here = L2Persistent.On(L2Persistent.All(rom), map);
        foreach (int actor in used.Where(actor => actor is >= L2Persistent.First and < L2Persistent.First + L2Persistent.Count).OrderBy(actor => actor))
        {
            if (sentByCutscene.Contains(actor) || start.Any(x => x.Id == actor && x.Map == map) || !start.Any(x => x.Id == actor)) continue;
            // the story never brings it here: character start 1, where the cutscene's own steps move it from
            if (here.FirstOrDefault(persistentPlace => persistentPlace.Id == actor) is { } persistentPlace) place.Add((actor, map, persistentPlace.Spot));
            else if (stages.Count > 0) place.Add((actor, map, 1));
        }
        return (flags, place);
    }

    // what Test in the game sets up for the cutscene's characters (Needs), in a line. empty if it needs nothing
    public static string DescribeNeeds(RomBuffer rom, int map, int head, int id)
    {
        List<int> need; List<(int Id, int Map, int Spot)> place;
        try { (need, place) = Needs(rom, map, head, id); } catch (Exception) { return ""; }
        var parts = new List<string>();
        if (need.Count > 0)
            parts.Add($"turns on story flag{(need.Count > 1 ? "s" : "")} {string.Join(" ", need.Select(f => f.ToString("X2")))} (the map makes the cutscene's characters only then)");
        if (place.Count > 0)
            parts.Add($"sends story character{(place.Count > 1 ? "s" : "")} {string.Join(", ", place.Select(p => $"{p.Id:X2} (character start {p.Spot})"))} here (the game draws one only on the map the story sent it to)");
        return parts.Count == 0 ? "" : "The test start also " + string.Join(" and ", parts) + ".";
    }

    // a talk cutscene that speaks as "the one talked to" (08), so it needs the talk
    private static bool SpeaksAsTalkedTo(RomBuffer rom, int map, int head, int id)
    {
        int block = L2Events.Block(rom, map);
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == head && x.Id == id);
        return entry == null || L2EventScript.Read(rom, block + entry.Offset, block).Steps.Any(st => st.Op == 0x08);
    }
}
