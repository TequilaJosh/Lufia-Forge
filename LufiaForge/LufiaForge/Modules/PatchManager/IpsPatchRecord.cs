namespace LufiaForge.Modules.PatchManager;

/// <summary>
/// Represents one decoded record from an IPS patch file, for display in the UI.
/// </summary>
public class IpsPatchRecord
{
    public int  Offset  { get; init; }
    public int  Size    { get; init; }
    public bool IsRle   { get; init; }
    public byte RleFill { get; init; }

    public string OffsetDisplay => $"0x{Offset:X6}";
    public string SizeDisplay   => IsRle ? $"RLE ×{Size}" : $"+{Size} bytes";
    public string TypeDisplay   => IsRle ? $"fill=0x{RleFill:X2}" : "data";
}
