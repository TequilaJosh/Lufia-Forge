using System.Text;

namespace LufiaForge.Core.Lufia2;

// lufia 2 staff roll (US rom, read from the renderer at $8E:B448, 2026-10-23): $91:9AFF (file 0x89AFF), read in
// order up to 00:
// - 05 text 00 = a heading
// - 01 text 00 = a name (centred)
// - 02 n = scroll n blank lines
// - 03 u16 = a picture tile (two rows: tile and tile + 0x40)
// - 04 = a screen effect ($0583 = 80)
// the text is ASCII in the 8x16 credits font. the block can use the room up to 0x8A1D4, where the actor animation
// scripts start
public static class L2Credits
{
    public const int Start = 0x89AFF, End = 0x8A1D4;

    // the credits as editable text: "heading: ...", "name: ...", "gap: n", "picture: XXXX", "effect"
    public static string Read(RomBuffer rom)
    {
        var sb = new StringBuilder();
        int position = Start;
        while (position < End)
        {
            int code = rom.ReadByte(position++);
            if (code == 0) break;
            switch (code)
            {
                case 0x05: sb.Append("heading: ").AppendLine(Text(rom, ref position)); break;
                case 0x01: sb.Append("name: ").AppendLine(Text(rom, ref position)); break;
                case 0x03: sb.AppendLine($"picture: {rom.ReadUInt16Le(position):X4}"); position += 2; break;
                case 0x04: sb.AppendLine("effect"); break;
                default: sb.AppendLine(code == 0x02 ? $"gap: {rom.ReadByte(position++)}" : $"gap: {rom.ReadByte(position++)} (code {code:X2})"); break;
            }
        }
        return sb.ToString();
    }

    private static string Text(RomBuffer rom, ref int position)
    {
        var bytes = rom.ReadUntil(position, 0, 64);
        position += bytes.Length + 1;
        return Encoding.ASCII.GetString(bytes);
    }

    // encodes the text form. null and an error if a line can't be read
    public static byte[]? Encode(string text, out string error)
    {
        error = "";
        var output = new List<byte>();
        int lineNumber = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNumber++;
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            int colon = line.IndexOf(':');
            string key = (colon < 0 ? line : line[..colon]).Trim().ToLowerInvariant();
            // the text after "name: " is kept as is (some names are padded with spaces)
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (key is not ("heading" or "name")) value = value.Trim();
            switch (key)
            {
                case "heading" or "name":
                    if (value.Any(ch => ch < 0x20 || ch > 0x7E)) { error = $"Line {lineNumber}: only plain letters, digits and punctuation can be shown."; return null; }
                    output.Add((byte)(key == "heading" ? 0x05 : 0x01)); output.AddRange(Encoding.ASCII.GetBytes(value)); output.Add(0);
                    break;
                case "gap":
                    if (!int.TryParse(value.Split(' ')[0], out int gap) || gap < 0 || gap > 255) { error = $"Line {lineNumber}: a gap is a number of lines, 0 to 255."; return null; }
                    output.Add(0x02); output.Add((byte)gap);
                    break;
                case "picture":
                    try { int tile = Convert.ToInt32(value, 16); output.Add(0x03); output.Add((byte)tile); output.Add((byte)(tile >> 8)); }
                    catch (Exception) { error = $"Line {lineNumber}: a picture is a tile number in hex."; return null; }
                    break;
                case "effect": output.Add(0x04); break;
                default: error = $"Line {lineNumber}: start it with heading:, name:, gap:, picture: or effect."; return null;
            }
        }
        output.Add(0);
        return output.ToArray();
    }

    // writes the credits, returns null or why not
    public static string? Write(RomBuffer rom, string text)
    {
        var bytes = Encode(text, out string error);
        if (bytes == null) return error;
        if (bytes.Length > End - Start) return $"{bytes.Length - (End - Start)} bytes too long (the credits have {End - Start} bytes of room).";
        rom.WriteBytes(Start, bytes);
        for (int position = Start + bytes.Length; position < End; position++) rom.WriteByte(position, 0);
        return null;
    }

    public static int Used(RomBuffer rom) => (Encode(Read(rom), out _)?.Length) ?? 0;
}
