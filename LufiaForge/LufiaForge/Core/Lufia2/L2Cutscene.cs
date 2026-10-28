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
                l.Add(new L2Step { Bytes = (byte[])Opener.Clone(), IsText = true, Text = Text, End = 0x01, OriginalEnd = 0xFF, OriginalText = "\u0001" });
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

    public L2StageState Copy()
    {
        var s = new L2StageState { CameraX = CameraX, CameraY = CameraY, CameraOnLeader = CameraOnLeader, Dark = Dark, Shaking = Shaking, Song = Song };
        foreach (var kv in Actors) s.Actors[kv.Key] = kv.Value.Copy();
        return s;
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
        foreach (var o in beat.Steps) script.Steps.Remove(o);
        foreach (var s in script.Steps) foreach (var j in s.Jumps) if (j.Step != null && beat.Steps.Contains(j.Step)) j.Step = next;
    }

    // ------------------------------------------------------------------ the stage

    /// <summary>The map's characters as the map is loaded: character starts (with the sprite their create gives),
    /// characters the start-up script makes without a start (not placed), and those it hides (cutscene characters).</summary>
    public static L2StageState Initial(RomBuffer rom, int map, int leaderX, int leaderY)
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

    /// <summary>The stage after each step of the script, read top to bottom (jumps aren't followed). Element 0 is the
    /// stage before the first step; element i + 1 after step i.</summary>
    public static List<L2StageState> Simulate(RomBuffer rom, int map, L2StageState start, IReadOnlyList<L2Step> steps, Func<int, string> name)
    {
        var points = L2StoryClear.PresetPoints(rom, map).ToDictionary(p => p.Id, p => (p.X, p.Y));
        var list = new List<L2StageState> { start.Copy() };
        var cur = start.Copy();
        foreach (var st in steps)
        {
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
                int who = SpeakerActor(b);
                s.Speaker = op switch { 0x9E => "", 0x08 => "the one talked to", 0x10 => "question", _ => name(who) };
                s.Text = st.Text;
                s.Caption = s.Speaker.Length > 0 ? $"{s.Speaker} speaks" : "Narration";
                s.Frames = 60 + st.Text.Length * 2;
            }
            else switch (op)
            {
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
                    if (move > 0 && a.Placed) { a.X += Dx[d] * move; a.Y += Dy[d] * move; s.MovedId = a.Id; }
                    if (act is <= 0x07 or (>= 0x18 and <= 0x1B) or (>= 0x94 and <= 0x97)) a.Dir = d;
                    if (act is >= 0x81 and <= 0x84) a.Dir = d ^ 1;
                    if (act is 0x85 or 0x61 or 0x5F or 0x5D or 0x63) a.Visible = false;
                    if (act is 0x86 or 0x62 or 0x60) a.Visible = true;
                    s.Caption = move > 0 ? $"{name(b[1])} walks {L2Beat.DirNames[d]}" : $"{name(b[1])}: {L2EventCommands.Actions.FirstOrDefault(x => x.Item1 == act).Item2 ?? $"action {act:X2}"}";
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
                case 0x16: s.Caption = "Go to another map"; s.Dark = true; break;
                case 0x00 or 0x42: s.Caption = "End"; s.Dark = false; s.CameraOnLeader = true; break;
            }
            if (s.CameraOnLeader && s.Actors.TryGetValue(0, out var lead) && lead.Placed) { s.CameraX = lead.X; s.CameraY = lead.Y; }
            list.Add(s);
            cur = s;
        }
        return list;
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
    /// put it on a tile and show it. Returns its actor id.
    /// </summary>
    public static int AddCast(RomBuffer rom, int map, int sprite, int flag, int x = 0, int y = 0)
    {
        var used = L2EventBlocks.CharacterIds(rom, map);
        int id = 0x50;
        while (used.Contains(id)) id++;
        if (id > 0x6F) throw new InvalidOperationException("The map has 32 characters already.");
        int init = L2EventBlocks.InitScript(rom, map);
        var script = L2EventScript.Read(rom, init, L2EventBlocks.InitBase(init));
        if (script.Problem != null) throw new InvalidOperationException("The map's start-up script can't be changed: " + script.Problem);
        if (script.Steps.Count(s => s.Op is 0x68 or 0x7B) >= 31) throw new InvalidOperationException("The map has as many characters as the game can show.");
        int spriteByte = L2Sprites.ToGame(rom, map, sprite, L2MapInfo.NewSpriteBytesUsed(rom, map, true));
        // a character start of its own (the game finds a character's actor slot through it; without one the game's
        // own characters can take the slot): where it waits, unseen, standing still
        if (!L2MapInfo.AddRecord(rom, map, L2MapInfo.Characters, 8, new[] { (byte)(id - 0x4F), (byte)x, (byte)y, (byte)x, (byte)y, (byte)(x + 1), (byte)(y + 1), (byte)0x04 }))
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
    private static HashSet<int> HiddenWhenShown(RomBuffer rom, int map)
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
    /// any other event gets the test start (the party on <paramref name="point"/>, then step on the area or talk).</summary>
    public static string SetTest(RomBuffer rom, int map, int head, int id, int point)
    {
        if (!L2StoryClear.CanStart(rom)) throw new InvalidOperationException("This ROM's new-game code isn't the US game's, so the start can't be moved.");
        if (!L2StoryClear.PresetPoints(rom, map).Any(p => p.Id == point)) throw new InvalidOperationException($"Map {map:X2} has no preset point {point}: add one on the Maps page.");
        if (head == L2EventBlocks.SceneList && Starter(rom, map, id) is { } st)
            return L2MapEdit.SetTestStart(rom, map, point) + $" On entering the map the cutscene starts (with the party on point {st.Point}).";
        if (head == L2EventBlocks.SceneList)
        {
            rom.WriteByte(L2StoryClear.NewGameMapAt, (byte)map);
            rom.WriteByte(L2StoryClear.NewGameSceneAt, (byte)id);
            rom.WriteByte(L2StoryClear.NewGamePresetAt, (byte)point);
            return $"A new game now starts with this cutscene (map {map:X2}, scene {id:X2}, the party on point {point}).";
        }
        return L2MapEdit.SetTestStart(rom, map, point) + (head == L2EventBlocks.TriggerList ? " Walk onto the area to start the cutscene." : head == L2EventBlocks.TalkList ? " Talk to the character to start the cutscene." : "");
    }
}
