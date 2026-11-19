namespace LufiaForge.Core.Maps;

// what's on screen at one frame of a cutscene
public sealed class FrameView
{
    public int Frame { get; init; }
    // the line the cutscene is at (the last one that has started)
    public int Line { get; set; }
    // tile the camera is centred on (fractional while it moves)
    public double CamX { get; set; }
    public double CamY { get; set; }
    // actor positions in tiles (fractional while walking)
    public Dictionary<int, (double X, double Y)> Positions { get; } = new();
    // the whole text of the box on screen, and the part typed so far
    public string? Text { get; set; }
    public string? TypedText { get; set; }
    public int Speaker { get; set; } = -1;
    // where 6B put the text window, or null for the usual dialogue box
    public (int Column, int Row)? TextWindow { get; set; }
    // white of a lightning flash, 0..1
    public double Flash { get; set; }
    // palette fade level: 0 = black, 32 = full colour (the console multiplies each colour by level/32)
    public int FadeLevel { get; set; } = 32;
    // white a lightning flash adds to each colour component (0-31)
    public int FlashAdd { get; set; }
    // frames the field (walks, the intro's clouds) has run up to the previous frame
    public double FieldFrames { get; set; }
    // 0 = full brightness, 1 = black
    public double Darkness { get; set; }
    // stage state of Line (visibility, facing, sprites)
    public StageState State { get; set; } = null!;
}

// frame-accurate replay of a cutscene. from the simulated states and the timeline it works out where everyone and the
// camera are at any frame. playback, the timeline playhead and the comparison with the real game all use it
public static class CutsceneFrame
{
    private sealed class Walk
    {
        public int Actor; public int Start; public bool CameraFollows;
        public List<(double X, double Y)> Points = new(); public List<int> FramesPerTile = new();
    }

    public static FrameView At(StageState initial, IReadOnlyList<StageState> states, IReadOnlyList<TimelineItem> timeline, EventScript script, int frame)
    {
        var view = new FrameView { Frame = frame, State = initial };
        var positions = initial.Actors.ToDictionary(kv => kv.Key, kv => ((double)kv.Value.X, (double)kv.Value.Y));
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

        foreach (var item in timeline.OrderBy(i => i.Start).ThenBy(i => i.Line))
        {
            if (item.Start > frame) break;
            if (item.Line >= states.Count) continue;
            var state = states[item.Line];
            view.Line = item.Line; view.State = state;
            var op = script.Ops[item.Line];
            if (op.IsText) { text = item; textWindow = window; textSilent = silent; silent = false; continue; }
            var bytes = op.Bytes;
            if (bytes.Length == 0) continue;
            switch (bytes[0])
            {
                case 0x6B when bytes.Length > 2:
                    window = (bytes[1], bytes[2]);
                    break;
                case 0x60:
                    flash = item;
                    break;
                case 0x3B: silent = true; break;
                case 0x3A: silent = false; break;
                case >= 0x20 and <= 0x27:
                {
                    int actor = CutsceneTimeline.ActorOf(bytes);
                    if (state.Actors.TryGetValue(actor, out var placed)) { walks.Remove(actor); positions[actor] = (placed.X, placed.Y); }
                    if (actor == 1) foreach (var follower in state.Actors.Values.Where(x => x.Id is >= 2 and <= 5)) { walks.Remove(follower.Id); positions[follower.Id] = (follower.X, follower.Y); }
                    break;
                }
                case 0x1A:
                    foreach (var kv in state.Actors) if (!positions.ContainsKey(kv.Key)) positions[kv.Key] = (kv.Value.X, kv.Value.Y);
                    break;
                case 0x09:
                    camFollow = -1; camFixed = true; scrollLen = 0;
                    cam = (state.CameraX, state.CameraY);
                    break;
                case >= 0x10 and <= 0x13:
                {
                    var now = CameraAt(item.Start);
                    camFollow = -1; camFixed = true;
                    scrollFrom = now; scrollTo = (state.CameraX, state.CameraY); scrollStart = item.Start; scrollLen = Math.Max(1, item.Duration);
                    cam = scrollTo;
                    break;
                }
                case 0x18: case 0x19:
                    darkFrom = DarkAt(item.Start); darkTo = bytes[0] == 0x19 ? 1 : 0; fadeStart = item.Start; fadeLen = Math.Max(1, item.VisibleLength);
                    fadeOp = bytes[0]; fadeStep = Math.Max(1, bytes.Length > 1 ? (int)bytes[1] : 1);
                    break;
                case 0x50 when bytes.Length > 1 && bytes[1] is 0x05 or 0x06 or 0x08:
                    darkFrom = DarkAt(item.Start); darkTo = bytes[1] == 0x06 ? 0 : 1; fadeStart = item.Start; fadeLen = 32;
                    fadeOp = 0x50; fadeStep = 1;
                    break;
            }
            foreach (var stageWalk in state.Walks)
            {
                var walk = new Walk { Actor = stageWalk.Actor, Start = item.Start, CameraFollows = stageWalk.CameraFollows };
                foreach (var point in stageWalk.Points) walk.Points.Add((point.X, point.Y));
                for (int k = 1; k < stageWalk.Points.Count; k++)
                    walk.FramesPerTile.Add(CutsceneStage.FramesPerTile(stageWalk.SegmentSpeeds != null && k - 1 < stageWalk.SegmentSpeeds.Count ? stageWalk.SegmentSpeeds[k - 1] : stageWalk.Speed));
                walks[stageWalk.Actor] = walk;
                if (stageWalk.CameraFollows) { camFollow = stageWalk.Actor; camFixed = true; scrollLen = 0; }
            }
        }

        // walks at this frame
        var slow = CutsceneTimeline.SlowSpans(script, timeline);
        var stalls = CutsceneTimeline.GameStalls(script, timeline);
        foreach (var walk in walks.Values) positions[walk.Actor] = WalkAt(walk, CutsceneTimeline.WalkProgress(walk.Start, frame - walk.Start, slow));
        foreach (var kv in positions) view.Positions[kv.Key] = kv.Value;

        // camera
        if (camFollow >= 0 && positions.TryGetValue(camFollow, out var followed)) cam = followed;
        else if (scrollLen > 0) cam = CameraAt(frame);
        else if (!camFixed && positions.TryGetValue(1, out var lead)) cam = lead;
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
        // (measured on the intro). fades out we just take as the same steps going down
        int FadeLevelAt(int atFrame)
        {
            if (fadeOp == 0) return dark >= 1 ? 0 : 32;
            int sinceFade = atFrame - fadeStart;
            return fadeOp switch
            {
                0x18 => Math.Clamp((sinceFade - 3) / fadeStep, 0, 32),
                0x19 => 32 - Math.Clamp(sinceFade / fadeStep, 0, 32),
                _ => (int)Math.Round(32 * (1 - DarkAt(atFrame))),
            };
        }

        (double, double) CameraAt(int atFrame)
        {
            if (scrollLen <= 0) return cam;
            double progress = Math.Clamp((atFrame - scrollStart) / (double)scrollLen, 0, 1);
            return (scrollFrom.X + (scrollTo.X - scrollFrom.X) * progress, scrollFrom.Y + (scrollTo.Y - scrollFrom.Y) * progress);
        }

        double DarkAt(int atFrame)
        {
            if (fadeLen <= 0) return darkTo;
            double progress = Math.Clamp((atFrame - fadeStart) / (double)fadeLen, 0, 1);
            return darkFrom + (darkTo - darkFrom) * progress;
        }
    }

    private static (double X, double Y) WalkAt(Walk walk, double elapsed)
    {
        if (walk.Points.Count == 0) return (0, 0);
        double left = Math.Max(0, elapsed);
        for (int k = 1; k < walk.Points.Count; k++)
        {
            var (ax, ay) = walk.Points[k - 1];
            var (bx, by) = walk.Points[k];
            double tiles = Math.Abs(bx - ax) + Math.Abs(by - ay);
            double frames = tiles * walk.FramesPerTile[k - 1];
            if (left < frames)
            {
                double fraction = frames == 0 ? 1 : left / frames;
                return (ax + (bx - ax) * fraction, ay + (by - ay) * fraction);
            }
            left -= frames;
        }
        return walk.Points[^1];
    }

    // frames until every walk and timed line has finished (the length of the preview)
    public static int Length(IReadOnlyList<StageState> states, IReadOnlyList<TimelineItem> timeline)
    {
        int end = timeline.Count == 0 ? 0 : timeline.Max(i => i.Start + Math.Max(1, i.VisibleLength));
        return end;
    }
}
