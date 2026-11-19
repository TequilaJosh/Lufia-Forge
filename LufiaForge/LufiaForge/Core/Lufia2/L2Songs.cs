using LufiaForge.Core.Audio;

namespace LufiaForge.Core.Lufia2;

// lufia 2's songs: packed files 00-3E, each a 2-byte length and the image the snes sends to sound RAM $4400
// ($80:941A). same sequence format as lufia 1 (SongCodec) with these differences, read from the driver:
//   header  "S2C", tempo, name +04-+0F, channel table +10-+17, +18 non-zero = the song's samples replace the shared
//           ones, +19 echo setting, order list offsets from $4400 at +20 (u16), sample list +30-+4F (FF ends, ids
//           from 80 are drum notes)
//   order   hh ll = phrase at ($44 + hh) << 8 | ll, 80+t = transpose t, FF = channel stops
//   phrase  F0 d x  vibrato depth x, wait d     F3 d    wait d (2 bytes)       F4 d t  tempo t
//           F6 d m  pitch multiplier m/128       FE      this channel ends      FC 01   does nothing
// loops ($37D2 / $3813): FB takes the outer slot if it's free, otherwise the inner one if that's free. FC n uses the
// inner slot while it's taken, and frees the slot it used when its count runs out
public static class L2Songs
{
    public const int Count = L2Sound.SongCount;
    public const int Base = L2Sound.SongAddress;
    // room for a song between $4400 and the samples at $5800
    public const int MaxSize = 0x1400;

    // the unpacked file (2-byte length, then the image)
    public static byte[] Read(RomBuffer rom, int song) => L2Rom.ReadFile(rom, song);

    // the name in the song's header (+04-+0F), like "FIELD"
    public static string Name(byte[] file)
    {
        if (file.Length < 2 + 0x10) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 4; i < 0x10; i++) { byte letter = file[2 + i]; sb.Append(letter is >= 0x20 and < 0x7F ? (char)letter : ' '); }
        return sb.ToString().Trim();
    }

    public static string Name(RomBuffer rom, int song)
    {
        try { return Name(Read(rom, song)); } catch { return ""; }
    }

    // the song's own sample list (+30, FF ends): ids below 80 are samples, 80 and up are drum notes
    public static List<int> SampleList(byte[] file)
    {
        var list = new List<int>();
        for (int i = 0x30; i < 0x50 && 2 + i < file.Length; i++)
        {
            int entry = file[2 + i];
            if (entry == 0xFF) break;
            list.Add(entry);
        }
        return list;
    }

    // the song as SongCodec events (one pass per channel, a loop that repeats forever ends in LoopEnd), so
    // SongCodec.ToMidi can write it. commands MIDI has no word for (vibrato F0, tempo F4, pitch multiplier F6,
    // effects FF) become Raw events with their wait byte set to 0. channel 0's phrase ends are Raw FD 00 markers
    // (PhraseEnd)
    public static SongCodec.Song Decode(byte[] file, int maxTicks = 48 * 4 * 400)
    {
        var memory = new byte[0x10000];
        Array.Copy(file, 2, memory, Base, Math.Min(file.Length - 2, 0x10000 - Base));
        var song = new SongCodec.Song { Tempo = memory[Base + 3] };
        Array.Copy(memory, Base, song.Header, 0, 0x40);
        for (int ch = 0; ch < 8; ch++)
        {
            int voice = memory[Base + 0x10 + ch];
            if (voice == 0xFF) continue;
            song.Drum[ch] = (voice & 0x80) != 0;
            int order = Base + (memory[Base + 0x20 + ch * 2] | memory[Base + 0x21 + ch * 2] << 8);
            if (order >= 0xFFFF) continue;
            DecodeChannel(memory, order & 0xFFFF, song.Channels[ch], maxTicks, ch == 0);
            KeepForeverLoop(song.Channels[ch]);
        }
        return song;
    }

    // marker for a phrase end on channel 0 (exported as the MIDI text "lufia: FD00")
    public static byte[] PhraseEnd => new byte[] { 0xFD, 0x00 };
    private static bool IsPhraseEnd(SongCodec.Event e) => e.Kind == SongCodec.Kind.Raw && e.Raw is [0xFD, 0x00];

    // loop markers say where the channel repeats forever: only the start of the loop that the LoopEnd closes stays
    // (finite loops get played out in the events), both as slot 0. the music is the same, it just makes the events
    // not depend on how the loops were nested, so encoding them and decoding again gives the same list
    private static void KeepForeverLoop(List<SongCodec.Event> events)
    {
        int end = events.FindLastIndex(e => e.Kind == SongCodec.Kind.LoopEnd);
        int start = end < 0 ? -1 : events.FindLastIndex(end, e => e.Kind == SongCodec.Kind.LoopStart && e.A == events[end].A);
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Kind == SongCodec.Kind.LoopStart && i != start) events.RemoveAt(i);
            else if (events[i].Kind is SongCodec.Kind.LoopStart or SongCodec.Kind.LoopEnd) events[i] = events[i] with { A = 0 };
        }
    }

    private static void DecodeChannel(byte[] memory, int order, List<SongCodec.Event> events, int maxTicks, bool markPhrases)
    {
        int tick = 0, transpose = 0, p;
        int lastDelay = 0, lastGate = 0, lastVel = 0;
        // loop slots 0 = outer, 1 = inner: taken, order entry, phrase position, counter (FE = new, FF = forever), transpose
        var taken = new bool[2]; var loopOrder = new int[2]; var loopPos = new int[2]; var loopCount = new int[2]; var loopTranspose = new int[2];

        // the order entry at 'order' (transpose bytes before it get taken), -1 at FF
        int Phrase()
        {
            while (true)
            {
                if (order + 1 >= memory.Length) return -1;
                int entry = memory[order];
                if (entry == 0xFF) return -1;
                if ((entry & 0x80) != 0) { transpose = entry & 0x7F; order++; continue; }
                return ((entry + 0x44) << 8 | memory[order + 1]) & 0xFFFF;
            }
        }
        p = Phrase();
        if (p < 0) return;
        for (int guard = 0; tick < maxTicks && guard < 200000; guard++)
        {
            if (p + 3 >= memory.Length) { events.Add(new(tick, SongCodec.Kind.End)); return; }
            int cmd = memory[p];
            if (cmd < 0x80)
            {
                lastDelay = memory[p + 1]; lastGate = memory[p + 2]; lastVel = memory[p + 3];
                events.Add(new(tick, SongCodec.Kind.Note, cmd + transpose & 0x7F, lastGate, lastVel));
                tick += lastDelay; p += 4;
                continue;
            }
            if (cmd < 0xF0)
            {
                events.Add(new(tick, SongCodec.Kind.Note, (cmd & 0x7F) + transpose & 0x7F, lastGate, lastVel));
                tick += lastDelay; p += 1;
                continue;
            }
            switch (cmd)
            {
                case 0xF0: events.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xF0, 0, memory[p + 2] })); tick += memory[p + 1]; p += 3; break;
                case 0xF1: events.Add(new(tick, SongCodec.Kind.Volume, memory[p + 2])); tick += memory[p + 1]; p += 3; break;
                case 0xF2: events.Add(new(tick, SongCodec.Kind.Pan, memory[p + 2])); tick += memory[p + 1]; p += 3; break;
                case 0xF3: tick += memory[p + 1]; p += 2; break;
                case 0xF4: events.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xF4, 0, memory[p + 2] })); tick += memory[p + 1]; p += 3; break;
                case 0xF6: events.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xF6, 0, memory[p + 2] })); tick += memory[p + 1]; p += 3; break;
                case 0xF7: events.Add(new(tick, SongCodec.Kind.Program, memory[p + 2])); tick += memory[p + 1]; p += 3; break;
                case 0xFB:
                {
                    int slot = !taken[0] ? 0 : !taken[1] ? 1 : -1;
                    if (slot >= 0)
                    {
                        taken[slot] = true; loopOrder[slot] = order; loopPos[slot] = p + 1; loopCount[slot] = 0xFE; loopTranspose[slot] = transpose;
                        events.Add(new(tick, SongCodec.Kind.LoopStart, slot));
                    }
                    p += 1;
                    break;
                }
                case 0xFC:
                {
                    int count = memory[p + 1] - 1 & 0xFF;
                    if (count == 0) { p += 2; break; }   // FC 01
                    int slot = taken[1] ? 1 : 0;
                    bool back;
                    if (loopCount[slot] == 0xFF) back = true;
                    else if (loopCount[slot] == 0xFE) { loopCount[slot] = count; back = true; }
                    else
                    {
                        loopCount[slot] = loopCount[slot] - 1 & 0xFF;
                        back = loopCount[slot] != 0;
                        if (!back) taken[slot] = false;
                    }
                    if (back && loopCount[slot] == 0xFF)
                    {
                        // forever: one pass ends here
                        events.Add(new(tick, SongCodec.Kind.LoopEnd, slot));
                        return;
                    }
                    if (back) { order = loopOrder[slot]; p = loopPos[slot]; transpose = loopTranspose[slot]; }
                    else p += 2;
                    break;
                }
                case 0xFD:
                    // channel 0's phrase ends are the song's sync points (driver $386C counts them in $8D, command 08
                    // reports it, event 4D waits for it). kept as markers so a re-encoded song ends phrases there too
                    if (markPhrases) events.Add(new(tick, SongCodec.Kind.Raw, Raw: PhraseEnd));
                    transpose = 0; order += 2;
                    p = Phrase();
                    if (p < 0) { events.Add(new(tick, SongCodec.Kind.End)); return; }
                    break;
                case 0xFE: events.Add(new(tick, SongCodec.Kind.End)); return;
                case 0xFF: events.Add(new(tick, SongCodec.Kind.Raw, Raw: new byte[] { 0xFF, memory[p + 1], memory[p + 2] })); p += 3; break;
                default: p += 1; break;   // F5, F8-FA
            }
        }
    }

    // the game plays note n an octave up. the samples' pitch multipliers are their loop length / 16 (one cycle per
    // loop), so note n sounds as MIDI note n + 12 (measured 2026-11-01 on every looping sample: the driver's pitch
    // table at $4366, 0x217D-0x3F38, against each sample's period)
    public const int MidiTranspose = 12;

    // a General MIDI program like each sample (00-38), so an exported song sounds close to the game in a MIDI player.
    // guessed from the samples' overtones, range and the songs that use them (2026-11-01), -1 = no guess (piano).
    // samples 00-06 are the drum kit, 07-0C, 17, 18, 1A, 1B and 25 are empty
    private static readonly int[] GmPrograms =
    {
        116, 118, 117, 115, 0, 47, 80, -1, -1, -1, -1, -1, -1, 0,      // 00-0D: drum kit (taiko, synth drum...), 06 square lead
        80, 73, 73, 68, 33, 46, 80, 56, 43, -1, -1, 56, -1, -1, 61, 11, 48, 52,   // 0E-1F
        115, 75, 21, 4, 40, -1, 71, 77, 0, 43, 10, 46, 46, 73, 34, 24, 55, 47, 60, 9, 42, 14, 73, 89, 72,   // 20-38
    };

    // the General MIDI program for that sample
    public static int GmProgram(int sample) => sample >= 0 && sample < GmPrograms.Length && GmPrograms[sample] >= 0 ? GmPrograms[sample] : 0;

    // the song as a MIDI file (48 ticks per quarter note, one track per channel, drums on MIDI channel 10): the notes
    // where the game sounds them (MidiTranspose) and a General MIDI program like each instrument's sample (GmProgram,
    // slots 00-1F are samples 00-1F, slots 20+ the song's own list)
    public static byte[] ToMidi(byte[] file)
    {
        var own = SampleList(file).TakeWhile(x => x < 0x80).ToList();
        int Program(int slot) => GmProgram(slot < 0x20 ? slot : slot - 0x20 < own.Count ? own[slot - 0x20] : -1);
        return SongCodec.ToMidi(Decode(file), Name(file), MidiTranspose, Program, fullLevel: true);
    }

    // encode

    public const int HeaderSize = 0x50;

    // the song file (2-byte length, then the image for $4400) from per-channel events. header bytes come from header
    // (0x50 bytes: name, +18, +19, sample list...) with the song's tempo, and the channel table keeps each channel's
    // voice with the drum bit from SongCodec.Song.Drum. a channel loops forever (FB ... FC 00) or ends with FE, as one
    // phrase or cut into pieces, whichever is smaller. a piece that repeats (also at another pitch) is stored once and
    // the order list plays it again. throws if the image is bigger than the driver's room ($4400-$57FF) and checkSize is set
    public static byte[] Encode(SongCodec.Song song, byte[] header, bool checkSize = true)
    {
        var used = Enumerable.Range(0, 8).Select(ch => song.Channels[ch].Any(e => e.Kind is not (SongCodec.Kind.LoopStart or SongCodec.Kind.LoopEnd or SongCodec.Kind.End))).ToArray();
        var plan = new List<Piece>[8];
        int[] grids = { 0, 24, 32, 48, 64, 96, 128, 192, 256, 384, 576, 768 };
        var none = Enumerable.Range(0, 8).Select(_ => new List<Piece>()).ToArray();
        int Size(int ch, List<Piece> pieces) { var one = (List<Piece>[])none.Clone(); one[ch] = pieces; return Layout(one).Count; }
        for (int ch = 0; ch < 8; ch++)
            plan[ch] = !used[ch] ? new List<Piece>()
                : song.Channels[ch].Any(IsPhraseEnd) ? EncodeChannel(song.Channels[ch], -1)   // phrases end where the markers say
                : grids.Select(g => Repeats(EncodeChannel(song.Channels[ch], g))).OrderBy(p => Size(ch, p)).First();
        var image = Layout(plan);
        for (int i = 0; i < HeaderSize; i++) if (i is < 0x10 or >= 0x18 and < 0x20 or >= 0x30) image[i] = i < header.Length ? header[i] : (byte)0xFF;
        image[3] = (byte)Math.Clamp(song.Tempo, 1, 255);
        for (int ch = 0; ch < 8; ch++)
        {
            int was = ch < header.Length - 0x10 ? header[0x10 + ch] : 0xFF;
            image[0x10 + ch] = !used[ch] ? (byte)was : (byte)((was == 0xFF ? ch : was & 0x7F) | (song.Drum[ch] ? 0x80 : 0));
        }
        if (checkSize && image.Count > MaxSize)
            throw new InvalidOperationException($"The song needs {image.Count:N0} bytes; the sound driver has room for {MaxSize:N0} ($4400-$57FF).");
        var data = new byte[image.Count + 2];
        data[0] = (byte)image.Count; data[1] = (byte)(image.Count >> 8);
        image.CopyTo(data, 2);
        return data;
    }

    // a phrase's bytes and where its note numbers are (pieces that only differ by a transposition share one phrase)
    private sealed record Piece(List<byte> Bytes, List<int> Notes)
    {
        public int Low => Notes.Count == 0 ? 0 : Notes.Min(i => Bytes[i] & 0x7F);
        public string Shape()
        {
            var bytes = Bytes.ToArray(); int low = Low;
            foreach (int i in Notes) bytes[i] = (byte)(bytes[i] & 0x80 | (bytes[i] & 0x7F) - low);
            return Convert.ToHexString(bytes);
        }
    }

    // pieces that follow each other again and again (a bar played 8 times, ...) become a finite loop: a piece "FB FD",
    // the body once, then "FC n FD". the loop-back lands on the FD after FB and the order list carries on with the body.
    // a loop before the forever loop's start takes the outer slot and frees it, one inside takes the inner slot
    // ($37D2 / $3813). not used where phrase ends are kept (channel 0's sync points), the two extra pieces would add
    // phrase ends
    private static List<Piece> Repeats(List<Piece> pieces)
    {
        if (pieces.Count < 3) return pieces;
        var keys = pieces.Select(p => Convert.ToHexString(p.Bytes.ToArray())).ToList();
        // the forever loop's own piece and the last piece (FC 00 / FE) stay where they are
        bool Fixed(int i) => i == pieces.Count - 1 || keys[i] == "FBFD";
        var output = new List<Piece>();
        for (int i = 0; i < pieces.Count;)
        {
            int bestLength = 0, bestTimes = 0, bestSave = 0;
            for (int length = 1; length <= 16 && i + 2 * length <= pieces.Count; length++)
            {
                if (Enumerable.Range(i, length).Any(Fixed)) break;
                int times = 1;
                // 254 at most: FC FF counts down from FE, the loop's own "not counting yet" mark, so it would never end
                while (times < 254 && i + (times + 1) * length <= pieces.Count &&
                       Enumerable.Range(0, length).All(k => !Fixed(i + times * length + k) && keys[i + times * length + k] == keys[i + k])) times++;
                int save = (times - 1) * length * 2 - 9;   // order entries saved, minus the two loop pieces and their entries
                if (times >= 2 && save > bestSave) { bestLength = length; bestTimes = times; bestSave = save; }
            }
            if (bestTimes == 0) { output.Add(pieces[i]); i++; continue; }
            output.Add(new Piece(new List<byte> { 0xFB, 0xFD }, new List<int>()));
            output.AddRange(pieces.Skip(i).Take(bestLength));
            output.Add(new Piece(new List<byte> { 0xFC, (byte)bestTimes, 0xFD }, new List<int>()));
            i += bestLength * bestTimes;
        }
        return output;
    }

    // the image from $4400: header (order offsets filled in), order lists, phrases
    private static List<byte> Layout(List<Piece>[] plan)
    {
        var image = new List<byte>(new byte[HeaderSize]);
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
            int orderOffset = HeaderSize + orders.Count;   // offset from $4400
            image[0x20 + ch * 2] = (byte)orderOffset; image[0x21 + ch * 2] = (byte)(orderOffset >> 8);
            for (int k = 0; k < plan[ch].Count; k++)
            {
                var piece = plan[ch][k]; string key = shapes[ch][k]; int low = lowest[key];
                if (!where.TryGetValue(key, out int at))
                {
                    at = phraseBase + store.Count; where[key] = at;
                    var bytes = piece.Bytes.ToArray(); int shift = piece.Low - low;
                    foreach (int i in piece.Notes) bytes[i] = (byte)(bytes[i] & 0x80 | (bytes[i] & 0x7F) - shift);
                    store.AddRange(bytes);
                }
                if (piece.Low > low) orders.Add((byte)(0x80 | piece.Low - low));
                // the driver adds $44 to the high byte
                orders.Add((byte)((at >> 8) - 0x44)); orders.Add((byte)at);
            }
            orders.Add(0xFF);
        }
        image.AddRange(orders);
        image.AddRange(store);
        return image;
    }

    // a channel's phrases (see SongCodec's encoder, waits are F3, notes 70-7F always in full).
    // grid -1 = a phrase ends at each phrase end marker and nowhere else
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
        var notesAndCommands = new List<SongCodec.Event>();
        int fbBefore = 0, loopTick = 0;
        bool fbAtEnd = false;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i == loopStartIndex) { fbBefore = notesAndCommands.Count; loopTick = ordered[i].Tick; }
            if (ordered[i].Kind is not (SongCodec.Kind.LoopStart or SongCodec.Kind.LoopEnd or SongCodec.Kind.End)) notesAndCommands.Add(ordered[i]);
        }
        if (loopStartIndex >= 0 && fbBefore >= notesAndCommands.Count) fbAtEnd = true;   // nothing inside the loop
        if (loopStartIndex < 0) { fbBefore = 0; loopTick = 0; }
        bool loops = stop == null;
        int end = stop?.Tick ?? loopEnd?.Tick ?? (notesAndCommands.Count == 0 ? 0 : notesAndCommands[^1].Tick + (notesAndCommands[^1].Kind == SongCodec.Kind.Note ? Math.Max(1, notesAndCommands[^1].B) : 1));
        if (fbAtEnd && end <= loopTick) { fbBefore = 0; loopTick = 0; fbAtEnd = false; }   // an empty loop would hang the driver

        var phrases = new List<Piece>();
        var bytes = new List<byte>();
        var notes = new List<int>();
        int time = 0, step = -1;
        int lastDelay = -1, lastGate = -1, lastVel = -1;
        void Wait(int frames) { for (; frames > 0; frames -= Math.Min(frames, 255)) bytes.AddRange(new byte[] { 0xF3, (byte)Math.Min(frames, 255) }); }
        void Cut() { if (bytes.Count > 0) { bytes.Add(0xFD); phrases.Add(new Piece(bytes, notes)); bytes = new List<byte>(); notes = new List<int>(); } lastDelay = -1; }
        void LoopStart()
        {
            if (grid > 0) Cut();
            Wait(loopTick - time); time = Math.Max(time, loopTick);
            if (grid > 0) Cut();
            bytes.Add(0xFB);
            if (grid > 0) Cut();
            lastDelay = -1;   // the pass after a loop-back starts with whatever note ended the last one
        }
        for (int i = 0; i < notesAndCommands.Count; i++)
        {
            if (loops && !fbAtEnd && i == fbBefore) LoopStart();
            var e = notesAndCommands[i];
            if (grid > 0 && e.Tick / grid != step) { step = e.Tick / grid; Cut(); }
            Wait(e.Tick - time); time = Math.Max(time, e.Tick);
            int next = loops && !fbAtEnd && i + 1 == fbBefore ? loopTick : i + 1 < notesAndCommands.Count ? notesAndCommands[i + 1].Tick : fbAtEnd && loops ? loopTick : end;
            int delay = Math.Clamp(next - e.Tick, 0, 255);
            switch (e.Kind)
            {
                case SongCodec.Kind.Note:
                {
                    int gate = Math.Clamp(e.B, 0, 255), vel = Math.Clamp(e.C, 0, 255), note = e.A & 0x7F;
                    notes.Add(bytes.Count);
                    if (note < 0x70 && delay == lastDelay && gate == lastGate && vel == lastVel) bytes.Add((byte)(0x80 | note));
                    else { bytes.AddRange(new[] { (byte)note, (byte)delay, (byte)gate, (byte)vel }); lastDelay = delay; lastGate = gate; lastVel = vel; }
                    break;
                }
                case SongCodec.Kind.Volume: bytes.AddRange(new byte[] { 0xF1, (byte)delay, (byte)Math.Clamp(e.A, 0, 255) }); break;
                case SongCodec.Kind.Pan: bytes.AddRange(new byte[] { 0xF2, (byte)delay, (byte)Math.Clamp(e.A, 0, 255) }); break;
                case SongCodec.Kind.Program: bytes.AddRange(new byte[] { 0xF7, (byte)delay, (byte)e.A }); break;
                case SongCodec.Kind.Raw:
                {
                    var raw = e.Raw;
                    if (IsPhraseEnd(e)) { if (grid < 0) Cut(); delay = 0; break; }
                    if (raw is { Length: 3 } && raw[0] is 0xF0 or 0xF4 or 0xF6) bytes.AddRange(new[] { raw[0], (byte)delay, raw[2] });
                    else if (raw is { Length: 3 } && raw[0] == 0xFF) { bytes.AddRange(raw); delay = 0; }   // no wait of its own
                    else delay = 0;                                                                 // not a lufia 2 command
                    break;
                }
                default: delay = 0; break;
            }
            time += delay;
        }
        if (loops && fbAtEnd) LoopStart();
        Wait(end - time);
        bytes.AddRange(loops ? new byte[] { 0xFC, 0x00, 0xFD } : new byte[] { 0xFE });
        phrases.Add(new Piece(bytes, notes));
        return phrases;
    }

    // MIDI import

    // a song file from a MIDI file to go in the place of current (the song file it replaces). keeps its header (name,
    // sample list, +18, +19) and channel voices, the tempo comes from the MIDI if it has one. commands travel as
    // "lufia:" texts the same way the MIDI export writes them. warnings gets notes on what may not sound as meant
    public static byte[] FromMidi(byte[] midi, byte[] current, out List<string> warnings)
    {
        var song = SongCodec.FromMidi(midi, out warnings, MidiTranspose);
        if (!song.Channels.Any(c => c.Any(e => e.Kind == SongCodec.Kind.Note))) throw new System.IO.InvalidDataException("the MIDI has no notes");
        if (!HasTempo(midi)) song.Tempo = current.Length > 5 ? current[2 + 3] : song.Tempo;
        return Build(song, current, warnings);
    }

    // the song file for song in the place of current (MIDI import, the note editor). keeps its header (name, sample
    // list, +18, +19) and channel voices with the song's tempo, leaves out commands that aren't lufia 2's, gives a
    // channel that never picks a sample the one it started with, adds drum samples a song with its own samples needs,
    // and puts notes in warnings on what may not sound as meant
    public static byte[] Build(SongCodec.Song song, byte[] current, List<string> warnings, bool checkSize = true)
    {
        var header = new byte[HeaderSize];
        Array.Fill(header, (byte)0xFF);
        Array.Copy(current, 2, header, 0, Math.Min(HeaderSize, current.Length - 2));

        // commands from a lufia 1 export (F6 with 4 bytes, ...) mean something else here
        int dropped = 0;
        foreach (var channel in song.Channels)
            dropped += channel.RemoveAll(e => e.Kind == SongCodec.Kind.Raw && !(e.Raw is { Length: 3 } r && r[0] is 0xF0 or 0xF4 or 0xF6 or 0xFF) && !IsPhraseEnd(e));
        if (dropped > 0) warnings.Add($"{dropped} \"lufia:\" commands aren't Lufia II commands and were left out.");
        // MIDI puts the loop start first within its tick. a phrase end at that tick closed the phrase before the loop
        // (inside it the driver would count it again on every pass)
        foreach (var channel in song.Channels)
        {
            int loopStart = channel.FindIndex(e => e.Kind == SongCodec.Kind.LoopStart);
            if (loopStart < 0) continue;
            var moved = channel.Skip(loopStart + 1).TakeWhile(e => e.Tick == channel[loopStart].Tick).Where(IsPhraseEnd).ToList();
            foreach (var e in moved) { channel.Remove(e); channel.Insert(loopStart, e); }
        }

        var list = SampleList(current);
        int own = list.TakeWhile(x => x < 0x80).Count();
        bool replaced = header[0x18] != 0;
        var template = Decode(current);
        for (int ch = 0; ch < 8; ch++)
        {
            var events = song.Channels[ch];
            if (!events.Any(e => e.Kind == SongCodec.Kind.Note)) continue;
            // a channel that never picks a sample plays the one the song's channel started with
            int first = events.Where(e => e.Kind == SongCodec.Kind.Note).Min(e => e.Tick);
            if (!song.Drum[ch] && !events.Any(e => e.Kind == SongCodec.Kind.Program && e.Tick <= first))
            {
                var program = template.Channels[ch].FirstOrDefault(e => e.Kind == SongCodec.Kind.Program)
                        ?? template.Channels.SelectMany(c => c).FirstOrDefault(e => e.Kind == SongCodec.Kind.Program);
                if (program != null) events.Insert(0, new SongCodec.Event(0, SongCodec.Kind.Program, program.A));
            }
        }
        // drum notes: the driver knows these (its list at $3C10). a song whose samples replace the shared ones loads
        // the drum samples it lists
        var drumNotes = new HashSet<int> { 0x24, 0x26, 0x2A, 0x2E, 0x30, 0x2D, 0x29, 0x31, 0x28 };
        var used = song.Channels.Where((c, ch) => song.Drum[ch]).SelectMany(c => c).Where(e => e.Kind == SongCodec.Kind.Note && e.B > 0).Select(e => e.A).Distinct().ToList();
        var unknown = used.Where(n => !drumNotes.Contains(n)).ToList();
        if (unknown.Count > 0) warnings.Add($"Drum notes {string.Join(" ", unknown.Select(n => n.ToString("X2")))} aren't in the driver's drum list (24 26 28 29 2A 2D 2E 30 31).");
        if (replaced)
        {
            var have = list.Where(x => x >= 0x80).Select(x => x & 0x7F).ToHashSet();
            foreach (int note in used.Where(n => drumNotes.Contains(n) && !have.Contains(n)).OrderBy(n => n))
            {
                if (list.Count < 0x20) { list.Add(0x80 | note); have.Add(note); }
                else warnings.Add($"Drum note {note:X2} can't be added to the sample list (32 entries).");
            }
            for (int i = 0; i < 0x20; i++) header[0x30 + i] = (byte)(i < list.Count ? list[i] : 0xFF);
        }
        var programs = song.Channels.SelectMany(c => c).Where(e => e.Kind == SongCodec.Kind.Program).Select(e => e.A).Distinct().OrderBy(a => a).ToList();
        var empty = programs.Where(a => a >= 0x20 + own || replaced && a < 0x20 && !SharedSurvives(list, a)).ToList();
        if (empty.Count > 0)
            warnings.Add($"Sample slots {string.Join(" ", empty.Select(a => a.ToString("X2")))} aren't loaded for this song (shared 00-1F, then its own from 20: {own} of them); those notes play with whatever is there.");

        return Encode(song, header, checkSize);
    }

    // true if shared slot slot still holds a sample in a song whose samples replace the shared ones (+18). only the
    // drum samples its list names get sent again, each into the slot of its number ($80:94AA, lists $80:9A34 / $80:9A59)
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
