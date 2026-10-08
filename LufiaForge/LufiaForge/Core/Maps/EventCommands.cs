using LufiaForge.Modules.GameData;

namespace LufiaForge.Core.Maps;

/// <summary>What kind of value an event-command field holds (picks the editor and the wording).</summary>
public enum ParamKind
{
    Number,      // decimal number
    Hex,         // hex byte
    Jump,        // event-relative jump (edited as a line choice)
    Flag,        // story flag 00-FF
    Item, Spell, Character, Map, Music, Sound, Formation, Shop,
    Who,         // map character or party actor (opcode bit 0 = party actor id)
    Npc,         // map character number (0 = leader where noted)
    Actor,       // actor id (1 = leader, 2-5 followers, 6 vehicle, 7+n = character n)
    PartyOrAll,  // party slot 0-3 or FF = everyone
    Direction,   // 0 right, 1 left, 2 down, 3 up
    ScrollSpeed, // index into the camera speed table
    ShortWait,   // 80-87 wait lengths
    SystemCall,  // 50 sub-function
    KeyItem,     // 1-based key item (F0 + n - 1)
    FlashKind,
}

/// <summary>One editable field of a command. Get/Set read and write it in the command bytes (opcode included).</summary>
public sealed class ParamDef
{
    public string Label { get; init; } = "";
    public ParamKind Kind { get; init; }
    public Func<byte[], int> Get { get; init; } = _ => 0;
    public Action<byte[], int> Set { get; init; } = (_, _) => { };
    public int Min { get; init; }
    public int Max { get; init; } = 255;
    /// <summary>Index into the command's jump targets (Jump fields only).</summary>
    public int JumpIndex { get; init; } = -1;

    public static ParamDef Byte(string label, ParamKind kind, int pos, int add = 0, int max = 255) => new()
    {
        Label = label, Kind = kind, Min = add, Max = max + add,
        Get = b => b[pos] + add,
        Set = (b, v) => b[pos] = (byte)(v - add),
    };

    public static ParamDef Word(string label, int pos, ParamKind kind = ParamKind.Number) => new()
    {
        Label = label, Kind = kind, Max = 0xFFFF,
        Get = b => b[pos] | (b[pos + 1] << 8),
        Set = (b, v) => { b[pos] = (byte)v; b[pos + 1] = (byte)(v >> 8); },
    };

    public static ParamDef Bits(string label, ParamKind kind, int pos, int mask, int shift, int add = 0) => new()
    {
        Label = label, Kind = kind, Min = add, Max = (mask >> shift) + add,
        Get = b => ((b[pos] & mask) >> shift) + add,
        Set = (b, v) => b[pos] = (byte)((b[pos] & ~mask) | (((v - add) << shift) & mask)),
    };

    public static ParamDef Jump(string label, int k) => new() { Label = label, Kind = ParamKind.Jump, JumpIndex = k };

    /// <summary>
    /// "Who" for commands with an even (map character) and an odd (actor id) opcode. Value: actor ids 0-6 as is,
    /// map character n as 0x100 + n. <paramref name="mask"/> keeps other bits of the operand (20-27 use bit 7 for X).
    /// </summary>
    public static ParamDef Who(int pos, int mask = 0xFF, string label = "who") => new()
    {
        Label = label, Kind = ParamKind.Who, Max = 0x1FF,
        Get = b => (b[0] & 1) == 1 && (b[pos] & mask) < 7 ? b[pos] & mask
                 : (b[0] & 1) == 1 ? 0x100 + (b[pos] & mask) - 7
                 : 0x100 + (b[pos] & mask),
        Set = (b, v) =>
        {
            if (v >= 0x100) { b[0] = (byte)(b[0] & ~1); b[pos] = (byte)((b[pos] & ~mask) | ((v - 0x100) & mask)); }
            else { b[0] = (byte)(b[0] | 1); b[pos] = (byte)((b[pos] & ~mask) | (v & mask)); }
        },
    };
}

/// <summary>A command (or a family of opcodes) with its name, menu category and fields.</summary>
public sealed class CommandDef
{
    public int First { get; init; }
    public int Last { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public ParamDef[] Params { get; init; } = Array.Empty<ParamDef>();
    public Func<byte[], EventCommands.Words, string> Describe { get; init; } = (_, _) => "";
    /// <summary>Bytes of a new command added from the menu (null = not offered in the menu).</summary>
    public byte[]? Template { get; init; }
    /// <summary>Script pauses here until something finishes.</summary>
    public string Waits { get; init; } = "";
    public bool Matches(int op) => op >= First && op <= Last;
}

/// <summary>
/// Every event-script command the engine has, decoded from the handlers at $01:C78C / $01:C868
/// (see research-script-engine.md, "Event command reference").
/// </summary>
public static class EventCommands
{
    /// <summary>Wording helpers for descriptions (names of items, flags, jump targets, ...).</summary>
    public sealed class Words
    {
        public Func<int, string> Item { get; init; } = i => $"item {i:X2}";
        public Func<int, string> Spell { get; init; } = i => $"spell {i:X2}";
        public Func<int, string> Flag { get; init; } = f => $"{f:X2}";
        public Func<int, string> Jump { get; init; } = k => "?";
        public Func<int, string> Map { get; init; } = m => $"map {m:X2}";
    }

    public static readonly string[] Directions = { "right", "left", "down", "up" };

    /// <summary>Event base of a map (set by the app when a ROM is loaded; command 51 counts from it).</summary>
    public static Func<int, int> EventBase { get; set; } = _ => 0;

    /// <summary>The event number command 51 (<paramref name="b"/> = its bytes) runs on its map.</summary>
    public static int ChainedEvent(byte[] b) => (EventBase(b[1]) + b[2]) & 0xFF;
    public static readonly int[] ShortWaitFrames = { 4, 8, 12, 20, 40, 60, 80, 100 };
    public static readonly int[] ScrollSpeeds = { 0x10, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };

    public static readonly string[] CharacterNames =
    {
        "Hero", "Lufia", "Aguro", "Jerin", "Maxim", "Selan", "Guy", "Artea",
        "Follower 8", "Follower 9", "Follower A", "Follower B", "Follower C", "Follower D", "Follower E", "Follower F",
    };

    public static readonly string[] SystemCalls =
    {
        "Open the object being examined", "Church: find members to cure", "Church: next member to cure", "Church: pay and cure",
        "Reset party to the hero only", "Fade out and wait", "Fade in and wait", "Clear $16BA",
        "Fade (mode 1) and wait", "Close overlay after 128 frames", "Instant text on", "Instant text off",
        "Clear $0CEF", "Set $0CEF", "Refresh characters", "Curse removal: find members",
        "Curse removal: next member", "Curse removal: pay and remove", "Lock music (ignore music changes)", "Unlock music",
        "Close overlay window", "Force-equip item 23 on the hero", "Take item 23 from the hero", "Count items E3/E4",
        "Trade E3/E4 pairs for item 7F", "Special screen sequence", "Level up every member twice", "Mark the game as completed",
        "Set $0D1E", "Give the selected item", "Test $0D9E", "Switch to screen 12", "Nothing",
    };

    public static string Who(int v) => v >= 0x100 ? $"character {v - 0x100}" : Actor(v);

    public static string Actor(int a) => a switch
    {
        0 => "actor 0",
        1 => "the leader",
        >= 2 and <= 5 => $"party member {a}",
        6 => "the vehicle",
        _ => $"character {a - 7}",
    };

    private static int W(byte[] b, int p) => b[p] | (b[p + 1] << 8);
    private static string Frames(int f) => f == 1 ? "1 frame" : $"{f} frames ({f / 60.0:0.##} s)";
    private static string PartyName(int v) => v == 0xFF ? "the whole party" : CharacterNames[v & 3];

    private static readonly List<CommandDef> All = Build();
    public static IReadOnlyList<CommandDef> Definitions => All;

    public static CommandDef? Find(int op) => All.FirstOrDefault(d => d.Matches(op));

    private static List<CommandDef> Build()
    {
        var l = new List<CommandDef>();
        void Add(int first, int last, string name, string cat, Func<byte[], Words, string> describe, byte[]? template, string waits = "",
                 params ParamDef[] ps) =>
            l.Add(new CommandDef { First = first, Last = last, Name = name, Category = cat, Describe = describe, Template = template, Waits = waits, Params = ps });
        var P = ParamDef.Byte;

        const string Flow = "Flow", Flags = "Story flags", Chars = "Characters", Cam = "Camera & screen", Snd = "Music & sound",
                     Time = "Timing", Party = "Party & items", Place = "Places & map", Serv = "Shops & services", Win = "Text window";

        // ── flow ──
        Add(0x00, 0x00, "End", Flow, (_, _) => "End of the event", new byte[] { 0x00 });
        Add(0x01, 0x01, "Go to", Flow, (_, w) => $"Go to {w.Jump(0)}", new byte[] { 0x01, 0, 0 }, "", ParamDef.Jump("go to", 0));
        Add(0x02, 0x02, "First time only", Flow,
            (b, w) => $"First time only (map flag {b[1]:X2}): first time go to {w.Jump(0)}, later go to {w.Jump(1)}",
            new byte[] { 0x02, 0, 0, 0, 0, 0 }, "", P("map flag", ParamKind.Hex, 1), ParamDef.Jump("first time", 0), ParamDef.Jump("later", 1));
        Add(0x03, 0x03, "Choice targets (for the next text box)", Flow,
            (b, w) => $"The next text box asks a question: " + string.Join(", ", Enumerable.Range(0, b[1]).Select(k => $"answer {k + 1} → {w.Jump(k)}")),
            new byte[] { 0x03, 2, 0, 0, 0, 0 });
        // the event number is counted from the target map's event base (byte 4 of its $03:8200 entry)
        Add(0x51, 0x51, "Run an event of another map", Flow,
            (b, w) => $"Continue with event {ChainedEvent(b)} of {w.Map(b[1])} (does not come back)", new byte[] { 0x51, 0, 0 }, "",
            P("map", ParamKind.Map, 1),
            new ParamDef
            {
                Label = "event", Kind = ParamKind.Number, Max = 255,
                Get = b => ChainedEvent(b),
                Set = (b, v) => b[2] = (byte)((v - EventBase(b[1])) & 0xFF),
            });
        Add(0x1E, 0x1E, "Church menu", Serv, (_, _) => "Church menu (revive, cure, save)", new byte[] { 0x1E });

        // ── conditions ──
        Add(0x04, 0x04, "If flag is set, go to", Flags, (b, w) => $"If story flag {w.Flag(b[1])} is set, go to {w.Jump(0)}",
            new byte[] { 0x04, 0, 0, 0 }, "", P("flag", ParamKind.Flag, 1), ParamDef.Jump("go to", 0));
        Add(0x05, 0x05, "If flag is not set, go to", Flags, (b, w) => $"If story flag {w.Flag(b[1])} is not set, go to {w.Jump(0)}",
            new byte[] { 0x05, 0, 0, 0 }, "", P("flag", ParamKind.Flag, 1), ParamDef.Jump("go to", 0));
        Add(0x06, 0x06, "Set story flag", Flags, (b, w) => $"Set story flag {w.Flag(b[1])}", new byte[] { 0x06, 0 }, "", P("flag", ParamKind.Flag, 1));
        Add(0x07, 0x07, "Clear story flag", Flags, (b, w) => $"Clear story flag {w.Flag(b[1])}", new byte[] { 0x07, 0 }, "", P("flag", ParamKind.Flag, 1));
        Add(0xC0, 0xCF, "If flag F0-FF is set, go to (short)", Flags, (b, w) => $"If story flag {w.Flag(0xF0 + (b[0] & 15))} is set, go to {w.Jump(0)}",
            null, "", ParamDef.Bits("flag", ParamKind.Flag, 0, 0x0F, 0, 0xF0), ParamDef.Jump("go to", 0));
        Add(0xD0, 0xDF, "If flag F0-FF is not set, go to (short)", Flags, (b, w) => $"If story flag {w.Flag(0xF0 + (b[0] & 15))} is not set, go to {w.Jump(0)}",
            null, "", ParamDef.Bits("flag", ParamKind.Flag, 0, 0x0F, 0, 0xF0), ParamDef.Jump("go to", 0));
        Add(0xE0, 0xEF, "Set flag F0-FF (short)", Flags, (b, w) => $"Set story flag {w.Flag(0xF0 + (b[0] & 15))}", null, "",
            ParamDef.Bits("flag", ParamKind.Flag, 0, 0x0F, 0, 0xF0));
        Add(0xF0, 0xFF, "Clear flag F0-FF (short)", Flags, (b, w) => $"Clear story flag {w.Flag(0xF0 + (b[0] & 15))}", null, "",
            ParamDef.Bits("flag", ParamKind.Flag, 0, 0x0F, 0, 0xF0));
        Add(0x45, 0x45, "If character knows a spell, go to", Flags,
            (b, w) => $"If {CharacterNames[b[1] & 15]} knows {w.Spell(b[2])}, go to {w.Jump(0)}", new byte[] { 0x45, 0, 0, 0, 0 }, "",
            P("character", ParamKind.Character, 1), P("spell", ParamKind.Spell, 2), ParamDef.Jump("go to", 0));
        Add(0x46, 0x46, "If the party has an item, go to", Flags,
            (b, w) => $"If the party has {(b[1] >= 0xF0 ? "" : $"at least {b[2]} × ")}{w.Item(b[1])}, go to {w.Jump(0)}", new byte[] { 0x46, 1, 1, 0, 0 }, "",
            P("item", ParamKind.Item, 1), P("count", ParamKind.Number, 2), ParamDef.Jump("go to", 0));
        Add(0x47, 0x47, "If level is at least, go to", Flags,
            (b, w) => $"If {CharacterNames[b[1] & 15]}'s level is {b[2]} or more, go to {w.Jump(0)}", new byte[] { 0x47, 0, 1, 0, 0 }, "",
            P("character", ParamKind.Character, 1), P("level", ParamKind.Number, 2), ParamDef.Jump("go to", 0));
        Add(0x48, 0x48, "If HP is at least, go to", Flags,
            (b, w) => $"If {CharacterNames[b[1] & 15]}'s HP is {W(b, 2)} or more, go to {w.Jump(0)}", new byte[] { 0x48, 0, 1, 0, 0, 0 }, "",
            P("character", ParamKind.Character, 1), ParamDef.Word("HP", 2), ParamDef.Jump("go to", 0));
        Add(0x49, 0x49, "If MP is at least, go to", Flags,
            (b, w) => $"If {CharacterNames[b[1] & 15]}'s MP is {W(b, 2)} or more, go to {w.Jump(0)}", new byte[] { 0x49, 0, 1, 0, 0, 0 }, "",
            P("character", ParamKind.Character, 1), ParamDef.Word("MP", 2), ParamDef.Jump("go to", 0));
        Add(0x4F, 0x4F, "If the party has a key item, go to", Flags,
            (b, w) => $"If the party has key item {b[1]} ({w.Item(0xF0 + b[1] - 1)}), go to {w.Jump(0)}", new byte[] { 0x4F, 1, 0, 0 }, "",
            P("key item", ParamKind.KeyItem, 1, 0, 16), ParamDef.Jump("go to", 0));

        // ── characters ──
        Add(0x20, 0x27, "Place a character", Chars,
            (b, _) => $"Place {Who(ParamDef.Who(1, 0x7F).Get(b))} at ({((b[1] >> 7) << 8) | b[2]}, {b[3]}) facing {Directions[(b[0] >> 1) & 3]}",
            new byte[] { 0x24, 1, 0, 0 }, "",
            ParamDef.Who(1, 0x7F), ParamDef.Bits("facing", ParamKind.Direction, 0, 6, 1),
            new ParamDef { Label = "X", Kind = ParamKind.Number, Max = 511, Get = b => ((b[1] >> 7) << 8) | b[2], Set = (b, v) => { b[2] = (byte)v; b[1] = (byte)((b[1] & 0x7F) | ((v >> 8 & 1) << 7)); } },
            P("Y", ParamKind.Number, 3));
        Add(0x28, 0x2F, "Turn a character", Chars,
            (b, _) => $"{Cap(Who(ParamDef.Who(1).Get(b)))} faces {Directions[(b[0] >> 1) & 3]}", new byte[] { 0x2C, 1 }, "",
            ParamDef.Who(1), ParamDef.Bits("facing", ParamKind.Direction, 0, 6, 1));
        Add(0xB0, 0xBF, "Turn a party member (short)", Chars,
            (b, _) => $"{Cap(Actor((b[0] & 3) + 1))} faces {Directions[(b[0] >> 2) & 3]}", null, "",
            ParamDef.Bits("party actor", ParamKind.Actor, 0, 3, 0, 1), ParamDef.Bits("facing", ParamKind.Direction, 0, 0x0C, 2));
        Add(0x14, 0x15, "Move a character along a path", Chars,
            (b, _) => $"{Cap(Who(ParamDef.Who(2).Get(b)))} walks path {b[1]} of this map", new byte[] { 0x15, 1, 1 }, "until the walk ends (unless the path is set to not wait)",
            P("path", ParamKind.Number, 1), ParamDef.Who(2));
        Add(0x16, 0x16, "Release all characters", Chars,
            (b, _) => b[1] == 0 ? "Release every character from script control (they move on their own again)" : $"Release character {b[1]} (has no effect: engine bug)",
            new byte[] { 0x16, 0 });
        Add(0x17, 0x17, "Release an actor (no effect)", Chars, (b, _) => $"Release actor {b[1]} (has no effect: engine bug)", null);
        Add(0x38, 0x39, "Freeze a character", Chars,
            (b, _) => $"Freeze {Who(ParamDef.Who(1).Get(b))} (stops wandering)", new byte[] { 0x38, 1 }, "", ParamDef.Who(1));
        Add(0x3C, 0x3C, "Gather the party on the leader", Chars, (_, _) => "The party gathers on the leader's tile", new byte[] { 0x3C }, "until everyone has arrived");
        Add(0x5C, 0x5D, "Hide or blink a character", Chars,
            (b, _) => b[2] == 0 ? $"Hide {Who(ParamDef.Who(1).Get(b))}" : $"{Cap(Who(ParamDef.Who(1).Get(b)))} blinks every {Frames(b[2])}",
            new byte[] { 0x5C, 1, 0 }, "", ParamDef.Who(1), P("blink every (0 = hide)", ParamKind.Number, 2));
        Add(0x5E, 0x5F, "Show a character", Chars, (b, _) => $"Show {Who(ParamDef.Who(1).Get(b))}", new byte[] { 0x5E, 1 }, "", ParamDef.Who(1));
        Add(0x1A, 0x1A, "Character joins", Chars,
            (b, _) => $"{CharacterNames[b[1] & 15]} joins the party (appears at {(b[2] == 0 ? "the leader" : $"character {b[2]}")})",
            new byte[] { 0x1A, 1, 0 }, "", P("character", ParamKind.Character, 1, 0, 15), P("appears at (0 = leader)", ParamKind.Npc, 2));
        Add(0x1B, 0x1B, "Character leaves", Chars, (b, _) => $"{CharacterNames[b[1] & 15]} leaves the party (the last follower disappears)",
            new byte[] { 0x1B, 1 }, "", P("character", ParamKind.Character, 1, 0, 15));

        // ── camera & screen ──
        Add(0x09, 0x09, "Move the camera to", Cam, (b, _) => $"Camera jumps to centre on ({W(b, 1)}, {W(b, 3)})", new byte[] { 0x09, 0, 0, 0, 0 }, "",
            ParamDef.Word("X", 1), ParamDef.Word("Y", 3));
        Add(0x10, 0x13, "Scroll the camera", Cam,
            (b, _) => $"Camera scrolls {Directions[b[0] & 3]} {(b[1] == 0 ? 256 : b[1])} tiles (speed {b[2]})", new byte[] { 0x12, 4, 1 }, "until the scroll ends",
            ParamDef.Bits("direction", ParamKind.Direction, 0, 3, 0), P("tiles", ParamKind.Number, 1), P("speed", ParamKind.ScrollSpeed, 2, 0, 7));
        // 18 fades the colours in from black, 19 fades them out (19 is always used right before warps that stay black)
        Add(0x18, 0x18, "Fade in", Cam, (b, _) => $"Screen fades in from black ({b[1]} frames per step)", new byte[] { 0x18, 2 }, "", P("frames per step", ParamKind.Number, 1));
        Add(0x19, 0x19, "Fade to black", Cam, (b, _) => $"Screen fades to black ({b[1]} frames per step)", new byte[] { 0x19, 1 }, "", P("frames per step", ParamKind.Number, 1));
        Add(0x5A, 0x5A, "Wait for the fade", Cam, (_, _) => "Wait until the screen fade has finished", new byte[] { 0x5A }, "until the fade ends");
        Add(0x60, 0x60, "Lightning flash", Cam,
            (b, _) => $"Screen flash ({((b[1] & 15) == 0 ? "white, with thunder" : "silent")}){(b[1] >> 4 == 0 ? "" : $", repeats at random (chance {b[1] >> 4})")}",
            new byte[] { 0x60, 0 }, "", ParamDef.Bits("kind", ParamKind.FlashKind, 1, 0x0F, 0), ParamDef.Bits("repeat chance", ParamKind.Number, 1, 0xF0, 4));
        Add(0x61, 0x61, "Stop the flash", Cam, (_, _) => "Stop the screen flash", new byte[] { 0x61 }, "until the current flash ends");
        Add(0x65, 0x66, "Shake the screen", Cam,
            (b, _) => $"Screen shakes {(b[0] == 0x65 ? "sideways" : "up and down")} ({(b[1] & 15) + 1} px)", new byte[] { 0x65, 0x11 }, "",
            ParamDef.Bits("strength (px)", ParamKind.Number, 1, 0x0F, 0, 1), ParamDef.Bits("speed", ParamKind.Number, 1, 0x70, 4));
        Add(0x67, 0x67, "Stop shaking", Cam, (_, _) => "Screen stops shaking", new byte[] { 0x67 });
        Add(0x62, 0x63, "Spinning orbs effect", Cam,
            (b, _) => $"Orbs spin {(b[0] == 0x62 ? "one way" : "the other way")} around ({b[4] | ((b[5] >> 7) << 8)}, {b[3]}), slot {b[1] & 7}, radius {b[2]}, {(b[5] & 15) + 1} arms",
            new byte[] { 0x62, 0x08, 0x30, 0, 0, 0x03 }, "",
            ParamDef.Bits("slot", ParamKind.Number, 1, 0x07, 0), ParamDef.Bits("speed", ParamKind.Number, 1, 0xF8, 3, 1), P("radius", ParamKind.Number, 2),
            new ParamDef { Label = "X", Kind = ParamKind.Number, Max = 511, Get = b => b[4] | ((b[5] >> 7) << 8), Set = (b, v) => { b[4] = (byte)v; b[5] = (byte)((b[5] & 0x7F) | ((v >> 8 & 1) << 7)); } },
            P("Y", ParamKind.Number, 3), ParamDef.Bits("arms", ParamKind.Number, 5, 0x0F, 0, 1), ParamDef.Bits("sprite", ParamKind.Number, 5, 0x30, 4));
        Add(0x64, 0x64, "Remove spinning orbs", Cam, (b, _) => $"Remove the orbs in slot {b[1]}", new byte[] { 0x64, 0 }, "", P("slot", ParamKind.Number, 1, 0, 3));
        Add(0x68, 0x68, "Change spinning orbs", Cam,
            (b, _) => $"Orbs in slot {b[1] & 7} change to speed {(b[1] >> 3) + 1}, radius {b[2]} (step {b[3] + 1})", new byte[] { 0x68, 0x08, 0x10, 0 }, "",
            ParamDef.Bits("slot", ParamKind.Number, 1, 0x07, 0), ParamDef.Bits("speed", ParamKind.Number, 1, 0xF8, 3, 1), P("radius", ParamKind.Number, 2), P("radius step", ParamKind.Number, 3, 1));
        Add(0x08, 0x08, "Refresh the map's characters", Cam, (_, _) => "Refresh the map's characters and objects (re-checks story flags)", new byte[] { 0x08 });
        Add(0x56, 0x56, "Redraw the screen", Cam, (_, _) => "Redraw the screen (after tile stamps)", new byte[] { 0x56 });

        // ── music & sound ──
        Add(0x0A, 0x0A, "Play music", Snd, (b, _) => b[1] == 0xFF ? "Stop the music" : $"Play music {b[1]:X2}", new byte[] { 0x0A, 1 }, "", P("song (FF = stop)", ParamKind.Music, 1));
        Add(0x54, 0x54, "Play sound effect", Snd, (b, _) => $"Play sound effect {b[1]:X2}", new byte[] { 0x54, 0x10 }, "", P("sound", ParamKind.Sound, 1));
        Add(0x3A, 0x3A, "Set the text sound", Snd, (b, _) => $"Text prints with sound {b[1]:X2}", new byte[] { 0x3A, 4 }, "", P("sound", ParamKind.Sound, 1));
        Add(0x3B, 0x3B, "Silent text", Snd, (_, _) => "Text prints without sound", new byte[] { 0x3B });
        Add(0x6C, 0x6C, "Wait for the music", Snd, (b, _) => $"Wait until the music reaches point {b[1]} (about {b[1] * 1.5:0.#} s into the song)", new byte[] { 0x6C, 1 }, "until the music reaches that point",
            P("music point", ParamKind.Number, 1));

        // ── timing ──
        Add(0x80, 0x87, "Wait (short)", Time, (b, _) => $"Wait {Frames(ShortWaitFrames[b[0] & 7])}", new byte[] { 0x84 }, "for the time given",
            ParamDef.Bits("length", ParamKind.ShortWait, 0, 7, 0));
        Add(0x0B, 0x0B, "Wait", Time, (b, _) => $"Wait {Frames((b[1] * 4) & 0xFF)}", new byte[] { 0x0B, 15 }, "for the time given",
            P("time (× 4 frames, max 63)", ParamKind.Number, 1, 0, 63));

        // ── text window ──
        Add(0x69, 0x69, "Text advances by itself", Win, (b, _) => $"Text boxes close by themselves after {Frames(b[1] * 8 + 1)}", new byte[] { 0x69, 8 }, "",
            P("time (× 8 frames)", ParamKind.Number, 1));
        Add(0x6A, 0x6A, "Text waits for a button again", Win, (_, _) => "Text boxes wait for a button again", new byte[] { 0x6A });
        Add(0x6B, 0x6B, "Place the text window", Win,
            (b, _) => b[1] == 0 && b[2] == 0 ? "Text window placed automatically" : $"Text window at {(b[1] >= 0x20 ? "the centre" : $"column {b[1]}")}, row {b[2]}",
            new byte[] { 0x6B, 0x20, 0x0A }, "", P("column (20+ = centre)", ParamKind.Number, 1), P("row", ParamKind.Number, 2));
        Add(0x6D, 0x6D, "Text window colours", Win, (b, _) => $"Text window colour scheme {b[1]}", new byte[] { 0x6D, 2 }, "", P("scheme", ParamKind.Number, 1, 0, 7));

        // ── party & items ──
        Add(0x3E, 0x3E, "Give item", Party, (b, w) => $"Give {w.Item(b[1])} × {b[2]} (flag FF set if it didn't fit)", new byte[] { 0x3E, 0x94, 1 }, "",
            P("item", ParamKind.Item, 1), P("quantity", ParamKind.Number, 2));
        Add(0x3F, 0x3F, "Take item", Party, (b, w) => $"Take {w.Item(b[1])} × {b[2]}", new byte[] { 0x3F, 0x94, 1 }, "",
            P("item", ParamKind.Item, 1), P("quantity", ParamKind.Number, 2));
        Add(0x4D, 0x4D, "Give key item", Party, (b, w) => $"Give key item {b[1]} ({w.Item(0xF0 + b[1] - 1)})", new byte[] { 0x4D, 1 }, "", P("key item", ParamKind.KeyItem, 1, 0, 16));
        Add(0x4E, 0x4E, "Take key item", Party, (b, w) => $"Take key item {b[1]} ({w.Item(0xF0 + b[1] - 1)})", new byte[] { 0x4E, 1 }, "", P("key item", ParamKind.KeyItem, 1, 0, 16));
        Add(0x40, 0x40, "Give gold", Party, (b, _) => $"Give {W(b, 1)} GP", new byte[] { 0x40, 100, 0 }, "", ParamDef.Word("GP", 1));
        Add(0x41, 0x41, "Take gold", Party, (b, _) => $"Take {W(b, 1)} GP (flag FF set if the party can't pay)", new byte[] { 0x41, 100, 0 }, "", ParamDef.Word("GP", 1));
        Add(0x3D, 0x3D, "Learn spell", Party, (b, w) => $"{CharacterNames[b[1] & 15]} learns {w.Spell(b[2])}", new byte[] { 0x3D, 0, 0 }, "",
            P("character", ParamKind.Character, 1, 0, 3), P("spell", ParamKind.Spell, 2));
        Add(0x42, 0x42, "Restore HP", Party, (b, _) => $"Restore HP of {PartyName(b[1])}", new byte[] { 0x42, 0xFF }, "", P("who", ParamKind.PartyOrAll, 1));
        Add(0x43, 0x43, "Restore MP", Party, (b, _) => $"Restore MP of {PartyName(b[1])}", new byte[] { 0x43, 0xFF }, "", P("who", ParamKind.PartyOrAll, 1));
        Add(0x44, 0x44, "Cure status", Party, (b, _) => $"Cure the status of {PartyName(b[1])}", new byte[] { 0x44, 0xFF }, "", P("who", ParamKind.PartyOrAll, 1));
        Add(0x59, 0x59, "Raise max MP", Party, (b, _) => $"{CharacterNames[b[1] & 15]}'s max MP +{b[2]}", new byte[] { 0x59, 0, 10 }, "",
            P("character", ParamKind.Character, 1, 0, 3), P("amount", ParamKind.Number, 2));
        Add(0x58, 0x58, "Raise max HP (crashes the game)", Party, (b, _) => "Raise max HP — this command crashes the game", null);

        // ── places & map ──
        Add(0x4C, 0x4C, "Warp to a map", Place, (b, w) => b[1] == 0 ? $"Move the party to arrival point {b[2]} of this map" : $"Warp to {w.Map(b[1])}, arrival point {b[2]}",
            new byte[] { 0x4C, 0, 1 }, "until the new map has faded in", P("map (00 = this map)", ParamKind.Map, 1), P("arrival point", ParamKind.Number, 2));
        Add(0x5B, 0x5B, "Warp to a map (stay black)", Place, (b, w) => $"Warp to {w.Map(b[1])}, arrival point {b[2]}; the screen stays black",
            new byte[] { 0x5B, 0, 1 }, "until the new map is loaded", P("map (00 = this map)", ParamKind.Map, 1), P("arrival point", ParamKind.Number, 2));
        Add(0x52, 0x52, "Open a map object", Place, (b, _) => $"Open map object {b[1]} (door, gate…) and remember it", new byte[] { 0x52, 1 }, "", P("object", ParamKind.Number, 1));
        Add(0x53, 0x53, "Draw a map object closed", Place, (b, _) => $"Draw map object {b[1] + 1} closed", new byte[] { 0x53, 0 }, "", P("object", ParamKind.Number, 1, 1));
        Add(0x55, 0x55, "Stamp tiles", Place, (b, _) => $"Stamp tile pattern {b[1]} at ({W(b, 2)}, {W(b, 4)})", new byte[] { 0x55, 1, 0, 0, 0, 0 }, "",
            P("pattern", ParamKind.Number, 1), ParamDef.Word("X", 2), ParamDef.Word("Y", 4));
        Add(0x57, 0x57, "Set the game-over return point", Place, (b, _) => b[1] == 0 ? "After a game over, return to the last town" : $"After a game over, return to point {b[1]}",
            new byte[] { 0x57, 0 }, "", P("point (0 = last town)", ParamKind.Number, 1));
        Add(0x4B, 0x4B, "Place a vehicle", Place, (b, _) => $"Vehicle {b[1]} waits at world map ({W(b, 2)}, {W(b, 4)})", new byte[] { 0x4B, 1, 0, 0, 0, 0 }, "",
            P("vehicle", ParamKind.Number, 1), ParamDef.Word("X", 2), ParamDef.Word("Y", 4));

        // ── shops & services ──
        Add(0x1C, 0x1C, "Open a shop", Serv, (b, _) => $"Open shop {b[1]} (flag FF set if something was bought or sold)", new byte[] { 0x1C, 1 }, "until the shop closes", P("shop", ParamKind.Shop, 1));
        Add(0x1D, 0x1D, "Stay at the inn", Serv, (b, _) => $"Stay at the inn for {W(b, 1)} GP (check the gold first)", new byte[] { 0x1D, 10, 0 }, "until the night is over", ParamDef.Word("price", 1));
        Add(0x4A, 0x4A, "Save menu", Serv, (_, _) => "Open the save menu", new byte[] { 0x4A }, "until the menu closes");
        Add(0x1F, 0x1F, "Start a battle", Serv, (b, _) => $"Battle with formation {b[1]:X2}", new byte[] { 0x1F, 0 }, "until the battle ends", P("formation", ParamKind.Formation, 1));
        Add(0x50, 0x50, "System function", Serv, (b, _) => b[1] < SystemCalls.Length ? $"System: {SystemCalls[b[1]]}" : $"System function {b[1]:X2}",
            new byte[] { 0x50, 0x05 }, "", P("function", ParamKind.SystemCall, 1, 0, SystemCalls.Length - 1));
        return l;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>Choices for a field kind, as (value, label) pairs; null = plain number field.</summary>
    public static IReadOnlyList<(int Value, string Label)>? Choices(ParamKind kind, RomBuffer rom, Func<int, string>? mapLabel = null) => kind switch
    {
        ParamKind.Item => Enumerable.Range(0, 256).Select(i => (i, $"{i:X2} {MapSetupScript.ItemName(rom, i)}")).ToList(),
        ParamKind.Spell => Enumerable.Range(0, GameDataOffsets.SpellOffsets.Length).Select(i => (i, $"{i:X2} {SpellName(rom, i)}")).ToList(),
        ParamKind.Character => CharacterNames.Select((n, i) => (i, n)).ToList(),
        ParamKind.Direction => Directions.Select((d, i) => (i, d)).ToList(),
        ParamKind.ShortWait => ShortWaitFrames.Select((f, i) => (i, Frames(f))).ToList(),
        ParamKind.ScrollSpeed => ScrollSpeeds.Select((s, i) => (i, $"{i}: {s switch { 2 => "slowest", 4 => "slow", 8 => "medium", 0x10 => "normal", 0x20 => "fast", 0x40 => "faster", _ => "fastest" }}")).ToList(),
        ParamKind.SystemCall => SystemCalls.Select((s, i) => (i, $"{i:X2} {s}")).ToList(),
        ParamKind.PartyOrAll => new List<(int, string)> { (0, "Hero"), (1, "Lufia"), (2, "Aguro"), (3, "Jerin"), (0xFF, "Everyone") },
        ParamKind.Who => Enumerable.Range(1, 6).Select(a => (a, Cap(Actor(a))))
                         .Concat(Enumerable.Range(1, 32).Select(n => (0x100 + n, $"Character {n}"))).ToList(),
        ParamKind.Actor => Enumerable.Range(1, 4).Select(a => (a, Cap(Actor(a)))).ToList(),
        ParamKind.KeyItem => Enumerable.Range(1, 16).Select(n => (n, $"{n}: {MapSetupScript.ItemName(rom, 0xF0 + n - 1)}")).ToList(),
        ParamKind.Map when mapLabel != null => Enumerable.Range(0, 256).Select(m => (m, m == 0 ? "00 (this map / none)" : mapLabel(m))).ToList(),
        ParamKind.FlashKind => new List<(int, string)> { (0, "White, with thunder"), (1, "Silent") },
        ParamKind.Flag => StoryFlags.Choices(rom),
        _ => null,
    };

    public static string SpellName(RomBuffer rom, int id)
    {
        if (id < 0 || id >= GameDataOffsets.SpellOffsets.Length) return $"spell {id:X2}";
        var chars = rom.ReadBytes(GameDataOffsets.SpellOffsets[id], GameDataOffsets.SpellNameLen).Select(b => b == (byte)'@' ? ' ' : (char)b);
        string name = new string(chars.ToArray()).TrimEnd(' ', '\0');
        return name.Length == 0 ? $"spell {id:X2}" : name;
    }
}
