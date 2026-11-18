namespace LufiaForge.Modules.PatchManager;

// one decoded record of an IPS patch file, for showing in the UI
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
