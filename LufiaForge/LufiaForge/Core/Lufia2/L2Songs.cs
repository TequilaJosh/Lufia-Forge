using LufiaForge.Core.Audio;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II's songs: packed files 00-3E, each a 2-byte length and the image the SNES sends to sound RAM $4400
/// ($80:941A). Same sequence format as Lufia 1 (<see cref="SongCodec"/>) with these differences, read from the driver:
/// <code>
/// header  "S2C", tempo, name +04-+0F, channel table +10-+17, +18 non-zero = the song's samples replace the shared
///         ones, +19 echo setting, order-list offsets from $4400 at +20 (u16), sample list +30-+4F (FF ends; ids
///         from 80 are drum notes)
/// order   hh ll = phrase at ($44 + hh) &lt;&lt; 8 | ll, 80+t = transpose t, FF = channel stops
/// phrase  F0 d x  vibrato depth x, wait d     F3 d    wait d (2 bytes)       F4 d t  tempo t
///         F6 d m  pitch multiplier m/128       FE      this channel ends      FC 01   does nothing
/// </code>
/// Loops ($37D2 / $3813): FB takes the outer slot when it's free, else the inner one when that's free; FC n uses the
/// inner slot while it's taken, and frees the slot it used when its count runs out.
/// </summary>
public static class L2Songs
{
    public const int Count = L2Sound.SongCount;
    public const int Base = L2Sound.SongAddress;
    /// <summary>Room for a song between $4400 and the samples at $5800.</summary>
    public const int MaxSize = 0x1400;

    /// <summary>The unpacked file (2-byte length, then the image).</summary>
    public static byte[] Read(RomBuffer rom, int song) => L2Rom.ReadFile(rom, song);

    /// <summary>The name in the song's header (+04-+0F), e.g. "FIELD".</summary>
    public static string Name(byte[] file)
    {
        if (file.Length < 2 + 0x10) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 4; i < 0x10; i++) { byte b = file[2 + i]; sb.Append(b is >= 0x20 and < 0x7F ? (char)b : ' '); }
        return sb.ToString().Trim();
    }

    public static string Name(RomBuffer rom, int song)
    {
        try { return Name(Read(rom, song)); } catch { return ""; }
    }

    /// <summary>The song's own sample list (+30, FF ends): ids below 80 are samples, 80 and up drum notes.</summary>
    public static List<int> SampleList(byte[] file)
    {
        var list = new List<int>();
        for (int i = 0x30; i < 0x50 && 2 + i < file.Length; i++)
        {
            int v = file[2 + i];
            if (v == 0xFF) break;
            list.Add(v);
        }
        return list;
    }

    /// <summary>
    /// The song as <see cref="SongCodec"/> events (one pass per channel; a loop that repeats forever ends in
    /// LoopEnd), so <see cref="SongCodec.ToMidi"/> can write it. Commands MIDI has no word for (vibrato F0, tempo
    /// F4, pitch multiplier F6, effects FF) become Raw events with their wait byte set to 0; channel 0's phrase ends
    /// are Raw FD 00 markers (<see cref="PhraseEnd"/>).
    /// </summary>
    public static SongCodec.Song Decode(byte[] file, int maxTicks = 48 * 4 * 400)
    {
        var m = new byte[0x10000];
        Array.Copy(file, 2, m, Base, Math.Min(file.Length - 2, 0x10000 - Base));
        var song = new SongCodec.Song { Tempo = m[Base + 3] };
        Array.Copy(m, Base, song.Header, 0, 0x40);
        for (int ch = 0; ch < 8; ch++)
        {
            int voice = m[Base + 0x10 + ch];
            if (voice == 0xFF) continue;
            song.Drum[ch] = (voice & 0x80) != 0;
            int order = Base + (m[Base + 0x20 + ch * 2] | m[Base + 0x21 + ch * 2] << 8);
            if (order >= 0xFFFF) continue;
            DecodeChannel(m, order & 0xFFFF, song.Channels[ch], maxTicks, ch == 0);
            KeepForeverLoop(song.Channels[ch]);
        }
        return song;
    }

    /// <summary>Marker for a phrase end on channel 0 (exported as the MIDI text "lufia: FD00").</summary>
    public static byte[] PhraseEnd => new byte[] { 0xFD, 0x00 };
    private static bool IsPhraseEnd(SongCodec.Event e) => e.Kind == SongCodec.Kind.Raw && e.Raw is [0xFD, 0x00];

    /// <summary>
    /// Loop markers say where the channel repeats forever: only the start of the loop that the LoopEnd closes stays
    /// (finite loops are played out in the events), both as slot 0. The music is the same; it makes the events
    /// independent of how the loops were nested, so encoding them and decoding again gives the same list.
    /// </summary>
    private static void KeepForeverLoop(List<SongCodec.Event> ev)
    {
        int end = ev.FindLastIndex(e => e.Kind == SongCodec.Kind.LoopEnd);
        int start = end < 0 ? -1 : ev.FindLastIndex(end, e => e.Kind == SongCodec.Kind.LoopStart && e.A == ev[end].A);
        for (int i = ev.Count - 1; i >= 0; i--)
        {
            if (ev[i].Kind == SongCodec.Kind.LoopStart && i != start) ev.RemoveAt(i);
            else if (ev[i].Kind is SongCodec.Kind.LoopStart or SongCodec.Kind.LoopEnd) ev[i] = ev[i] with { A = 0 };
        }
    }

    private static void DecodeChannel(byte[] m, int order, List<SongCodec.Event> ev, int maxTicks, bool markPhrases)
    {
        int tick = 0, transpose = 0, p;
        int lastDelay = 0, lastGate = 0, lastVel = 0;
        // loop slots 0 = outer, 1 = inner: taken, order entry, phrase position, counter (FE = new, FF = forever), transpose
        var taken = new bool[2]; var lo = new int[2]; var lp = new int[2]; var lc = new int[2]; var lt = new int[2];

        // the order entry at 'order' (transpose bytes before it are taken); -1 at FF
        int Phrase()
        {
            while (true)
            {
                if (order + 1 >= m.Length) return -1;
                int b = m[order];
                if (b == 0xFF) return -1;
                if ((b & 0x80) != 0) { transpose = b & 0x7F; order++; continue; }
                return ((b + 0x44) << 8 | m[order + 1]) & 0xFFFF;
            }
        }
        p = Phrase();
        if (p < 0) return;
        for (int guard = 0; tick < maxTicks && guard < 200000; guard++)
        {
            if (p + 3 >= m.Length) { ev.Add(new(tick, SongCodec.Kind.End)); return; }
            int e = m[p];
            if (e < 0x80)
            {
                lastDelay = m[p + 1]; lastGate = m[p + 2]; lastVel = m[p + 3];
                ev.Add(new(tick, SongCodec.Kind.Note, e + transpose & 0x7F, lastGate, lastVel));
                tick += lastDelay; p += 4;
                continue;
            }
            if (e < 0xF0)
            {
                ev.Add(new(tick, SongCodec.Kind.Note, (e & 0x7F) + transpose & 0x7F, lastGate, lastVel));
                tick += lastDelay; p += 1;
                continue;
            }
            switch (e)
            {
                case 0xF0: ev.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xF0, 0, m[p + 2] })); tick += m[p + 1]; p += 3; break;
                case 0xF1: ev.Add(new(tick, SongCodec.Kind.Volume, m[p + 2])); tick += m[p + 1]; p += 3; break;
                case 0xF2: ev.Add(new(tick, SongCodec.Kind.Pan, m[p + 2])); tick += m[p + 1]; p += 3; break;
                case 0xF3: tick += m[p + 1]; p += 2; break;
                case 0xF4: ev.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xF4, 0, m[p + 2] })); tick += m[p + 1]; p += 3; break;
                case 0xF6: ev.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xF6, 0, m[p + 2] })); tick += m[p + 1]; p += 3; break;
                case 0xF7: ev.Add(new(tick, SongCodec.Kind.Program, m[p + 2])); tick += m[p + 1]; p += 3; break;
                case 0xFB:
                {
                    int s = !taken[0] ? 0 : !taken[1] ? 1 : -1;
                    if (s >= 0)
                    {
                        taken[s] = true; lo[s] = order; lp[s] = p + 1; lc[s] = 0xFE; lt[s] = transpose;
                        ev.Add(new(tick, SongCodec.Kind.LoopStart, s));
                    }
                    p += 1;
                    break;
                }
                case 0xFC:
                {
                    int n = m[p + 1] - 1 & 0xFF;
                    if (n == 0) { p += 2; break; }   // FC 01
                    int s = taken[1] ? 1 : 0;
                    bool back;
                    if (lc[s] == 0xFF) back = true;
                    else if (lc[s] == 0xFE) { lc[s] = n; back = true; }
                    else
                    {
                        lc[s] = lc[s] - 1 & 0xFF;
                        back = lc[s] != 0;
                        if (!back) taken[s] = false;
                    }
                    if (back && lc[s] == 0xFF)
                    {
                        // forever: one pass ends here
                        ev.Add(new(tick, SongCodec.Kind.LoopEnd, s));
                        return;
                    }
                    if (back) { order = lo[s]; p = lp[s]; transpose = lt[s]; }
                    else p += 2;
                    break;
                }
                case 0xFD:
                    // channel 0's phrase ends are the song's sync points (driver $386C counts them in $8D, command 08
                    // reports it, event 4D waits for it): kept as markers so a re-encoded song ends phrases there too
                    if (markPhrases) ev.Add(new(tick, SongCodec.Kind.Raw, Raw: PhraseEnd));
                    transpose = 0; order += 2;
                    p = Phrase();
                    if (p < 0) { ev.Add(new(tick, SongCodec.Kind.End)); return; }
                    break;
                case 0xFE: ev.Add(new(tick, SongCodec.Kind.End)); return;
                case 0xFF: ev.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xFF, m[p + 1], m[p + 2] })); p += 3; break;
                default: p += 1; break;   // F5, F8-FA
            }
        }
    }

    /// <summary>The song as a MIDI file (48 ticks per quarter note, one track per channel, drums on MIDI channel 10).</summary>
    public static byte[] ToMidi(byte[] file) => SongCodec.ToMidi(Decode(file), Name(file));

    // ── encode ──

    public const int HeaderSize = 0x50;

    /// <summary>
    /// The song file (2-byte length, then the image for $4400) from per-channel events. Header bytes come from
    /// <paramref name="header"/> (0x50 bytes: name, +18, +19, sample list...) with the song's tempo; the channel table keeps
    /// each channel's voice with the drum bit from <see cref="SongCodec.Song.Drum"/>. A channel loops forever
    /// (FB ... FC 00) or ends with FE, as one phrase or cut into pieces, whichever is smaller; a piece that repeats (also
    /// at another pitch) is stored once and the order list plays it again. Throws when the image is bigger than the
    /// driver's room ($4400-$57FF) and <paramref name="checkSize"/> is set.
    /// </summary>
    public static byte[] Encode(SongCodec.Song song, byte[] header, bool checkSize = true)
    {
        var used = Enumerable.Range(0, 8).Select(ch => song.Channels[ch].Any(e => e.Kind is not (SongCodec.Kind.LoopStart or SongCodec.Kind.LoopEnd or SongCodec.Kind.End))).ToArray();
        var plan = new List<Piece>[8];
        int[] grids = { 0, 24, 32, 48, 64, 96, 128, 192, 256, 384, 576, 768 };
        var none = Enumerable.Range(0, 8).Select(_ => new List<Piece>()).ToArray();
        int Size(int ch, List<Piece> p) { var one = (List<Piece>[])none.Clone(); one[ch] = p; return Layout(one).Count; }
        for (int ch = 0; ch < 8; ch++)
            plan[ch] = !used[ch] ? new List<Piece>()
                : song.Channels[ch].Any(IsPhraseEnd) ? EncodeChannel(song.Channels[ch], -1)   // phrases end where the markers say
                : grids.Select(g => Repeats(EncodeChannel(song.Channels[ch], g))).OrderBy(p => Size(ch, p)).First();
        var img = Layout(plan);
        for (int i = 0; i < HeaderSize; i++) if (i is < 0x10 or >= 0x18 and < 0x20 or >= 0x30) img[i] = i < header.Length ? header[i] : (byte)0xFF;
        img[3] = (byte)Math.Clamp(song.Tempo, 1, 255);
        for (int ch = 0; ch < 8; ch++)
        {
            int was = ch < header.Length - 0x10 ? header[0x10 + ch] : 0xFF;
            img[0x10 + ch] = !used[ch] ? (byte)was : (byte)((was == 0xFF ? ch : was & 0x7F) | (song.Drum[ch] ? 0x80 : 0));
        }
        if (checkSize && img.Count > MaxSize)
            throw new InvalidOperationException($"The song needs {img.Count:N0} bytes; the sound driver has room for {MaxSize:N0} ($4400-$57FF).");
        var data = new byte[img.Count + 2];
        data[0] = (byte)img.Count; data[1] = (byte)(img.Count >> 8);
        img.CopyTo(data, 2);
        return data;
    }

    /// <summary>A phrase's bytes and where its note numbers are (pieces that differ only by a transposition share one phrase).</summary>
    private sealed record Piece(List<byte> Bytes, List<int> Notes)
    {
        public int Low => Notes.Count == 0 ? 0 : Notes.Min(i => Bytes[i] & 0x7F);
        public string Shape()
        {
            var b = Bytes.ToArray(); int low = Low;
            foreach (int i in Notes) b[i] = (byte)(b[i] & 0x80 | (b[i] & 0x7F) - low);
            return Convert.ToHexString(b);
        }
    }

    /// <summary>
    /// Pieces that follow each other again and again (a bar played 8 times, ...) become a finite loop: a piece "FB FD",
    /// the body once, then "FC n FD". The loop-back lands on the FD after FB and the order list carries on with the body.
    /// A loop before the forever loop's start takes the outer slot and frees it; one inside takes the inner slot ($37D2 /
    /// $3813). Not used where phrase ends are kept (channel 0's sync points): the two extra pieces would add phrase ends.
    /// </summary>
    private static List<Piece> Repeats(List<Piece> pieces)
    {
        if (pieces.Count < 3) return pieces;
        var keys = pieces.Select(p => Convert.ToHexString(p.Bytes.ToArray())).ToList();
        // the forever loop's own piece and the last piece (FC 00 / FE) stay where they are
        bool Fixed(int i) => i == pieces.Count - 1 || keys[i] == "FBFD";
        var outp = new List<Piece>();
        for (int i = 0; i < pieces.Count;)
        {
            int bestL = 0, bestN = 0, bestSave = 0;
            for (int len = 1; len <= 16 && i + 2 * len <= pieces.Count; len++)
            {
                if (Enumerable.Range(i, len).Any(Fixed)) break;
                int n = 1;
                while (n < 255 && i + (n + 1) * len <= pieces.Count &&
                       Enumerable.Range(0, len).All(k => !Fixed(i + n * len + k) && keys[i + n * len + k] == keys[i + k])) n++;
                int save = (n - 1) * len * 2 - 9;   // order entries saved, less the two loop pieces and their entries
                if (n >= 2 && save > bestSave) { bestL = len; bestN = n; bestSave = save; }
            }
            if (bestN == 0) { outp.Add(pieces[i]); i++; continue; }
            outp.Add(new Piece(new List<byte> { 0xFB, 0xFD }, new List<int>()));
            outp.AddRange(pieces.Skip(i).Take(bestL));
            outp.Add(new Piece(new List<byte> { 0xFC, (byte)bestN, 0xFD }, new List<int>()));
            i += bestL * bestN;
        }
        return outp;
    }

    /// <summary>The image from $4400: header (order offsets filled in), order lists, phrases.</summary>
    private static List<byte> Layout(List<Piece>[] plan)
    {
        var img = new List<byte>(new byte[HeaderSize]);
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
        int phraseBase = Base + HeaderSize + orderBytes;
        var orders = new List<byte>();
        for (int ch = 0; ch < 8; ch++)
        {
            int o = HeaderSize + orders.Count;   // offset from $4400
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
                // the driver adds $44 to the high byte
                orders.Add((byte)((at >> 8) - 0x44)); orders.Add((byte)at);
            }
            orders.Add(0xFF);
        }
        img.AddRange(orders);
        img.AddRange(store);
        return img;
    }

    /// <summary>A channel's phrases (see <see cref="SongCodec"/>'s encoder; waits are F3, notes 70-7F always in full).
    /// Grid -1: a phrase ends at each phrase-end marker and nowhere else.</summary>
    private static List<Piece> EncodeChannel(List<SongCodec.Event> events, int grid)
    {
        var ordered = events.Select((e, i) => (e, i)).OrderBy(x => x.e.Tick).ThenBy(x => x.i).Select(x => x.e).ToList();
        var loopEnd = ordered.LastOrDefault(e => e.Kind == SongCodec.Kind.LoopEnd);
        var stop = ordered.FirstOrDefault(e => e.Kind == SongCodec.Kind.End);
        if (stop != null) { loopEnd = null; ordered = ordered.Where(e => e.Tick <= stop.Tick && (e.Kind != SongCodec.Kind.End || e == stop)).ToList(); }
        int loopStartIndex = -1;
        if (loopEnd != null)
        {
            int endAt = ordered.IndexOf(loopEnd);
            loopStartIndex = ordered.FindLastIndex(endAt, e => e.Kind == SongCodec.Kind.LoopStart && e.A == loopEnd.A);
        }
        var ev = new List<SongCodec.Event>();
        int fbBefore = 0, loopTick = 0;
        bool fbAtEnd = false;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i == loopStartIndex) { fbBefore = ev.Count; loopTick = ordered[i].Tick; }
            if (ordered[i].Kind is not (SongCodec.Kind.LoopStart or SongCodec.Kind.LoopEnd or SongCodec.Kind.End)) ev.Add(ordered[i]);
        }
        if (loopStartIndex >= 0 && fbBefore >= ev.Count) fbAtEnd = true;   // nothing inside the loop
        if (loopStartIndex < 0) { fbBefore = 0; loopTick = 0; }
        bool loops = stop == null;
        int end = stop?.Tick ?? loopEnd?.Tick ?? (ev.Count == 0 ? 0 : ev[^1].Tick + (ev[^1].Kind == SongCodec.Kind.Note ? Math.Max(1, ev[^1].B) : 1));
        if (fbAtEnd && end <= loopTick) { fbBefore = 0; loopTick = 0; fbAtEnd = false; }   // an empty loop would hang the driver

        var phrases = new List<Piece>();
        var o = new List<byte>();
        var notes = new List<int>();
        int time = 0, step = -1;
        int lastDelay = -1, lastGate = -1, lastVel = -1;
        void Wait(int n) { for (; n > 0; n -= Math.Min(n, 255)) o.AddRange(new byte[] { 0xF3, (byte)Math.Min(n, 255) }); }
        void Cut() { if (o.Count > 0) { o.Add(0xFD); phrases.Add(new Piece(o, notes)); o = new List<byte>(); notes = new List<int>(); } lastDelay = -1; }
        void LoopStart()
        {
            if (grid > 0) Cut();
            Wait(loopTick - time); time = Math.Max(time, loopTick);
            if (grid > 0) Cut();
            o.Add(0xFB);
            if (grid > 0) Cut();
            lastDelay = -1;   // the pass after a loop-back starts with whatever note ended the last one
        }
        for (int i = 0; i < ev.Count; i++)
        {
            if (loops && !fbAtEnd && i == fbBefore) LoopStart();
            var e = ev[i];
            if (grid > 0 && e.Tick / grid != step) { step = e.Tick / grid; Cut(); }
            Wait(e.Tick - time); time = Math.Max(time, e.Tick);
            int next = loops && !fbAtEnd && i + 1 == fbBefore ? loopTick : i + 1 < ev.Count ? ev[i + 1].Tick : fbAtEnd && loops ? loopTick : end;
            int d = Math.Clamp(next - e.Tick, 0, 255);
            switch (e.Kind)
            {
                case SongCodec.Kind.Note:
                {
                    int gate = Math.Clamp(e.B, 0, 255), vel = Math.Clamp(e.C, 0, 255), n = e.A & 0x7F;
                    notes.Add(o.Count);
                    if (n < 0x70 && d == lastDelay && gate == lastGate && vel == lastVel) o.Add((byte)(0x80 | n));
                    else { o.AddRange(new[] { (byte)n, (byte)d, (byte)gate, (byte)vel }); lastDelay = d; lastGate = gate; lastVel = vel; }
                    break;
                }
                case SongCodec.Kind.Volume: o.AddRange(new byte[] { 0xF1, (byte)d, (byte)Math.Clamp(e.A, 0, 255) }); break;
                case SongCodec.Kind.Pan: o.AddRange(new byte[] { 0xF2, (byte)d, (byte)Math.Clamp(e.A, 0, 255) }); break;
                case SongCodec.Kind.Program: o.AddRange(new byte[] { 0xF7, (byte)d, (byte)e.A }); break;
                case SongCodec.Kind.Raw:
                {
                    var r = e.Raw;
                    if (IsPhraseEnd(e)) { if (grid < 0) Cut(); d = 0; break; }
                    if (r is { Length: 3 } && r[0] is 0xF0 or 0xF4 or 0xF6) o.AddRange(new[] { r[0], (byte)d, r[2] });
                    else if (r is { Length: 3 } && r[0] == 0xFF) { o.AddRange(r); d = 0; }   // no wait of its own
                    else d = 0;                                                             // not a Lufia II command
                    break;
                }
                default: d = 0; break;
            }
            time += d;
        }
        if (loops && fbAtEnd) LoopStart();
        Wait(end - time);
        o.AddRange(loops ? new byte[] { 0xFC, 0x00, 0xFD } : new byte[] { 0xFE });
        phrases.Add(new Piece(o, notes));
        return phrases;
    }

    // ── MIDI import ──

    /// <summary>
    /// A song file from a MIDI file for the place of <paramref name="current"/> (the song file it replaces): keeps its
    /// header (name, sample list, +18, +19) and channel voices; the tempo comes from the MIDI when it has one. Commands
    /// travel as "lufia:" texts like the MIDI export writes them. Notes in <paramref name="warnings"/> what may not
    /// sound as meant.
    /// </summary>
    public static byte[] FromMidi(byte[] midi, byte[] current, out List<string> warnings)
    {
        var song = SongCodec.FromMidi(midi, out warnings);
        if (!song.Channels.Any(c => c.Any(e => e.Kind == SongCodec.Kind.Note))) throw new System.IO.InvalidDataException("the MIDI has no notes");
        var header = new byte[HeaderSize];
        Array.Fill(header, (byte)0xFF);
        Array.Copy(current, 2, header, 0, Math.Min(HeaderSize, current.Length - 2));
        if (!HasTempo(midi)) song.Tempo = header[3];

        // commands from a Lufia 1 export (F6 with 4 bytes, ...) mean something else here
        int dropped = 0;
        foreach (var c in song.Channels)
            dropped += c.RemoveAll(e => e.Kind == SongCodec.Kind.Raw && !(e.Raw is { Length: 3 } r && r[0] is 0xF0 or 0xF4 or 0xF6 or 0xFF) && !IsPhraseEnd(e));
        if (dropped > 0) warnings.Add($"{dropped} \"lufia:\" commands aren't Lufia II commands and were left out.");
        // MIDI puts the loop start first within its tick; a phrase end at that tick closed the phrase before the
        // loop (inside it the driver would count it again on every pass)
        foreach (var c in song.Channels)
        {
            int ls = c.FindIndex(e => e.Kind == SongCodec.Kind.LoopStart);
            if (ls < 0) continue;
            var moved = c.Skip(ls + 1).TakeWhile(e => e.Tick == c[ls].Tick).Where(IsPhraseEnd).ToList();
            foreach (var e in moved) { c.Remove(e); c.Insert(ls, e); }
        }

        var list = SampleList(current);
        int own = list.TakeWhile(x => x < 0x80).Count();
        bool replaced = header[0x18] != 0;
        var template = Decode(current);
        for (int ch = 0; ch < 8; ch++)
        {
            var ev = song.Channels[ch];
            if (!ev.Any(e => e.Kind == SongCodec.Kind.Note)) continue;
            // a channel that never picks a sample plays the one the song's channel started with
            int first = ev.Where(e => e.Kind == SongCodec.Kind.Note).Min(e => e.Tick);
            if (!song.Drum[ch] && !ev.Any(e => e.Kind == SongCodec.Kind.Program && e.Tick <= first))
            {
                var p = template.Channels[ch].FirstOrDefault(e => e.Kind == SongCodec.Kind.Program)
                        ?? template.Channels.SelectMany(c => c).FirstOrDefault(e => e.Kind == SongCodec.Kind.Program);
                if (p != null) ev.Insert(0, new SongCodec.Event(0, SongCodec.Kind.Program, p.A));
            }
        }
        // drum notes: the driver knows these (its list at $3C10); a song whose samples replace the shared ones loads
        // the drum samples it lists
        var drumNotes = new HashSet<int> { 0x24, 0x26, 0x2A, 0x2E, 0x30, 0x2D, 0x29, 0x31, 0x28 };
        var used = song.Channels.Where((c, ch) => song.Drum[ch]).SelectMany(c => c).Where(e => e.Kind == SongCodec.Kind.Note && e.B > 0).Select(e => e.A).Distinct().ToList();
        var unknown = used.Where(n => !drumNotes.Contains(n)).ToList();
        if (unknown.Count > 0) warnings.Add($"Drum notes {string.Join(" ", unknown.Select(n => n.ToString("X2")))} aren't in the driver's drum list (24 26 28 29 2A 2D 2E 30 31).");
        if (replaced)
        {
            var have = list.Where(x => x >= 0x80).Select(x => x & 0x7F).ToHashSet();
            foreach (int n in used.Where(n => drumNotes.Contains(n) && !have.Contains(n)).OrderBy(n => n))
            {
                if (list.Count < 0x20) { list.Add(0x80 | n); have.Add(n); }
                else warnings.Add($"Drum note {n:X2} can't be added to the sample list (32 entries).");
            }
            for (int i = 0; i < 0x20; i++) header[0x30 + i] = (byte)(i < list.Count ? list[i] : 0xFF);
        }
        var programs = song.Channels.SelectMany(c => c).Where(e => e.Kind == SongCodec.Kind.Program).Select(e => e.A).Distinct().OrderBy(a => a).ToList();
        var empty = programs.Where(a => a >= 0x20 + own || replaced && a < 0x20 && !SharedSurvives(list, a)).ToList();
        if (empty.Count > 0)
            warnings.Add($"Sample slots {string.Join(" ", empty.Select(a => a.ToString("X2")))} aren't loaded for this song (shared 00-1F, then its own from 20: {own} of them); those notes play with whatever is there.");

        return Encode(song, header);
    }

    /// <summary>
    /// Whether shared slot <paramref name="slot"/> holds a sample in a song whose samples replace the shared ones (+18):
    /// only the drum samples its list names are sent again, each into the slot of its number ($80:94AA, lists $80:9A34 /
    /// $80:9A59).
    /// </summary>
    private static bool SharedSurvives(List<int> list, int slot)
    {
        int[] notes = { 0x24, 0x26, 0x2A, 0x2E, 0x30, 0x2D, 0x29, 0x31, 0x28 }, samples = { 0, 1, 3, 4, 5, 5, 5, 6, 2 };
        return list.Where(x => x >= 0x80 && x != 0xFF).Any(x => Array.IndexOf(notes, x & 0x7F) is int k && k >= 0 && samples[k] == slot);
    }

    private static bool HasTempo(byte[] midi)
    {
        for (int i = 0; i + 2 < midi.Length; i++) if (midi[i] == 0xFF && midi[i + 1] == 0x51 && midi[i + 2] == 0x03) return true;
        return false;
    }
}
