namespace LufiaForge.Core.Maps;

/// <summary>One line of a cutscene placed in time.</summary>
/// <param name="Blocking">The cutscene waits for it to finish before the next line.</param>
public sealed record TimelineItem(int Line, int Start, int Duration, bool Blocking, string Track, string Label, string Kind);

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
    public const int FramesPerMusicPoint = 90;

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

    public static List<TimelineItem> Build(EventScript script, IReadOnlyList<StageState> states)
    {
        var items = new List<TimelineItem>();
        int t = 0, fadeEnd = 0, musicStart = 0;
        int autoText = 0;   // command 69: boxes close by themselves after this many frames (0 = the player closes them)   // music is assumed to start with the event unless a line starts it
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
                dur = autoText > 0 ? autoText : TextFrames(op.Text); blocking = true;
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
                            int tiles = 0;
                            for (int k = 1; k < w.Points.Count; k++) tiles += Math.Abs(w.Points[k].X - w.Points[k - 1].X) + Math.Abs(w.Points[k].Y - w.Points[k - 1].Y);
                            dur = Math.Max(1, tiles * CutsceneStage.FramesPerTile(w.Speed));
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
                        track = "Screen"; kind = "screen"; dur = 32 * Math.Max(1, (int)b[1]); fadeEnd = t + dur; break;
                    case 0x5A: track = "Screen"; kind = "wait"; dur = Math.Max(1, fadeEnd - t); blocking = true; break;
                    case 0x50 when b.Length > 1 && b[1] is 0x05 or 0x06 or 0x08: track = "Screen"; kind = "screen"; dur = 32; blocking = true; break;
                    case >= 0x60 and <= 0x68: track = "Screen"; kind = "screen"; break;
                    case 0x0A:
                        track = "Sound"; kind = "sound";
                        if (b.Length > 1 && b[1] != 0xFF) musicStart = t;
                        break;
                    case 0x54 or 0x3A or 0x3B: track = "Sound"; kind = "sound"; break;
                    case 0x69 when b.Length > 1: track = "Other"; kind = "other"; autoText = b[1] * 8 + 1; break;
                    case 0x6A: track = "Other"; kind = "other"; autoText = 0; break;
                    case 0x6C:
                        // until the music reaches point n
                        track = "Sound"; kind = "wait"; blocking = true;
                        dur = Math.Max(1, musicStart + b[1] * FramesPerMusicPoint - t);
                        break;
                    case >= 0x80 and <= 0x87 or 0x0B: track = "Wait"; kind = "wait"; dur = Math.Max(1, st.WaitFrames); blocking = true; break;
                    case <= 0x07 or >= 0xC0 or 0x51 or >= 0x45 and <= 0x4F when def?.Category is "Flow" or "Story flags":
                        track = "Logic"; kind = "logic"; break;
                    default: track = def?.Category is "Flow" or "Story flags" ? "Logic" : "Other"; kind = "other"; break;
                }
            }
            items.Add(new TimelineItem(i, t, dur, blocking, track, label, kind));
            if (blocking) t += dur;
            if (st.Leaves != null) break;
        }
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
