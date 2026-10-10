using System.IO;
using System.Text;

namespace LufiaForge.Core.Audio;

/// <summary>
/// Lufia's song format, read from the sound driver (song loaded at $2800 in sound RAM; driver at $0400):
/// <code>
/// header  "SFC", tempo (timer 0 = tempo x 125 µs per tick, $1789), name to 0x10, channel table 0x10-0x17
///         (voice number, +80 = drum channel: the note picks the drum sound, FF = channel not used), 8 order-list
///         pointers at 0x20, instruments to load at 0x30.
/// order   [hi][lo] = phrase address, 80+t = transpose t for the next phrase (each phrase end resets it), FF = end ($1B70).
/// phrase  00-7F n d g v   note n (+ transpose), next event after d ticks, sounds g ticks (0 = rest), velocity v
///         80-EF           note (n &amp; 7F) with the last full note's d, g and v
///         F0 d x          wait d          F1 d v  channel volume    F2 d p  pan (40 = centre)
///         F6 d t x        tune            F7 d i  instrument         F3-F5 F8-FA  nothing
///         FB              loop start      FC n    loop end: the body plays n times, 0 = forever
///         FD              phrase end      FE      channel end        FF s p  effect (echo, noise, ADSR...)
/// </code>
/// The first FB of a channel takes the outer loop slot for good, later ones the inner slot ($1A56-$1B4F).
/// MIDI: 48 ticks per quarter note, one track per channel; commands MIDI has no word for travel as
/// "lufia:" text events so a song survives export and import.
/// </summary>
public static class SongCodec
{
    public const int Base = 0x2800;
    public const int MaxSize = 0x1800;
    public const int TicksPerQuarter = 48;

    /// <summary>End: the song stops here (FE, or the end of the order list).</summary>
    public enum Kind { Note, Volume, Pan, Program, Raw, LoopStart, LoopEnd, End }

    public sealed record Event(int Tick, Kind Kind, int A = 0, int B = 0, int C = 0, byte[]? Raw = null);

    public sealed class Song
    {
        public int Tempo;
        public byte[] Header = new byte[0x40];
        public List<Event>[] Channels = Enumerable.Range(0, 8).Select(_ => new List<Event>()).ToArray();
        public bool[] Drum = new bool[8];
        /// <summary>Microseconds per quarter note.</summary>
        public int MicrosPerQuarter => Tempo * 125 * TicksPerQuarter;
    }

    // ── decode ──

    /// <summary>Song data as stored in the ROM (2-byte length, then the sound RAM image from $2800).</summary>
    public static Song Decode(byte[] data, int maxTicks = 48 * 4 * 400)
    {
        var m = new byte[0x10000];
        Array.Copy(data, 2, m, Base, Math.Min(data.Length - 2, MaxSize));
        var song = new Song { Tempo = m[Base + 3] };
        Array.Copy(m, Base, song.Header, 0, 0x40);
        for (int ch = 0; ch < 8; ch++)
        {
            if (m[Base + 0x10 + ch] == 0xFF) continue;   // not used (the driver skips it, $18A8)
            song.Drum[ch] = (m[Base + 0x10 + ch] & 0x80) != 0;
            int order = m[Base + 0x20 + ch * 2] | m[Base + 0x21 + ch * 2] << 8;
            if (order < Base || order >= Base + MaxSize) continue;
            DecodeChannel(m, order, song.Channels[ch], maxTicks);
        }
        return song;
    }

    private static void DecodeChannel(byte[] m, int order, List<Event> ev, int maxTicks)
    {
        int tick = 0, transpose = 0, p = -1;
        int lastDelay = 0, lastGate = 0, lastVel = 0x60;
        // loop slots: (order, phrase, transpose, counter); counter FE = not started, FF = forever
        int[] lo = { -1, -1 }, lp = { 0, 0 }, lt = { 0, 0 }, lc = { 0, 0 };
        bool outerTaken = false, innerTaken = false;
        bool forever = false;
        int guard = 0;

        bool NextPhrase()
        {
            while (true)
            {
                if (order < 0 || order >= m.Length) return false;
                int b = m[order];
                if (b == 0xFF) { if (p >= 0) ev.Add(new Event(tick, Kind.End)); return false; }
                if ((b & 0x80) != 0) { transpose = b & 0x7F; order++; continue; }
                p = b << 8 | m[order + 1];
                order += 2;
                return true;
            }
        }
        if (!NextPhrase()) return;
        while (tick < maxTicks && guard++ < 200000)
        {
            int e = m[p];
            if (e < 0x80)
            {
                lastDelay = m[p + 1]; lastGate = m[p + 2]; lastVel = m[p + 3];
                ev.Add(new Event(tick, Kind.Note, e + transpose & 0x7F, lastGate, lastVel));
                tick += lastDelay; p += 4;
                continue;
            }
            if (e < 0xF0)
            {
                ev.Add(new Event(tick, Kind.Note, (e & 0x7F) + transpose & 0x7F, lastGate, lastVel));
                tick += lastDelay; p += 1;
                continue;
            }
            switch (e)
            {
                case 0xF0: tick += m[p + 1]; p += 3; break;
                case 0xF1: ev.Add(new Event(tick, Kind.Volume, m[p + 2])); tick += m[p + 1]; p += 3; break;
                case 0xF2: ev.Add(new Event(tick, Kind.Pan, m[p + 2])); tick += m[p + 1]; p += 3; break;
                case 0xF6: ev.Add(new Event(tick, Kind.Raw, Raw: new byte[] { 0xF6, 0, m[p + 2], m[p + 3] })); tick += m[p + 1]; p += 4; break;
                case 0xF7: ev.Add(new Event(tick, Kind.Program, m[p + 2])); tick += m[p + 1]; p += 3; break;
                case 0xFB:
                {
                    int s = !outerTaken ? 0 : !innerTaken ? 1 : -1;
                    if (s >= 0)
                    {
                        if (s == 0) outerTaken = true; else innerTaken = true;
                        lo[s] = order; lp[s] = p + 1; lt[s] = transpose; lc[s] = 0xFE;
                        ev.Add(new Event(tick, Kind.LoopStart, s));
                    }
                    p += 1;
                    break;
                }
                case 0xFC:
                {
                    // as $1ABD: the inner slot when its counter is set, else the outer one
                    int s = innerTaken && lc[1] != 0 ? 1 : 0;
                    int c = lc[s];
                    bool back;
                    if (c == 0xFF) back = true;                                   // forever
                    else if (c == 0xFE)                                            // first time at this end
                    {
                        int n = m[p + 1] - 1 & 0xFF;
                        back = n != 0;
                        if (back) lc[s] = n;                                       // (n = 1: leaves, counter untouched)
                    }
                    else
                    {
                        lc[s] = c - 1 & 0xFF;
                        back = lc[s] != 0;
                        if (!back) { innerTaken = false; lc[1] = 0; }             // the driver clears the inner slot
                    }
                    if (back && lc[s] == 0xFF)
                    {
                        // forever: mark the end of one pass and stop here
                        ev.Add(new Event(tick, Kind.LoopEnd, s));
                        return;
                    }
                    if (back) { order = lo[s]; p = lp[s]; transpose = lt[s]; }
                    else p += 2;
                    break;
                }
                case 0xFD: transpose = 0; if (!NextPhrase()) return; break;   // $1B52 clears the transpose
                case 0xFE: ev.Add(new Event(tick, Kind.End)); return;   // $1B95: the whole song stops
                case 0xFF: ev.Add(new Event(tick, Kind.Raw, Raw: new byte[] { 0xFF, m[p + 1], m[p + 2] })); p += 3; break;
                default: p += 1; break;
            }
        }
    }

    // ── encode ──

    /// <summary>
    /// Song data (with the 2-byte length) from per-channel events, header bytes from <paramref name="headerTemplate"/>
    /// with the new tempo and drum flags. Each channel loops forever (FB ... FC 00) as one phrase or cut into pieces,
    /// whichever is smaller; a piece that repeats (also at another pitch) is stored once and the order list plays it
    /// again, as the game's own songs do.
    /// </summary>
    public static byte[] Encode(Song song, byte[] headerTemplate)
    {
        var used = Enumerable.Range(0, 8).Select(ch => song.Channels[ch].Any(e => e.Kind is not (Kind.LoopStart or Kind.LoopEnd or Kind.End))).ToArray();
        var plan = new List<Piece>[8];
        // a channel with nothing to play gets an empty order list (FE would stop the whole song)
        for (int ch = 0; ch < 8; ch++) plan[ch] = used[ch] ? EncodeChannel(song.Channels[ch], 0) : new List<Piece>();
        // pieces shared through the order list usually come out smaller (more room to edit, faster to upload)
        {
            int[] grids = { 0, 48, 96, 192, 384, 768 };
            var none = Enumerable.Range(0, 8).Select(_ => new List<Piece>()).ToArray();
            int Size(int ch, List<Piece> p) { var one = (List<Piece>[])none.Clone(); one[ch] = p; return Layout(one).Count; }
            for (int ch = 0; ch < 8; ch++)
                if (used[ch])
                    plan[ch] = grids.Select(g => EncodeChannel(song.Channels[ch], g)).OrderBy(p => Size(ch, p)).First();
        }
        var img = Layout(plan);
        for (int i = 0; i < 0x40; i++) if (i is < 0x10 or >= 0x18 and < 0x20 or >= 0x30) img[i] = headerTemplate[i];
        img[3] = (byte)Math.Clamp(song.Tempo, 1, 255);
        for (int ch = 0; ch < 8; ch++)
            // channel table: the voice (its own number), +80 = drums (the note picks the drum sound); FF = not used
            img[0x10 + ch] = used[ch] ? (byte)(ch | (song.Drum[ch] ? 0x80 : 0)) : (byte)0xFF;
        if (img.Count > MaxSize) throw new InvalidOperationException($"The song needs {img.Count} bytes; the sound driver gives songs {MaxSize}.");
        var data = new byte[img.Count + 2];
        data[0] = (byte)img.Count; data[1] = (byte)(img.Count >> 8);
        img.CopyTo(data, 2);
        return data;
    }

    /// <summary>A phrase's bytes and where its note numbers are (pieces that differ only by a transposition share one phrase).</summary>
    private sealed record Piece(List<byte> Bytes, List<int> Notes)
    {
        public int Low => Notes.Count == 0 ? 0 : Notes.Min(i => Bytes[i] & 0x7F);
        /// <summary>The bytes with the notes moved down by the lowest one.</summary>
        public string Shape()
        {
            var b = Bytes.ToArray(); int low = Low;
            foreach (int i in Notes) b[i] = (byte)(b[i] & 0x80 | (b[i] & 0x7F) - low);
            return Convert.ToHexString(b);
        }
    }

    /// <summary>
    /// The sound RAM image from $2800: header (order pointers filled in), order lists, phrases. A phrase is stored
    /// once at the lowest pitch it's used at; higher uses get a transpose (80+t) before them in the order list.
    /// </summary>
    private static List<byte> Layout(List<Piece>[] plan)
    {
        var img = new List<byte>(new byte[0x40]);
        var shapes = plan.Select(p => p.Select(x => x.Shape()).ToArray()).ToArray();
        var lowest = new Dictionary<string, int>();
        for (int ch = 0; ch < 8; ch++)
            for (int k = 0; k < plan[ch].Count; k++)
                lowest[shapes[ch][k]] = Math.Min(plan[ch][k].Low, lowest.GetValueOrDefault(shapes[ch][k], 999));
        int orderBytes = 0;
        for (int ch = 0; ch < 8; ch++)
        {
            orderBytes++;
            for (int k = 0; k < plan[ch].Count; k++) orderBytes += plan[ch][k].Low > lowest[shapes[ch][k]] ? 3 : 2;
        }
        var where = new Dictionary<string, int>();
        var store = new List<byte>();
        int phraseBase = Base + 0x40 + orderBytes;
        var orders = new List<byte>();
        for (int ch = 0; ch < 8; ch++)
        {
            int o = Base + 0x40 + orders.Count;
            img[0x20 + ch * 2] = (byte)o; img[0x21 + ch * 2] = (byte)(o >> 8);
            for (int k = 0; k < plan[ch].Count; k++)
            {
                var piece = plan[ch][k]; string key = shapes[ch][k]; int low = lowest[key];
                if (!where.TryGetValue(key, out int at))
                {
                    at = phraseBase + store.Count; where[key] = at;
                    var b = piece.Bytes.ToArray(); int shift = piece.Low - low;
                    foreach (int i in piece.Notes) b[i] = (byte)(b[i] & 0x80 | (b[i] & 0x7F) - shift);
                    store.AddRange(b);
                }
                if (piece.Low > low) orders.Add((byte)(0x80 | piece.Low - low));
                orders.Add((byte)(at >> 8)); orders.Add((byte)at);
            }
            orders.Add(0xFF);
        }
        img.AddRange(orders);
        img.AddRange(store);
        return img;
    }

    /// <summary>
    /// A channel's phrases, each ending in FD (the last one in FC 00 FD, or FE where the song stops). Grid 0: one
    /// phrase with the loop start inside it; otherwise a new phrase at the first event of every grid step and the
    /// loop start as a phrase of its own (FB FD: the loop-back lands on the FD and carries on with the next entry).
    /// </summary>
    private static List<Piece> EncodeChannel(List<Event> events, int grid)
    {
        // events in time order, keeping the original order within a tick (markers included)
        var ordered = events.Select((e, i) => (e, i)).OrderBy(x => x.e.Tick).ThenBy(x => x.i).Select(x => x.e).ToList();
        // the loop that repeats forever starts at the LoopStart of the LoopEnd's slot (the last one before it);
        // without one the whole channel repeats
        var loopEnd = ordered.LastOrDefault(e => e.Kind == Kind.LoopEnd);
        var stop = ordered.FirstOrDefault(e => e.Kind == Kind.End);
        if (stop != null) { loopEnd = null; ordered = ordered.Where(e => e.Tick <= stop.Tick && (e.Kind != Kind.End || e == stop)).ToList(); }
        int loopStartIndex = -1;
        if (loopEnd != null)
        {
            int endAt = ordered.IndexOf(loopEnd);
            loopStartIndex = ordered.FindLastIndex(endAt, e => e.Kind == Kind.LoopStart && e.A == loopEnd.A);
        }
        var ev = new List<Event>();
        int fbBefore = 0, loopTick = 0;   // FB goes before this playable event, at this tick
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i == loopStartIndex) { fbBefore = ev.Count; loopTick = ordered[i].Tick; }
            if (ordered[i].Kind is not (Kind.LoopStart or Kind.LoopEnd or Kind.End)) ev.Add(ordered[i]);
        }
        if (fbBefore >= ev.Count) { fbBefore = 0; loopTick = 0; }   // an empty loop would hang the driver: repeat everything
        bool loops = stop == null;
        // (a note may sound past the loop end: its gate can be longer than its delay)
        int end = stop?.Tick ?? loopEnd?.Tick ?? (ev.Count == 0 ? 0 : ev[^1].Tick + (ev[^1].Kind == Kind.Note ? Math.Max(1, ev[^1].B) : 1));

        var phrases = new List<Piece>();
        var o = new List<byte>();
        var notes = new List<int>();
        int time = 0, step = -1;
        int lastDelay = -1, lastGate = -1, lastVel = -1;
        void Wait(int n) { for (; n > 0; n -= Math.Min(n, 255)) o.AddRange(new byte[] { 0xF0, (byte)Math.Min(n, 255), 0 }); }
        void Cut() { if (o.Count > 0) { o.Add(0xFD); phrases.Add(new Piece(o, notes)); o = new List<byte>(); notes = new List<int>(); } lastDelay = -1; }
        for (int i = 0; i < ev.Count; i++)
        {
            if (loops && i == fbBefore)
            {
                if (grid > 0) Cut();
                Wait(loopTick - time); time = Math.Max(time, loopTick);
                if (grid > 0) Cut();
                o.Add(0xFB);
                if (grid > 0) Cut();
                lastDelay = -1;   // the pass after a loop-back starts with whatever note ended the last one
            }
            var e = ev[i];
            if (grid > 0 && e.Tick / grid != step) { step = e.Tick / grid; Cut(); }
            Wait(e.Tick - time); time = Math.Max(time, e.Tick);
            int next = loops && i + 1 == fbBefore ? loopTick : i + 1 < ev.Count ? ev[i + 1].Tick : end;
            int d = Math.Clamp(next - e.Tick, 0, 255);
            switch (e.Kind)
            {
                case Kind.Note:
                {
                    int gate = Math.Clamp(e.B, 0, 255), vel = Math.Clamp(e.C, 0, 127), n = e.A & 0x7F;
                    notes.Add(o.Count);
                    if (d == lastDelay && gate == lastGate && vel == lastVel) o.Add((byte)(0x80 | n));
                    else { o.AddRange(new[] { (byte)n, (byte)d, (byte)gate, (byte)vel }); lastDelay = d; lastGate = gate; lastVel = vel; }
                    break;
                }
                case Kind.Volume: o.AddRange(new byte[] { 0xF1, (byte)d, (byte)Math.Clamp(e.A, 0, 127) }); break;
                case Kind.Pan: o.AddRange(new byte[] { 0xF2, (byte)d, (byte)Math.Clamp(e.A, 0, 127) }); break;
                case Kind.Program: o.AddRange(new byte[] { 0xF7, (byte)d, (byte)e.A }); break;
                case Kind.Raw:
                {
                    var r = (byte[])e.Raw!.Clone();
                    if (r[0] is 0xF6 && r.Length >= 4) r[1] = (byte)d;
                    else d = 0;   // no wait of its own: the gap is filled with F0
                    o.AddRange(r);
                    break;
                }
            }
            time += d;
        }
        Wait(end - time);
        o.AddRange(loops ? new byte[] { 0xFC, 0x00, 0xFD } : new byte[] { 0xFE });
        phrases.Add(new Piece(o, notes));
        return phrases;
    }

    // ── MIDI ──

    public static byte[] ToMidi(Song song, string name)
    {
        var tracks = new List<byte[]>();
        // track 0: tempo and name
        var t0 = new List<byte>();
        Meta(t0, 0, 0x03, Encoding.ASCII.GetBytes(name));
        int us = song.MicrosPerQuarter;
        Meta(t0, 0, 0x51, new[] { (byte)(us >> 16), (byte)(us >> 8), (byte)us });
        Meta(t0, 0, 0x01, Encoding.ASCII.GetBytes($"lufia: tempo {song.Tempo}"));
        EndTrack(t0, 0);
        tracks.Add(t0.ToArray());
        for (int ch = 0; ch < 8; ch++)
        {
            var list = song.Channels[ch];
            if (list.Count == 0) continue;
            int mc = song.Drum[ch] ? 9 : ch;
            var msgs = new List<(int Tick, int Order, byte[] Bytes, bool Meta)>();
            msgs.Add((0, 0, Encoding.ASCII.GetBytes($"lufia: channel {ch}{(song.Drum[ch] ? " drums" : "")}"), true));
            var sounding = list.Where(x => x.Kind == Kind.Note && x.B > 0).ToList();
            foreach (var e in list)
            {
                switch (e.Kind)
                {
                    case Kind.Note:
                    {
                        if (e.B <= 0) break;   // a rest
                        // a gate past the next note's start ties into it (the driver slurs, $1958); MIDI ends the
                        // note there and a text keeps the real gate
                        int at = sounding.IndexOf(e);
                        int room = at + 1 < sounding.Count ? sounding[at + 1].Tick - e.Tick : int.MaxValue;
                        int len = Math.Min(e.B, room);
                        if (len < e.B) msgs.Add((e.Tick, 2, Encoding.ASCII.GetBytes($"lufia: gate {e.B}"), true));
                        if (e.C <= 0) msgs.Add((e.Tick, 2, Encoding.ASCII.GetBytes("lufia: velocity 0"), true));   // MIDI's velocity 0 = note off
                        if (len <= 0) break;
                        msgs.Add((e.Tick, 2, new[] { (byte)(0x90 | mc), (byte)e.A, (byte)Math.Clamp(e.C, 1, 127) }, false));
                        msgs.Add((e.Tick + len, 1, new[] { (byte)(0x80 | mc), (byte)e.A, (byte)0 }, false));
                        break;
                    }
                    case Kind.Volume: msgs.Add((e.Tick, 2, new[] { (byte)(0xB0 | mc), (byte)7, (byte)Math.Clamp(e.A, 0, 127) }, false)); break;
                    case Kind.Pan: msgs.Add((e.Tick, 2, new[] { (byte)(0xB0 | mc), (byte)10, (byte)Math.Clamp(e.A, 0, 127) }, false)); break;
                    case Kind.Program: msgs.Add((e.Tick, 2, new[] { (byte)(0xC0 | mc), (byte)(e.A & 0x7F) }, false));
                        if (e.A > 0x7F) msgs.Add((e.Tick, 2, Encoding.ASCII.GetBytes($"lufia: instrument {e.A:X2}"), true));
                        break;
                    case Kind.Raw: msgs.Add((e.Tick, 2, Encoding.ASCII.GetBytes("lufia: " + Convert.ToHexString(e.Raw!)), true)); break;
                    case Kind.LoopStart:
                        // only the start of the loop that repeats forever (the LoopEnd's slot, last before it)
                        var le = list.LastOrDefault(x => x.Kind == Kind.LoopEnd);
                        if (le != null && e == list.Take(list.IndexOf(le)).LastOrDefault(x => x.Kind == Kind.LoopStart && x.A == le.A))
                            msgs.Add((e.Tick, 0, Encoding.ASCII.GetBytes("loopStart"), true));
                        break;
                    case Kind.LoopEnd: msgs.Add((e.Tick, 0, Encoding.ASCII.GetBytes("loopEnd"), true)); break;
                    case Kind.End: msgs.Add((e.Tick, 3, Encoding.ASCII.GetBytes("songEnd"), true)); break;
                }
            }
            var tr = new List<byte>();
            int last = 0;
            foreach (var (tick, _, bytes, meta) in msgs.OrderBy(x => x.Tick).ThenBy(x => x.Order))
            {
                if (meta) Meta(tr, tick - last, bytes.Length > 0 && Encoding.ASCII.GetString(bytes).StartsWith("loop") || Encoding.ASCII.GetString(bytes) == "songEnd" ? 0x06 : 0x01, bytes);
                else { VarLen(tr, tick - last); tr.AddRange(bytes); }
                last = tick;
            }
            EndTrack(tr, 0);
            tracks.Add(tr.ToArray());
        }
        var f = new List<byte>();
        f.AddRange(Encoding.ASCII.GetBytes("MThd")); Be32(f, 6); Be16(f, 1); Be16(f, tracks.Count); Be16(f, TicksPerQuarter);
        foreach (var t in tracks) { f.AddRange(Encoding.ASCII.GetBytes("MTrk")); Be32(f, t.Length); f.AddRange(t); }
        return f.ToArray();
    }

    /// <summary>
    /// A song from a MIDI file: each track (and MIDI channel within it) becomes a game channel - the one named by a
    /// "lufia: channel N" text, else the next free one (at most 8; MIDI channel 10 = drums). A game channel plays
    /// one note at a time; times are scaled to 48 per quarter note.
    /// </summary>
    public static Song FromMidi(byte[] midi, out List<string> warnings)
    {
        warnings = new List<string>();
        var song = new Song { Tempo = 83 };
        var (ppq, events) = ReadMidi(midi);
        double scale = TicksPerQuarter / (double)ppq;
        int Q(long t) => (int)Math.Round(t * scale);
        var tempoEv = events.FirstOrDefault(e => e.Type == 0x51);
        if (tempoEv.Data != null) song.Tempo = Math.Clamp((int)Math.Round((tempoEv.Data[0] << 16 | tempoEv.Data[1] << 8 | tempoEv.Data[2]) / (125.0 * TicksPerQuarter)), 1, 255);
        if (events.Where(e => e.Type == 0x51).Select(e => Convert.ToHexString(e.Data!)).Distinct().Count() > 1)
            warnings.Add("The MIDI changes tempo; the game plays a song at one tempo (the first one is used).");
        static bool IsVoice(MidiEv e) => e.Status is >= 0x80 and < 0xF0;
        static string Text(MidiEv e) => Encoding.ASCII.GetString(e.Data!).Trim();
        // units: (track, MIDI channel) pairs that play something
        var units = events.Where(IsVoice).Select(e => (e.Track, Ch: e.Status & 15)).Distinct().ToList();
        foreach (var e in events.Where(e => e.Type == 0x01 && Text(e).StartsWith("lufia: channel ")))
            if (!units.Any(u => u.Track == e.Track) && int.TryParse(Text(e)[15..].Split(' ')[0], out int n))
                units.Add((e.Track, Text(e).EndsWith("drums") ? 9 : n));
        var wanted = new Dictionary<(int Track, int Ch), int>();
        foreach (var u in units)
        {
            var name = events.FirstOrDefault(e => e.Track == u.Track && e.Type == 0x01 && Text(e).StartsWith("lufia: channel "));
            if (name.Data != null && int.TryParse(Text(name)[15..].Split(' ')[0], out int n) && n is >= 0 and < 8 && !wanted.ContainsValue(n))
                wanted[u] = n;
        }
        var free = new Queue<int>(Enumerable.Range(0, 8).Where(n => !wanted.ContainsValue(n)));
        foreach (var u in units.Where(u => !wanted.ContainsKey(u)))
        {
            if (free.Count == 0) { warnings.Add($"The MIDI has {units.Count} tracks/channels that play; the game has 8 channels, the rest are left out."); break; }
            wanted[u] = free.Dequeue();
        }
        foreach (var (u, slot) in wanted)
        {
            song.Drum[slot] = u.Ch == 9;
            var list = song.Channels[slot];
            var on = new Dictionary<int, (int Tick, int Vel)>();
            var gates = new Dictionary<int, int>();
            var silent = new HashSet<int>();
            // texts and markers sit in the unit's track (shared by the track's channels when it has several)
            foreach (var e in events.Where(e => e.Track == u.Track && (IsVoice(e) ? (e.Status & 15) == u.Ch : e.Type is 0x01 or 0x06)))
            {
                int kind = e.Status & 0xF0;
                if (e.Type == 0x06)
                {
                    string marker = Text(e);
                    if (marker.Equals("loopStart", StringComparison.OrdinalIgnoreCase)) list.Add(new Event(Q(e.Tick), Kind.LoopStart, 0));
                    else if (marker.Equals("loopEnd", StringComparison.OrdinalIgnoreCase)) list.Add(new Event(Q(e.Tick), Kind.LoopEnd, 0));
                    else if (marker.Equals("songEnd", StringComparison.OrdinalIgnoreCase)) list.Add(new Event(Q(e.Tick), Kind.End));
                    continue;
                }
                if (e.Type == 0x01)
                {
                    string text = Text(e);
                    if (!text.StartsWith("lufia: ") || text.Length <= 7) continue;
                    string body = text[7..];
                    if (body.StartsWith("instrument ") && int.TryParse(body[11..], System.Globalization.NumberStyles.HexNumber, null, out int ins))
                    {
                        list.RemoveAll(x => x.Kind == Kind.Program && x.Tick == Q(e.Tick));
                        list.Add(new Event(Q(e.Tick), Kind.Program, ins));
                    }
                    else if (body.StartsWith("gate ") && int.TryParse(body[5..], out int g)) gates[Q(e.Tick)] = g;
                    else if (body == "velocity 0") silent.Add(Q(e.Tick));
                    else if (body.Length >= 4 && body.Length % 2 == 0 && body.All(Uri.IsHexDigit))
                        list.Add(new Event(Q(e.Tick), Kind.Raw, Raw: Convert.FromHexString(body)));
                    continue;
                }
                if (kind == 0x90 && e.B > 0)
                {
                    // the same key again before its note-off: the first one ends here
                    if (on.Remove(e.A, out var held)) list.Add(new Event(held.Tick, Kind.Note, e.A, Math.Max(1, Q(e.Tick) - held.Tick), held.Vel));
                    on[e.A] = (Q(e.Tick), e.B);
                }
                else if (kind == 0x80 || kind == 0x90)
                {
                    if (on.Remove(e.A, out var s))
                        list.Add(new Event(s.Tick, Kind.Note, e.A, Math.Max(1, Q(e.Tick) - s.Tick), s.Vel));
                }
                else if (kind == 0xB0 && e.A == 7) list.Add(new Event(Q(e.Tick), Kind.Volume, e.B));
                else if (kind == 0xB0 && e.A == 10) list.Add(new Event(Q(e.Tick), Kind.Pan, e.B));
                else if (kind == 0xC0 && !list.Any(x => x.Kind == Kind.Program && x.Tick == Q(e.Tick))) list.Add(new Event(Q(e.Tick), Kind.Program, e.A));
            }
            foreach (var (k, s) in on) list.Add(new Event(s.Tick, Kind.Note, k, 1, s.Vel));   // never released
            // the real gate of notes that tie into the next one, velocity 0
            for (int k = 0; k < list.Count; k++)
            {
                if (list[k].Kind != Kind.Note) continue;
                if (gates.TryGetValue(list[k].Tick, out int g)) list[k] = list[k] with { B = g };
                if (silent.Contains(list[k].Tick)) list[k] = list[k] with { C = 0 };
            }
            // one note at a time: of notes starting together the highest stays
            var chords = list.Where(x => x.Kind == Kind.Note).GroupBy(x => x.Tick).Where(gr => gr.Count() > 1).ToList();
            foreach (var gr in chords) foreach (var x in gr.OrderByDescending(x => x.A).Skip(1)) list.Remove(x);
            if (chords.Count > 0)
                warnings.Add($"Track {u.Track} (MIDI channel {u.Ch + 1}): {chords.Count} chords cut to their top note (a game channel plays one note at a time).");
            // in time order; within a tick: loop start, settings, the note, then loop end / song end
            static int Rank(Event x) => x.Kind switch { Kind.LoopStart => 0, Kind.Note => 2, Kind.LoopEnd or Kind.End => 3, _ => 1 };
            var sorted = list.OrderBy(x => x.Tick).ThenBy(Rank).ToList();
            list.Clear(); list.AddRange(sorted);
        }
        // a MIDI without loop markers (not from here) repeats as a whole: every channel loops at the song's end
        // (the last end-of-track, or where the last note stops), so the channels stay together
        if (!song.Channels.Any(c => c.Any(x => x.Kind is Kind.LoopEnd or Kind.End)))
        {
            int last = song.Channels.SelectMany(c => c).Select(x => x.Tick + (x.Kind == Kind.Note ? x.B : 0)).DefaultIfEmpty(0).Max();
            int endOfTrack = events.Where(e => e.Type == 0x2F).Select(e => Q(e.Tick)).DefaultIfEmpty(0).Max();
            int loopAt = Math.Max(last, endOfTrack);
            foreach (var c in song.Channels.Where(c => c.Any(x => x.Kind == Kind.Note)))
            {
                c.Insert(0, new Event(0, Kind.LoopStart, 0));
                c.Add(new Event(loopAt, Kind.LoopEnd, 0));
            }
        }
        return song;
    }

    private record struct MidiEv(int Track, long Tick, int Status, int A, int B, int Type, byte[]? Data);

    private static (int Ppq, List<MidiEv>) ReadMidi(byte[] f)
    {
        if (f.Length < 14 || Encoding.ASCII.GetString(f, 0, 4) != "MThd") throw new InvalidDataException("Not a MIDI file.");
        int ntr = f[10] << 8 | f[11], div = f[12] << 8 | f[13];
        if ((div & 0x8000) != 0) throw new InvalidDataException("MIDI files timed in SMPTE frames aren't supported.");
        var list = new List<MidiEv>();
        int p = 8 + (f[4] << 24 | f[5] << 16 | f[6] << 8 | f[7]);
        for (int t = 0; t < ntr && p + 8 <= f.Length; t++)
        {
            int len = f[p + 4] << 24 | f[p + 5] << 16 | f[p + 6] << 8 | f[p + 7];
            int q = p + 8, end = Math.Min(f.Length, q + len);
            long tick = 0; int status = 0;
            while (q < end)
            {
                long dv = 0; byte b;
                do { b = f[q++]; dv = dv << 7 | (uint)(b & 0x7F); } while ((b & 0x80) != 0 && q < end);
                tick += dv;
                int s = f[q];
                if (s >= 0x80) { status = s; q++; }
                if (status == 0xFF)
                {
                    int type = f[q++]; int l = 0;
                    do { b = f[q++]; l = l << 7 | (b & 0x7F); } while ((b & 0x80) != 0);
                    list.Add(new MidiEv(t, tick, 0xFF, 0, 0, type, f.AsSpan(q, Math.Min(l, end - q)).ToArray()));
                    q += l;
                    if (type == 0x2F) break;
                }
                else if (status is 0xF0 or 0xF7)
                {
                    int l = 0;
                    do { b = f[q++]; l = l << 7 | (b & 0x7F); } while ((b & 0x80) != 0);
                    q += l;
                }
                else
                {
                    int hi = status & 0xF0;
                    int a = f[q++], bb = hi is 0xC0 or 0xD0 ? 0 : f[q++];
                    list.Add(new MidiEv(t, tick, status, a, bb, -1, null));
                }
            }
            p = 8 + len + p;
        }
        return (div, list.OrderBy(e => e.Tick).ToList());
    }

    private static void VarLen(List<byte> o, int v)
    {
        v = Math.Max(0, v);
        var stack = new Stack<byte>();
        stack.Push((byte)(v & 0x7F));
        while ((v >>= 7) > 0) stack.Push((byte)(0x80 | v & 0x7F));
        o.AddRange(stack);
    }

    private static void Meta(List<byte> o, int delta, int type, byte[] data)
    {
        VarLen(o, delta); o.Add(0xFF); o.Add((byte)type); VarLen(o, data.Length); o.AddRange(data);
    }

    private static void EndTrack(List<byte> o, int delta) => Meta(o, delta, 0x2F, Array.Empty<byte>());
    private static void Be32(List<byte> o, int v) { o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v); }
    private static void Be16(List<byte> o, int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
}
