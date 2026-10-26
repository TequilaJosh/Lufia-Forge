namespace LufiaForge.Core.Lufia2;

/// <summary>What kind of value a field of a Lufia II event command holds (picks the editor and the wording).</summary>
public enum L2Kind
{
    Number,     // plain number
    Hex,        // a byte shown as hex
    Signed,     // -128..127
    Word,       // u16 number
    Flag,       // story flag 00-FF
    Variable,   // event variable 00-FF
    Hero,       // 0 Maxim .. 6 Lexis
    Who,        // actor: party member, map character, story character
    WhoOrAll,   // the same, FF = everyone
    Item,       // item 000-1FF
    Spell, Song, Sound, Map, Shop, Capsule, Battle, Sprite,
    Point,      // preset point of this map (map info list +0E)
    Path,       // path of this map (list +12)
    Area,       // area of this map (list +14)
    Exit,       // exit of this map (list +06)
    Door,       // door of this map (list +02)
    Scene,      // scene (event block +4 list) of the map in the command's "map" field
    ArrivalPoint, // preset point of the map in the command's "map" field
    Frames,     // a count of frames (60 = 1 second)
    Jump,       // another step of the event
    Choice,     // one of a fixed list (Options)
}

/// <summary>One field of a command: where it sits in the bytes (opcode at 0) and what it means.</summary>
public sealed class L2ParamDef
{
    public string Label { get; init; } = "";
    public L2Kind Kind { get; init; }
    public Func<byte[], int> Get { get; init; } = _ => 0;
    public Action<byte[], int> Set { get; init; } = (_, _) => { };
    public int Min { get; init; }
    public int Max { get; init; } = 255;
    /// <summary>The fixed choices of a <see cref="L2Kind.Choice"/> field.</summary>
    public IReadOnlyList<(int Value, string Name)>? Options { get; init; }
    /// <summary>Which jump of the command (Jump fields).</summary>
    public int JumpIndex { get; init; } = -1;
    /// <summary>A short explanation shown under the field.</summary>
    public string Hint { get; init; } = "";
    /// <summary>For fields that change the command's shape (a camera form, a check added or taken out, the number of
    /// answers): new bytes for the value, and for each jump of the new bytes the old jump it keeps (-1: a new one).
    /// Null: <see cref="Set"/> is enough.</summary>
    public Func<byte[], int, (byte[] Bytes, int[]? JumpMap)>? Rebuild { get; init; }

    public static L2ParamDef Byte(string label, L2Kind kind, int pos, int max = 255, string hint = "", int min = 0) => new()
    {
        Label = label, Kind = kind, Min = min, Max = max, Hint = hint,
        Get = b => kind == L2Kind.Signed ? (sbyte)b[pos] : b[pos],
        Set = (b, v) => b[pos] = (byte)v,
    };

    public static L2ParamDef Word(string label, L2Kind kind, int pos, string hint = "") => new()
    {
        Label = label, Kind = kind, Max = 0xFFFF, Hint = hint,
        Get = b => b[pos] | b[pos + 1] << 8,
        Set = (b, v) => { b[pos] = (byte)v; b[pos + 1] = (byte)(v >> 8); },
    };

    public static L2ParamDef Pick(string label, int pos, IReadOnlyList<(int, string)> options, string hint = "") => new()
    {
        Label = label, Kind = L2Kind.Choice, Options = options, Hint = hint,
        Get = b => b[pos], Set = (b, v) => b[pos] = (byte)v,
    };

    public static L2ParamDef Nibble(string label, L2Kind kind, int pos, bool high, int max = 15, string hint = "", IReadOnlyList<(int, string)>? options = null) => new()
    {
        Label = label, Kind = options != null ? L2Kind.Choice : kind, Max = max, Hint = hint, Options = options,
        Get = b => high ? b[pos] >> 4 : b[pos] & 15,
        Set = (b, v) => b[pos] = (byte)(high ? (b[pos] & 0x0F) | (v & 15) << 4 : (b[pos] & 0xF0) | (v & 15)),
    };

    public static L2ParamDef Jump(string label, int k) => new() { Label = label, Kind = L2Kind.Jump, JumpIndex = k };
}

/// <summary>A command (or a family of opcodes shown as one, like "Give an item" = 20 and 21).</summary>
public sealed class L2CommandDef
{
    public int[] Ops { get; init; } = Array.Empty<int>();
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Help { get; init; } = "";
    public L2ParamDef[] Params { get; init; } = Array.Empty<L2ParamDef>();
    /// <summary>Fields that depend on the bytes (command 41's forms, 14's conditions); null = <see cref="Params"/>.</summary>
    public Func<byte[], L2ParamDef[]>? ParamsFor { get; init; }
    public Func<byte[], L2EventCommands.Words, string> Describe { get; init; } = (_, _) => "";
    /// <summary>Bytes of the command when added from the menu (null: not offered).</summary>
    public byte[]? Template { get; init; }
    /// <summary>The event waits here until something finishes.</summary>
    public string Waits { get; init; } = "";
    /// <summary>How sure the meaning is: G = checked in the game, C = read from the game's code, D = from how the game's own
    /// scripts use it, ? = a guess.</summary>
    public char Sure { get; init; } = 'C';
    /// <summary>Only meaningful in a map's start-up script (or never used by the game: offered under "Rarely used").</summary>
    public bool Rare { get; init; }

    public L2ParamDef[] FieldsFor(byte[] b) => ParamsFor?.Invoke(b) ?? Params;

    public string SureText => Sure switch
    {
        'G' => "Checked in the game.",
        'C' => "Read from the game's code.",
        'D' => "From how the game's own events use it.",
        _ => "A best guess (not confirmed).",
    };
}

/// <summary>
/// Every Lufia II event command in plain words, with its fields (research of 2026-10-25 from the interpreter at
/// $80:9D32 and the 2962 scripts of the 242 maps; docs/lufia2/events.md has the evidence). Directions in commands 31
/// and 41: 1 up, 2 down, 3 left, 4 right. Characters: 0-6 the heroes, 10-4F story characters (they keep their map
/// and hidden state), 50 + n map character n (the map's character start n + 1).
/// </summary>
public static class L2EventCommands
{
    /// <summary>Names for descriptions, supplied by the editor for the map being edited.</summary>
    public sealed class Words
    {
        public Func<int, string> Who { get; init; } = DefaultWho;
        public Func<int, string> Item { get; init; } = i => $"item {i:X3}";
        public Func<int, string> Spell { get; init; } = i => $"spell {i:X2}";
        public Func<int, string> Song { get; init; } = i => $"song {i:X2}";
        public Func<int, string> Map { get; init; } = m => $"map {m:X2}";
        public Func<int, string> Flag { get; init; } = f => $"story flag {f:X2}";
        public Func<int, string> Jump { get; init; } = k => "another step";
        public Func<int, string> Point { get; init; } = p => $"point {p}";
        public Func<int, string> Path { get; init; } = p => $"path {p}";
        public Func<int, string> Area { get; init; } = a => $"area {a}";
        public Func<int, string> Exit { get; init; } = e => $"exit {e}";
        public Func<int, string> Door { get; init; } = d => $"door {d}";
        public Func<int, string> Shop { get; init; } = s => $"shop {s:X2}";
        public Func<int, string> Capsule { get; init; } = c => c < CapsuleNames.Length ? CapsuleNames[c] : $"capsule monster {c}";
    }

    public static string DefaultWho(int id) => id switch
    {
        < 7 => HeroNames[id],
        0xFF => "everyone",
        >= 0x50 and < 0x80 => $"character {id - 0x4F}",
        >= 0x10 and < 0x50 => $"story character {id:X2}",
        _ => $"actor {id:X2}",
    };

    public static readonly string[] HeroNames = { "Maxim", "Selan", "Guy", "Artea", "Tia", "Dekar", "Lexis" };
    public static readonly string[] CapsuleNames = { "Jelze", "Flash", "Gusto", "Zeppy", "Darbi", "Sully", "Blaze" };
    /// <summary>Who speaks for 61-67 (the code maps them to actors 0, 1, 2, 3, 5, 4, 6).</summary>
    public static readonly int[] SpeakerOf61 = { 0, 1, 2, 3, 5, 4, 6 };

    private static (int, string)[] L(params string[] names) => names.Select((n, i) => (i, n)).ToArray();
    private static (int, string)[] L(params (int, string)[] items) => items;

    public static readonly (int, string)[] Directions = L((1, "up"), (2, "down"), (3, "left"), (4, "right"));
    public static readonly (int, string)[] Facings = L((0, "down"), (2, "left"), (4, "up"), (6, "right"));
    public static readonly (int, string)[] Stats = L("HP", "MP", "STR", "AGL", "INT", "GUT", "MGR");
    public static readonly (int, string)[] MoveModes = L(
        (0x00, "Wander around (inside its walk area)"), (0x02, "Wander, more often"), (0x04, "Stand still, never turn"),
        (0x05, "Stand facing down (turns back after talking)"), (0x06, "Stand facing up (turns back after talking)"),
        (0x07, "Stand facing right (turns back after talking)"), (0x08, "Stand facing left (turns back after talking)"),
        (0x09, "Stand still, keep facing"), (0x0A, "Follow the party"));

    /// <summary>Actions (<c>A3</c>, <c>86</c>, <c>BF</c>; the game's table at $91:8000). Groups of four go up, down, left,
    /// right.</summary>
    public static readonly (int, string)[] Actions = L(
        (0x00, "Step up one tile"), (0x01, "Step down one tile"), (0x02, "Step left one tile"), (0x03, "Step right one tile"),
        (0x04, "Walk in place (up)"), (0x05, "Walk in place (down)"), (0x06, "Walk in place (left)"), (0x07, "Walk in place (right)"),
        (0x08, "Back away one tile (down, facing up)"), (0x09, "Jump down off a ledge (3 tiles)"), (0x0A, "Jump down-left off a ledge"), (0x0B, "Jump down-right off a ledge"),
        (0x0C, "Swing (up)"), (0x0D, "Swing (down)"), (0x0E, "Swing (left)"), (0x0F, "Swing (right)"),
        (0x10, "Lift from the tile above"), (0x11, "Lift from the tile below"), (0x12, "Lift from the left"), (0x13, "Lift from the right"),
        (0x14, "Spin while hopping left"),
        (0x18, "Face up"), (0x19, "Face down"), (0x1A, "Face left"), (0x1B, "Face right"),
        (0x1C, "Change to the sprite set by the action settings"), (0x1D, "Stick to the party leader"),
        (0x1E, "Drop from the sky onto its tile"), (0x1F, "Block its tile again"),
        (0x20, "Quick gesture (up)"), (0x21, "Quick gesture (down)"), (0x22, "Quick gesture (left)"), (0x23, "Quick gesture (right)"),
        (0x28, "Turn right, then up"), (0x29, "Turn left, then down"), (0x2A, "Turn up, then left"), (0x2B, "Turn down, then right"),
        (0x30, "Long gesture (up)"), (0x31, "Long gesture (down)"), (0x32, "Long gesture (left)"), (0x33, "Long gesture (right)"),
        (0x3C, "Fall 2 tiles (up)"), (0x3D, "Fall 2 tiles (down)"), (0x3E, "Fall 2 tiles (left)"), (0x3F, "Fall 2 tiles (right)"),
        (0x40, "Hold up an item (facing up)"), (0x41, "Hold up an item (facing down)"), (0x42, "Hold up an item (facing left)"), (0x43, "Hold up an item (facing right)"),
        (0x44, "Hop up onto the tile above"), (0x45, "Hop up from below"), (0x46, "Hop up from the left"), (0x47, "Hop up from the right"),
        (0x50, "Fall 6 tiles (up)"), (0x51, "Fall 6 tiles (down)"), (0x52, "Fall 6 tiles (left)"), (0x53, "Fall 6 tiles (right)"),
        (0x54, "Shift one tile up at once"), (0x55, "Shift one tile down at once"), (0x56, "Shift one tile left at once"), (0x57, "Shift one tile right at once"),
        (0x58, "Slide one tile up"), (0x59, "Slide one tile down"), (0x5A, "Slide one tile left"), (0x5B, "Slide one tile right"),
        (0x5D, "Remove from the map"), (0x5F, "Vanish in a burst"), (0x60, "Reappear in a burst"),
        (0x61, "Vanish (flash)"), (0x62, "Appear (flash)"), (0x63, "Flicker and vanish"),
        (0x64, "Hop in surprise"), (0x68, "Hop one tile up"), (0x69, "Hop one tile down"), (0x6A, "Hop one tile left"), (0x6B, "Hop one tile right"),
        (0x6C, "Big jump 2 tiles up"), (0x6D, "Drop 2 tiles down"),
        (0x6E, "Set down onto the tile above"), (0x6F, "Set down onto the tile below"), (0x70, "Set down onto the left tile"), (0x71, "Set down onto the right tile"),
        (0x74, "Walk up without turning"), (0x75, "Walk down without turning"), (0x76, "Walk left without turning"), (0x77, "Walk right without turning"),
        (0x79, "Collapse (up)"), (0x7A, "Collapse (down)"), (0x7B, "Collapse (left)"), (0x7C, "Collapse (right)"),
        (0x81, "Step back up"), (0x82, "Step back down"), (0x83, "Step back left"), (0x84, "Step back right"),
        (0x85, "Vanish in a flash (warp out)"), (0x86, "Appear in a flash (warp in)"), (0x87, "Blink out and back"),
        (0x88, "Slide slowly up"), (0x89, "Slide slowly down"), (0x8A, "Slide slowly left"), (0x8B, "Slide slowly right"),
        (0x8C, "Tremble (slow, keeps going)"), (0x8D, "Tremble (fast, keeps going)"), (0x8F, "Shake (keeps going)"), (0x90, "Shake, other pose (keeps going)"),
        (0x91, "Fly off up-left"), (0x92, "Ride along with another character (spinning)"), (0x93, "Ride along with another character"),
        (0x94, "Walk up through anything"), (0x95, "Walk down through anything"), (0x96, "Walk left through anything"), (0x97, "Walk right through anything"),
        (0x9A, "Jump 2 tiles up (with a sound)"), (0x9B, "Rocket off into the sky"), (0x9C, "Do nothing for a second"), (0x9D, "Slide one tile up slowly"));

    public static readonly (int, string)[] ActionGroups = L(
        (0x08, "Back away from the leader"), (0x40, "Hold up an item"), (0x64, "Hop in surprise"), (0x79, "Collapse"),
        (0x00, "Step one tile"), (0x18, "Face"), (0x3C, "Fall 2 tiles"), (0x68, "Hop one tile"), (0x81, "Step back"));

    public static readonly (int, string)[] Speeds = Enumerable.Range(0, 16).Select(i => (i, i == 0 ? "at once" : $"{i} px a frame")).ToArray();
    public static readonly (int, string)[] FadeSteps = Enumerable.Range(1, 15).Select(i => (i, i == 1 ? "1 (smooth)" : $"{i}")).Prepend((0, "0")).ToArray();
    public static readonly (int, string)[] TextStyles = L((0x00, "Normal speech box"), (0x10, "Thought box (no voice)"));
    public static readonly (int, string)[] MapModeBits = L((0x01, "dungeon controls"), (0x02, "special palette"), (0x04, "(unknown)"),
        (0x08, "no Escape"), (0x10, "no Warp"), (0x20, "can step down slopes"), (0x40, "no field tools"), (0x80, "no Reset spell"));
    public static readonly (int, string)[] ShipAbilities = L((0, "Sail only"), (1, "Sail and dive"), (2, "Sail, dive and fly"));
    public static readonly (int, string)[] ShipStates = L((0x00, "On the sea"), (0x01, "Under the sea"), (0x02, "In the air"), (0xFF, "No ship"));
    public static readonly (int, string)[] Ferries = L((2, "Aleyn to Narcysus"), (3, "Narcysus to Aleyn"), (4, "Aleyn to Gruberik"),
        (5, "Gruberik to Aleyn"), (6, "Narcysus to Gruberik"), (7, "Gruberik to Narcysus"));
    public static readonly (int, string)[] OnOff = L((0, "off"), (1, "on"));
    public static readonly (int, string)[] Compare = L((0, "is"), (1, "is not"), (2, "is at least"), (3, "is at most"), (4, "is more than"), (5, "is less than"));

    private static string SpeedText(int s) => s == 0 ? "at once" : $"speed {s}";
    private static string Frames(int f) => f == 1 ? "1 frame" : f % 60 == 0 && f > 0 ? $"{f} frames ({f / 60} s)" : $"{f} frames ({f / 60.0:0.#} s)";
    private static string DirName(int d) => d is >= 1 and <= 4 ? Directions[d - 1].Item2 : $"direction {d}";
    private static string Opt(IReadOnlyList<(int, string)> list, int v) => list.FirstOrDefault(o => o.Item1 == v).Item2 ?? $"{v:X2}";
    private static int W(byte[] b, int p) => b[p] | b[p + 1] << 8;

    private static readonly List<L2CommandDef> All = Build();
    private static readonly Dictionary<int, L2CommandDef> ByOp = All.SelectMany(d => d.Ops.Select(o => (o, d))).GroupBy(p => p.o).ToDictionary(g => g.Key, g => g.First().d);

    public static IReadOnlyList<L2CommandDef> Definitions => All;
    public static L2CommandDef? Find(int op) => ByOp.GetValueOrDefault(op);

    public const string Text = "Text", Flow = "Flow and checks", Flags = "Story flags and counters", Chars = "Characters",
        Moves = "Character actions", Party = "Party and items", Cam = "Camera", Screen = "Screen effects", Sound = "Music and sound",
        Places = "Maps and places", Shops = "Shops and services", Time = "Waiting", Special = "Rarely used";

    /// <summary>The order categories are offered in.</summary>
    public static readonly string[] Categories = { Text, Chars, Moves, Party, Flags, Flow, Places, Shops, Cam, Screen, Sound, Time, Special };

    private static List<L2CommandDef> Build()
    {
        var l = new List<L2CommandDef>();
        void Add(int[] ops, string name, string cat, Func<byte[], Words, string> describe, byte[]? template, string help = "",
                 char sure = 'C', string waits = "", bool rare = false, params L2ParamDef[] ps) =>
            l.Add(new L2CommandDef { Ops = ops, Name = name, Category = cat, Describe = describe, Template = template, Help = help, Sure = sure, Waits = waits, Rare = rare, Params = ps });
        void One(int op, string name, string cat, Func<byte[], Words, string> describe, byte[]? template, string help = "",
                 char sure = 'C', string waits = "", bool rare = false, params L2ParamDef[] ps) =>
            Add(new[] { op }, name, cat, describe, template, help, sure, waits, rare, ps);
        L2ParamDef P(string label, L2Kind kind, int pos, int max = 255, string hint = "", int min = 0) => L2ParamDef.Byte(label, kind, pos, max, hint, min);
        L2ParamDef Wd(string label, L2Kind kind, int pos) => L2ParamDef.Word(label, kind, pos);
        L2ParamDef Pk(string label, int pos, IReadOnlyList<(int, string)> options) => L2ParamDef.Pick(label, pos, options);
        L2ParamDef Who(int pos, string label = "who") => P(label, L2Kind.Who, pos);
        L2ParamDef ItemOf(int lowOp) => new()
        {
            Label = "item", Kind = L2Kind.Item, Max = 0x1FF,
            Get = b => (b[0] == lowOp + 1 ? 0x100 : 0) + b[1],
            Set = (b, v) => { b[0] = (byte)(lowOp + (v >= 0x100 ? 1 : 0)); b[1] = (byte)v; },
        };

        // ── text (the text itself is edited in the step; these are the commands around it) ──
        One(0x08, "Text: the person talked to speaks", Text, (_, _) => "The person talked to says:", new byte[] { 0x08 },
            "Opens the text box for the character the player talked to.", sure: 'G');
        One(0x13, "Text: a character speaks", Text, (b, w) => $"{Cap(w.Who(b[1]))} says:", new byte[] { 0x13, 0x50 },
            "Opens the text box for this character (the voice is the character's).", ps: Who(1, "speaker"));
        Add(new[] { 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67 }, "Text: a hero speaks", Text,
            (b, w) => $"{HeroNames[SpeakerOf61[b[0] - 0x61]]} says:", new byte[] { 0x61 },
            "Opens the text box for a hero of the party.", 'G', ps: new L2ParamDef
            {
                Label = "hero", Kind = L2Kind.Choice, Options = Enumerable.Range(0, 7).Select(i => (0x61 + i, HeroNames[SpeakerOf61[i]])).OrderBy(o => SpeakerOf61[o.Item1 - 0x61]).ToArray(),
                Get = b => b[0], Set = (b, v) => b[0] = (byte)v,
            });
        One(0x9E, "Text: narration (centred box)", Text, (b, _) => $"Narration (box at row {b[1]}):", new byte[] { 0x9E, 0x05 },
            "A text box centred on its text, at this row of the screen (0 top, 27 bottom). The voice is off.", ps: P("row", L2Kind.Number, 1, 27));
        l.Add(new L2CommandDef
        {
            Ops = new[] { 0x10 }, Name = "Ask a question", Category = Text, Template = new byte[] { 0x10, 0x02, 0, 0, 0, 0 },
            Describe = (b, w) => "Ask: " + string.Join(", ", Enumerable.Range(0, b[1]).Select(k => $"answer {k + 1} goes to {w.Jump(k)}")),
            Help = "Shows the text with a cursor on the answers (the last lines of the text, one per answer); each answer goes to its step. B closes the box and the event carries on with the next step.",
            ParamsFor = b => Enumerable.Range(0, b[1]).Select(k => L2ParamDef.Jump($"answer {k + 1} goes to", k)).Prepend(Count("answers", 1, 1, 2)).ToArray(),
        });
        One(0x47, "Text box style", Text, (b, _) => $"Text box style: {Opt(TextStyles, b[1]).ToLowerInvariant()}", new byte[] { 0x47, 0x10 },
            "Applies to the text boxes that follow, until changed back (the game always sets it back to normal).", 'G', ps: Pk("style", 1, TextStyles));
        One(0x48, "Text box position", Text, (b, _) => $"Put the next text box at column {b[1]}, row {b[2]}", new byte[] { 0x48, 0x02, 0x02 },
            "For the next text box only. Also turns its voice off.", 'G', ps: new[] { P("column", L2Kind.Number, 1, 31), P("row", L2Kind.Number, 2, 27) });
        One(0x49, "Text turns pages by itself", Text, (b, _) => b[1] == 0 ? "Text waits for a button again" : $"Text goes on by itself after {Frames(b[1])}",
            new byte[] { 0x49, 0xB4 }, "Until the text ends; 0 = wait for a button.", ps: P("frames", L2Kind.Frames, 1));
        One(0x4A, "Text background", Text, (b, _) => $"Text background: {(b[1] == 0 ? "normal" : "see-through")}", new byte[] { 0x4A, 0x00 },
            "The game's opening uses it for its text without a box.", 'G', rare: true, ps: Pk("background", 1, L((0, "normal"), (1, "see-through"))));
        One(0x50, "Text voice off", Text, (_, _) => "Text voice off", new byte[] { 0x50 }, "", rare: true);
        One(0x51, "Text voice from the speaker", Text, (_, _) => "Text voice: the speaker's", new byte[] { 0x51 }, "", rare: true);
        One(0x52, "Text voice: a sound", Text, (b, _) => $"Text voice: sound {b[1]:X2}", new byte[] { 0x52, 0x45 }, "Played every other letter.", rare: true,
            ps: P("sound", L2Kind.Sound, 1, 0x8E));
        One(0xB8, "Next message stays up while the player walks on", Text, (_, _) => "The next message stays up while the player walks on (like a sign)",
            new byte[] { 0xB8 }, "The event ends as soon as the next message is shown; the box stays up for 4 seconds or until the player moves.");

        // ── flow ──
        One(0x00, "End the event", Flow, (_, _) => "End of the event", new byte[] { 0x00 }, "Closes any text box, brings the camera back and ends.", 'G');
        One(0x42, "End the event", Flow, (_, _) => "End of the event", null, "The same as the usual end.", rare: true);
        One(0x1C, "Go to a step", Flow, (_, w) => $"Go to {w.Jump(0)}", new byte[] { 0x1C, 0, 0 }, "", 'G', ps: L2ParamDef.Jump("go to", 0));
        One(0x15, "If a story flag is on, go to a step", Flow, (b, w) => $"If {w.Flag(b[1])} is on, go to {w.Jump(0)}", new byte[] { 0x15, 0, 0, 0 },
            "", 'G', ps: new[] { P("story flag", L2Kind.Flag, 1), L2ParamDef.Jump("go to", 0) });
        One(0x6A, "If a story flag is off, go to a step", Flow, (b, w) => $"If {w.Flag(b[1])} is off, go to {w.Jump(0)}", new byte[] { 0x6A, 0, 0, 0 },
            "", ps: new[] { P("story flag", L2Kind.Flag, 1), L2ParamDef.Jump("go to", 0) });
        l.Add(new L2CommandDef
        {
            Ops = new[] { 0x14 }, Name = "Check conditions", Category = Flow, Template = new byte[] { 0x14, 0x00, 0x00, 0x30, 0, 0, 0xFF },
            Help = "The checks run in order; story flag checks can be joined with \"and\" / \"or\". \"If so, go to\" and \"if not, go to\" jump on the result so far; when no jump is taken the event carries on with the next step.",
            Describe = (b, w) => "Check whether " + L2Conditions.Describe(b, w), ParamsFor = L2Conditions.Fields,
        });
        l.Add(new L2CommandDef
        {
            Ops = new[] { 0x12 }, Name = "Say something different each time", Category = Flow, Template = new byte[] { 0x12, 0x0F, 0x02, 0, 0, 0, 0 },
            Describe = (b, w) => $"Counter {b[1]:X2} picks the next of: " + string.Join(", ", Enumerable.Range(0, b[2]).Select(k => w.Jump(k))) + " (the last one repeats)",
            Help = "Goes to the first step the first time, the second the next time, and so on; the counter is an event variable (start it at 0).",
            ParamsFor = b => Enumerable.Range(0, b[2]).Select(k => L2ParamDef.Jump($"time {k + 1} goes to", k))
                .Prepend(Count("choices", 2, 2, 3)).Prepend(P("counter", L2Kind.Variable, 1)).ToArray(),
        });
        One(0x84, "Stop here forever", Flow, (_, _) => "Stop here forever", null, "The event never goes on (the game never uses it).", rare: true);
        One(0x91, "Check casino coins", Flow, (b, _) => $"Check: at least {W(b, 1)} casino coins (the answer goes in flag FF)", new byte[] { 0x91, 0x01, 0x00 },
            "Follow it with \"If a story flag is on\" for flag FF.", 'D', ps: Wd("coins", L2Kind.Word, 1));
        One(0x92, "Check whether a door is open", Flow, (b, w) => $"Check: is {w.Door(b[1])} open (the answer goes in flag FF)", new byte[] { 0x92, 0x00 },
            "Follow it with \"If a story flag is on\" for flag FF.", ps: P("door", L2Kind.Door, 1));
        One(0xAB, "Check Dragon eggs", Flow, (b, _) => $"Check: at least {b[1]} Dragon eggs (the answer goes in flag FF)", new byte[] { 0xAB, 0x08 },
            "Follow it with \"If a story flag is on\" for flag FF.", ps: P("eggs", L2Kind.Number, 1));

        // ── story flags and counters ──
        One(0x1A, "Turn a story flag on", Flags, (b, w) => $"Turn {w.Flag(b[1])} on", new byte[] { 0x1A, 0x00 },
            "Story flags remember what happened (shared by the whole game).", 'G', ps: P("story flag", L2Kind.Flag, 1));
        One(0x1B, "Turn a story flag off", Flags, (b, w) => $"Turn {w.Flag(b[1])} off", new byte[] { 0x1B, 0x00 }, "", ps: P("story flag", L2Kind.Flag, 1));
        One(0x1D, "Set a counter", Flags, (b, _) => $"Counter {b[1]:X2} = {b[2]}", new byte[] { 0x1D, 0x0F, 0x00 },
            "Counters are event variables (0-255); the game uses 01-0E.", ps: new[] { P("counter", L2Kind.Variable, 1), P("value", L2Kind.Number, 2) });
        One(0x1E, "Add to a counter", Flags, (b, _) => $"Counter {b[1]:X2} + {b[2]}", new byte[] { 0x1E, 0x0F, 0x01 }, "", ps: new[] { P("counter", L2Kind.Variable, 1), P("add", L2Kind.Number, 2) });
        One(0x1F, "Take from a counter", Flags, (b, _) => $"Counter {b[1]:X2} - {b[2]}", new byte[] { 0x1F, 0x0F, 0x01 }, "", ps: new[] { P("counter", L2Kind.Variable, 1), P("take", L2Kind.Number, 2) });

        // ── party and items ──
        Add(new[] { 0x20, 0x21 }, "Give an item", Party, (b, w) => $"Give {w.Item((b[0] - 0x20) * 0x100 + b[1])}{(b[2] != 1 ? $" x{b[2]}" : "")}",
            new byte[] { 0x20, 0x01, 0x01 }, "No message is shown by itself: say it in a text box before.", ps: new[] { ItemOf(0x20), P("how many", L2Kind.Number, 2, 99, min: 1) });
        Add(new[] { 0x24, 0x25 }, "Take an item", Party, (b, w) => $"Take {w.Item((b[0] - 0x24) * 0x100 + b[1])}{(b[2] != 1 ? $" x{b[2]}" : "")}",
            new byte[] { 0x24, 0x01, 0x01 }, "Only if the party has it. A key item is switched instead: taking one the party doesn't have gives it.",
            ps: new[] { ItemOf(0x24), P("how many", L2Kind.Number, 2, 99, min: 1) });
        One(0x22, "Give gold", Party, (b, _) => $"Give {W(b, 1)} gold", new byte[] { 0x22, 0x64, 0x00 }, "", ps: Wd("gold", L2Kind.Word, 1));
        One(0x26, "Take gold", Party, (b, _) => $"Take {W(b, 1)} gold", new byte[] { 0x26, 0x64, 0x00 }, "Nothing is taken when the party has less.", ps: Wd("gold", L2Kind.Word, 1));
        One(0x23, "A hero learns a spell", Party, (b, w) => $"{HeroName(b[1])} learns {w.Spell(b[2])}", new byte[] { 0x23, 0x00, 0x00 },
            "Only when the spell can be learned by that hero; no message.", ps: new[] { P("hero", L2Kind.Hero, 1, 6), P("spell", L2Kind.Spell, 2) });
        One(0x2B, "A hero joins the party", Party, (b, _) => $"{HeroName(b[1])} joins the party", new byte[] { 0x2B, 0x01 },
            "Follow it with \"Line up the party\" so the sprite shows.", ps: P("hero", L2Kind.Hero, 1, 6));
        One(0x2C, "A hero leaves the party", Party, (b, _) => $"{HeroName(b[1])} leaves the party", new byte[] { 0x2C, 0x01 }, "The last member never leaves.", ps: P("hero", L2Kind.Hero, 1, 6));
        One(0x28, "Fully restore the party", Party, (_, _) => "Fully restore the party (HP, MP, revives)", new byte[] { 0x28 }, "No fade and no music, unlike the inn.");
        One(0x29, "Raise a hero's stat", Party, (b, _) => $"{HeroName(b[1])}: {Opt(Stats, b[2])} + {b[3]}/256 of its base", new byte[] { 0x29, 0x00, 0x00, 0x10 },
            "A permanent bonus of base x amount / 256.", ps: new[] { P("hero", L2Kind.Hero, 1, 6), Pk("stat", 2, Stats), P("amount", L2Kind.Number, 3) });
        One(0xB9, "Fill a hero's IP gauge", Party, (b, _) => $"Fill {HeroName(b[1])}'s IP gauge", new byte[] { 0xB9, 0x00 }, "", ps: P("hero", L2Kind.Hero, 1, 6));
        One(0xBA, "Take off a hero's equipment", Party, (b, _) => $"Take off all of {HeroName(b[1])}'s equipment", new byte[] { 0xBA, 0x00 },
            "Used when a hero leaves for good; the items can be bought back in a shop.", 'D', ps: P("hero", L2Kind.Hero, 1, 6));
        One(0x81, "A capsule monster joins", Party, (b, w) => $"{w.Capsule(b[1])} joins (and the event ends)", new byte[] { 0x81, 0x00 },
            "Shows the capsule monster screen; the event ends there.", 'G', ps: P("capsule monster", L2Kind.Capsule, 1, 6));
        One(0x3C, "The party gathers on the leader", Party, (_, _) => "The party walks together onto the leader", new byte[] { 0x3C },
            "", waits: "until they are there");
        One(0x3D, "Line up the party", Party, (_, _) => "Line up the party behind the leader (after members join or leave)", new byte[] { 0x3D, 0x00 }, "", 'D');

        // ── characters ──
        One(0x2D, "Show a character", Chars, (b, w) => $"Show {w.Who(b[1])}", new byte[] { 0x2D, 0x50 },
            "Story characters (10-4F) stay shown.", ps: Who(1));
        One(0x2E, "Hide a character", Chars, (b, w) => $"Hide {w.Who(b[1])}", new byte[] { 0x2E, 0x50 }, "Story characters (10-4F) stay hidden.", ps: Who(1));
        One(0x30, "Put a character on a point", Chars, (b, w) => $"Put {w.Who(b[1])} on {w.Point(b[2])}", new byte[] { 0x30, 0x50, 0x01 },
            "Moves it there at once and holds it still.", ps: new[] { Who(1), P("point", L2Kind.Point, 2) });
        One(0x2F, "Put a character on a tile", Chars, (b, w) => $"Put {w.Who(b[1])} on tile ({b[2]}, {b[3]})", new byte[] { 0x2F, 0x50, 0, 0 },
            "Moves it there at once and holds it still.", rare: true, ps: new[] { Who(1), P("x", L2Kind.Number, 2), P("y", L2Kind.Number, 3) });
        One(0x31, "A character turns", Chars, (b, w) => $"{Cap(w.Who(b[1]))} turns {DirName(b[2])}", new byte[] { 0x31, 0x00, 0x02 },
            "", ps: new[] { Who(1), Pk("to face", 2, Directions) });
        One(0x33, "A character walks a path", Chars, (b, w) => $"{Cap(w.Who(b[2]))} walks {w.Path(b[1])}", new byte[] { 0x33, 0x00, 0x00 },
            "Paths belong to the map; most make the event wait until the walk is done.", ps: new[] { Who(2), P("path", L2Kind.Path, 1) });
        One(0x32, "Let a character move on its own", Chars,
            (b, w) => b[1] == 0xFF ? "Everyone held by the event moves on their own again" : $"{Cap(w.Who(b[1]))}: {Opt(MoveModes, b[2]).ToLowerInvariant()}",
            new byte[] { 0x32, 0x50, 0x05 }, "Releases a character the event holds; with everyone, each goes back to its own way of moving.",
            ps: new[] { P("who", L2Kind.WhoOrAll, 1), Pk("moves", 2, MoveModes) });
        One(0x77, "Hold a character still", Chars, (b, w) => $"Hold {w.Who(b[1])} still", new byte[] { 0x77, 0x50 }, "It stops wandering until let go.", ps: Who(1));
        One(0xBE, "Hold a character still", Chars, (b, w) => $"Hold {w.Who(b[1])} still", null, "", ps: Who(1));
        One(0x78, "Everyone stands still", Chars, (_, _) => "All map characters stand still", new byte[] { 0x78 }, "\"Let everyone move on their own\" undoes it.", 'D');
        One(0x79, "Wait for everyone to stop walking", Chars, (_, _) => "Wait until nobody is walking a path", new byte[] { 0x79 }, "", waits: "until every walk is done");
        One(0x36, "Wait for a character to stop walking", Chars, (b, w) => $"Wait until {w.Who(b[1])} is done walking", new byte[] { 0x36, 0x00 }, "",
            waits: "until the walk is done", rare: true, ps: Who(1));
        One(0x34, "Change a character's sprite", Chars, (b, w) => $"{Cap(w.Who(b[1]))} looks like sprite {b[2]:X2}", new byte[] { 0x34, 0x50, 0x0A },
            "", ps: new[] { Who(1), P("sprite", L2Kind.Sprite, 2) });
        One(0x35, "Send a story character to another map", Chars, (b, w) => $"{Cap(w.Who(b[1]))} goes to {w.Map(b[2])}, character start {b[3]}",
            new byte[] { 0x35, 0x10, 0x03, 0x01 }, "For story characters (10-4F): from now on they stand on that map at that start. Hidden here at once.",
            ps: new[] { P("story character", L2Kind.Who, 1, 0x4F, min: 0x10), P("map", L2Kind.Map, 2), P("start", L2Kind.Number, 3) });
        One(0x68, "Add a map character (start-up script)", Chars, (b, w) => $"Add {w.Who(b[1])} with sprite {b[2]:X2}", null,
            "Only in a map's start-up script, before anything else that uses characters.", 'G', rare: true, ps: new[] { Who(1), P("sprite", L2Kind.Sprite, 2) });
        One(0x7B, "Add a field monster (start-up script)", Chars, (b, w) => $"Add field monster {w.Who(b[1])} with sprite {b[2]:X2}", null,
            "Only in a map's start-up script.", rare: true, ps: new[] { Who(1), P("sprite", L2Kind.Sprite, 2) });
        One(0x6C, "Draw a character shifted", Chars, (b, w) => $"Draw {w.Who(b[1])} shifted by ({(sbyte)b[2]}, {(sbyte)b[3]}) pixels", new byte[] { 0x6C, 0x50, 0, 0 },
            "Only the picture moves; the character stays on its tile.", ps: new[] { Who(1), P("x pixels", L2Kind.Signed, 2, 127, min: -128), P("y pixels", L2Kind.Signed, 3, 127, min: -128) });
        One(0x8B, "Nudge a character", Chars, (b, w) => $"Nudge {w.Who(b[1])} by ({(sbyte)b[2]}, {(sbyte)b[3]}) pixels", new byte[] { 0x8B, 0x50, 0, 0 },
            "", ps: new[] { Who(1), P("x pixels", L2Kind.Signed, 2, 127, min: -128), P("y pixels", L2Kind.Signed, 3, 127, min: -128) });
        One(0xC0, "Nudge a character onto another tile", Chars, (b, w) => $"Nudge {w.Who(b[1])} by ({(sbyte)b[2]}, {(sbyte)b[3]}) pixels and settle it on that tile",
            new byte[] { 0xC0, 0x50, 0, 0x10 }, "", ps: new[] { Who(1), P("x pixels", L2Kind.Signed, 2, 127, min: -128), P("y pixels", L2Kind.Signed, 3, 127, min: -128) });
        One(0x7A, "Show a sprite frame", Chars, (b, w) => $"{Cap(w.Who(b[1]))} shows frame {b[2]:X2}", new byte[] { 0x7A, 0x50, 0x00 },
            "00/01 down, 02/03 left, 04/05 up, 06/07 right; more poses follow from 08.", 'G', rare: true, ps: new[] { Who(1), P("frame", L2Kind.Hex, 2) });
        One(0xAF, "A character blinks", Chars, (b, w) => $"{Cap(w.Who(b[1]))} blinks", new byte[] { 0xAF, 0x50, 0x11 }, "", ps: new[] { Who(1), P("rate", L2Kind.Hex, 2) });
        One(0xB0, "A character stops blinking", Chars, (b, w) => $"{Cap(w.Who(b[1]))} stops blinking", new byte[] { 0xB0, 0x50 }, "", ps: Who(1));
        One(0xB6, "Character drawing", Chars, (b, w) => $"{Cap(w.Who(b[1]))}: drawing {(b[2] & 0x80) switch { 0 => "on", _ => "off" }} (setting {b[2]:X2})",
            new byte[] { 0xB6, 0x50, 0x20 }, "20 = normal, A0 = not drawn (still blocks the way).", 'G', ps: new[] { Who(1), P("setting", L2Kind.Hex, 2) });
        One(0xB1, "A character faces another", Chars, (b, w) => $"{Cap(w.Who(b[1]))} turns to face {w.Who(b[2])}", new byte[] { 0xB1, 0x00, 0x50 }, "", rare: true,
            ps: new[] { Who(1), Who(2, "toward") });
        One(0xB2, "Two characters face each other", Chars, (b, w) => $"{Cap(w.Who(b[1]))} and {w.Who(b[2])} face each other", new byte[] { 0xB2, 0x00, 0x50 }, "", rare: true,
            ps: new[] { Who(1), Who(2, "and") });
        One(0xB3, "The party looks at a character", Chars, (b, w) => $"The party turns to look at {w.Who(b[1])}", new byte[] { 0xB3, 0x50 }, "", rare: true, ps: Who(1));
        One(0xB5, "Forget story character 4F's map", Chars, (_, _) => "Story character 4F leaves every map", null, "", '?', rare: true);
        One(0xB7, "Bring back a defeated field monster", Chars, (b, w) => $"Bring back {w.Who(b[1])}", null, "", '?', rare: true, ps: Who(1));
        One(0xBD, "Add an effect character", Chars, (b, _) => $"Add effect character {b[1]:X2} (type {b[2]:X2})", null, "Used once by the game.", '?', rare: true,
            ps: new[] { P("id", L2Kind.Hex, 1), P("type", L2Kind.Hex, 2) });

        // ── character actions ──
        One(0xA3, "A character does an action", Moves, (b, w) => $"{Cap(w.Who(b[1]))}: {Opt(Actions, b[2]).ToLowerInvariant()}", new byte[] { 0xA3, 0x00, 0x64 },
            "Follow it with \"Wait for the action\" to wait until it's done.", ps: new[] { Who(1), Pk("action", 2, Actions) });
        One(0x86, "A character acts toward the leader's facing", Moves, (b, w) => $"{Cap(w.Who(b[1]))}: {Opt(ActionGroups, b[2]).ToLowerInvariant()} (turned the way the leader faces)",
            new byte[] { 0x86, 0x00, 0x64 }, "The action's up/down/left/right version is picked by the way the party leader faces.", ps: new[] { Who(1), Pk("action", 2, ActionGroups) });
        One(0x89, "Wait for the action", Moves, (b, w) => $"Wait until {w.Who(b[1])} finishes the action", new byte[] { 0x89, 0x00 }, "Then holds the character still.",
            waits: "until the action ends", ps: Who(1));
        One(0x98, "A character appears on a point (flash)", Moves, (b, w) => $"{Cap(w.Who(b[1]))} appears in a flash on {w.Point(b[2])}", new byte[] { 0x98, 0x50, 0x01 },
            "", ps: new[] { Who(1), P("point", L2Kind.Point, 2) });
        One(0x99, "A character vanishes (flash)", Moves, (b, w) => $"{Cap(w.Who(b[1]))} vanishes in a flash", new byte[] { 0x99, 0x50 }, "", ps: Who(1));
        One(0x9D, "A character flickers away", Moves, (b, w) => $"{Cap(w.Who(b[1]))} flickers and vanishes", new byte[] { 0x9D, 0x50 }, "", ps: Who(1));
        One(0xBF, "A character rides along with another", Moves, (b, w) => $"{Cap(w.Who(b[3]))} rides along with {w.Who(b[2])}", new byte[] { 0xBF, 0x93, 0x50, 0x51 },
            "", ps: new[] { Who(3, "rider"), Who(2, "carried by"), Pk("how", 1, L((0x93, "still"), (0x92, "spinning"))) });
        One(0x8C, "Action settings", Moves, (b, w) => $"Action settings for {w.Who(b[1])}: {b[2]:X2}, {b[3]:X2}", new byte[] { 0x8C, 0x00, 0x0A, 0x00 },
            "Used before \"Hold up an item\" (the second value picks what is shown) and the sprite-change action.", 'D',
            ps: new[] { Who(1), P("first", L2Kind.Hex, 2), P("second", L2Kind.Hex, 3) });
        One(0xC6, "Play an animation on a character", Moves, (b, w) => $"Animation {b[1]:X2} on {w.Who(b[2])}", new byte[] { 0xC6, 0x00, 0x50 }, "",
            ps: new[] { P("animation", L2Kind.Hex, 1), Who(2, "on") });
        One(0x73, "Play an animation on a point", Moves, (b, w) => $"Animation {b[1]:X2} on {w.Point(b[2])}", new byte[] { 0x73, 0x25, 0x01 }, "",
            ps: new[] { P("animation", L2Kind.Hex, 1, 0x5F), P("point", L2Kind.Point, 2) });
        One(0x9C, "Scatter animations over an area", Moves, (b, w) => $"{b[2]} x animation {b[3]:X2} scattered over {w.Area(b[1])}", new byte[] { 0x9C, 0x01, 0x08, 0x26 }, "",
            ps: new[] { P("area", L2Kind.Area, 1), P("how many", L2Kind.Number, 2), P("animation", L2Kind.Hex, 3) });
        foreach (var (op, from, kind) in new[] { (0x8D, "a point", 0x3C), (0xA0, "a character", 0x3C), (0xC2, "a point", 0x4F), (0xC3, "a character", 0x4F) })
        {
            bool point = from == "a point";
            One(op, $"Ring of sparkles around {from}" + (kind == 0x4F ? " (second kind)" : ""), Moves,
                (b, w) => $"Ring {b[1]:X2}: {b[3]} sparkles around {(point ? w.Point(b[2]) : w.Who(b[2]))}", new byte[] { (byte)op, 0x00, point ? (byte)1 : (byte)0, 0x0A, 0x64, 0x40, 0x00 },
                "A group of effect sprites; the ring commands below change it.", 'D', rare: true,
                ps: new[] { P("ring", L2Kind.Hex, 1), point ? P("point", L2Kind.Point, 2) : Who(2, "around"), P("how many", L2Kind.Number, 3), P("size", L2Kind.Hex, 4), P("spin", L2Kind.Hex, 5), P("other", L2Kind.Hex, 6) });
        }
        foreach (var (op, from) in new[] { (0xC8, "a point"), (0xC9, "a character") })
        {
            bool point = from == "a point";
            One(op, $"Ring of effect sprites around {from}", Moves, (b, w) => $"Ring {b[1]:X2}: {b[4]} x sprite {b[3]:X2} around {(point ? w.Point(b[2]) : w.Who(b[2]))}", null, "", 'D', rare: true,
                ps: new[] { P("ring", L2Kind.Hex, 1), point ? P("point", L2Kind.Point, 2) : Who(2, "around"), P("sprite type", L2Kind.Hex, 3), P("how many", L2Kind.Number, 4), P("size", L2Kind.Hex, 5), P("spin", L2Kind.Hex, 6), P("other", L2Kind.Hex, 7) });
        }
        One(0x8E, "Grow or shrink a ring", Moves, (b, _) => $"Ring {b[1]:X2}: size changes (stop {b[2]:X2}, rate {b[3]:X2})", null, "", 'C', rare: true,
            ps: new[] { P("ring", L2Kind.Hex, 1), P("stop at", L2Kind.Hex, 2), P("rate", L2Kind.Hex, 3) });
        One(0x9F, "Grow or shrink a ring, then remove it", Moves, (b, _) => $"Ring {b[1]:X2}: size changes, then it goes", null, "", 'C', rare: true,
            ps: new[] { P("ring", L2Kind.Hex, 1), P("stop at", L2Kind.Hex, 2), P("rate", L2Kind.Hex, 3) });
        One(0x8F, "Tilt a ring", Moves, (b, _) => $"Ring {b[1]:X2}: tilts (stop {b[2]:X2}, rate {b[3]:X2})", null, "", 'C', rare: true,
            ps: new[] { P("ring", L2Kind.Hex, 1), P("stop at", L2Kind.Hex, 2), P("rate", L2Kind.Hex, 3) });
        One(0x90, "Tilt a ring the other way", Moves, (b, _) => $"Ring {b[1]:X2}: tilts the other way (stop {b[2]:X2}, rate {b[3]:X2})", null, "", 'C', rare: true,
            ps: new[] { P("ring", L2Kind.Hex, 1), P("stop at", L2Kind.Hex, 2), P("rate", L2Kind.Hex, 3) });
        One(0x9A, "Remove a ring", Moves, (b, _) => $"Ring {b[1]:X2} fades away", null, "", 'C', rare: true, ps: P("ring", L2Kind.Hex, 1));
        One(0xA1, "Wait for a ring", Moves, (b, _) => $"Wait until ring {b[1]:X2} is done changing", null, "", 'C', "until it stops changing", true,
            P("ring", L2Kind.Hex, 1), Pk("change", 2, L((0, "size"), (1, "tilt"), (2, "other tilt"))));
        One(0xA2, "Wait until a ring is gone", Moves, (b, _) => $"Wait until ring {b[1]:X2} is gone", null, "", 'C', "until it is gone", true, P("ring", L2Kind.Hex, 1));
        One(0xC1, "Effect settings", Moves, (b, _) => $"Effect settings {b[1]:X2}, {b[2]:X2}", null, "Unknown use; always before a ring of sparkles.", '?', rare: true,
            ps: new[] { P("first", L2Kind.Hex, 1), P("second", L2Kind.Hex, 2) });

        // ── maps and places ──
        One(0x16, "Go to another map", Places, (b, w) => $"Go to {w.Map(b[1])}, arriving at point {b[3]}, and play its scene {b[2]}", new byte[] { 0x16, 0x03, 0x01, 0x01 },
            "The scene (an event of that map's scene list) runs on arrival; it must exist. The party faces down.", 'D',
            ps: new[] { P("map", L2Kind.Map, 1), P("scene", L2Kind.Scene, 2), P("arrival point", L2Kind.ArrivalPoint, 3) });
        One(0x5E, "Leave through an exit", Places, (b, w) => $"Leave through {w.Exit(b[1])}", new byte[] { 0x5E, 0x00 }, "As if the party walked through that exit of the map.",
            ps: P("exit", L2Kind.Exit, 1));
        One(0x83, "Send the player elsewhere (before the map loads)", Places, (b, w) => $"Instead, go through {w.Exit(b[1])}", null,
            "Only in the \"before the map loads\" event.", rare: true, ps: P("exit", L2Kind.Exit, 1));
        One(0x85, "Choose where the player arrives (before the map loads)", Places, (b, w) => $"Arrive at {w.Point(b[1])}, facing {Opt(Facings, b[2] >> 4)}", null,
            "Only in the \"before the map loads\" event.", rare: true, ps: new[] { P("point", L2Kind.Point, 1), new L2ParamDef { Label = "facing", Kind = L2Kind.Choice, Options = Facings, Get = b => b[2] >> 4, Set = (b, v) => b[2] = (byte)(v << 4) } });
        One(0x69, "Map settings", Places, (b, _) => "Map settings: " + (b[1] == 0 ? "none" : string.Join(", ", MapModeBits.Where(m => (b[1] & m.Item1) != 0).Select(m => m.Item2))),
            new byte[] { 0x69, 0x08 }, "Usually in the start-up script: towns use 08 or 98, dungeons 01 or 11.", ps: P("settings", L2Kind.Hex, 1));
        One(0xA9, "Where Escape leads", Places, (b, w) => b[1] >= 2 ? $"Escape leads to {w.Map(b[1])}, point {b[2]}" : $"Escape leads to world map spot {b[2]}", null,
            "Usually in the start-up script.", 'D', rare: true, ps: new[] { P("map", L2Kind.Map, 1), P("point", L2Kind.Number, 2) });
        One(0xAC, "Where the party appears when the map reloads", Places, (b, w) => $"After a battle or the menu, the party appears at {w.Point(b[1])}", null, "", 'C', rare: true,
            ps: new[] { P("point", L2Kind.Point, 1), Pk("facing", 2, Facings) });
        One(0x54, "Open a door", Places, (b, w) => $"Open {w.Door(b[1])}", new byte[] { 0x54, 0x00 }, "", 'D', ps: P("door", L2Kind.Door, 1));
        One(0x55, "Close a door", Places, (b, w) => $"Close {w.Door(b[1])}", new byte[] { 0x55, 0x00 }, "", 'D', ps: P("door", L2Kind.Door, 1));
        One(0x43, "Copy map tiles from one area to another", Places, (b, w) => $"Copy the tiles of {w.Area(b[2])} onto {w.Area(b[1])}", new byte[] { 0x43, 0x01, 0x02 },
            "Changes the map (both layers) and where the party can walk.", ps: new[] { P("onto area", L2Kind.Area, 1), P("from area", L2Kind.Area, 2) });
        One(0x87, "Copy map tiles (quick, for animations)", Places, (b, w) => $"Copy the tiles of {w.Area(b[2])} onto {w.Area(b[1])} (quick)", new byte[] { 0x87, 0x01, 0x02 },
            "Follow it with \"Update where the party can walk\".", 'D', ps: new[] { P("onto area", L2Kind.Area, 1), P("from area", L2Kind.Area, 2) });
        One(0xB4, "Copy map tiles on chosen layers", Places, (b, w) => $"Copy the tiles of {w.Area(b[2])} onto {w.Area(b[1])} (layers {b[3]:X2})", null, "", rare: true,
            ps: new[] { P("onto area", L2Kind.Area, 1), P("from area", L2Kind.Area, 2), P("layers", L2Kind.Hex, 3) });
        One(0x6B, "Update where the party can walk", Places, (_, _) => "Update where the party can walk", new byte[] { 0x6B }, "After changing map tiles.");
        One(0x88, "Update where the party can walk", Places, (_, _) => "Update where the party can walk", null, "", rare: true);
        One(0x7C, "Ship: what it can do", Places, (b, _) => $"Ship: {Opt(ShipAbilities, b[1]).ToLowerInvariant()}", null, "", '?', rare: true, ps: Pk("can", 1, ShipAbilities));
        One(0x7D, "Ship: park it on the world map", Places, (b, _) => $"Park the ship at world map point {b[1]}", null, "", 'C', rare: true, ps: P("point", L2Kind.Number, 1));
        One(0x7E, "Ship: where it is", Places, (b, _) => $"Ship: {Opt(ShipStates, b[1]).ToLowerInvariant()}", null, "", 'C', rare: true, ps: Pk("state", 1, ShipStates));
        One(0x7F, "Ship: party aboard", Places, (b, _) => b[1] == 0 ? "The party is on foot" : "The party is aboard the ship", null, "", 'C', rare: true, ps: Pk("party", 1, L((0, "on foot"), (0xFF, "aboard"))));
        One(0x80, "Ship: facing", Places, (b, _) => $"Ship faces {b[1]:X2}", null, "", '?', rare: true, ps: P("facing", L2Kind.Hex, 1));

        // ── shops and services ──
        One(0x17, "Rest at the inn", Shops, (b, _) => $"Rest at the inn ({b[1]} gold)", new byte[] { 0x17, 0x0A }, "Music, fade, everyone alive is healed, then the gold is paid (nothing when the party has less).",
            waits: "until the rest is over", ps: P("price", L2Kind.Number, 1));
        One(0x18, "Open a shop", Shops, (b, w) => $"Open {w.Shop(b[1])} (the event ends)", new byte[] { 0x18, 0x01 }, "The event ends when the shop closes.", 'G', ps: P("shop", L2Kind.Shop, 1));
        One(0x19, "Open the church", Shops, (_, _) => "Open the church menu (the event ends)", new byte[] { 0x19 }, "Save, cure and so on; the event ends there.");
        One(0x53, "Start a story battle", Shops, (b, _) => $"Story battle {b[1]:X2}", new byte[] { 0x53, 0x00 },
            "Afterwards story flag FF is on when the party did not win (losing is game over except battles 0B and 25).", 'G', ps: P("battle", L2Kind.Battle, 1, 0x26));
        One(0xA5, "Game over", Shops, (_, _) => "Game over (back to the title screen)", null, "", rare: true);

        // ── camera ──
        l.Add(new L2CommandDef
        {
            Ops = new[] { 0x41 }, Name = "Camera", Category = Cam, ParamsFor = CameraFields,
            Describe = (b, w) => CameraForm(b) switch
            {
                4 => "Wait until the camera stops",
                3 => $"Camera scrolls to tile ({b[2] + 1}, {b[3]}) at the top left, {SpeedText(b[1] & 15)}",
                2 => $"Camera scrolls to {w.Point(b[2])}, {SpeedText(b[1] & 15)}",
                0 => $"Camera goes back to the leader, {SpeedText(b[1] & 15)}, and waits",
                _ => $"Camera moves {b[2]} tile{(b[2] == 1 ? "" : "s")} {DirName(b[1] >> 4)}, {SpeedText(b[1] & 15)}",
            },
            Template = new byte[] { 0x41, 0x12, 0x04 }, Waits = "\"wait\" and \"back to the leader\" wait for the camera",
            Help = "The camera stays where the event put it until it goes back to the leader (it also does when the event ends). Moves don't wait: add \"wait until it stops\".",
        });
        One(0x75, "Camera points at the leader", Cam, (_, _) => "Camera turns to the leader", new byte[] { 0x75 }, "");
        One(0xC4, "Camera scrolls to a tile", Cam, (b, _) => $"Camera scrolls to tile ({b[1]}, {b[2]})", new byte[] { 0xC4, 0, 0 }, "Centred on it.",
            ps: new[] { P("x", L2Kind.Number, 1), P("y", L2Kind.Number, 2) });
        One(0x3B, "Camera jumps to a point", Cam, (b, w) => $"Camera jumps to {w.Point(b[1])}", new byte[] { 0x3B, 0x01 }, "At once (the map is drawn again).", ps: P("point", L2Kind.Point, 1));
        One(0x3A, "Camera jumps to a tile", Cam, (b, _) => $"Camera jumps to tile ({b[1]}, {b[2]})", new byte[] { 0x3A, 0, 0 }, "At once (the map is drawn again).", rare: true,
            ps: new[] { P("x", L2Kind.Number, 1), P("y", L2Kind.Number, 2) });

        // ── screen effects ──
        One(0x58, "Fade the screen out", Screen, (b, _) => $"Fade to black (step {b[1] >> 4}, every {Math.Max(1, b[1] & 15)} frames)", new byte[] { 0x58, 0x11 },
            "Follow it with \"Wait for the fade\". Also stops a quake and any tint.", ps: new[] { L2ParamDef.Nibble("step", L2Kind.Number, 1, true), L2ParamDef.Nibble("frames per step", L2Kind.Number, 1, false) });
        One(0x59, "Fade the screen in", Screen, (b, _) => $"Fade in from black (step {b[1] >> 4}, every {Math.Max(1, b[1] & 15)} frames)", new byte[] { 0x59, 0x11 },
            "Use step 1, 3, 5 or 15 (others stay black). Follow it with \"Wait for the fade\".", ps: new[] { L2ParamDef.Nibble("step", L2Kind.Number, 1, true), L2ParamDef.Nibble("frames per step", L2Kind.Number, 1, false) });
        One(0xA4, "Fade in quickly", Screen, (_, _) => "Fade in quickly", new byte[] { 0xA4 }, "About a quarter of a second; used after story battles.");
        One(0x76, "Wait for the fade", Screen, (_, _) => "Wait for the fade", new byte[] { 0x76 }, "", waits: "until the fade is done");
        One(0x57, "Wait for every screen effect", Screen, (_, _) => "Wait until every screen effect stops", null, "Never ends during a quake or while the event moves the camera.", rare: true,
            waits: "until all effects stop");
        One(0x5A, "Shake the screen", Screen, (b, _) => $"The screen shakes (strength {b[2]}, how often {b[3]:X2})", new byte[] { 0x5A, 0x05, 0x02, 0x7F },
            "Keeps shaking until \"Stop the shaking\".", 'G', ps: new[] { P("strength", L2Kind.Number, 2, 15), P("how often (of 255)", L2Kind.Hex, 3) });
        One(0x8A, "Stop the shaking", Screen, (_, _) => "Stop the shaking", new byte[] { 0x8A }, "", 'G');
        One(0x56, "Stop all screen effects", Screen, (_, _) => "Stop all screen effects", new byte[] { 0x56 }, "Shaking, fades and the camera hold.");
        One(0x95, "Flash a colour", Screen, (b, _) => $"Flash {ColourName(b[1])} (strength {b[1] & 31} to {b[2]}, {Frames(b[3])})", new byte[] { 0x95, 0xFF, 0x00, 0x3C },
            "Adds a colour over the screen; \"End the colour\" takes it away.", ps: ColourFields());
        One(0x94, "Darken with a colour", Screen, (b, _) => $"Darken with {ColourName(b[1])} (strength {b[1] & 31} to {b[2]}, {Frames(b[3])})", new byte[] { 0x94, 0xE0, 0x10, 0x78 },
            "", ps: ColourFields());
        One(0x96, "Darken with a colour (half)", Screen, (b, _) => $"Darken half with {ColourName(b[1])}", null, "", rare: true, ps: ColourFields());
        One(0x97, "End the colour", Screen, (_, _) => "End the colour (back to the map's look)", new byte[] { 0x97 }, "");
        One(0xC5, "Keep the colour as it is", Screen, (_, _) => "Keep the colour as it is now", null, "", '?', rare: true);
        One(0x93, "Wash the colours toward white", Screen, (b, _) => $"Colours toward white (channels {b[1]:X2}, step {(sbyte)b[2]}, every {b[3]} frames)", null, "", rare: true,
            ps: new[] { P("channels", L2Kind.Hex, 1), P("step", L2Kind.Signed, 2, 127, min: -128), P("frames per step", L2Kind.Number, 3) });
        One(0x9B, "Custom colour fade", Screen, (b, _) => "Custom colour fade", null, "", rare: true);
        One(0x46, "Set a palette colour", Screen, (b, _) => $"Palette colour {b[1]:X2} = {W(b, 2):X4}", null, "", rare: true, ps: new[] { P("colour", L2Kind.Hex, 1), Wd("value (BGR15)", L2Kind.Hex, 2) });
        One(0x44, "Add to a palette colour", Screen, (b, _) => $"Palette colour {b[1]:X2} + {W(b, 2):X4}", null, "", rare: true, ps: new[] { P("colour", L2Kind.Hex, 1), Wd("add (BGR15)", L2Kind.Hex, 2) });
        One(0x45, "Take from a palette colour", Screen, (b, _) => $"Palette colour {b[1]:X2} - {W(b, 2):X4}", null, "", rare: true, ps: new[] { P("colour", L2Kind.Hex, 1), Wd("take (BGR15)", L2Kind.Hex, 2) });
        One(0x3E, "Screen off", Screen, (_, _) => "Screen off (black at once)", null, "", rare: true);
        One(0x3F, "Screen on", Screen, (_, _) => "Screen on (full brightness at once)", null, "", rare: true);
        One(0x40, "Draw the map again", Screen, (_, _) => "Draw the whole map again", null, "", rare: true);
        One(0xC7, "Draw the map again (dark screen)", Screen, (_, _) => "Draw the map again (while the screen is dark)", null, "", 'D', rare: true);
        One(0x82, "Refresh around the hero", Screen, (_, _) => "Refresh the map around the hero", null, "", '?', rare: true);
        One(0x60, "Write a screen register", Screen, (b, _) => $"Screen register 21{b[1]:X2} = {b[2]:X2}", null, "", rare: true, ps: new[] { P("register", L2Kind.Hex, 1), P("value", L2Kind.Hex, 2) });
        One(0xCA, "Layer scrolling", Screen, (b, _) => $"Layer {b[1] / 2 + 1} scrolls in mode {b[2]} ({b[3]:X2})", null, "", rare: true,
            ps: new[] { Pk("layer", 1, L((0, "1"), (2, "2"), (4, "3"), (6, "4"))), Pk("mode", 2, L((0, "follows the camera"), (1, "fixed"), (2, "parallax"), (3, "scrolls by itself"), (4, "parallax (other)"))), P("setting", L2Kind.Hex, 3) });
        One(0x74, "Battle background", Screen, (b, _) => $"Battles here use background {b[1]:X2}", null, "Usually in the start-up script.", rare: true, ps: P("background", L2Kind.Hex, 1));

        // ── music and sound ──
        One(0x4B, "Set the map's music", Sound, (b, w) => $"Music: {w.Song(b[1])}", new byte[] { 0x4B, 0x01 }, "Nothing changes when that song already plays; it comes back after a battle.",
            ps: P("song", L2Kind.Song, 1, 0x3E));
        One(0x6F, "Play a song now", Sound, (b, w) => $"Play {w.Song(b[1])} now", new byte[] { 0x6F, 0x02 }, "For a scene; the map's own music comes back later.", ps: P("song", L2Kind.Song, 1, 0x3E));
        One(0x70, "Fade the music out", Sound, (_, _) => "Fade the music out", new byte[] { 0x70 }, "Takes a second; \"Wait for the music fade\" waits for it.");
        One(0x71, "Wait for the music fade", Sound, (_, _) => "Wait for the music to fade out", new byte[] { 0x71 }, "", waits: "a second");
        One(0x72, "Stop the music", Sound, (_, _) => "Stop the music", new byte[] { 0x72 }, "");
        One(0x4C, "Play a sound", Sound, (b, _) => $"Sound {b[1]:X2}", new byte[] { 0x4C, 0x15 }, "", ps: P("sound", L2Kind.Sound, 1, 0x8E));
        One(0xAE, "Sounds on or off", Sound, (b, _) => b[1] == 0xFF ? "Sounds on again" : "Sounds off", new byte[] { 0xAE, 0x00 }, "", ps: Pk("sounds", 1, L((0x00, "off"), (0xFF, "on"))));
        One(0x4D, "Wait for a music cue", Sound, (b, _) => $"Wait for music cue {b[1]}", null, "", waits: "until the song gets there", rare: true, ps: P("cue", L2Kind.Number, 1));
        One(0x4E, "Wait for the sound to end", Sound, (_, _) => "Wait for the sound to end", null, "", '?', "until the sound ends", true);

        // ── waiting ──
        One(0x37, "Wait (frames)", Time, (b, _) => $"Wait {Frames(b[1])}", new byte[] { 0x37, 0x1E }, "60 frames are a second.", waits: "the time given", ps: P("frames", L2Kind.Frames, 1));
        One(0x38, "Wait (seconds)", Time, (b, _) => $"Wait {b[1]} second{(b[1] == 1 ? "" : "s")}", new byte[] { 0x38, 0x01 }, "", waits: "the time given", ps: P("seconds", L2Kind.Number, 1));

        // ── rarely used / system ──
        var specials = L((0, "screen bars"), (1, "title sequence"), (2, "cinema bars"), (3, "reset the game"), (4, "ferry crossing"),
                         (5, "reload after the staff roll"), (6, "hang"), (7, "big font"), (8, "normal font"));
        var specialWhat = new L2ParamDef
        {
            Label = "what", Kind = L2Kind.Choice, Options = specials, Get = b => b[1], Set = (b, v) => b[1] = (byte)v,
            Rebuild = (b, v) => (v is 3 or 6 ? new byte[] { 0x5B, (byte)v } : new byte[] { 0x5B, (byte)v, (byte)(v == 4 ? 2 : b.Length > 2 ? b[2] : 0) }, null),
        };
        l.Add(new L2CommandDef
        {
            Ops = new[] { 0x5B }, Name = "Special scene", Category = Special, Rare = true, Template = new byte[] { 0x5B, 0x04, 0x02 },
            Help = "Ferry crossings are followed by \"Go to another map\" to the port.",
            ParamsFor = b => b.Length > 2 ? new[] { specialWhat, b[1] == 4 ? Pk("route", 2, Ferries) : P("value", L2Kind.Hex, 2) } : new[] { specialWhat },
            Describe = (b, _) => Special5B(b),
        });
        string Special5B(byte[] b) => b[1] switch
        {
            0x00 => b[2] != 0 ? "Darken the screen bars" : "Brighten the screen bars",
            0x01 => b[2] == 0 ? "Play the title sequence (the opening, then the title screen)" : "Play another bank 86 sequence",
            0x02 => b[2] != 0 ? "Cinema bars on" : "Cinema bars off (screen left dark)",
            0x03 => "Reset the game",
            0x04 => $"Ferry crossing: {Opt(Ferries, b[2])}",
            0x05 => "Reload after the staff roll",
            0x06 => "Hang forever",
            0x07 => "Load the big font",
            0x08 => "Load the normal font again",
            _ => $"Special {b[1]:X2}",
        };
        One(0xA7, "Followers stop or resume", Special, (b, _) => b[1] == 0 ? "Followers stop following the leader's steps" : "Followers follow again", null, "", 'C', rare: true,
            ps: Pk("followers", 1, L((0x00, "stop"), (0xFF, "follow"))));
        One(0xA8, "Enter or leave the Ancient Cave", Special, (b, _) => b[1] == 0 ? "Enter the Ancient Cave (party and items reset)" : "Leave the Ancient Cave", null, "", 'D', rare: true,
            ps: Pk("", 1, L((0, "enter"), (1, "leave"))));
        One(0xA6, "Place the cave floor's monsters", Special, (_, _) => "Place this floor's monsters (Ancient Cave)", null, "", '?', rare: true);
        One(0xAA, "Clear the puzzle movement flags", Special, (_, _) => "Clear the puzzle movement flags", null, "", 'C', rare: true);
        One(0xAD, "Rebuild the treasure list", Special, (_, _) => "Rebuild a treasure list (Dragon egg wish?)", null, "", '?', rare: true);
        One(0xBB, "Show the report screen", Special, (_, _) => "Show the report screen (defeats and other records) and end", null, "The game uses it at the end.", 'G', rare: true);
        One(0xBC, "Draw the map again and end", Special, (_, _) => "Draw the map again and end the event", null, "", 'C', rare: true);
        One(0xCB, "Message speed", Special, (b, _) => $"Message speed = {b[1]}", null, "", 'D', rare: true, ps: P("speed", L2Kind.Number, 1));
        One(0xCC, "Remember the last story battle", Special, (_, _) => "Remember the last story battle", null, "", '?', rare: true);
        One(0x6D, "Credits text", Special, (b, _) => "Credits text", null, "", rare: true);
        One(0x6E, "Credits text (tall)", Special, (b, _) => "Credits text (tall letters)", null, "", rare: true);
        foreach (int op in new[] { 0x27, 0x2A, 0x39, 0x4F, 0x5F })
            One(op, "Nothing", Special, (_, _) => "(does nothing)", null, "This command does nothing.", rare: true);
        foreach (int op in new[] { 0x02, 0x11, 0x5C, 0x5D })
            One(op, "Crash", Special, (_, _) => "(crashes the game)", null, "Not a real command: the game stops here.", rare: true);
        return l;
    }

    /// <summary>A count of jumps (question answers, counter choices) at <paramref name="pos"/>; the jumps follow from
    /// <paramref name="jumpsAt"/>, 2 bytes each.</summary>
    private static L2ParamDef Count(string label, int pos, int min, int jumpsAt) => new()
    {
        Label = label, Kind = L2Kind.Number, Min = min, Max = 8,
        Get = b => b[pos], Set = (_, _) => { },
        Rebuild = (b, v) =>
        {
            var n = new byte[jumpsAt + 2 * v];
            Array.Copy(b, n, Math.Min(b.Length, n.Length));
            n[pos] = (byte)v;
            return (n, Enumerable.Range(0, v).Select(k => k < b[pos] ? k : -1).ToArray());
        },
    };

    /// <summary>The fields of a camera command (41) for the form its bytes have.</summary>
    private static L2ParamDef[] CameraFields(byte[] b)
    {
        var form = new L2ParamDef
        {
            Label = "camera", Kind = L2Kind.Choice,
            Options = L((0, "goes back to the leader"), (1, "moves some tiles"), (2, "scrolls to a point"), (3, "scrolls to a tile"), (4, "wait until it stops")),
            Get = CameraForm, Set = (_, _) => { },
            Rebuild = (old, v) => (CameraBytes(v, old[1] & 15), null),
        };
        var speed = L2ParamDef.Nibble("speed", L2Kind.Number, 1, false, hint: "pixels a frame; 0 = at once", options: Speeds);
        return CameraForm(b) switch
        {
            1 => new[] { form, L2ParamDef.Nibble("direction", L2Kind.Choice, 1, true, options: Directions), L2ParamDef.Byte("tiles", L2Kind.Number, 2, 63, min: 1), speed },
            2 => new[] { form, L2ParamDef.Byte("point", L2Kind.Point, 2), speed },
            3 => new[] { form, L2ParamDef.Byte("x (left edge)", L2Kind.Number, 2), L2ParamDef.Byte("y (top edge)", L2Kind.Number, 3), speed },
            4 => new[] { form },
            _ => new[] { form, speed },
        };
    }

    /// <summary>Which form a camera command has: 0 back, 1 move, 2 to a point, 3 to a tile, 4 wait.</summary>
    public static int CameraForm(byte[] b) =>
        b.Length < 3 ? 4 : b[2] == 0xFF ? 4 : (b[1] >> 4) == 6 ? 3 : (b[1] >> 4) == 7 ? 2 : b[2] == 0 ? 0 : 1;

    /// <summary>A camera command of the given form, keeping the speed.</summary>
    public static byte[] CameraBytes(int form, int speed) => form switch
    {
        1 => new byte[] { 0x41, (byte)(0x10 | speed), 0x02 },
        2 => new byte[] { 0x41, (byte)(0x70 | speed), 0x01 },
        3 => new byte[] { 0x41, (byte)(0x60 | speed), 0, 0 },
        4 => new byte[] { 0x41, 0x00, 0xFF },
        _ => new byte[] { 0x41, (byte)speed, 0x00 },
    };

    private static L2ParamDef[] ColourFields() => new[]
    {
        new L2ParamDef { Label = "colour", Kind = L2Kind.Choice, Options = L((0xE0, "white / black"), (0x20, "red"), (0x40, "green"), (0x80, "blue"), (0x60, "yellow"), (0xA0, "magenta"), (0xC0, "cyan")),
            Get = b => b[1] & 0xE0, Set = (b, v) => b[1] = (byte)((b[1] & 0x1F) | v) },
        new L2ParamDef { Label = "start strength", Kind = L2Kind.Number, Max = 31, Get = b => b[1] & 0x1F, Set = (b, v) => b[1] = (byte)((b[1] & 0xE0) | (v & 0x1F)) },
        L2ParamDef.Byte("end strength", L2Kind.Number, 2, 31),
        L2ParamDef.Byte("frames", L2Kind.Frames, 3),
    };

    private static string ColourName(int c) => (c & 0xE0) switch
    {
        0xE0 => "white", 0x20 => "red", 0x40 => "green", 0x80 => "blue", 0x60 => "yellow", 0xA0 => "magenta", 0xC0 => "cyan", _ => "no colour",
    };

    public static string HeroName(int h) => h < HeroNames.Length ? HeroNames[h] : $"hero {h}";
    public static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
