namespace LufiaForge.Core.Maps;

// one line of a cutscene placed in time.
// Blocking = the cutscene waits for it to finish before the next line.
// Shown = how long it stays visible when that's longer than Duration (a text box that closed by itself stays on screen)
public sealed record TimelineItem(int Line, int Start, int Duration, bool Blocking, string Track, string Label, string Kind, int Shown = 0)
{
    public int VisibleLength => Math.Max(Duration, Shown);
}

// places every line of a cutscene in time (phase 6.5): when it starts, how long it lasts and which track it's on
// (a character, the camera, dialogue, screen, sound, waits, logic). timing follows the engine. waits are exact,
// walks take tiles x frames-per-tile at their speed, camera scrolls the same, fades take 32 steps, and dialogue
// is a guess (the player closes the boxes)
public static class CutsceneTimeline
{
    public static readonly string[] FixedTracks = { "Dialogue", "Camera", "Screen", "Sound", "Wait", "Logic", "Other" };

    // frames per music "point" (command 6C waits until the music reaches point n). measured in the game on the
    // intro: point n is reached n x 90 frames (1.5 s, one bar) after the song starts
    public const double FramesPerMusicPoint = 89.7;   // fitted over the intro: points 12-55 in 3858 frames

    // stretches of time when walking is slowed down. measured on the intro: a fade in (18) stops walks for its 2 setup
    // frames, then on t+5/t+6 and every other frame from t+9 until the fade ends. fades to black (19) aren't measured
    // yet, we assume they halve walking speed. GameStalls are in here too
    public static List<(int Start, int End, double Rate)> SlowSpans(EventScript script, IEnumerable<TimelineItem> items)
    {
        var spans = new List<(int, int, double)>();
        foreach (var item in items)
        {
            if (item.Line >= script.Ops.Count || script.Ops[item.Line] is not { IsText: false } op || op.Bytes.Length == 0) continue;
            if (op.Bytes[0] == 0x18)
            {
                // frames where walking stands still (measured on the intro, fade in at 2 frames per step):
                // the 2 setup frames, then t+5 and t+6, then every other frame from t+9 until the fade ends
                int fadeStart = item.Start, end = item.Start + item.VisibleLength;
                var stalls = new List<int> { fadeStart, fadeStart + 1, fadeStart + 5, fadeStart + 6 };
                for (int f = fadeStart + 9; f < end; f += 2) stalls.Add(f);
                foreach (int f in stalls) spans.Add((f - 1, f, 0));   // the step into frame f doesn't happen
            }
            else if (op.Bytes[0] == 0x19) spans.Add((item.Start, item.Start + item.VisibleLength, 0.5));
        }
        spans.AddRange(GameStalls(script, items));
        return spans;
    }

    // frames where the whole game holds (walks, typing and screen effects all stand still), as spans for
    // WalkProgress. so far that's only the intro's second lightning flash, measured in the game
    public static List<(int Start, int End, double Rate)> GameStalls(EventScript script, IEnumerable<TimelineItem> items)
    {
        var spans = new List<(int, int, double)>();
        int flashes = 0;
        foreach (var item in items.OrderBy(i => i.Start).ThenBy(i => i.Line))
        {
            if (item.Line >= script.Ops.Count || script.Ops[item.Line] is not { IsText: false } op || op.Bytes.Length == 0 || op.Bytes[0] != 0x60) continue;
            flashes++;
            if (FlashStalls.Contains((script.MapId, script.Event, flashes))) spans.Add((item.Start, item.Start + 1, 0));
        }
        return spans;
    }

    // lightning flashes that hold walks for a frame, measured in the game: (map, event, nth flash of the event).
    // most flashes cost nothing. in the intro the second one (before "They possessed the frightening powers...")
    // holds the game for the next frame, so the walk, the flash itself and the typing all start a frame late.
    // taking that flash out of the rom takes the stall away too
    private static readonly HashSet<(int Map, int Event, int Flash)> FlashStalls = new() { (0x4F, 8, 2) };

    // frames of walking done between start and start + elapsed
    public static double WalkProgress(int start, int elapsed, IReadOnlyList<(int Start, int End, double Rate)> spans)
    {
        double progress = elapsed;
        int end = start + elapsed;
        foreach (var (spanStart, spanEnd, rate) in spans)
        {
            int overlap = Math.Min(spanEnd, end) - Math.Max(spanStart, start);
            if (overlap > 0) progress -= overlap * (1 - rate);
        }
        return Math.Max(0, progress);
    }

    // real frames a walk that needs frames frames of progress takes, starting at start
    public static int WalkLength(int start, int frames, IReadOnlyList<(int Start, int End, double Rate)> spans)
    {
        int real = frames;
        while (WalkProgress(start, real, spans) < frames && real < frames * 4 + 1000) real++;
        return real;
    }

    // frames we assume a dialogue box stays open
    public static int TextFrames(string text) => Math.Clamp(text.Length * 3, 90, 360);

    // the actor a command acts on, or -1
    public static int ActorOf(byte[] bytes)
    {
        if (bytes.Length == 0) return -1;
        try
        {
            return bytes[0] switch
            {
                >= 0x20 and <= 0x27 => CutsceneStage.ActorOf(ParamDef.Who(1, 0x7F).Get(bytes)),
                >= 0x28 and <= 0x2F or 0x38 or 0x39 or >= 0x5C and <= 0x5F => CutsceneStage.ActorOf(ParamDef.Who(1).Get(bytes)),
                0x14 or 0x15 => CutsceneStage.ActorOf(ParamDef.Who(2).Get(bytes)),
                >= 0xB0 and <= 0xBF => (bytes[0] & 3) + 1,
                _ => -1,
            };
        }
        catch { return -1; }
    }

    // musicLead = frames the music has already been playing when the event starts (the intro's song starts before
    // its event, most events start with the map's music or start a song themselves)
    public static List<TimelineItem> Build(EventScript script, IReadOnlyList<StageState> states, int musicLead = -1)
    {
        var items = new List<TimelineItem>();
        // the song playing when the event starts (known for some cutscenes) and how long it's been playing
        var (song, lead) = MusicSync.StartOf(script.MapId, script.Event);
        if (musicLead >= 0) lead = musicLead;
        int now = 0, fadeEnd = 0, musicStart = -lead;
        int autoText = 0;   // command 69: boxes close by themselves after this many frames (0 = the player closes them)
        int lingering = -1;  // index of a self-closing box still on screen   // music is assumed to start with the event unless a line starts it
        for (int i = 0; i < script.Ops.Count && i < states.Count; i++)
        {
            var op = script.Ops[i];
            var state = states[i];
            string Name(int actor) => actor >= 0 && state.Actors.TryGetValue(actor, out var stageActor) ? stageActor.Name : actor >= 7 ? $"Character {actor - 7}" : EventCommands.Actor(actor);
            string track, kind, label;
            int duration = 1;
            bool blocking = false;
            if (op.IsText)
            {
                track = "Dialogue"; kind = "text";
                string who = state.Speaker >= 0 ? Name(state.Speaker) : EventScript.SpeakerName(op.Bytes);
                label = $"{who}: {op.Text.Replace("\n", " ")}";
                // a self-closing box holds the script for its delay (delay 0 = not at all, measured on the intro)
                duration = autoText > 0 ? Math.Max(1, autoText - 1) : TextFrames(op.Text); blocking = autoText == 0 || autoText > 1;
            }
            else
            {
                var bytes = op.Bytes;
                int command = bytes.Length > 0 ? bytes[0] : 0;
                var def = EventCommands.Find(command);
                label = script.Describe(op);
                int actor = ActorOf(bytes);
                switch (command)
                {
                    case 0x14 or 0x15:
                    {
                        track = Name(actor); kind = "walk";
                        var walk = state.Walks.FirstOrDefault();
                        if (walk != null)
                        {
                            duration = Math.Max(1, WalkLength(now, walk.Frames(), SlowSpans(script, items)));
                            blocking = walk.Waits;
                        }
                        break;
                    }
                    case 0x3C:
                    {
                        track = "Party"; kind = "walk";
                        duration = state.Walks.Count == 0 ? 1 : state.Walks.Max(w => Math.Max(1, (Math.Abs(w.Points[^1].X - w.Points[0].X) + Math.Abs(w.Points[^1].Y - w.Points[0].Y)) * CutsceneStage.FramesPerTile(w.Speed)));
                        blocking = true;
                        break;
                    }
                    case >= 0x20 and <= 0x2F or >= 0xB0 and <= 0xBF or 0x38 or 0x39 or >= 0x5C and <= 0x5F or 0x1A or 0x1B:
                        track = command is 0x1A or 0x1B ? "Party" : Name(actor); kind = "actor"; break;
                    case 0x09: track = "Camera"; kind = "camera"; break;
                    case >= 0x10 and <= 0x13: track = "Camera"; kind = "camera"; duration = Math.Max(1, state.WaitFrames); blocking = true; break;
                    case 0x18 or 0x19:
                    {
                        // measured: fade in (18) spends 2 frames building its palette, then fades in 33 steps of b[1] frames
                        track = "Screen"; kind = "screen";
                        int setup = bytes[0] == 0x18 ? 2 : 0;
                        duration = setup + 33 * Math.Max(1, (int)bytes[1]);
                        fadeEnd = now + duration;
                        if (setup > 0) { items.Add(new TimelineItem(i, now, setup, true, track, label, kind, duration)); now += setup; goto Next; }
                        break;
                    }
                    case 0x5A: track = "Screen"; kind = "wait"; duration = Math.Max(1, fadeEnd - now); blocking = fadeEnd > now; break;   // no fade running = no wait
                    case 0x50 when bytes.Length > 1 && bytes[1] is 0x05 or 0x06 or 0x08: track = "Screen"; kind = "screen"; duration = 32; blocking = true; break;
                    case 0x50 when bytes.Length > 1 && bytes[1] == 0x14: track = "Dialogue"; kind = "other"; break;   // closing the text overlay is instant (measured)
                    case >= 0x60 and <= 0x68: track = "Screen"; kind = "screen"; break;
                    case 0x0A:
                        track = "Sound"; kind = "sound";
                        if (bytes.Length > 1 && bytes[1] != 0xFF) { musicStart = now; song = bytes[1]; }
                        break;
                    case 0x54 or 0x3A or 0x3B: track = "Sound"; kind = "sound"; break;
                    case 0x69 when bytes.Length > 1: track = "Other"; kind = "other"; autoText = bytes[1] * 8 + 1; break;
                    case 0x6A: track = "Other"; kind = "other"; autoText = 0; break;
                    case 0x6C:
                        // until the music reaches point n
                        track = "Sound"; kind = "wait"; blocking = true;
                        int reach = musicStart + MusicSync.FrameOfPoint(song, bytes[1]);
                        duration = Math.Max(1, reach - now);
                        blocking = reach > now;   // point already passed, carries straight on
                        break;
                    case >= 0x80 and <= 0x87 or 0x0B: track = "Wait"; kind = "wait"; duration = Math.Max(1, state.WaitFrames); blocking = true; break;
                    case <= 0x07 or >= 0xC0 or 0x51 or >= 0x45 and <= 0x4F when def?.Category is "Flow" or "Story flags":
                        track = "Logic"; kind = "logic"; break;
                    default: track = def?.Category is "Flow" or "Story flags" ? "Logic" : "Other"; kind = "other"; break;
                }
            }
            // a self-closing box stays visible until the next box or a clear (overlay closed, fade, auto text off, event end)
            bool clears = op.IsText || (!op.IsText && op.Bytes.Length > 1 && (op.Bytes[0] == 0x19 || op.Bytes[0] == 0x6A || (op.Bytes[0] == 0x50 && op.Bytes[1] is 0x05 or 0x08 or 0x09 or 0x14)));
            if (clears && lingering >= 0) { var box = items[lingering]; items[lingering] = box with { Shown = now - box.Start }; lingering = -1; }
            items.Add(new TimelineItem(i, now, duration, blocking, track, label, kind));
            if (op.IsText && autoText > 0) lingering = items.Count - 1;
            if (blocking) now += duration;
            if (state.Leaves != null) break;
            continue;
        Next:
            if (state.Leaves != null) break;
        }
        if (lingering >= 0) { var box = items[lingering]; items[lingering] = box with { Shown = now - box.Start }; }
        return items;
    }

    // tracks in display order: characters first (by first use), then the fixed ones that get used
    public static List<string> Tracks(IEnumerable<TimelineItem> items)
    {
        var list = items.Select(i => i.Track).Distinct().ToList();
        var actors = list.Where(t => !FixedTracks.Contains(t)).ToList();
        return actors.Concat(FixedTracks.Where(list.Contains)).ToList();
    }

    // commands that wait frames frames (0B waits up to 63 x 4 frames each)
    public static List<byte[]> WaitCommands(int frames)
    {
        var list = new List<byte[]>();
        int units = (frames + 2) / 4;
        while (units > 0) { int chunk = Math.Min(63, units); list.Add(new byte[] { 0x0B, (byte)chunk }); units -= chunk; }
        return list;
    }

    // frames a wait command waits, or -1 if it isn't a wait
    public static int WaitFrames(ScriptOp op) => op.IsText || op.Bytes.Length == 0 ? -1 : op.Bytes[0] switch
    {
        0x0B when op.Bytes.Length > 1 => (op.Bytes[1] * 4) & 0xFF,
        >= 0x80 and <= 0x87 => EventCommands.ShortWaitFrames[op.Bytes[0] & 7],
        _ => -1,
    };
}
