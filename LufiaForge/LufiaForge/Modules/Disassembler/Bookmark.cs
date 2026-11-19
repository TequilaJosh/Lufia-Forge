using System.Windows.Media;

namespace LufiaForge.Modules.Disassembler;

// a named, coloured note on a SNES address
public sealed class Bookmark
{
    // 24-bit SNES address, like 0x808000
    public int SnesAddress { get; set; }

    // short name shown inline in the disassembly as a label
    public string Label { get; set; } = string.Empty;

    // optional longer note (shown in the bookmark panel tooltip)
    public string? Comment { get; set; }

    // gutter colour for this bookmark (saved as an ARGB hex string)
    public string ColorHex { get; set; } = "#C8942A"; // default: gold

    // how many bytes of data start here (0 = an ordinary label). data regions show as .db lines and the
    // cross-reference builder skips them
    public int DataLength { get; set; }

    // the SNES address formatted for display
    public string SnesAddressHex => $"${SnesAddress:X6}";

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.Color GutterColor
    {
        get
        {
            try
            {
                string hex = ColorHex.TrimStart('#');
                if (hex.Length == 6)
                    hex = "FF" + hex;
                if (hex.Length == 8)
                {
                    byte a = Convert.ToByte(hex[0..2], 16);
                    byte r = Convert.ToByte(hex[2..4], 16);
                    byte g = Convert.ToByte(hex[4..6], 16);
                    byte b = Convert.ToByte(hex[6..8], 16);
                    return System.Windows.Media.Color.FromArgb(a, r, g, b);
                }
            }
            catch { }
            return Colors.Goldenrod;
        }
    }
}
