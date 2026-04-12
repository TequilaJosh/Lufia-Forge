using System.Windows.Media;

namespace LufiaForge.Modules.Disassembler;

/// <summary>A named, colored annotation attached to a SNES address.</summary>
public sealed class Bookmark
{
    /// <summary>SNES address (24-bit), e.g. 0x808000.</summary>
    public int SnesAddress { get; set; }

    /// <summary>Short identifier shown inline in the disassembly as a label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional longer note (shown in the bookmark panel tooltip).</summary>
    public string? Comment { get; set; }

    /// <summary>Gutter color for this bookmark (serialized as ARGB hex string).</summary>
    public string ColorHex { get; set; } = "#C8942A"; // default: gold

    /// <summary>Formatted SNES address for display.</summary>
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
