namespace LufiaForge.Core.Maps;

/// <summary>What is on screen at one frame of a cutscene.</summary>
public sealed class FrameView
{
    public int Frame { get; init; }
    /// <summary>The line the cutscene is at (the last one that has started).</summary>
    public int Line { get; set; }
    /// <summary>Tile the camera is centred on (fractional while it moves).</summary>
    public double CamX { get; set; }
    public double CamY { get; set; }
    /// <summary>Actor positions in tiles (fractional while walking).</summary>
    public Dictionary<int, (double X, double Y)> Positions { get; } = new();
    /// <summary>The whole text of the box on screen, and the part typed so far.</summary>
    public string? Text { get; set; }
    public string? TypedText { get; set; }
    public int Speaker { get; set; } = -1;
    /// <summary>Where 6B put the text window, or null for the usual dialogue box.</summary>
    public (int Column, int Row)? TextWindow { get; set; }
    /// <summary>White of a lightning flash, 0..1.</summary>
    public double Flash { get; set; }
    /// <summary>Palette fade level: 0 = black, 32 = full colour (the console multiplies each colour by level/32).</summary>
    public int FadeLevel { get; set; } = 32;
    /// <summary>White a lightning flash adds to each colour component (0-31).</summary>
    public int FlashAdd { get; set; }
    /// <summary>Frames the field (walks, the intro's clouds) has run up to the previous frame.</summary>
    public double FieldFrames { get; set; }
    /// <summary>0 = full brightness, 1 = black.</summary>
    public double Darkness { get; set; }
    /// <summary>The stage state of <see cref="Line"/> (visibility, facing, sprites).</summary>
    public StageState State { get; set; } = null!;
}

/// <summary>
/// Frame-accurate replay of a cutscene: from the simulated states and the timeline, works out where everyone and the
/// camera are at any frame. Used by playback, the timeline playhead and the comparison with the real game.
/// </summary>
public static class CutsceneFrame
{
    private sealed class Walk
    {
        public int Actor; public int Start; public bool CameraFollows;
        public List<(double X, double Y)> Pts = new(); public List<int> FramesPerTile = new();
    }

    public static FrameView At(StageState initial, IReadOnlyList<StageState> states, IReadOnlyList<TimelineItem> timeline, EventScript script, int frame)
    {
        var view = new FrameView { Frame = frame, State = initial };
        var pos = initial.Actors.ToDictionary(kv => kv.Key, kv => ((double)kv.Value.X, (double)kv.Value.Y));
        var walks = new Dictionary<int, Walk>();
        int camFollow = -1;                          // actor the camera follows
        bool camFixed = false;
        (double X, double Y) cam = (initial.CameraX, initial.CameraY);
        (double X, double Y) scrollFrom = cam, scrollTo = cam; int scrollStart = 0, scrollLen = 0;
        double dark = initial.Black || CutsceneStage.StartsBlack(script) ? 1 : 0;
        double darkFrom = dark, darkTo = dark; int fadeStart = 0, fadeLen = 0;
        int fadeOp = 0, fadeStep = 1;   // the last fade command and its frames per step
        TimelineItem? text = null, flash = null;
        (int, int)? window = null, textWindow = null;
        bool silent = false, textSilent = false;   // 3B: the next text prints without sound

        foreach (var it in timeline.OrderBy(i => i.Start).ThenBy(i => i.Line))
        {
            if (it.Start > frame) break;
            if (it.Line >= states.Count) continue;
            var st = states[it.Line];
            view.Line = it.Line; view.State = st;
            var op = script.Ops[it.Line];
            if (op.IsText) { text = it; textWindow = window; textSilent = silent; silent = false; continue; }
            var b = op.Bytes;
            if (b.Length == 0) continue;
            switch (b[0])
            {
                case 0x6B when b.Length > 2:
                    window = (b[1], b[2]);
                    break;
                case 0x60:
                    flash = it;
                    break;
                case 0x3B: silent = true; break;
                case 0x3A: silent = false; break;
                case >= 0x20 and <= 0x27:
                {
                    int a = CutsceneTimeline.ActorOf(b);
                    if (st.Actors.TryGetValue(a, out var placed)) { walks.Remove(a); pos[a] = (placed.X, placed.Y); }
                    if (a == 1) foreach (var f in st.Actors.Values.Where(x => x.Id is >= 2 and <= 5)) { walks.Remove(f.Id); pos[f.Id] = (f.X, f.Y); }
                    break;
                }
                case 0x1A:
                    foreach (var kv in st.Actors) if (!pos.ContainsKey(kv.Key)) pos[kv.Key] = (kv.Value.X, kv.Value.Y);
                    break;
                case 0x09:
                    camFollow = -1; camFixed = true; scrollLen = 0;
                    cam = (st.CameraX, st.CameraY);
                    break;
                case >= 0x10 and <= 0x13:
                {
                    var now = CameraAt(it.Start);
                    camFollow = -1; camFixed = true;
                    scrollFrom = now; scrollTo = (st.CameraX, st.CameraY); scrollStart = it.Start; scrollLen = Math.Max(1, it.Duration);
                    cam = scrollTo;
                    break;
                }
                case 0x18: case 0x19:
                    darkFrom = DarkAt(it.Start); darkTo = b[0] == 0x19 ? 1 : 0; fadeStart = it.Start; fadeLen = Math.Max(1, it.VisibleLength);
                    fadeOp = b[0]; fadeStep = Math.Max(1, b.Length > 1 ? (int)b[1] : 1);
                    break;
                case 0x50 when b.Length > 1 && b[1] is 0x05 or 0x06 or 0x08:
                    darkFrom = DarkAt(it.Start); darkTo = b[1] == 0x06 ? 0 : 1; fadeStart = it.Start; fadeLen = 32;
                    fadeOp = 0x50; fadeStep = 1;
                    break;
            }
            foreach (var w in st.Walks)
            {
                var walk = new Walk { Actor = w.Actor, Start = it.Start, CameraFollows = w.CameraFollows };
                foreach (var p in w.Points) walk.Pts.Add((p.X, p.Y));
                for (int k = 1; k < w.Points.Count; k++)
                    walk.FramesPerTile.Add(CutsceneStage.FramesPerTile(w.SegmentSpeeds != null && k - 1 < w.SegmentSpeeds.Count ? w.SegmentSpeeds[k - 1] : w.Speed));
                walks[w.Actor] = walk;
                if (w.CameraFollows) { camFollow = w.Actor; camFixed = true; scrollLen = 0; }
            }
        }

        // walks at this frame
        var slow = CutsceneTimeline.SlowSpans(script, timeline);
        var stalls = CutsceneTimeline.GameStalls(script, timeline);
        foreach (var w in walks.Values) pos[w.Actor] = WalkAt(w, CutsceneTimeline.WalkProgress(w.Start, frame - w.Start, slow));
        foreach (var kv in pos) view.Positions[kv.Key] = kv.Value;

        // camera
        if (camFollow >= 0 && pos.TryGetValue(camFollow, out var fp)) cam = fp;
        else if (scrollLen > 0) cam = CameraAt(frame);
        else if (!camFixed && pos.TryGetValue(1, out var lead)) cam = lead;
        view.CamX = cam.X; view.CamY = cam.Y;

        // text on screen
        if (text != null && frame < text.Start + Math.Max(1, text.VisibleLength))
        {
            view.Text = script.Ops[text.Line].Text;
            view.TypedText = CutsceneText.Typed(view.Text, CutsceneTimeline.WalkProgress(text.Start, frame - text.Start, stalls), textSilent);
            view.Speaker = states[text.Line].Speaker;
            view.TextWindow = textWindow;
        }
        if (flash != null)
        {
            int k = (int)CutsceneTimeline.WalkProgress(flash.Start, frame - flash.Start, stalls);
            if (k < CutsceneText.FlashCurve.Length) view.Flash = CutsceneText.FlashCurve[k];
        }
        view.Darkness = DarkAt(frame);
        view.FadeLevel = FadeLevelAt(frame);
        if (flash != null)
        {
            int k = (int)CutsceneTimeline.WalkProgress(flash.Start, frame - flash.Start, stalls);
            if (k < CutsceneText.FlashAdd.Length) view.FlashAdd = CutsceneText.FlashAdd[k];
        }
        view.FieldFrames = frame <= 0 ? 0 : CutsceneTimeline.WalkProgress(0, frame - 1, slow);
        return view;

        // palette fade: a fade in (18 s) waits 3 frames, then raises the level by one every s frames up to 32
        // (measured on the intro); fades out are taken as the same steps downwards
        int FadeLevelAt(int f)
        {
            if (fadeOp == 0) return dark >= 1 ? 0 : 32;
            int t = f - fadeStart;
            return fadeOp switch
            {
                0x18 => Math.Clamp((t - 3) / fadeStep, 0, 32),
                0x19 => 32 - Math.Clamp(t / fadeStep, 0, 32),
                _ => (int)Math.Round(32 * (1 - DarkAt(f))),
            };
        }

        (double, double) CameraAt(int f)
        {
            if (scrollLen <= 0) return cam;
            double t = Math.Clamp((f - scrollStart) / (double)scrollLen, 0, 1);
            return (scrollFrom.X + (scrollTo.X - scrollFrom.X) * t, scrollFrom.Y + (scrollTo.Y - scrollFrom.Y) * t);
        }

        double DarkAt(int f)
        {
            if (fadeLen <= 0) return darkTo;
            double t = Math.Clamp((f - fadeStart) / (double)fadeLen, 0, 1);
            return darkFrom + (darkTo - darkFrom) * t;
        }
    }

    private static (double X, double Y) WalkAt(Walk w, double elapsed)
    {
        if (w.Pts.Count == 0) return (0, 0);
        double left = Math.Max(0, elapsed);
        for (int k = 1; k < w.Pts.Count; k++)
        {
            var (ax, ay) = w.Pts[k - 1];
            var (bx, by) = w.Pts[k];
            double tiles = Math.Abs(bx - ax) + Math.Abs(by - ay);
            double frames = tiles * w.FramesPerTile[k - 1];
            if (left < frames)
            {
                double f = frames == 0 ? 1 : left / frames;
                return (ax + (bx - ax) * f, ay + (by - ay) * f);
            }
            left -= frames;
        }
        return w.Pts[^1];
    }

    /// <summary>Frames until every walk and timed line has finished (the length of the preview).</summary>
    public static int Length(IReadOnlyList<StageState> states, IReadOnlyList<TimelineItem> timeline)
    {
        int end = timeline.Count == 0 ? 0 : timeline.Max(i => i.Start + Math.Max(1, i.VisibleLength));
        return end;
    }
}
