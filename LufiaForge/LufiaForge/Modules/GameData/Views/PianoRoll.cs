using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Size = System.Windows.Size;

namespace LufiaForge.Modules.GameData.Views;

/// <summary>
/// The note editor's piano roll: a keyboard on the left, a ruler on top (bars, the loop, the cursor), the channel's
/// events (instrument, volume...) in a lane under it and the notes in a grid. Other channels show faintly behind.
/// Mouse: click empty space = new note (drag to set its length), drag a note = move, drag its right edge = length,
/// right-click = delete, Shift+drag = select a box, Ctrl+click = add to the selection, click the ruler = cursor,
/// drag the green/red ruler marks = loop start/end. Wheel = up/down, Shift+wheel = sideways, Ctrl+wheel = zoom.
/// </summary>
public sealed class PianoRoll : FrameworkElement
{
    private const double KeyW = 54, RulerH = 22, LaneH = 18, Bar = 14;
    private double Top => RulerH + LaneH;

    private readonly VisualCollection _children;
    private readonly ScrollBar _hBar = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollBar _vBar = new() { Orientation = Orientation.Vertical };

    public PianoRoll()
    {
        _children = new VisualCollection(this) { _hBar, _vBar };
        Focusable = true;
        ClipToBounds = true;
        _hBar.Scroll += (_, _) => { ScrollX = _hBar.Value; InvalidateVisual(); };
        _vBar.Scroll += (_, _) => { ScrollY = _vBar.Value; InvalidateVisual(); };
        _hBar.ValueChanged += (_, _) => { ScrollX = _hBar.Value; InvalidateVisual(); };
        _vBar.ValueChanged += (_, _) => { ScrollY = _vBar.Value; InvalidateVisual(); };
        ScrollY = 127 - 84;   // C6 at the top
    }

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(NoteEditorViewModel), typeof(PianoRoll), new PropertyMetadata(null, (d, e) => ((PianoRoll)d).OnModel(e)));
    public NoteEditorViewModel? Model { get => (NoteEditorViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    private void OnModel(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is NoteEditorViewModel o) { o.Changed -= Redraw; o.PropertyChanged -= ModelProp; }
        if (e.NewValue is NoteEditorViewModel n) { n.Changed += Redraw; n.PropertyChanged += ModelProp; }
        Redraw();
    }

    private bool _scrolledToNotes;
    private void Redraw()
    {
        UpdateBars();
        InvalidateVisual();
    }

    /// <summary>A song was just opened: centre its notes (once there's a size to centre in).</summary>
    private void CentreOnNotes()
    {
        if (_scrolledToNotes || Model is not { Loaded: true } m || ActualHeight <= 0) return;
        var notes = m.Channels.SelectMany(c => c.Notes).ToList();
        if (notes.Count == 0) return;
        double mid = notes.Average(n => n.Pitch);
        ScrollY = Math.Clamp(127 - mid - Rows / 2, 0, Math.Max(0, 128 - Rows));
        _scrolledToNotes = true;
        UpdateBars();
    }

    private void ModelProp(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteEditorViewModel.PlayheadTick) && Model != null)
        {
            double t = Model.PlayheadTick;
            if (t >= 0 && Model.FollowPlayhead && (t < ScrollX || t > ScrollX + ViewTicks * 0.9)) { ScrollX = Math.Max(0, t - ViewTicks * 0.1); UpdateBars(); }
            InvalidateVisual();
        }
        else if (e.PropertyName is nameof(NoteEditorViewModel.CursorTick) or nameof(NoteEditorViewModel.SnapIndex)) InvalidateVisual();
        else if (e.PropertyName == nameof(NoteEditorViewModel.Loaded)) { _scrolledToNotes = false; ScrollX = 0; CentreOnNotes(); Redraw(); }
    }

    // ── geometry ────────────────────────────────────────────────────────────

    /// <summary>Pixels per tick.</summary>
    public double Zoom { get; set; } = 0.75;
    public double RowH { get; set; } = 11;
    /// <summary>First tick shown.</summary>
    public double ScrollX { get; set; }
    /// <summary>First row shown (row 0 = pitch 127).</summary>
    public double ScrollY { get; set; }

    private double GridW => Math.Max(10, ActualWidth - KeyW - Bar);
    private double GridH => Math.Max(10, ActualHeight - Top - Bar);
    private double ViewTicks => GridW / Zoom;
    private double Rows => GridH / RowH;
    private double X(double tick) => KeyW + (tick - ScrollX) * Zoom;
    private double Y(int pitch) => Top + (127 - pitch - ScrollY) * RowH;
    private double TickAt(double x) => ScrollX + (x - KeyW) / Zoom;
    private int PitchAt(double y) => 127 - (int)Math.Floor((y - Top) / RowH + ScrollY);

    private void UpdateBars()
    {
        int len = (Model?.LengthTicks ?? 768) + NoteEditorViewModel.TicksPerBar * 4;
        _hBar.Minimum = 0; _hBar.Maximum = Math.Max(0, len - ViewTicks); _hBar.ViewportSize = ViewTicks; _hBar.LargeChange = ViewTicks * 0.8; _hBar.SmallChange = 48;
        _vBar.Minimum = 0; _vBar.Maximum = Math.Max(0, 128 - Rows); _vBar.ViewportSize = Rows; _vBar.LargeChange = Rows * 0.8; _vBar.SmallChange = 1;
        ScrollX = Math.Clamp(ScrollX, 0, _hBar.Maximum); ScrollY = Math.Clamp(ScrollY, 0, _vBar.Maximum);
        _hBar.Value = ScrollX; _vBar.Value = ScrollY;
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];
    protected override Size MeasureOverride(Size available)
    {
        _hBar.Measure(available); _vBar.Measure(available);
        return new Size(double.IsInfinity(available.Width) ? 800 : available.Width, double.IsInfinity(available.Height) ? 500 : available.Height);
    }
    protected override Size ArrangeOverride(Size size)
    {
        _hBar.Arrange(new Rect(KeyW, size.Height - Bar, Math.Max(0, size.Width - KeyW - Bar), Bar));
        _vBar.Arrange(new Rect(size.Width - Bar, Top, Bar, Math.Max(0, size.Height - Top - Bar)));
        Dispatcher.BeginInvoke(() => { CentreOnNotes(); UpdateBars(); });
        return size;
    }

    // ── drawing ─────────────────────────────────────────────────────────────

    private static readonly Color[] ChannelColours =
    {
        Color.FromRgb(0xF2, 0xC1, 0x4E), Color.FromRgb(0x6F, 0xC3, 0xF7), Color.FromRgb(0x8E, 0xE0, 0x7A), Color.FromRgb(0xF7, 0x8C, 0x6B),
        Color.FromRgb(0xC7, 0x9B, 0xF2), Color.FromRgb(0x5F, 0xE0, 0xC8), Color.FromRgb(0xF2, 0x7F, 0xB8), Color.FromRgb(0xD0, 0xD0, 0xD0),
    };
    public static Color ChannelColour(int c) => ChannelColours[c & 7];

    private static SolidColorBrush B(byte r, byte g, byte b, byte a = 255) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
    private static Pen P(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }
    private static readonly Brush BgWhite = B(0x2A, 0x20, 0x45), BgBlack = B(0x22, 0x1A, 0x38), BgPast = B(0x00, 0x00, 0x00, 0x60),
        KeyWhite = B(0xE8, 0xE4, 0xF0), KeyBlack = B(0x30, 0x28, 0x40), RulerBg = B(0x1A, 0x14, 0x2C), LaneBg = B(0x20, 0x18, 0x34),
        TextB = B(0xC8, 0xBE, 0xE0), Gold = B(0xF2, 0xC1, 0x4E), LoopIn = B(0x6E, 0xE0, 0x7A), LoopOut = B(0xF0, 0x60, 0x60), CursorB = B(0x6F, 0xC3, 0xF7),
        Playhead = B(0xFF, 0xFF, 0xFF), SelBox = B(0x6F, 0xC3, 0xF7, 0x30);
    private static readonly Pen RowLine = P(B(0x3A, 0x30, 0x58), 0.5), BeatLine = P(B(0x48, 0x3C, 0x6C), 1), BarLine = P(B(0x80, 0x70, 0xB0), 1),
        SubLine = P(B(0x34, 0x2A, 0x50), 0.5), SelPen = P(B(0xFF, 0xFF, 0xFF), 1.5), NotePen = P(B(0, 0, 0, 0x90), 1), CPen = P(B(0x50, 0x44, 0x78), 1),
        SelBoxPen = P(B(0x6F, 0xC3, 0xF7), 1);
    private static readonly Typeface Face = new("Segoe UI");

    private FormattedText Text(string s, double size, Brush b) =>
        new(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgWhite, null, new Rect(0, 0, w, h));
        var m = Model;
        if (m == null || !m.Loaded) { dc.DrawText(Text("Pick a song.", 13, TextB), new Point(KeyW + 10, Top + 10)); return; }
        var ch = m.Current;
        double t0 = ScrollX, t1 = ScrollX + ViewTicks;
        int pTop = Math.Min(127, PitchAt(Top) + 1), pBottom = Math.Max(0, PitchAt(Top + GridH) - 1);
        var grid = new Rect(KeyW, Top, GridW, GridH);

        // rows
        dc.PushClip(new RectangleGeometry(grid));
        for (int p = pBottom; p <= pTop; p++)
        {
            bool black = (p % 12) is 1 or 3 or 6 or 8 or 10;
            dc.DrawRectangle(black ? BgBlack : BgWhite, null, new Rect(KeyW, Y(p), GridW, RowH));
            dc.DrawLine(p % 12 == 0 ? CPen : RowLine, new Point(KeyW, Y(p) + RowH), new Point(KeyW + GridW, Y(p) + RowH));
        }
        // past the loop end / song end: shaded
        int? stop = ch.LoopEnd ?? ch.EndTick;
        if (stop is int st && st < t1) dc.DrawRectangle(BgPast, null, new Rect(X(Math.Max(st, t0)), Top, X(t1) - X(Math.Max(st, t0)), GridH));
        // vertical lines
        int snap = m.Snap >= 4 && m.Snap * Zoom >= 5 ? m.Snap : 48;
        for (int t = (int)(t0 / snap) * snap; t <= t1; t += snap)
        {
            var pen = t % NoteEditorViewModel.TicksPerBar == 0 ? BarLine : t % NoteEditorViewModel.TicksPerBeat == 0 ? BeatLine : SubLine;
            dc.DrawLine(pen, new Point(X(t), Top), new Point(X(t), Top + GridH));
        }
        // other channels, faint
        for (int c = 0; c < 8; c++)
        {
            if (c == m.CurrentChannel) continue;
            var col = ChannelColour(c); var fill = new SolidColorBrush(Color.FromArgb(0x38, col.R, col.G, col.B));
            foreach (var n in m.Channels[c].Notes)
            {
                if (n.End < t0 || n.Tick > t1 || n.Pitch < pBottom || n.Pitch > pTop) continue;
                dc.DrawRectangle(fill, null, new Rect(X(n.Tick), Y(n.Pitch) + 1, Math.Max(2, n.Length * Zoom), RowH - 2));
            }
        }
        // this channel's notes
        var c0 = ChannelColour(m.CurrentChannel);
        foreach (var n in ch.Notes)
        {
            if (n.End < t0 || n.Tick > t1 || n.Pitch < pBottom || n.Pitch > pTop) continue;
            byte a = (byte)(110 + n.Velocity);
            var fill = new SolidColorBrush(Color.FromArgb(a, c0.R, c0.G, c0.B));
            var r = new Rect(X(n.Tick), Y(n.Pitch) + 0.5, Math.Max(3, n.Length * Zoom), RowH - 1);
            dc.DrawRoundedRectangle(fill, m.Selected.Contains(n) ? SelPen : NotePen, r, 2, 2);
            if (r.Width > 26 && RowH >= 10) dc.DrawText(Text(NoteEditorViewModel.NoteName(n.Pitch), 8.5, Brushes.Black), new Point(r.X + 2, r.Y));
        }
        if (_mode == Mode.Box) dc.DrawRectangle(SelBox, SelBoxPen, _box);
        dc.Pop();

        // ruler and event lane
        dc.DrawRectangle(RulerBg, null, new Rect(KeyW, 0, w - KeyW, RulerH));
        dc.DrawRectangle(LaneBg, null, new Rect(KeyW, RulerH, w - KeyW, LaneH));
        dc.PushClip(new RectangleGeometry(new Rect(KeyW, 0, GridW, Top + GridH)));
        int barT = NoteEditorViewModel.TicksPerBar;
        int every = Zoom * barT < 40 ? 4 : 1;
        for (int t = (int)(t0 / barT) * barT; t <= t1; t += barT)
        {
            dc.DrawLine(BarLine, new Point(X(t), RulerH - 8), new Point(X(t), RulerH));
            if (t / barT % every == 0) dc.DrawText(Text((t / barT + 1).ToString(CultureInfo.InvariantCulture), 10, TextB), new Point(X(t) + 3, 2));
        }
        // the channel's events, one label per tick ("I0A V96 P48")
        foreach (var g in ch.Others.Where(o => o.Tick >= t0 - 400 && o.Tick <= t1).GroupBy(o => o.Tick))
        {
            string label = string.Join(" ", g.Select(o => o.Kind switch
            {
                Core.Audio.SongCodec.Kind.Program => $"I{o.A:X2}", Core.Audio.SongCodec.Kind.Volume => $"V{o.A}",
                Core.Audio.SongCodec.Kind.Pan => $"P{o.A}", _ => "fx",
            }));
            dc.DrawRectangle(Gold, null, new Rect(X(g.Key), RulerH + 2, 2, LaneH - 4));
            dc.DrawText(Text(label, 9, Gold), new Point(X(g.Key) + 4, RulerH + 3));
        }
        if (ch.LoopEnd is int le)
        {
            Flag(dc, ch.LoopStart ?? 0, LoopIn, "loop");
            Flag(dc, le, LoopOut, "back");
        }
        else if (ch.EndTick is int et) Flag(dc, et, LoopOut, "end");
        // cursor and playhead
        dc.DrawLine(P(CursorB, 1.5), new Point(X(m.CursorTick), 0), new Point(X(m.CursorTick), Top + GridH));
        if (m.PlayheadTick >= 0) dc.DrawLine(P(Playhead, 1.5), new Point(X(m.PlayheadTick), 0), new Point(X(m.PlayheadTick), Top + GridH));
        dc.Pop();
        if (stop is int s2 && s2 >= t0 && s2 <= t1) dc.DrawLine(P(LoopOut, 1), new Point(X(s2), Top), new Point(X(s2), Top + GridH));

        // keyboard
        dc.DrawRectangle(RulerBg, null, new Rect(0, 0, KeyW, Top));
        dc.DrawText(Text($"Ch {m.CurrentChannel + 1}", 11, new SolidColorBrush(c0)), new Point(6, 4));
        dc.PushClip(new RectangleGeometry(new Rect(0, Top, KeyW, GridH)));
        for (int p = pBottom; p <= pTop; p++)
        {
            bool black = (p % 12) is 1 or 3 or 6 or 8 or 10;
            var r = new Rect(0, Y(p), black ? KeyW * 0.62 : KeyW, RowH);
            if (black) dc.DrawRectangle(KeyWhite, null, new Rect(0, Y(p), KeyW, RowH));
            dc.DrawRectangle(black ? KeyBlack : KeyWhite, RowLine, r);
            if (p % 12 == 0 || RowH >= 13 && !black)
                dc.DrawText(Text(NoteEditorViewModel.NoteName(p), 8.5, Brushes.Black), new Point(KeyW - 26, Y(p) + (RowH - 11) / 2));
        }
        dc.Pop();
        dc.DrawRectangle(RulerBg, null, new Rect(w - Bar, 0, Bar, Top));
        dc.DrawRectangle(RulerBg, null, new Rect(0, h - Bar, KeyW, Bar));
    }

    private void Flag(DrawingContext dc, int tick, Brush b, string label)
    {
        double x = X(tick);
        dc.DrawGeometry(b, null, new PathGeometry(new[] { new PathFigure(new Point(x, 0), new PathSegment[]
            { new LineSegment(new Point(x + 9, 0), true), new LineSegment(new Point(x, 10), true) }, true) }));
        dc.DrawLine(P(b, 2), new Point(x, 0), new Point(x, Top));
        dc.DrawText(Text(label, 9, b), new Point(x + 10, 9));
    }

    // ── mouse ───────────────────────────────────────────────────────────────

    private enum Mode { None, Move, Resize, Box, Cursor, LoopStart, LoopEnd, EndMark }
    private Mode _mode;
    private Point _down;
    private int _downTick, _downPitch;
    private List<(Modules.GameData.NoteItem N, int Tick, int Pitch, int Length)> _orig = new();
    private bool _changed;
    private Rect _box;

    private int SnapDown(double t) { int s = Model?.Snap ?? 1; return Math.Max(0, (int)Math.Floor(t / s) * s); }
    private int SnapRound(double t) { int s = Model?.Snap ?? 1; return Math.Max(0, (int)Math.Round(t / s) * s); }

    private NoteItem? HitNote(Point p)
    {
        if (Model == null) return null;
        double t = TickAt(p.X); int pitch = PitchAt(p.Y);
        return Model.Current.Notes.LastOrDefault(n => n.Pitch == pitch && t >= n.Tick && t < n.Tick + Math.Max(n.Length, 3 / Zoom));
    }

    private bool NearEnd(NoteItem n, Point p) => Math.Abs(p.X - X(n.End)) <= 5 && n.Length * Zoom > 8;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (Model == null || !Model.Loaded) return;
        Focus();
        if (Down(e.GetPosition(this), e.ChangedButton, Keyboard.Modifiers)) { CaptureMouse(); e.Handled = true; }
    }

    /// <summary>A mouse button pressed at <paramref name="p"/>; true when a drag starts (or the click was used).</summary>
    public bool Down(Point p, MouseButton button, ModifierKeys mods)
    {
        var m = Model;
        if (m == null || !m.Loaded) return false;
        if (p.X >= ActualWidth - Bar || p.Y >= ActualHeight - Bar) return false;
        _down = p; _changed = false; _orig.Clear();
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);

        if (p.Y < Top && p.X > KeyW)
        {
            // ruler: loop marks or the cursor
            var ch = m.Current;
            if (ch.LoopEnd is int le && Math.Abs(p.X - X(le)) <= 6) _mode = Mode.LoopEnd;
            else if (ch.LoopEnd != null && Math.Abs(p.X - X(ch.LoopStart ?? 0)) <= 6) _mode = Mode.LoopStart;
            else if (ch.EndTick is int et && Math.Abs(p.X - X(et)) <= 6) _mode = Mode.EndMark;
            else { _mode = Mode.Cursor; m.CursorTick = SnapRound(TickAt(p.X)); }
            if (_mode is Mode.LoopStart or Mode.LoopEnd or Mode.EndMark) m.Checkpoint();
            return true;
        }
        if (p.X < KeyW)
        {
            if (p.Y > Top) m.PreviewNote(PitchAt(p.Y), m.CursorTick);
            return false;
        }

        var hit = HitNote(p);
        if (button == MouseButton.Right)
        {
            if (hit == null) return false;
            m.Checkpoint();
            if (m.Selected.Contains(hit)) { m.Current.Notes.RemoveAll(m.Selected.Contains); m.Selected.Clear(); }
            else m.Current.Notes.Remove(hit);
            m.Touch();
            return false;
        }
        if (button != MouseButton.Left) return false;

        if (hit != null)
        {
            if (ctrl) { if (!m.Selected.Remove(hit)) m.Selected.Add(hit); m.Refresh(); return false; }
            if (!m.Selected.Contains(hit)) { m.Selected.Clear(); m.Selected.Add(hit); }
            _mode = NearEnd(hit, p) ? Mode.Resize : Mode.Move;
            _downTick = (int)TickAt(p.X); _downPitch = hit.Pitch;
            foreach (var n in m.Selected) _orig.Add((n, n.Tick, n.Pitch, n.Length));
            if (_mode == Mode.Move) m.PreviewNote(hit.Pitch, hit.Tick);
            m.Refresh();
        }
        else if (shift)
        {
            _mode = Mode.Box; _box = new Rect(p, p);
            if (!ctrl) m.Selected.Clear();
        }
        else
        {
            // a new note; dragging sets its length
            m.Selected.Clear();
            m.Checkpoint(); _changed = true;
            int len = m.LengthIndex >= 0 ? m.NewLength : m.LastLength;
            var n = m.AddNote(SnapDown(TickAt(p.X)), PitchAt(p.Y), len);
            m.Selected.Add(n);
            _orig.Add((n, n.Tick, n.Pitch, n.Length));
            _downTick = n.Tick + n.Length; _downPitch = n.Pitch;
            _mode = Mode.Resize;
            m.CursorTick = n.Tick;
            m.PreviewNote(n.Pitch, n.Tick);
            m.Touch();
        }
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Move(e.GetPosition(this), Keyboard.Modifiers);
    }

    /// <summary>The mouse moved to <paramref name="p"/> (dragging when a button went down on the roll).</summary>
    public void Move(Point p, ModifierKeys mods)
    {
        var m = Model;
        if (m == null) return;
        if (_mode == Mode.None)
        {
            var hit = p.X > KeyW && p.Y > Top ? HitNote(p) : null;
            bool onMark = p.Y < Top && m.Current is var ch &&
                (ch.LoopEnd is int le && Math.Abs(p.X - X(le)) <= 6 || ch.LoopEnd != null && Math.Abs(p.X - X(ch.LoopStart ?? 0)) <= 6 || ch.EndTick is int et && Math.Abs(p.X - X(et)) <= 6);
            Cursor = hit != null && NearEnd(hit, p) || onMark ? Cursors.SizeWE : hit != null ? Cursors.SizeAll : p.X > KeyW && p.Y > Top ? Cursors.Pen : Cursors.Arrow;
            return;
        }
        double t = TickAt(p.X);
        switch (_mode)
        {
            case Mode.Move:
            {
                int dt = SnapRound(Math.Abs(t - _downTick)) * Math.Sign(t - _downTick), dp = PitchAt(p.Y) - _downPitch;
                int least = _orig.Min(o => o.Tick);
                if (least + dt < 0) dt = -least;
                if (dt == 0 && dp == 0 && !_changed) return;
                if (!_changed) { m.Checkpoint(); _changed = true; }
                bool pitchMoved = false;
                foreach (var o in _orig)
                {
                    int np = Math.Clamp(o.Pitch + dp, 0, 127);
                    if (o.N.Pitch != np) pitchMoved = true;
                    o.N.Tick = o.Tick + dt; o.N.Pitch = np;
                }
                if (pitchMoved && _orig.Count == 1) m.PreviewNote(_orig[0].N.Pitch, _orig[0].N.Tick);
                m.Touch();
                break;
            }
            case Mode.Resize:
            {
                int s = Math.Max(1, m.Snap);
                int dt = (int)Math.Round((t - _downTick) / s) * s;
                if (dt == 0 && !_changed) return;
                if (!_changed) { m.Checkpoint(); _changed = true; }
                foreach (var o in _orig) o.N.Length = Math.Clamp(o.Length + dt, Math.Min(s, 255), 255);
                m.LastLength = _orig[0].N.Length;
                m.Touch();
                break;
            }
            case Mode.Box:
            {
                _box = new Rect(_down, p);
                double a = TickAt(_box.Left), b = TickAt(_box.Right);
                int hi = PitchAt(_box.Top), lo = PitchAt(_box.Bottom);
                if (!mods.HasFlag(ModifierKeys.Control)) m.Selected.Clear();
                foreach (var n in m.Current.Notes) if (n.Pitch >= lo && n.Pitch <= hi && n.End > a && n.Tick < b) m.Selected.Add(n);
                m.Refresh();
                InvalidateVisual();
                break;
            }
            case Mode.Cursor: m.CursorTick = SnapRound(t); break;
            case Mode.LoopStart: m.SetLoopStart(SnapRound(t)); m.Touch(); break;
            case Mode.LoopEnd: m.SetLoopEnd(Math.Max(1, SnapRound(t))); m.Touch(); break;
            case Mode.EndMark:
                foreach (var c in m.LoopAllChannels ? m.Channels.Where(c => c.EndTick != null) : new[] { m.Current }) c.EndTick = Math.Max(1, SnapRound(t));
                m.Touch(); break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_mode == Mode.None) return;
        Up();
        ReleaseMouseCapture();
    }

    /// <summary>The button came up: the drag ends.</summary>
    public void Up()
    {
        _mode = Mode.None;
        Model?.Refresh();
        InvalidateVisual();
    }

    /// <summary>Screen position of a tick and pitch (the middle of that grid cell), for tests.</summary>
    public Point PointAt(double tick, int pitch) => new(X(tick), Y(pitch) + RowH / 2);
    /// <summary>Shows this tick and pitch range.</summary>
    public void ScrollTo(double tick, int topPitch) { ScrollX = tick; ScrollY = 127 - topPitch; UpdateBars(); InvalidateVisual(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double at = TickAt(p.X);
            Zoom = Math.Clamp(Zoom * (e.Delta > 0 ? 1.25 : 0.8), 0.05, 8);
            ScrollX = Math.Max(0, at - (p.X - KeyW) / Zoom);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ScrollX -= e.Delta / 120.0 * 96 / Math.Max(0.25, Zoom);
        else ScrollY -= e.Delta / 120.0 * 3;
        UpdateBars();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var m = Model;
        if (m == null) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Delete or Key.Back: m.DeleteSelectedCommand.Execute(null); break;
            case Key.Z when ctrl: m.UndoCommand.Execute(null); break;
            case Key.Y when ctrl: m.RedoCommand.Execute(null); break;
            case Key.C when ctrl: m.CopyCommand.Execute(null); break;
            case Key.X when ctrl: m.CutCommand.Execute(null); break;
            case Key.V when ctrl: m.PasteCommand.Execute(null); break;
            case Key.A when ctrl: m.SelectAllCommand.Execute(null); break;
            case Key.Up: m.Transpose(shift ? 12 : 1); break;
            case Key.Down: m.Transpose(shift ? -12 : -1); break;
            case Key.Left: m.Shift(-Math.Max(1, m.Snap)); break;
            case Key.Right: m.Shift(Math.Max(1, m.Snap)); break;
            case Key.Space:
                if (m.PlayheadTick >= 0) m.StopPlayingCommand.Execute(null); else m.PlayFromCursorCommand.Execute(null);
                break;
            case Key.Escape: m.Selected.Clear(); m.Refresh(); break;
            default: return;
        }
        e.Handled = true;
    }
}
