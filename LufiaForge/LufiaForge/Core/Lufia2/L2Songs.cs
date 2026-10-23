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
    /// F4, pitch multiplier F6, effects FF) become Raw events with their wait byte set to 0.
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
            DecodeChannel(m, order & 0xFFFF, song.Channels[ch], maxTicks);
        }
        return song;
    }

    private static void DecodeChannel(byte[] m, int order, List<SongCodec.Event> ev, int maxTicks)
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
}
