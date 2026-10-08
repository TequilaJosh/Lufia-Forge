namespace LufiaForge.Core.Maps;

/// <summary>One line of a cutscene placed in time.</summary>
/// <param name="Blocking">The cutscene waits for it to finish before the next line.</param>
/// <param name="Shown">How long it stays visible when longer than <paramref name="Duration"/> (a text box that closed by itself stays on screen).</param>
public sealed record TimelineItem(int Line, int Start, int Duration, bool Blocking, string Track, string Label, string Kind, int Shown = 0)
{
    public int VisibleLength => Math.Max(Duration, Shown);
}

/// <summary>
/// Places every line of a cutscene in time (phase 6.5): when it starts, how long it lasts and on which track
/// (a character, the camera, dialogue, screen, sound, waits, logic). Timing follows the engine: waits are exact,
/// walks take tiles × frames-per-tile at their speed, camera scrolls likewise, fades take 32 steps, and dialogue
/// is an estimate (the player closes the boxes).
/// </summary>
public static class CutsceneTimeline
{
    public static readonly string[] FixedTracks = { "Dialogue", "Camera", "Screen", "Sound", "Wait", "Logic", "Other" };

    /// <summary>
    /// Frames per music "point" (command 6C waits until the music reaches point n). Measured in the game on the
    /// intro: point n is reached n × 90 frames (1.5 s, one bar) after the song starts.
    /// </summary>
    public const double FramesPerMusicPoint = 89.7;   // fitted over the intro: points 12-55 in 3858 frames

    /// <summary>
    /// Stretches of time when walking is slowed. Measured on the intro: a fade in (18) stops walks for its 2 setup
    /// frames, then on t+5/t+6 and every other frame from t+9 until the fade ends. Fades to black (19) aren't measured
    /// yet and are assumed to halve walking speed. Includes the <see cref="GameStalls"/>.
    /// </summary>
    public static List<(int Start, int End, double Rate)> SlowSpans(EventScript script, IEnumerable<TimelineItem> items)
    {
        var spans = new List<(int, int, double)>();
        foreach (var it in items)
        {
            if (it.Line >= script.Ops.Count || script.Ops[it.Line] is not { IsText: false } op || op.Bytes.Length == 0) continue;
            if (op.Bytes[0] == 0x18)
            {
                // frames on which walking stands still (measured on the intro, fade in at 2 frames per step):
                // the 2 setup frames, then t+5 and t+6, then every other frame from t+9 until the fade ends
                int t = it.Start, end = it.Start + it.VisibleLength;
                var stalls = new List<int> { t, t + 1, t + 5, t + 6 };
                for (int f = t + 9; f < end; f += 2) stalls.Add(f);
                foreach (int f in stalls) spans.Add((f - 1, f, 0));   // the step into frame f doesn't happen
            }
            else if (op.Bytes[0] == 0x19) spans.Add((it.Start, it.Start + it.VisibleLength, 0.5));
        }
        spans.AddRange(GameStalls(script, items));
        return spans;
    }

    /// <summary>
    /// Frames on which the whole game holds (walks, typing and screen effects all stand still), as spans for
    /// <see cref="WalkProgress"/>. So far only the intro's second lightning flash, measured in the game.
    /// </summary>
    public static List<(int Start, int End, double Rate)> GameStalls(EventScript script, IEnumerable<TimelineItem> items)
    {
        var spans = new List<(int, int, double)>();
        int flashes = 0;
        foreach (var it in items.OrderBy(i => i.Start).ThenBy(i => i.Line))
        {
            if (it.Line >= script.Ops.Count || script.Ops[it.Line] is not { IsText: false } op || op.Bytes.Length == 0 || op.Bytes[0] != 0x60) continue;
            flashes++;
            if (FlashStalls.Contains((script.MapId, script.Event, flashes))) spans.Add((it.Start, it.Start + 1, 0));
        }
        return spans;
    }

    /// <summary>
    /// Lightning flashes that hold walks for a frame, measured in the game: (map, event, nth flash of the event).
    /// Most flashes cost nothing; in the intro the second one (before "They possessed the frightening powers...")
    /// holds the game for the next frame: the walk, the flash itself and the typing all start a frame late, and
    /// removing that flash from the ROM removes the stall.
    /// </summary>
    private static readonly HashSet<(int Map, int Event, int Flash)> FlashStalls = new() { (0x4F, 8, 2) };

    /// <summary>Frames of walking done between <paramref name="start"/> and <paramref name="start"/> + <paramref name="elapsed"/>.</summary>
    public static double WalkProgress(int start, int elapsed, IReadOnlyList<(int Start, int End, double Rate)> spans)
    {
        double progress = elapsed;
        int end = start + elapsed;
        foreach (var (s, e, rate) in spans)
        {
            int overlap = Math.Min(e, end) - Math.Max(s, start);
            if (overlap > 0) progress -= overlap * (1 - rate);
        }
        return Math.Max(0, progress);
    }

    /// <summary>Real frames a walk needing <paramref name="frames"/> frames of progress takes from <paramref name="start"/>.</summary>
    public static int WalkLength(int start, int frames, IReadOnlyList<(int Start, int End, double Rate)> spans)
    {
        int real = frames;
        while (WalkProgress(start, real, spans) < frames && real < frames * 4 + 1000) real++;
        return real;
    }

    /// <summary>Frames a dialogue box is assumed to stay open.</summary>
    public static int TextFrames(string text) => Math.Clamp(text.Length * 3, 90, 360);

    /// <summary>The actor a command acts on, or -1.</summary>
    public static int ActorOf(byte[] b)
    {
        if (b.Length == 0) return -1;
        try
        {
            return b[0] switch
            {
                >= 0x20 and <= 0x27 => CutsceneStage.ActorOf(ParamDef.Who(1, 0x7F).Get(b)),
                >= 0x28 and <= 0x2F or 0x38 or 0x39 or >= 0x5C and <= 0x5F => CutsceneStage.ActorOf(ParamDef.Who(1).Get(b)),
                0x14 or 0x15 => CutsceneStage.ActorOf(ParamDef.Who(2).Get(b)),
                >= 0xB0 and <= 0xBF => (b[0] & 3) + 1,
                _ => -1,
            };
        }
        catch { return -1; }
    }

    /// <param name="musicLead">Frames the music has already been playing when the event starts (the intro's song starts
    /// before its event; most events start with the map's music, or start a song themselves).</param>
    public static List<TimelineItem> Build(EventScript script, IReadOnlyList<StageState> states, int musicLead = -1)
    {
        var items = new List<TimelineItem>();
        // the song playing when the event starts (known for some cutscenes) and how long it has been playing
        var (song, lead) = MusicSync.StartOf(script.MapId, script.Event);
        if (musicLead >= 0) lead = musicLead;
        int t = 0, fadeEnd = 0, musicStart = -lead;
        int autoText = 0;   // command 69: boxes close by themselves after this many frames (0 = the player closes them)
        int lingering = -1;  // index of a self-closing box still on screen   // music is assumed to start with the event unless a line starts it
        for (int i = 0; i < script.Ops.Count && i < states.Count; i++)
        {
            var op = script.Ops[i];
            var st = states[i];
            string Name(int actor) => actor >= 0 && st.Actors.TryGetValue(actor, out var a) ? a.Name : actor >= 7 ? $"Character {actor - 7}" : EventCommands.Actor(actor);
            string track, kind, label;
            int dur = 1;
            bool blocking = false;
            if (op.IsText)
            {
                track = "Dialogue"; kind = "text";
                string who = st.Speaker >= 0 ? Name(st.Speaker) : EventScript.SpeakerName(op.Bytes);
                label = $"{who}: {op.Text.Replace("\n", " ")}";
                // a self-closing box holds the script for its delay (delay 0: not at all, measured on the intro)
                dur = autoText > 0 ? Math.Max(1, autoText - 1) : TextFrames(op.Text); blocking = autoText == 0 || autoText > 1;
            }
            else
            {
                var b = op.Bytes;
                int o = b.Length > 0 ? b[0] : 0;
                var def = EventCommands.Find(o);
                label = script.Describe(op);
                int actor = ActorOf(b);
                switch (o)
                {
                    case 0x14 or 0x15:
                    {
                        track = Name(actor); kind = "walk";
                        var w = st.Walks.FirstOrDefault();
                        if (w != null)
                        {
                            dur = Math.Max(1, WalkLength(t, w.Frames(), SlowSpans(script, items)));
                            blocking = w.Waits;
                        }
                        break;
                    }
                    case 0x3C:
                    {
                        track = "Party"; kind = "walk";
                        dur = st.Walks.Count == 0 ? 1 : st.Walks.Max(w => Math.Max(1, (Math.Abs(w.Points[^1].X - w.Points[0].X) + Math.Abs(w.Points[^1].Y - w.Points[0].Y)) * CutsceneStage.FramesPerTile(w.Speed)));
                        blocking = true;
                        break;
                    }
                    case >= 0x20 and <= 0x2F or >= 0xB0 and <= 0xBF or 0x38 or 0x39 or >= 0x5C and <= 0x5F or 0x1A or 0x1B:
                        track = o is 0x1A or 0x1B ? "Party" : Name(actor); kind = "actor"; break;
                    case 0x09: track = "Camera"; kind = "camera"; break;
                    case >= 0x10 and <= 0x13: track = "Camera"; kind = "camera"; dur = Math.Max(1, st.WaitFrames); blocking = true; break;
                    case 0x18 or 0x19:
                    {
                        // measured: fade in (18) spends 2 frames building its palette, then fades in 33 steps of b[1] frames
                        track = "Screen"; kind = "screen";
                        int setup = b[0] == 0x18 ? 2 : 0;
                        dur = setup + 33 * Math.Max(1, (int)b[1]);
                        fadeEnd = t + dur;
                        if (setup > 0) { items.Add(new TimelineItem(i, t, setup, true, track, label, kind, dur)); t += setup; goto Next; }
                        break;
                    }
                    case 0x5A: track = "Screen"; kind = "wait"; dur = Math.Max(1, fadeEnd - t); blocking = fadeEnd > t; break;   // no fade running: no wait
                    case 0x50 when b.Length > 1 && b[1] is 0x05 or 0x06 or 0x08: track = "Screen"; kind = "screen"; dur = 32; blocking = true; break;
                    case 0x50 when b.Length > 1 && b[1] == 0x14: track = "Dialogue"; kind = "other"; break;   // closing the text overlay: instant (measured)
                    case >= 0x60 and <= 0x68: track = "Screen"; kind = "screen"; break;
                    case 0x0A:
                        track = "Sound"; kind = "sound";
                        if (b.Length > 1 && b[1] != 0xFF) { musicStart = t; song = b[1]; }
                        break;
                    case 0x54 or 0x3A or 0x3B: track = "Sound"; kind = "sound"; break;
                    case 0x69 when b.Length > 1: track = "Other"; kind = "other"; autoText = b[1] * 8 + 1; break;
                    case 0x6A: track = "Other"; kind = "other"; autoText = 0; break;
                    case 0x6C:
                        // until the music reaches point n
                        track = "Sound"; kind = "wait"; blocking = true;
                        int reach = musicStart + MusicSync.FrameOfPoint(song, b[1]);
                        dur = Math.Max(1, reach - t);
                        blocking = reach > t;   // point already passed: carries straight on
                        break;
                    case >= 0x80 and <= 0x87 or 0x0B: track = "Wait"; kind = "wait"; dur = Math.Max(1, st.WaitFrames); blocking = true; break;
                    case <= 0x07 or >= 0xC0 or 0x51 or >= 0x45 and <= 0x4F when def?.Category is "Flow" or "Story flags":
                        track = "Logic"; kind = "logic"; break;
                    default: track = def?.Category is "Flow" or "Story flags" ? "Logic" : "Other"; kind = "other"; break;
                }
            }
            // a self-closing box stays visible until the next box or a clear (overlay closed, fade, auto text off, event end)
            bool clears = op.IsText || (!op.IsText && op.Bytes.Length > 1 && (op.Bytes[0] == 0x19 || op.Bytes[0] == 0x6A || (op.Bytes[0] == 0x50 && op.Bytes[1] is 0x05 or 0x08 or 0x09 or 0x14)));
            if (clears && lingering >= 0) { var l = items[lingering]; items[lingering] = l with { Shown = t - l.Start }; lingering = -1; }
            items.Add(new TimelineItem(i, t, dur, blocking, track, label, kind));
            if (op.IsText && autoText > 0) lingering = items.Count - 1;
            if (blocking) t += dur;
            if (st.Leaves != null) break;
            continue;
        Next:
            if (st.Leaves != null) break;
        }
        if (lingering >= 0) { var l = items[lingering]; items[lingering] = l with { Shown = t - l.Start }; }
        return items;
    }

    /// <summary>Tracks in display order: characters first (by first use), then the fixed ones that are used.</summary>
    public static List<string> Tracks(IEnumerable<TimelineItem> items)
    {
        var list = items.Select(i => i.Track).Distinct().ToList();
        var actors = list.Where(t => !FixedTracks.Contains(t)).ToList();
        return actors.Concat(FixedTracks.Where(list.Contains)).ToList();
    }

    /// <summary>Commands that wait <paramref name="frames"/> frames (0B waits up to 63 × 4 frames each).</summary>
    public static List<byte[]> WaitCommands(int frames)
    {
        var list = new List<byte[]>();
        int units = (frames + 2) / 4;
        while (units > 0) { int n = Math.Min(63, units); list.Add(new byte[] { 0x0B, (byte)n }); units -= n; }
        return list;
    }

    /// <summary>Frames a wait command waits, or -1 when it isn't a wait.</summary>
    public static int WaitFrames(ScriptOp op) => op.IsText || op.Bytes.Length == 0 ? -1 : op.Bytes[0] switch
    {
        0x0B when op.Bytes.Length > 1 => (op.Bytes[1] * 4) & 0xFF,
        >= 0x80 and <= 0x87 => EventCommands.ShortWaitFrames[op.Bytes[0] & 7],
        _ => -1,
    };
}
