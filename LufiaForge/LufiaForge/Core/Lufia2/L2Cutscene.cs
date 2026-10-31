namespace LufiaForge.Core.Lufia2;

/// <summary>What a cutscene step does (one or a few commands of the script, seen as one thing).</summary>
public enum L2BeatKind
{
    Appear, Leave, Walk, Face, Act, Say, Camera, CameraBack, FadeOut, FadeIn, Music, Sound, Wait, Shake, StopShake,
    FlagOn, FlagOff, GoTo, End, Other,
}

/// <summary>
/// One step of a cutscene in plain words: a few script commands that belong together ("Guy walks 3 tiles left" is
/// three times "A3 Guy step-left" + "89 Guy"). <see cref="Build"/> makes the commands from the fields;
/// <see cref="L2Cutscene.Beats"/> reads them back. Steps the cutscene editor doesn't know are <see cref="L2BeatKind.Other"/>
/// and stay as they are.
/// </summary>
public sealed class L2Beat
{
    public L2BeatKind Kind { get; set; }
    /// <summary>The script steps it is made of (as read, or as last built).</summary>
    public List<L2Step> Steps { get; } = new();
    /// <summary>The character (actor id), and a second one walking along (-1: none).</summary>
    public int Who { get; set; }
    public int Who2 { get; set; } = -1;
    /// <summary>0 up, 1 down, 2 left, 3 right.</summary>
    public int Dir { get; set; } = 1;
    /// <summary>Tiles walked.</summary>
    public int Count { get; set; } = 1;
    /// <summary>A tile (appear, camera); -1: where it is.</summary>
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    /// <summary>Action, song, sound, frames, story flag, fade speed, camera speed, map, shake strength.</summary>
    public int Value { get; set; }
    /// <summary>Go to: scene; shake: how often.</summary>
    public int Value2 { get; set; }
    /// <summary>Go to: arrival point.</summary>
    public int Value3 { get; set; }
    /// <summary>Appear / leave in a flash.</summary>
    public bool Flash { get; set; }
    /// <summary>Camera: jumps at once instead of scrolling.</summary>
    public bool AtOnce { get; set; }
    /// <summary>Say: the opening command (61-67 a hero, 13 xx a character, 9E xx narration, 08 the one talked to).</summary>
    public byte[] Opener { get; set; } = { 0x61 };
    public string Text { get; set; } = "";

    public static readonly string[] DirNames = { "up", "down", "left", "right" };
    /// <summary>The frame 7A shows for a facing (0 up, 1 down, 2 left, 3 right).</summary>
    public static readonly int[] FaceFrame = { 4, 0, 2, 6 };

    /// <summary>The commands for the fields.</summary>
    public List<L2Step> Build()
    {
        var l = new List<L2Step>();
        void C(params int[] b) => l.Add(new L2Step { Bytes = b.Select(x => (byte)x).ToArray() });
        switch (Kind)
        {
            case L2BeatKind.Appear:
                // shown first: the game's commands that find a character skip hidden ones ($80:BF6F), so "put" on a
                // hidden character would move another one (both run in the same frame: no flicker)
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
                l.Add(new L2Step { Bytes = (byte[])Opener.Clone(), IsText = true, Text = Text, End = Steps.FirstOrDefault(x => x.IsText)?.End ?? 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" });
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
            case L2BeatKind.FlagOn: C(0x1A, Value); break;
            case L2BeatKind.FlagOff: C(0x1B, Value); break;
            case L2BeatKind.GoTo: C(0x16, Value, Value2, Value3); break;
            case L2BeatKind.End: C(0x00); break;
            default: return Steps.Select(s => s).ToList();
        }
        return l;
    }

    /// <summary>A new step of a kind with sensible values.</summary>
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

/// <summary>Where everyone is at one moment of a cutscene (for the stage).</summary>
public sealed class L2StageState
{
    public sealed class Actor
    {
        public int Id { get; init; }
        public int X { get; set; } = -1;
        public int Y { get; set; } = -1;
        /// <summary>0 up, 1 down, 2 left, 3 right.</summary>
        public int Dir { get; set; } = 1;
        public bool Visible { get; set; }
        public int Sprite { get; set; } = -1;
        public bool Placed => X >= 0 && Y >= 0;
        public Actor Copy() => new() { Id = Id, X = X, Y = Y, Dir = Dir, Visible = Visible, Sprite = Sprite };
    }

    public Dictionary<int, Actor> Actors { get; } = new();
    /// <summary>The tile the camera is centred on.</summary>
    public int CameraX { get; set; }
    public int CameraY { get; set; }
    public bool CameraOnLeader { get; set; } = true;
    /// <summary>The text box shown at this step (null: none).</summary>
    public string? Speaker { get; set; }
    public string? Text { get; set; }
    public bool Dark { get; set; }
    public bool Shaking { get; set; }
    public int Song { get; set; } = -1;
    /// <summary>What this step does, in a few words (for the stage's caption).</summary>
    public string Caption { get; set; } = "";
    /// <summary>Roughly how long the step takes in the game, in frames (for playback).</summary>
    public int Frames { get; set; }
    /// <summary>The actor that moved in this step (-1: none) and where it came from.</summary>
    public int MovedId { get; set; } = -1;
    /// <summary>The story flags that are on (the ones set at the start, then turned on and off by the steps).</summary>
    public HashSet<int> Flags { get; } = new();
    /// <summary>Event counters (variables) that aren't 0.</summary>
    public Dictionary<int, int> Counters { get; } = new();
    /// <summary>A walk in this step that the game stops (a wall, another character), and the tile it hits; null: none.</summary>
    public string? Warning { get; set; }
    public int WarnX { get; set; } = -1;
    public int WarnY { get; set; } = -1;
    /// <summary>A check or a jump, as playback followed it: why it went where it went (null: not a check or jump).</summary>
    public string? BranchWhy { get; set; }
    /// <summary>The step index it went to (-1: the event ended, -2: a step outside the event).</summary>
    public int BranchTo { get; set; } = -1;
    /// <summary>It jumped (false: went on to the next step).</summary>
    public bool Jumped { get; set; }
    /// <summary>The game's current speaker ($09AB: 08 and 10 speak as it) and the one a text ending 01 goes back to
    /// ($09AC: the one talked to in a talk event); FF: none (<see cref="L2Speakers"/>).</summary>
    public int Talker { get; set; } = 0xFF;
    public int TalkSave { get; set; } = 0xFF;

    public L2StageState Copy()
    {
        var s = new L2StageState { CameraX = CameraX, CameraY = CameraY, CameraOnLeader = CameraOnLeader, Dark = Dark, Shaking = Shaking, Song = Song, Talker = Talker, TalkSave = TalkSave };
        foreach (var kv in Actors) s.Actors[kv.Key] = kv.Value.Copy();
        s.Flags.UnionWith(Flags);
        foreach (var kv in Counters) s.Counters[kv.Key] = kv.Value;
        return s;
    }
}

/// <summary>What the stage knows of the map: its size, collision (the upper layer's cell flags) and preset points.</summary>
public sealed class L2StageMap
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    private byte[] _flags = Array.Empty<byte>();
    public Dictionary<int, (int X, int Y)> Points { get; private init; } = new();
    /// <summary>The walk area of each character with a character start (map info +10: x1, y1 to x2, y2, the right and
    /// bottom edges left out): a step out of it is refused in the game.</summary>
    public Dictionary<int, (int X1, int Y1, int X2, int Y2)> Areas { get; private init; } = new();

    public static L2StageMap Read(RomBuffer rom, int map)
    {
        var points = new Dictionary<int, (int, int)>();
        try { foreach (var p in L2StoryClear.PresetPoints(rom, map)) points.TryAdd(p.Id, (p.X, p.Y)); } catch (Exception) { }
        var areas = new Dictionary<int, (int, int, int, int)>();
        try
        {
            // characters hidden as the map is shown (cutscene characters) walked out of a one-tile area in the game:
            // only those standing on the map from the start are kept in
            var hidden = L2Cutscene.HiddenWhenShown(rom, map);
            foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
                if (!hidden.Contains(0x4F + b[0])) areas.TryAdd(0x4F + b[0], (b[3], b[4], b[5], b[6]));
        }
        catch (Exception) { }
        if (map < 2) return new L2StageMap { Points = points, Areas = areas };   // the world maps: no cell flags
        try
        {
            var m = L2Maps.Read(rom, map);
            var l = m.Layers.Length > 1 ? m.Layers[1] : m.Layers[0];
            return new L2StageMap { Width = l.Width, Height = l.Height, _flags = l.Flags, Points = points, Areas = areas };
        }
        catch (Exception) { return new L2StageMap { Points = points, Areas = areas }; }
    }

    /// <summary>Whether a character with a character start may step onto (x, y): inside its walk area (or it has none).</summary>
    public bool InArea(int id, int x, int y) =>
        !Areas.TryGetValue(id, out var a) || x >= a.X1 && x < a.X2 && y >= a.Y1 && y < a.Y2;

    private static readonly int[] Dx = { 0, 0, -1, 1 }, Dy = { -1, 1, 0, 0 };

    /// <summary>What stops a step from (x, y) one tile in direction <paramref name="dir"/> (0 up, 1 down, 2 left, 3 right):
    /// the edge of the map, a block that can't be walked on (flag 80), a wall on the top (04) or left (08) edge of a
    /// block; null when the way is clear (or the map has no collision here).</summary>
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

/// <summary>
/// Lufia II cutscenes: a map's event seen as a scene on a stage. Groups script steps into <see cref="L2Beat"/>s,
/// works out where everyone stands after each step (<see cref="Simulate"/>), makes new cutscenes with the way they
/// start (<see cref="Create"/>), adds characters that appear only in a cutscene (<see cref="AddCast"/>) and points a
/// new game at a cutscene for testing (<see cref="SetTest"/>).
/// </summary>
public static class L2Cutscene
{
    /// <summary>How a new cutscene starts.</summary>
    public enum StartKind
    {
        /// <summary>A scene of the map (+4 list): run on arriving through "Go to another map" with its number (from
        /// another cutscene), or as a new game's start.</summary>
        Arrive,
        /// <summary>A scene plus a few steps in the map's "on entering" event (+2 id 01) that go to it when a story
        /// flag allows, once.</summary>
        MapLoads,
        /// <summary>Stepping onto an area (map info +0C, event list +8).</summary>
        StepOn,
        /// <summary>Talking to a character (event list +6).</summary>
        Talk,
    }

    // ------------------------------------------------------------------ reading steps as beats

    private static bool Is(L2Step s, int op, int len) => !s.IsText && s.Bytes.Length == len && s.Op == op;

    /// <summary>The script's steps grouped into cutscene steps. A step another step jumps to always starts a group.</summary>
    public static List<L2Beat> Beats(L2EventScript script)
    {
        var targets = new HashSet<L2Step>(script.Steps.SelectMany(s => s.Jumps).Where(j => j.Step != null).Select(j => j.Step!));
        var st = script.Steps;
        var beats = new List<L2Beat>();
        int i = 0;
        bool Free(int k) => k < st.Count && !targets.Contains(st[k]);
        while (i < st.Count)
        {
            var s = st[i];
            L2Beat? b = null;
            int used = 1;
            if (s.IsText)
                b = new L2Beat { Kind = L2BeatKind.Say, Opener = (byte[])s.Bytes.Clone(), Text = s.Text, Who = SpeakerActor(s.Bytes) };
            else if (Is(s, 0x2F, 4) && Free(i + 1) && Is(st[i + 1], 0x2D, 2) && st[i + 1].Bytes[1] == s.Bytes[1])
            {
                b = new L2Beat { Kind = L2BeatKind.Appear, Who = s.Bytes[1], X = s.Bytes[2], Y = s.Bytes[3] };
                used = 2;
                if (Free(i + 2) && Free(i + 3) && Is(st[i + 2], 0xA3, 3) && st[i + 2].Bytes[1] == s.Bytes[1] && st[i + 2].Bytes[2] == 0x86 && Is(st[i + 3], 0x89, 2) && st[i + 3].Bytes[1] == s.Bytes[1])
                { b.Flash = true; used = 4; }
            }
            else if (Is(s, 0x2D, 2))
            {
                b = new L2Beat { Kind = L2BeatKind.Appear, Who = s.Bytes[1] };
                if (Free(i + 1) && Is(st[i + 1], 0x2F, 4) && st[i + 1].Bytes[1] == s.Bytes[1]) { b.X = st[i + 1].Bytes[2]; b.Y = st[i + 1].Bytes[3]; used = 2; }
                if (Free(i + used) && Free(i + used + 1) && Is(st[i + used], 0xA3, 3) && st[i + used].Bytes[1] == s.Bytes[1] && st[i + used].Bytes[2] == 0x86
                    && Is(st[i + used + 1], 0x89, 2) && st[i + used + 1].Bytes[1] == s.Bytes[1])
                { b.Flash = true; used += 2; }
            }
            else if (Is(s, 0x2E, 2)) b = new L2Beat { Kind = L2BeatKind.Leave, Who = s.Bytes[1] };
            else if (Is(s, 0x99, 2)) b = new L2Beat { Kind = L2BeatKind.Leave, Who = s.Bytes[1], Flash = true };
            else if (Is(s, 0xA3, 3) && s.Bytes[2] <= 3) (b, used) = ReadWalk(st, i, Free);
            else if (Is(s, 0xA3, 3) && Free(i + 1) && Is(st[i + 1], 0x89, 2) && st[i + 1].Bytes[1] == s.Bytes[1])
            { b = new L2Beat { Kind = L2BeatKind.Act, Who = s.Bytes[1], Value = s.Bytes[2] }; used = 2; }
            else if (Is(s, 0x7A, 3) && Array.IndexOf(L2Beat.FaceFrame, (int)s.Bytes[2]) is var f && f >= 0)
                b = new L2Beat { Kind = L2BeatKind.Face, Who = s.Bytes[1], Dir = f };
            else if (Is(s, 0x3A, 3)) b = new L2Beat { Kind = L2BeatKind.Camera, X = s.Bytes[1], Y = s.Bytes[2], AtOnce = true };
            else if (Is(s, 0xC4, 3) && Free(i + 1) && Is(st[i + 1], 0x41, 3) && st[i + 1].Bytes[2] == 0xFF)
            { b = new L2Beat { Kind = L2BeatKind.Camera, X = s.Bytes[1], Y = s.Bytes[2] }; used = 2; }
            else if (Is(s, 0x41, 3) && s.Bytes[2] == 0x00 && s.Bytes[1] < 0x10) b = new L2Beat { Kind = L2BeatKind.CameraBack, Value = s.Bytes[1] };
            else if ((Is(s, 0x58, 2) || Is(s, 0x59, 2)) && Free(i + 1) && Is(st[i + 1], 0x76, 1))
            { b = new L2Beat { Kind = s.Op == 0x58 ? L2BeatKind.FadeOut : L2BeatKind.FadeIn, Value = s.Bytes[1] }; used = 2; }
            else if (Is(s, 0x6F, 2)) b = new L2Beat { Kind = L2BeatKind.Music, Value = s.Bytes[1] };
            else if (Is(s, 0x4C, 2)) b = new L2Beat { Kind = L2BeatKind.Sound, Value = s.Bytes[1] };
            else if (Is(s, 0x37, 2)) b = new L2Beat { Kind = L2BeatKind.Wait, Value = s.Bytes[1] };
            else if (Is(s, 0x5A, 4)) b = new L2Beat { Kind = L2BeatKind.Shake, Value = s.Bytes[2], Value2 = s.Bytes[3] };
            else if (Is(s, 0x8A, 1)) b = new L2Beat { Kind = L2BeatKind.StopShake };
            else if (Is(s, 0x1A, 2)) b = new L2Beat { Kind = L2BeatKind.FlagOn, Value = s.Bytes[1] };
            else if (Is(s, 0x1B, 2)) b = new L2Beat { Kind = L2BeatKind.FlagOff, Value = s.Bytes[1] };
            else if (Is(s, 0x16, 4)) b = new L2Beat { Kind = L2BeatKind.GoTo, Value = s.Bytes[1], Value2 = s.Bytes[2], Value3 = s.Bytes[3] };
            else if (Is(s, 0x00, 1)) b = new L2Beat { Kind = L2BeatKind.End };
            b ??= new L2Beat { Kind = L2BeatKind.Other };
            b.Steps.AddRange(st.Skip(i).Take(used));
            beats.Add(b);
            i += used;
        }
        return beats;
    }

    /// <summary>"A3 a d (A3 b d) 89 a (89 b)" repeated with the same characters and direction.</summary>
    private static (L2Beat, int) ReadWalk(List<L2Step> st, int i, Func<int, bool> free)
    {
        int a = st[i].Bytes[1], d = st[i].Bytes[2];
        bool Cmd(int k, int op, int who, int act = -1) => k < st.Count && Is(st[k], op, op == 0x89 ? 2 : 3) && st[k].Bytes[1] == who && (act < 0 || st[k].Bytes[2] == act);
        // with a second walker?
        int b2 = -1;
        if (free(i + 1) && i + 1 < st.Count && Is(st[i + 1], 0xA3, 3) && st[i + 1].Bytes[2] == d && st[i + 1].Bytes[1] != a)
        {
            int c = st[i + 1].Bytes[1];
            if (free(i + 2) && Cmd(i + 2, 0x89, a) && free(i + 3) && Cmd(i + 3, 0x89, c)) b2 = c;
        }
        int per = b2 >= 0 ? 4 : 2;
        int count = 0, k0 = i;
        while (true)
        {
            int k = k0 + count * per;
            if (count > 0 && !free(k)) break;
            bool ok = b2 >= 0
                ? Cmd(k, 0xA3, a, d) && free(k + 1) && Cmd(k + 1, 0xA3, b2, d) && free(k + 2) && Cmd(k + 2, 0x89, a) && free(k + 3) && Cmd(k + 3, 0x89, b2)
                : Cmd(k, 0xA3, a, d) && free(k + 1) && Cmd(k + 1, 0x89, a);
            if (!ok) break;
            count++;
        }
        if (count == 0) return (new L2Beat { Kind = L2BeatKind.Other }, 1);   // no wait after it: shown as is
        return (new L2Beat { Kind = L2BeatKind.Walk, Who = a, Who2 = b2, Dir = d, Count = count }, count * per);
    }

    /// <summary>The actor a text step's speaker is (-1: narration or the one talked to).</summary>
    public static int SpeakerActor(byte[] opener) => opener.Length == 0 ? -1 : opener[0] switch
    {
        >= 0x61 and <= 0x67 => L2EventCommands.SpeakerOf61[opener[0] - 0x61],
        0x13 when opener.Length > 1 => opener[1],
        _ => -1,
    };

    /// <summary>The text opener for an actor: a hero's own (61-67) or "a character speaks" (13 id).</summary>
    public static byte[] OpenerFor(int actor) => actor switch
    {
        >= 0 and < 7 => new[] { (byte)(0x61 + Array.IndexOf(L2EventCommands.SpeakerOf61, actor)) },
        _ => new byte[] { 0x13, (byte)actor },
    };

    /// <summary>Puts a beat's newly built steps in place of its old ones; jumps that went to any of the old steps go to
    /// the first new one. Steps keep their place in the ROM when the commands keep their kind and size.</summary>
    public static void Replace(L2EventScript script, L2Beat beat)
    {
        var fresh = beat.Build();
        var old = beat.Steps.ToList();
        if (fresh.Count == 0) return;
        if (old.Count == fresh.Count && old.Zip(fresh).All(p => p.First.IsText == p.Second.IsText && p.First.Bytes.Length == p.Second.Bytes.Length && (!p.First.IsText || p.First.Op == p.Second.Op)))
        {
            foreach (var (o, n) in old.Zip(fresh)) { o.Bytes = n.Bytes; if (o.IsText) { o.Text = n.Text; } }
            return;
        }
        int at = old.Count > 0 ? script.Steps.IndexOf(old[0]) : script.Steps.Count;
        foreach (var o in old) script.Steps.Remove(o);
        script.Steps.InsertRange(at, fresh);
        foreach (var s in script.Steps) foreach (var j in s.Jumps) if (j.Step != null && old.Contains(j.Step)) j.Step = fresh[0];
        beat.Steps.Clear();
        beat.Steps.AddRange(fresh);
    }

    /// <summary>Inserts a new beat's steps before step index <paramref name="at"/> of the script.</summary>
    public static void Insert(L2EventScript script, int at, L2Beat beat)
    {
        var fresh = beat.Build();
        script.Steps.InsertRange(Math.Clamp(at, 0, script.Steps.Count), fresh);
        beat.Steps.Clear();
        beat.Steps.AddRange(fresh);
    }

    /// <summary>Takes a beat's steps out; jumps to them go to the step after it.</summary>
    public static void Remove(L2EventScript script, L2Beat beat)
    {
        if (beat.Steps.Count == 0) return;
        int last = script.Steps.IndexOf(beat.Steps[^1]);
        var next = script.Steps.Skip(last + 1).FirstOrDefault(s => !beat.Steps.Contains(s));
        // the last step taken out while a jump leads to it (the End a "played once" check jumps to): a new End there
        if (next == null && script.Steps.Any(s => s.Jumps.Any(j => j.Step != null && beat.Steps.Contains(j.Step))))
        {
            next = new L2Step { Bytes = new byte[] { 0x00 } };
            script.Steps.Insert(last + 1, next);
        }
        foreach (var o in beat.Steps) script.Steps.Remove(o);
        foreach (var s in script.Steps) foreach (var j in s.Jumps) if (j.Step != null && beat.Steps.Contains(j.Step)) j.Step = next;
    }

    // ------------------------------------------------------------------ the stage

    /// <summary>The map's characters as the map is loaded: character starts (with the sprite their create gives),
    /// characters the start-up script makes without a start (not placed), and those it hides (cutscene characters).</summary>
    public static L2StageState Initial(RomBuffer rom, int map, int leaderX, int leaderY, IEnumerable<L2Persistent.Place>? persistent = null)
    {
        var s = new L2StageState();
        var sprites = Creates(rom, map);
        foreach (var (_, b) in L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8))
        {
            int id = 0x4F + b[0];
            if (!sprites.TryGetValue(id, out int spr)) continue;   // a start no create makes: not on the map
            s.Actors[id] = new L2StageState.Actor { Id = id, X = b[1], Y = b[2], Visible = true, Sprite = spr };
        }
        foreach (var (id, spr) in sprites) if (!s.Actors.ContainsKey(id)) s.Actors[id] = new L2StageState.Actor { Id = id, Visible = false, Sprite = spr };
        foreach (int id in HiddenWhenShown(rom, map)) if (s.Actors.TryGetValue(id, out var a)) a.Visible = false;
        // persistent characters (10-4F) that stand on this map, on their character start (the first place listed)
        if (persistent != null)
        {
            var starts = L2MapInfo.Records(rom, map, L2MapInfo.Characters, 8).Select(r => r.Bytes).ToList();
            foreach (var p in persistent.Where(p => p.Map == map))
                if (!s.Actors.ContainsKey(p.Id) && starts.FirstOrDefault(b => b[0] == p.Spot) is { } b)
                    s.Actors[p.Id] = new L2StageState.Actor { Id = p.Id, X = b[1], Y = b[2], Visible = true, Sprite = p.Sprite };
        }
        s.Actors[0] = new L2StageState.Actor { Id = 0, X = leaderX, Y = leaderY, Visible = true, Sprite = 0 };
        s.CameraX = leaderX; s.CameraY = leaderY;
        return s;
    }

    /// <summary>The characters the map's start-up script makes (68 / 7B, every step it can reach) and their sprites
    /// (Lufia Forge's numbers); the first create of an id wins.</summary>
    public static Dictionary<int, int> Creates(RomBuffer rom, int map)
    {
        var d = new Dictionary<int, int>();
        try
        {
            int init = L2EventBlocks.InitScript(rom, map);
            foreach (var st in L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init)).Steps)
                if ((Is(st, 0x68, 3) || Is(st, 0x7B, 3)) && st.Bytes[1] >= 0x10)
                    d.TryAdd(st.Bytes[1], L2Sprites.ToEditor(rom, map, st.Bytes[2]));
        }
        catch (Exception) { }
        return d;
    }

    private static readonly int[] Dx = { 0, 0, -1, 1 }, Dy = { -1, 1, 0, 0 };

    /// <summary>The stage after each step of the script, read top to bottom (jumps aren't followed: <see cref="Run"/>
    /// follows them). Element 0 is the stage before the first step; element i + 1 after step i.</summary>
    public static List<L2StageState> Simulate(RomBuffer rom, int map, L2StageState start, IReadOnlyList<L2Step> steps, Func<int, string> name)
    {
        var m = L2StageMap.Read(rom, map);
        var sp = new L2Speakers(rom, map);
        var list = new List<L2StageState> { start.Copy() };
        var cur = start.Copy();
        for (int i = 0; i < steps.Count; i++)
        {
            cur = Apply(cur, steps, i, m, name, sp);
            list.Add(cur);
        }
        return list;
    }

    /// <summary>One step of the script as playback runs it: the step index and the stage after it (with
    /// <see cref="L2StageState.BranchWhy"/> for checks and jumps).</summary>
    public sealed record RunStep(int Step, L2StageState After);

    /// <summary>
    /// Plays the script the way the game would: from the first step, following jumps and checks with the story flags and
    /// counters of <paramref name="start"/> (and those the steps set on the way). Questions take answer
    /// <paramref name="answer"/> (0-based). Stops at the end of the event, a step that leaves it, or after
    /// <paramref name="limit"/> steps (a loop).
    /// </summary>
    public static List<RunStep> Run(RomBuffer rom, int map, L2StageState start, IReadOnlyList<L2Step> steps, Func<int, string> name, int answer = 0, int limit = 3000)
    {
        var m = L2StageMap.Read(rom, map);
        var sp = new L2Speakers(rom, map);
        var run = new List<RunStep>();
        var cur = start.Copy();
        int i = 0;
        while (i >= 0 && i < steps.Count && run.Count < limit)
        {
            cur = Apply(cur, steps, i, m, name, sp);
            int next = Next(steps, i, cur, answer);
            run.Add(new RunStep(i, cur));
            i = next;
        }
        return run;
    }

    /// <summary>Where the event goes after step <paramref name="i"/> (the stage after it given: flags and counters);
    /// sets the state's branch fields for a check or a jump. -1: it ends.</summary>
    private static int Next(IReadOnlyList<L2Step> steps, int i, L2StageState s, int answer)
    {
        var st = steps[i];
        var b = st.Bytes;
        int Target(int k)
        {
            if (k >= st.Jumps.Count || st.Jumps[k].Step is not { } t) return -2;
            for (int q = 0; q < steps.Count; q++) if (ReferenceEquals(steps[q], t)) return q;
            return -2;
        }
        int Go(int k, string why) { s.BranchWhy = why; s.BranchTo = Target(k); s.Jumped = true; return s.BranchTo; }
        int On(string why) { s.BranchWhy = why; s.BranchTo = i + 1 < steps.Count ? i + 1 : -1; s.Jumped = false; return s.BranchTo; }
        bool Flag(int f) => s.Flags.Contains(f);
        int Counter(int c) => s.Counters.TryGetValue(c, out int v) ? v : 0;
        switch (st.Op)
        {
            case 0x10 when st.IsText && b.Length > 1 && b[1] > 0:
            {
                int k = Math.Clamp(answer, 0, b[1] - 1);
                return Go(k, $"answer {k + 1} is picked");
            }
            case 0x1C when !st.IsText: return Go(0, "always");
            case 0x15 when !st.IsText && b.Length >= 2:
                return Flag(b[1]) ? Go(0, $"story flag {b[1]:X2} is on") : On($"story flag {b[1]:X2} is off");
            case 0x6A when !st.IsText && b.Length >= 2:
                return !Flag(b[1]) ? Go(0, $"story flag {b[1]:X2} is off") : On($"story flag {b[1]:X2} is on");
            case 0x12 when !st.IsText && b.Length >= 3 && b[2] > 0:
            {
                int c = Counter(b[1]), k = Math.Min(c, b[2] - 1);
                s.Counters[b[1]] = Math.Min(255, c + 1);
                return Go(k, $"counter {b[1]:X2} is {c}: choice {k + 1}");
            }
            case 0x14 when !st.IsText:
            {
                bool result = false;
                int jump = 0;
                var why = new List<string>();
                foreach (var c in L2Conditions.Parse(b))
                {
                    int at = c.At;
                    int join = c.Code & 0xC0;
                    bool v;
                    switch (c.Kind)
                    {
                        case L2Conditions.KFlag when at + 1 < b.Length:
                            v = Flag(b[at + 1]) == ((c.Code & 1) == 0);
                            why.Add($"story flag {b[at + 1]:X2} is {(Flag(b[at + 1]) ? "on" : "off")}");
                            break;
                        case L2Conditions.KCounter when at + 2 < b.Length:
                        {
                            int val = Counter(b[at + 1]), w = b[at + 2];
                            v = (c.Code & 7) switch { 0 => val == w, 1 => val != w, 2 => val >= w, 3 => val <= w, 4 => val > w, _ => val < w };
                            why.Add($"counter {b[at + 1]:X2} is {val}");
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
                            // items, HP, spells, levels...: the stage doesn't know them
                            v = false; join = 0;
                            why.Add("a check of the party (taken as not so)");
                            break;
                    }
                    result = join switch { 0x40 => result || v, 0x80 => result && v, _ => v };
                }
                return On((why.Count > 0 ? string.Join(", ", why) : "the check") + ": goes on");
            }
        }
        if (st.EndsFlow) return -1;
        return i + 1;
    }

    /// <summary>The stage after step <paramref name="i"/> of <paramref name="steps"/>, from <paramref name="cur"/>.</summary>
    private static L2StageState Apply(L2StageState cur, IReadOnlyList<L2Step> steps, int i, L2StageMap m, Func<int, string> name, L2Speakers sp)
    {
        var st = steps[i];
        var points = m.Points;
        var s = cur.Copy();
        s.Speaker = null; s.Text = null; s.MovedId = -1;
        s.Frames = 6;
        L2StageState.Actor A(int id)
        {
            if (!s.Actors.TryGetValue(id, out var a)) s.Actors[id] = a = new L2StageState.Actor { Id = id, Sprite = id < 7 ? id : -1 };
            return a;
        }
        var b = st.Bytes;
        int op = st.Op;
        if (st.IsText)
        {
            // as the game picks the speaker (L2Speakers): 13 / 61-67 name one, 08 and 10 speak as the current one
            if (op is 0x13 or (>= 0x61 and <= 0x67)) s.Talker = SpeakerActor(b);
            int who = s.Talker;
            int drawnAs = who is >= 0 and < 7 && s.Actors.TryGetValue(who, out var spk) && spk.Sprite >= 0 && spk.Sprite != who ? spk.Sprite : -1;
            string label = who == 0xFF ? "" : sp.Label(who, drawnAs, sprite: false);
            s.Speaker = op switch { 0x9E => "", 0x10 => label.Length > 0 ? label + " asks" : "question", _ => label.Length > 0 ? label : "no speaker set" };
            s.Text = st.Text;
            s.Caption = op == 0x9E ? "Narration" : who == 0xFF ? (op == 0x10 ? "A question" : "Text with no speaker set (the box goes over the last character the game found)") : $"{label} speaks";
            s.Frames = 60 + st.Text.Length * 2;
            if (st.End == 0x01 && s.TalkSave != 0xFF) s.Talker = s.TalkSave;
        }
        else switch (op)
        {
            case 0x34 when b.Length >= 3: { var a = A(b[1]); a.Sprite = L2Sprites.ToEditor(sp.Rom, sp.Map, b[2]); s.Caption = $"{name(b[1])} is shown as sprite {L2Sprites.Name(a.Sprite)}"; break; }
            case 0x2F when b.Length >= 4: { var a = A(b[1]); a.X = b[2]; a.Y = b[3]; s.Caption = $"{name(b[1])} is put on ({b[2]}, {b[3]})"; break; }
            case 0x30 when b.Length >= 3 && points.TryGetValue(b[2], out var p): { var a = A(b[1]); a.X = p.X; a.Y = p.Y; s.Caption = $"{name(b[1])} is put on point {b[2]}"; break; }
            case 0x98 when b.Length >= 3: { var a = A(b[1]); if (points.TryGetValue(b[2], out var p2)) { a.X = p2.X; a.Y = p2.Y; } a.Visible = true; s.Caption = $"{name(b[1])} appears"; s.Frames = 30; break; }
            case 0x2D when b.Length >= 2: A(b[1]).Visible = true; s.Caption = $"{name(b[1])} appears"; break;
            case 0x2E or 0x99 or 0x9D when b.Length >= 2: A(b[1]).Visible = false; s.Caption = $"{name(b[1])} leaves"; s.Frames = op == 0x2E ? 6 : 30; break;
            case 0x31 when b.Length >= 3 && b[2] is >= 1 and <= 4: A(b[1]).Dir = new[] { 0, 0, 1, 2, 3 }[b[2]]; s.Caption = $"{name(b[1])} turns"; break;
            case 0x7A when b.Length >= 3 && b[2] < 8: A(b[1]).Dir = new[] { 1, 2, 0, 3 }[b[2] / 2]; s.Caption = $"{name(b[1])} turns"; break;
            case 0xA3 when b.Length >= 3:
            {
                var a = A(b[1]);
                int act = b[2], d = act & 3;
                int move = act switch
                {
                    <= 0x03 or (>= 0x94 and <= 0x97) or (>= 0x74 and <= 0x77) or (>= 0x54 and <= 0x5B) or (>= 0x88 and <= 0x8B) or (>= 0x68 and <= 0x6B) => 1,
                    >= 0x81 and <= 0x84 => 1,
                    >= 0x3C and <= 0x3F => 2,
                    >= 0x50 and <= 0x53 => 6,
                    _ => 0,
                };
                if (act is >= 0x81 and <= 0x84) d = act - 0x81;
                if (act is <= 0x07 or (>= 0x18 and <= 0x1B) or (>= 0x94 and <= 0x97)) a.Dir = d;
                if (act is >= 0x81 and <= 0x84) a.Dir = d ^ 1;
                // a plain step (A3 00-03) stops at walls and other characters, as in the game
                if (move > 0 && a.Placed && act <= 0x03 && a.Visible)
                {
                    string? hit = m.Wall(a.X, a.Y, d);
                    int nx = a.X + Dx[d], ny = a.Y + Dy[d];
                    if (hit == null && !m.InArea(a.Id, nx, ny) && m.Areas.TryGetValue(a.Id, out var ar))
                        hit = $"the edge of its walk area ({ar.X1}, {ar.Y1})-({ar.X2 - 1}, {ar.Y2 - 1}) (its character start on the Maps page)";
                    if (hit == null)
                    {
                        var other = s.Actors.Values.FirstOrDefault(o => o.Id != a.Id && o.Visible && o.X == nx && o.Y == ny && !MovesAway(steps, i, o.Id, d));
                        if (other != null) hit = name(other.Id);
                    }
                    if (hit != null)
                    {
                        s.Warning = $"{Cap(name(b[1]))} can't walk {L2Beat.DirNames[d]} from ({a.X}, {a.Y}): {hit} is in the way, so the game stops the walk there.";
                        s.WarnX = nx; s.WarnY = ny;
                        move = 0;
                    }
                }
                if (move > 0 && a.Placed) { a.X += Dx[d] * move; a.Y += Dy[d] * move; s.MovedId = a.Id; }
                if (act is 0x85 or 0x61 or 0x5F or 0x5D or 0x63) a.Visible = false;
                if (act is 0x86 or 0x62 or 0x60) a.Visible = true;
                s.Caption = s.Warning != null ? $"{name(b[1])} is stopped" : move > 0 ? $"{name(b[1])} walks {L2Beat.DirNames[d]}" : $"{name(b[1])}: {L2EventCommands.Actions.FirstOrDefault(x => x.Item1 == act).Item2 ?? $"action {act:X2}"}";
                s.Frames = move > 0 ? 16 * move : 24;
                break;
            }
            case 0x89: s.Frames = 0; s.Caption = ""; break;
            case 0x3A when b.Length >= 3: s.CameraX = b[1]; s.CameraY = b[2]; s.CameraOnLeader = false; s.Caption = "The camera jumps"; break;
            case 0xC4 when b.Length >= 3: s.CameraX = b[1]; s.CameraY = b[2]; s.CameraOnLeader = false; s.Caption = "The camera scrolls"; s.Frames = 40; break;
            case 0x3B when b.Length >= 2 && points.TryGetValue(b[1], out var p3): s.CameraX = p3.X; s.CameraY = p3.Y; s.CameraOnLeader = false; break;
            case 0x41 when b.Length >= 3:
            {
                int form = L2EventCommands.CameraForm(b);
                if (form == 0) { s.CameraOnLeader = true; s.Caption = "The camera goes back to the leader"; s.Frames = 30; }
                else if (form == 1) { int d = (b[1] >> 4) - 1; if (d is >= 0 and <= 3) { s.CameraX += Dx[d] * b[2]; s.CameraY += Dy[d] * b[2]; s.CameraOnLeader = false; s.Frames = 30; } }
                else if (form == 3 && b.Length >= 4) { s.CameraX = b[2] + 1 + 8; s.CameraY = b[3] + 7; s.CameraOnLeader = false; }
                else if (form == 2 && points.TryGetValue(b[2], out var p4)) { s.CameraX = p4.X; s.CameraY = p4.Y; s.CameraOnLeader = false; }
                break;
            }
            case 0x75: s.CameraOnLeader = true; break;
            case 0x58: s.Dark = true; s.Caption = "Fade to black"; s.Frames = 30; break;
            case 0x59 or 0xA4: s.Dark = false; s.Caption = "Fade in"; s.Frames = 30; break;
            case 0x3E: s.Dark = true; break;
            case 0x3F: s.Dark = false; break;
            case 0x6F or 0x4B when b.Length >= 2: s.Song = b[1]; s.Caption = "Music"; break;
            case 0x72: s.Song = -1; break;
            case 0x5A: s.Shaking = true; s.Caption = "The screen shakes"; break;
            case 0x8A or 0x56: s.Shaking = false; break;
            case 0x37 when b.Length >= 2: s.Frames = b[1]; s.Caption = "Wait"; break;
            case 0x38 when b.Length >= 2: s.Frames = b[1] * 60; s.Caption = "Wait"; break;
            case 0x1A when b.Length >= 2: s.Flags.Add(b[1]); s.Caption = $"Story flag {b[1]:X2} on"; break;
            case 0x1B when b.Length >= 2: s.Flags.Remove(b[1]); s.Caption = $"Story flag {b[1]:X2} off"; break;
            case 0x1D when b.Length >= 3: s.Counters[b[1]] = b[2]; s.Caption = $"Counter {b[1]:X2} = {b[2]}"; break;
            case 0x1E when b.Length >= 3: s.Counters[b[1]] = Math.Min(255, (s.Counters.TryGetValue(b[1], out int c1) ? c1 : 0) + b[2]); s.Caption = $"Counter {b[1]:X2} + {b[2]}"; break;
            case 0x1F when b.Length >= 3: s.Counters[b[1]] = Math.Max(0, (s.Counters.TryGetValue(b[1], out int c2) ? c2 : 0) - b[2]); s.Caption = $"Counter {b[1]:X2} - {b[2]}"; break;
            case 0x16: s.Caption = "Go to another map"; s.Dark = true; break;
            case 0x00 or 0x42: s.Caption = "End"; s.Dark = false; s.CameraOnLeader = true; break;
        }
        if (s.CameraOnLeader && s.Actors.TryGetValue(0, out var lead) && lead.Placed) { s.CameraX = lead.X; s.CameraY = lead.Y; }
        return s;
    }

    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>Whether actor <paramref name="id"/> steps in direction <paramref name="dir"/> in the same round as step
    /// <paramref name="i"/> (the A3 commands next to it, before the waits): walking together in a line, the one in front
    /// moves out of the way.</summary>
    private static bool MovesAway(IReadOnlyList<L2Step> steps, int i, int id, int dir)
    {
        bool Step(L2Step x) => !x.IsText && x.Op == 0xA3 && x.Bytes.Length >= 3 && x.Bytes[2] <= 0x03;
        for (int k = i + 1; k < steps.Count && Step(steps[k]); k++) if (steps[k].Bytes[1] == id && steps[k].Bytes[2] == dir) return true;
        for (int k = i - 1; k >= 0 && Step(steps[k]); k--) if (steps[k].Bytes[1] == id && steps[k].Bytes[2] == dir) return true;
        return false;
    }

    /// <summary>The story flags and counters the script checks (15, 6A, 14 and 12), for the stage's starting state.</summary>
    public static (SortedSet<int> Flags, SortedSet<int> Counters) Checked(IEnumerable<L2Step> steps)
    {
        var flags = new SortedSet<int>();
        var counters = new SortedSet<int>();
        foreach (var st in steps.Where(x => !x.IsText))
        {
            var b = st.Bytes;
            if (st.Op is 0x15 or 0x6A && b.Length >= 2) flags.Add(b[1]);
            if (st.Op == 0x12 && b.Length >= 2) counters.Add(b[1]);
            if (st.Op == 0x14)
                foreach (var c in L2Conditions.Parse(b))
                {
                    if (c.Kind == L2Conditions.KFlag && c.At + 1 < b.Length) flags.Add(b[c.At + 1]);
                    if (c.Kind == L2Conditions.KCounter && c.At + 1 < b.Length) counters.Add(b[c.At + 1]);
                }
        }
        return (flags, counters);
    }

    /// <summary>The frame of a map sprite for a facing (0 up, 1 down, 2 left, 3 right) and walking phase (0 or 1):
    /// sprites with six frames have two each for down (0, 1), left (2, 3) and up (4, 5); right is left mirrored (as
    /// "Face" 7A's frames 00 down, 02 left, 04 up, 06 right). Sprites with two frames only step in place, others have
    /// one picture.</summary>
    public static (int Frame, bool Flip) FacingFrame(RomBuffer rom, int sprite, int dir, int phase)
    {
        int flags = 0;
        try { if (sprite >= 0 && sprite < L2Sprites.Count(rom)) flags = rom.ReadByte(L2Sprites.EntryAt(rom, sprite)); } catch (Exception) { }
        phase &= 1;
        if ((flags & 0x10) != 0) return ((dir & 3) switch { 0 => 4, 1 => 0, _ => 2 } + phase, (dir & 3) == 3);
        if ((flags & 0x08) != 0) return (phase, false);
        return (0, false);
    }

    /// <summary>Whether the sprite has pictures for each facing.</summary>
    public static bool HasFacings(RomBuffer rom, int sprite)
    {
        try { return sprite >= 0 && sprite < L2Sprites.Count(rom) && (rom.ReadByte(L2Sprites.EntryAt(rom, sprite)) & 0x10) != 0; }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ making cutscenes

    /// <summary>A story flag no event and no game code uses (from EF down), not in <paramref name="avoid"/>; -1 if none.</summary>
    public static int FreeFlag(RomBuffer rom, IEnumerable<int>? avoid = null)
    {
        var used = new HashSet<int>(avoid ?? Array.Empty<int>());
        for (int m = 0; m < L2Maps.Count; m++)
            foreach (var (_, ops) in L2Script.MapScripts(rom, m))
                foreach (var o in ops.Where(o => !o.IsText))
                {
                    int c = rom.ReadByte(o.At);
                    if (c is 0x1A or 0x1B or 0x15 or 0x6A) used.Add(rom.ReadByte(o.At + 1));
                    if (c == 0x14)
                        for (int q = o.At + 1, g = 0; g < 64 && q < o.At + o.Length; g++)
                        {
                            int k = rom.ReadByte(q++);
                            if (k == 0xFF) break;
                            if ((k & 0xC0) == 0xC0) { q += (k & 0xF8) switch { 0xC0 => 4, 0xC8 or 0xD0 or 0xD8 => 3, _ => 2 }; continue; }
                            if ((k & 0x30) == 0) { used.Add(rom.ReadByte(q)); q += 1; } else q += 2;
                        }
                }
        for (int f = 0xEF; f > 0; f--)
            if (!used.Contains(f) && f is not (>= 0xC8 and <= 0xD0) && f is not (0x95 or 0x41 or 0x50 or 0x5F or 0x62)) return f;
        return -1;
    }

    /// <summary>A scene number the map doesn't use yet (from 0x20 up, below the F0 the test start uses).</summary>
    public static int FreeScene(RomBuffer rom, int map)
    {
        int block = L2Events.Block(rom, map);
        var used = block < 0 ? new HashSet<int>() : L2EventBlocks.ReadLists(rom, block).Where(e => e.Head == L2EventBlocks.SceneList).Select(e => e.Id).ToHashSet();
        for (int id = 0x20; id < 0xF0; id++) if (!used.Contains(id)) return id;
        throw new InvalidOperationException("The map has no free scene number.");
    }

    /// <summary>What a new cutscene is made with.</summary>
    public sealed class NewOptions
    {
        public StartKind Start { get; init; }
        /// <summary>Plays only once (a free story flag is checked first and turned on).</summary>
        public bool Once { get; init; } = true;
        /// <summary>Map loads: a story flag that must be on (or off, <see cref="FlagMustBeOn"/>) for it to play (-1: none).</summary>
        public int Flag { get; init; } = -1;
        public bool FlagMustBeOn { get; init; } = true;
        /// <summary>Map loads / arrive: the preset point the party stands on.</summary>
        public int Point { get; init; } = -1;
        /// <summary>Step on: the area (inclusive tiles).</summary>
        public int X1 { get; init; }
        public int Y1 { get; init; }
        public int X2 { get; init; }
        public int Y2 { get; init; }
        /// <summary>Talk: the character.</summary>
        public int Character { get; init; } = -1;
    }

    /// <summary>The result of <see cref="Create"/>: the event made and what was done.</summary>
    public sealed record Made(int Head, int Id, int Flag, string What);

    /// <summary>Makes a new cutscene on a map: its event (a first line of text to start from) and the way it starts.</summary>
    public static Made Create(RomBuffer rom, int map, NewOptions o)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) throw new InvalidOperationException("This map has no event block.");
        switch (o.Start)
        {
            case StartKind.Arrive:
            case StartKind.MapLoads:
            {
                int scene = FreeScene(rom, map);
                var s = L2EventScript.New(block);
                // arriving through "Go to another map" or a new game, the screen is black and the leader hidden
                var steps = new List<L2Step>
                {
                    new() { Bytes = new byte[] { 0x2D, 0x00 } },
                    new() { Bytes = new byte[] { 0x59, 0x13 } }, new() { Bytes = new byte[] { 0x76 } },
                    new() { Bytes = new byte[] { 0x61 }, IsText = true, Text = "...", End = 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" },
                };
                s.Steps.InsertRange(0, steps);
                int flag = -1;
                string how = "";
                if (o.Start == StartKind.MapLoads)
                {
                    if (o.Point < 0) throw new InvalidOperationException("Pick the point where the party stands.");
                    flag = FreeFlag(rom, o.Flag >= 0 ? new[] { o.Flag } : null);
                    if (flag < 0) throw new InvalidOperationException("No story flag is free.");
                    // the "on entering" event must take the starter before the scene is written
                    if (L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1) is { } enter &&
                        L2EventScript.Read(rom, block + enter.Offset, block).Problem is { } why)
                        throw new InvalidOperationException("The map's \"on entering\" event can't be changed: " + why);
                    L2EventBlocks.Add(rom, map, L2EventBlocks.SceneList, scene, s);
                    how = " " + AddStarter(rom, map, scene, o.Point, flag, o.Flag, o.FlagMustBeOn);
                }
                else L2EventBlocks.Add(rom, map, L2EventBlocks.SceneList, scene, s);
                return new Made(L2EventBlocks.SceneList, scene, flag, $"New cutscene: scene {scene:X2} of map {map:X2}.{how}");
            }
            case StartKind.StepOn:
            {
                int flag = o.Once ? FreeFlag(rom) : -1;
                if (o.Once && flag < 0) throw new InvalidOperationException("No story flag is free.");
                var s = L2EventBlocks.NewScript(block, new byte[] { 0x61 }, "...", flag);
                int id = L2EventBlocks.AddTrigger(rom, map, Math.Min(o.X1, o.X2), Math.Min(o.Y1, o.Y2), Math.Max(o.X1, o.X2), Math.Max(o.Y1, o.Y2), s);
                return new Made(L2EventBlocks.TriggerList, id, flag, $"New cutscene: stepping onto ({o.X1},{o.Y1})-({o.X2},{o.Y2}) of map {map:X2} starts it{(flag >= 0 ? $", once (story flag {flag:X2})" : "")}.");
            }
            default:
            {
                if (o.Character < 0) throw new InvalidOperationException("Pick the character the party talks to.");
                if (L2EventBlocks.ReadLists(rom, block).Any(e => e.Head == L2EventBlocks.TalkList && e.Id == o.Character))
                    throw new InvalidOperationException("Talking to this character already starts an event: pick it in the list to make it a cutscene.");
                int flag = o.Once ? FreeFlag(rom) : -1;
                if (o.Once && flag < 0) throw new InvalidOperationException("No story flag is free.");
                var s = L2EventBlocks.NewScript(block, new byte[] { 0x08 }, "...", flag);
                L2EventBlocks.Add(rom, map, L2EventBlocks.TalkList, o.Character, s);
                return new Made(L2EventBlocks.TalkList, o.Character, flag, $"New cutscene: talking to character {o.Character - 0x4F} of map {map:X2} starts it{(flag >= 0 ? $", once (story flag {flag:X2})" : "")}.");
            }
        }
    }

    /// <summary>Steps at the top of the map's "on entering" event (+2 id 01): when <paramref name="played"/> is off (and
    /// the condition flag allows), turn it on and go to the scene on this map. The scene then runs as the map is shown
    /// again.</summary>
    public static string AddStarter(RomBuffer rom, int map, int scene, int point, int played, int flag, bool mustBeOn)
    {
        int block = L2Events.Block(rom, map);
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1);
        var s = entry != null ? L2EventScript.Read(rom, block + entry.Offset, block) : L2EventScript.New(block);
        if (s.Problem != null) throw new InvalidOperationException("The map's \"on entering\" event can't be changed: " + s.Problem);
        var rest = s.Steps[0];
        var add = new List<L2Step>();
        L2Step Skip(int op, int f) { var st = new L2Step { Bytes = new byte[] { (byte)op, (byte)f, 0, 0 } }; st.Jumps.Add(new L2Jump { Step = rest }); return st; }
        add.Add(Skip(0x15, played));
        if (flag >= 0) add.Add(Skip(mustBeOn ? 0x6A : 0x15, flag));
        add.Add(new L2Step { Bytes = new byte[] { 0x1A, (byte)played } });
        add.Add(new L2Step { Bytes = new byte[] { 0x16, (byte)map, (byte)scene, (byte)point } });
        s.Steps.InsertRange(0, add);
        if (entry != null) L2EventBlocks.Save(rom, map, entry, s); else L2EventBlocks.Add(rom, map, L2EventBlocks.GameList, 1, s);
        return $"It plays when the party enters the map{(flag >= 0 ? $" while story flag {flag:X2} is {(mustBeOn ? "on" : "off")}" : "")}, once (story flag {played:X2}), with the party on point {point}.";
    }

    /// <summary>The "on entering" steps that start scene <paramref name="scene"/> of this map: (played flag, condition
    /// flag, must be on, point), or null.</summary>
    public static (int Played, int Flag, bool MustBeOn, int Point)? Starter(RomBuffer rom, int map, int scene)
    {
        int block = L2Events.Block(rom, map);
        if (block < 0) return null;
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 1);
        if (entry == null) return null;
        var st = L2EventScript.Read(rom, block + entry.Offset, block).Steps;
        for (int i = 0; i < st.Count; i++)
        {
            if (!Is(st[i], 0x16, 4) || st[i].Bytes[1] != map || st[i].Bytes[2] != scene) continue;
            int played = i >= 1 && Is(st[i - 1], 0x1A, 2) ? st[i - 1].Bytes[1] : -1;
            int flag = -1; bool on = true;
            if (i >= 3 && (Is(st[i - 2], 0x6A, 4) || Is(st[i - 2], 0x15, 4)) && st[i - 2].Bytes[1] != played) { flag = st[i - 2].Bytes[1]; on = st[i - 2].Op == 0x6A; }
            return (played, flag, on, st[i].Bytes[3]);
        }
        return null;
    }

    /// <summary>The story flag a cutscene checks first to play once ("if it is on, go to the end"), or -1.</summary>
    public static int OnceFlag(L2EventScript script) =>
        script.Steps.Count > 1 && Is(script.Steps[0], 0x15, 4) && script.Steps.Skip(1).Take(3).Any(s => Is(s, 0x1A, 2) && s.Bytes[1] == script.Steps[0].Bytes[1])
            ? script.Steps[0].Bytes[1] : -1;

    /// <summary>
    /// A character that appears only in cutscenes: "Add a map character" at the top of the start-up script (skipped once
    /// <paramref name="flag"/> is on, when it is given), hidden right after the map's characters are made. Cutscene steps
    /// put it on a tile and show it. Not <paramref name="hidden"/>: a character that stands on its tile from the start
    /// (one to talk to). Returns its actor id.
    /// </summary>
    public static int AddCast(RomBuffer rom, int map, int sprite, int flag, int x = 0, int y = 0, bool hidden = true)
    {
        var used = L2EventBlocks.CharacterIds(rom, map);
        int id = 0x50;
        while (used.Contains(id)) id++;
        if (id > 0x6F) throw new InvalidOperationException("The map has 32 characters already.");
        int init = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
        if (script.Problem != null) throw new InvalidOperationException("The map's start-up script can't be changed: " + script.Problem);
        if (script.Steps.Count(s => s.Op is 0x68 or 0x7B) >= 31) throw new InvalidOperationException("The map has as many characters as the game can show.");
        // what a hidden one needs is checked before anything is written (a half-made character would stay in the ROM)
        int block0 = L2Events.Block(rom, map);
        if (hidden && block0 < 0) throw new InvalidOperationException("This map has no event block to hide the character in.");
        if (hidden && L2EventBlocks.ReadLists(rom, block0).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 0) is { } shown0 &&
            L2EventScript.Read(rom, block0 + shown0.Offset, block0).Problem is { } why0)
            throw new InvalidOperationException("The map's \"when shown\" event can't be changed: " + why0);
        int spriteByte = L2Sprites.ToGame(rom, map, sprite, L2MapInfo.NewSpriteBytesUsed(rom, map, true));
        // a character start of its own (the game finds a character's actor slot through it; without one the game's
        // own characters can take the slot): where it waits, standing still. Its walk area is the whole map: a step
        // (A3 00-03) out of the area is refused (checked in the game: a character with a one-tile area didn't walk).
        int w = 255, h = 255;
        try { var m = L2Maps.Read(rom, map); w = Math.Min(255, m.Layers.Max(l => l.Width)); h = Math.Min(255, m.Layers.Max(l => l.Height)); } catch (Exception) { }
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Characters, 8, new[] { (byte)(id - 0x4F), (byte)x, (byte)y, (byte)0, (byte)0, (byte)w, (byte)h, (byte)0x04 }))
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
        // hidden when the map is shown (event +2 id 00). Not in the start-up script: every command there that finds a
        // character ($80:BF6F / BF92) leaves its slot in $A7, the counter 68 makes characters with, and the story
        // characters the game adds after the script would then take the slots of characters already made.
        int block = L2Events.Block(rom, map);
        var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 0);
        var shown = entry != null ? L2EventScript.Read(rom, block + entry.Offset, block) : L2EventScript.New(block);
        if (shown.Problem != null) throw new InvalidOperationException("The map's \"when shown\" event can't be changed: " + shown.Problem);
        shown.Steps.Insert(0, new L2Step { Bytes = new byte[] { 0x2E, (byte)id } });
        if (entry != null) L2EventBlocks.Save(rom, map, entry, shown); else L2EventBlocks.Add(rom, map, L2EventBlocks.GameList, 0, shown);
        return id;
    }

    /// <summary>The characters the map's "when shown" event (+2 id 00) hides: cutscene characters.</summary>
    internal static HashSet<int> HiddenWhenShown(RomBuffer rom, int map)
    {
        var ids = new HashSet<int>();
        try
        {
            int block = L2Events.Block(rom, map);
            var entry = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(e => e.Head == L2EventBlocks.GameList && e.Id == 0);
            if (entry == null) return ids;
            foreach (var s in L2EventScript.Read(rom, block + entry.Offset, block).Steps) if (Is(s, 0x2E, 2)) ids.Add(s.Bytes[1]);
        }
        catch (Exception) { }
        return ids;
    }

    /// <summary>Characters the start-up script makes and hides at once (made for cutscenes).</summary>
    public static HashSet<int> CastIds(RomBuffer rom, int map)
    {
        var made = Creates(rom, map).Keys.ToHashSet();
        var ids = HiddenWhenShown(rom, map);
        ids.IntersectWith(made);
        return ids;
    }

    /// <summary>A new game starts with this cutscene: a scene runs on arrival (the party at <paramref name="point"/>);
    /// a stepping-on cutscene, or a talk one that doesn't speak as "the one talked to", plays right after the test start
    /// (the party on <paramref name="point"/>); others wait for the talk. The story <paramref name="flags"/> and
    /// <paramref name="counters"/> are set as the new game starts (the game's own cutscenes check the story so far and
    /// skip themselves without them), and so is what the cutscene's characters need (<see cref="Needs"/>).</summary>
    public static string SetTest(RomBuffer rom, int map, int head, int id, int point, IReadOnlyCollection<int>? flags = null, IReadOnlyDictionary<int, int>? counters = null)
    {
        if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
        if (!L2StoryClear.PresetPoints(rom, map).Any(p => p.Id == point)) throw new InvalidOperationException($"Map {map:X2} has no preset point {point}: add one on the Maps page.");
        L2StartParty.Keep(rom);   // the whole starting party on the map, for the cutscene's walks
        var (need, place) = Needs(rom, map, head, id);
        var all = (flags ?? Array.Empty<int>()).Union(need).OrderBy(f => f).ToList();
        // a flag ticked on that keeps the map from making one of the cutscene's characters (made only while it is off)
        string clash = "";
        try
        {
            int blk = L2Events.Block(rom, map);
            var ev = blk < 0 ? null : L2EventBlocks.ReadLists(rom, blk).FirstOrDefault(x => x.Head == head && x.Id == id);
            if (ev != null)
            {
                var used = ActorsUsed(L2EventScript.Read(rom, blk + ev.Offset, blk), head, id);
                var off = L2MapInfo.Stages(rom, map).Where(c => used.Contains(c.Id) && c.Flags.Any(f => !f.On && all.Contains(f.Flag)) && !L2MapInfo.Stages(rom, map).Any(o => o.Id == c.Id && !o.Flags.Any(f => !f.On && all.Contains(f.Flag))))
                    .Select(c => c.Id).Distinct().ToList();
                if (off.Count > 0) clash = $" Careful: with the flags ticked the map doesn't make character{(off.Count > 1 ? "s" : "")} {string.Join(", ", off.Select(x => (x - 0x4F).ToString()))}, which the cutscene uses.";
            }
        }
        catch (Exception) { }
        string extra = clash + (need.Count == 0 ? "" : $" Story flag{(need.Count > 1 ? "s" : "")} {string.Join(" ", need.Select(f => f.ToString("X2")))} {(need.Count > 1 ? "are" : "is")} on too: the map makes the cutscene's characters only then.") +
                       (place.Count == 0 ? "" : $" Persistent character{(place.Count > 1 ? "s" : "")} {string.Join(" ", place.Select(p => p.Id.ToString("X2")))} {(place.Count > 1 ? "are" : "is")} sent here first, as the story does.");
        if (head == L2EventBlocks.SceneList && Starter(rom, map, id) is { } st)
        {
            // the "on entering" check: its flag on when it must be, the played flag and a must-be-off flag off
            all.Remove(st.Played);
            if (st.Flag >= 0) { if (st.MustBeOn) { if (!all.Contains(st.Flag)) all.Add(st.Flag); } else all.Remove(st.Flag); }
            return L2MapEdit.SetTestStart(rom, map, point, all, counters, null, place) + $" On entering the map the cutscene starts (with the party on point {st.Point})." + extra;
        }
        if (head == L2EventBlocks.SceneList && all.Count + (counters?.Count ?? 0) + place.Count > 0)
            return L2MapEdit.StartWithFlags(rom, map, id, point, all, counters, place) + $" Then the cutscene (scene {id:X2}) plays." + extra;
        if (head == L2EventBlocks.SceneList)
        {
            rom.WriteByte(L2StoryClear.NewGameMapAt, (byte)map);
            rom.WriteByte(L2StoryClear.NewGameSceneAt, (byte)id);
            rom.WriteByte(L2StoryClear.NewGamePresetAt, (byte)point);
            return $"A new game now starts with this cutscene (map {map:X2}, scene {id:X2}, the party on point {point}).";
        }
        if (head == L2EventBlocks.TriggerList || head == L2EventBlocks.TalkList && !SpeaksAsTalkedTo(rom, map, head, id))
            return L2MapEdit.SetTestStart(rom, map, point, all, counters, (head, id), place) + " The cutscene plays as the game starts" +
                   (head == L2EventBlocks.TriggerList ? " (pick a point on its area: its walks start from there)." : ".") + extra;
        return L2MapEdit.SetTestStart(rom, map, point, all, counters, null, place) +
               (head == L2EventBlocks.TalkList ? " Talk to the character to start the cutscene (it speaks as the one talked to, so it needs the talk)." : "") + extra;
    }

    /// <summary>Commands whose first operand is an actor (33, "walks path", has it second).</summary>
    private static readonly HashSet<int> ActorFirst = new() { 0x13, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x34, 0x6C, 0x7A, 0x89, 0x98, 0x99, 0x9D, 0xA3 };

    /// <summary>The actors a cutscene's steps name (the one talked to included).</summary>
    public static HashSet<int> ActorsUsed(L2EventScript s, int head, int id)
    {
        var ids = new HashSet<int>();
        if (head == L2EventBlocks.TalkList) ids.Add(id);
        foreach (var st in s.Steps)
        {
            var b = st.Bytes;
            if (ActorFirst.Contains(st.Op) && b.Length >= 2) ids.Add(b[1]);
            else if (st.Op == 0x33 && b.Length >= 3) ids.Add(b[2]);
        }
        return ids;
    }

    /// <summary>What a test start needs for the cutscene's characters to be there: the story flags under which the map's
    /// start-up script makes them (only those that must be on: a new game has every flag off), and the persistent
    /// characters (10-4F) it uses that stand on this map at some point but don't start a new game here, with their
    /// place (sent there with 35 id map spot, as the story's own event does).</summary>
    public static (List<int> Flags, List<(int Id, int Map, int Spot)> Place) Needs(RomBuffer rom, int map, int head, int id)
    {
        var flags = new List<int>();
        var place = new List<(int, int, int)>();
        int block = L2Events.Block(rom, map);
        var e = block < 0 ? null : L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == head && x.Id == id);
        if (e == null) return (flags, place);
        var s = L2EventScript.Read(rom, block + e.Offset, block);
        if (s.Problem != null) return (flags, place);
        var used = ActorsUsed(s, head, id);
        var stages = L2MapInfo.Stages(rom, map);
        foreach (int a in used.Where(a => a >= 0x50))
        {
            var creates = stages.Where(c => c.Id == a).ToList();
            if (creates.Count == 0 || creates.Any(c => c.Flags.Count == 0)) continue;   // always made, or never by the map
            foreach (var (f, on) in creates[0].Flags) if (on && !flags.Contains(f)) flags.Add(f);
        }
        // every persistent character the cutscene uses stands where the story puts it on this map: the game draws one
        // only on the map it was sent to, and a step that puts it on a point, shows it or walks it does nothing
        // otherwise (checked in the game: Parcelyte 2C scene 01's Tia, put on a point, and 2D scene 01's Selan, walked,
        // never showed up unsent). One the cutscene sends here itself (35) before using it is left to the cutscene.
        var sentByCutscene = new HashSet<int>();
        var usedFirst = new HashSet<int>();
        foreach (var st in s.Steps)
        {
            var b = st.Bytes;
            if (st.Op == 0x35 && b.Length >= 4 && b[2] == map && !usedFirst.Contains(b[1])) sentByCutscene.Add(b[1]);
            int who = ActorFirst.Contains(st.Op) && b.Length >= 2 ? b[1] : st.Op == 0x33 && b.Length >= 3 ? b[2] : -1;
            if (who >= 0) usedFirst.Add(who);
        }
        var start = L2Persistent.Start(rom);
        var here = L2Persistent.On(L2Persistent.All(rom), map);
        foreach (int a in used.Where(a => a is >= L2Persistent.First and < L2Persistent.First + L2Persistent.Count).OrderBy(a => a))
        {
            if (sentByCutscene.Contains(a) || start.Any(x => x.Id == a && x.Map == map) || !start.Any(x => x.Id == a)) continue;
            // the story never brings it here: character start 1, where the cutscene's own steps move it from
            if (here.FirstOrDefault(p => p.Id == a) is { } p) place.Add((a, map, p.Spot));
            else if (stages.Count > 0) place.Add((a, map, 1));
        }
        return (flags, place);
    }

    /// <summary>What Test in the game sets up for the cutscene's characters (<see cref="Needs"/>), in a line; empty when
    /// it needs nothing.</summary>
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

    /// <summary>A talk cutscene that speaks as "the one talked to" (08): it needs the talk.</summary>
    private static bool SpeaksAsTalkedTo(RomBuffer rom, int map, int head, int id)
    {
        int block = L2Events.Block(rom, map);
        var e = L2EventBlocks.ReadLists(rom, block).FirstOrDefault(x => x.Head == head && x.Id == id);
        return e == null || L2EventScript.Read(rom, block + e.Offset, block).Steps.Any(st => st.Op == 0x08);
    }
}
