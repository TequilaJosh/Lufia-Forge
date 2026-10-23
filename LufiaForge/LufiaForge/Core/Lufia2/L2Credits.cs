using System.Text;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II staff roll (US ROM, read from the renderer at $8E:B448, 2026-10-23): $91:9AFF (file 0x89AFF), read in
/// order up to 00: 05 text 00 = a heading, 01 text 00 = a name (centred), 02 n = scroll n blank lines, 03 u16 = a
/// picture tile (two rows: tile and tile + 0x40), 04 = a screen effect ($0583 = 80). The text is ASCII in the 8x16
/// credits font. The block may use the room up to 0x8A1D4, where the actor animation scripts start.
/// </summary>
public static class L2Credits
{
    public const int Start = 0x89AFF, End = 0x8A1D4;

    /// <summary>The credits as editable text: "heading: ...", "name: ...", "gap: n", "picture: XXXX", "effect".</summary>
    public static string Read(RomBuffer rom)
    {
        var sb = new StringBuilder();
        int p = Start;
        while (p < End)
        {
            int c = rom.ReadByte(p++);
            if (c == 0) break;
            switch (c)
            {
                case 0x05: sb.Append("heading: ").AppendLine(Text(rom, ref p)); break;
                case 0x01: sb.Append("name: ").AppendLine(Text(rom, ref p)); break;
                case 0x03: sb.AppendLine($"picture: {rom.ReadUInt16Le(p):X4}"); p += 2; break;
                case 0x04: sb.AppendLine("effect"); break;
                default: sb.AppendLine(c == 0x02 ? $"gap: {rom.ReadByte(p++)}" : $"gap: {rom.ReadByte(p++)} (code {c:X2})"); break;
            }
        }
        return sb.ToString();
    }

    private static string Text(RomBuffer rom, ref int p)
    {
        var b = rom.ReadUntil(p, 0, 64);
        p += b.Length + 1;
        return Encoding.ASCII.GetString(b);
    }

    /// <summary>Encodes the text form; null and an error when a line can't be read.</summary>
    public static byte[]? Encode(string text, out string error)
    {
        error = "";
        var o = new List<byte>();
        int n = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            int colon = line.IndexOf(':');
            string key = (colon < 0 ? line : line[..colon]).Trim().ToLowerInvariant();
            // the text after "name: " is kept as it is (some names are padded with spaces)
            string val = colon < 0 ? "" : line[(colon + 1)..];
            if (val.StartsWith(' ')) val = val[1..];
            if (key is not ("heading" or "name")) val = val.Trim();
            switch (key)
            {
                case "heading" or "name":
                    if (val.Any(ch => ch < 0x20 || ch > 0x7E)) { error = $"Line {n}: only plain letters, digits and punctuation can be shown."; return null; }
                    o.Add((byte)(key == "heading" ? 0x05 : 0x01)); o.AddRange(Encoding.ASCII.GetBytes(val)); o.Add(0);
                    break;
                case "gap":
                    if (!int.TryParse(val.Split(' ')[0], out int g) || g < 0 || g > 255) { error = $"Line {n}: a gap is a number of lines, 0 to 255."; return null; }
                    o.Add(0x02); o.Add((byte)g);
                    break;
                case "picture":
                    try { int t = Convert.ToInt32(val, 16); o.Add(0x03); o.Add((byte)t); o.Add((byte)(t >> 8)); }
                    catch (Exception) { error = $"Line {n}: a picture is a tile number in hex."; return null; }
                    break;
                case "effect": o.Add(0x04); break;
                default: error = $"Line {n}: start it with heading:, name:, gap:, picture: or effect."; return null;
            }
        }
        o.Add(0);
        return o.ToArray();
    }

    /// <summary>Writes the credits; returns null, or why not.</summary>
    public static string? Write(RomBuffer rom, string text)
    {
        var bytes = Encode(text, out string error);
        if (bytes == null) return error;
        if (bytes.Length > End - Start) return $"{bytes.Length - (End - Start)} bytes too long (the credits have {End - Start} bytes of room).";
        rom.WriteBytes(Start, bytes);
        for (int p = Start + bytes.Length; p < End; p++) rom.WriteByte(p, 0);
        return null;
    }

    public static int Used(RomBuffer rom) => (Encode(Read(rom), out _)?.Length) ?? 0;
}
