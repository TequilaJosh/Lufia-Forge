using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Modules.Emulator;

public enum WatchType { U8, U16, U32 }

/// <summary>One entry in the watchlist — a pinned RAM address with live value tracking.</summary>
public sealed partial class WatchEntry : ObservableObject
{
    [ObservableProperty] private string    _label        = "";
    [ObservableProperty] private int       _snesAddress;
    [ObservableProperty] private WatchType _type         = WatchType.U8;
    [ObservableProperty] private string    _currentValue = "--";
    [ObservableProperty] private string    _previousValue= "--";
    [ObservableProperty] private string    _delta        = "--";

    public string AddressHex => $"${SnesAddress:X6}";

    public void Update(MemorySnapshot snap)
    {
        int offset = Snes9xAddressMap.SnesAddressToRamOffset(SnesAddress);
        if (offset < 0) return;

        string newVal = Type switch
        {
            WatchType.U8  => snap.ReadByte(offset).ToString(),
            WatchType.U16 => snap.ReadUInt16Le(offset).ToString(),
            WatchType.U32 => ((uint)(snap.ReadUInt16Le(offset) | (snap.ReadUInt16Le(offset + 2) << 16))).ToString(),
            _             => "--",
        };

        if (newVal != CurrentValue)
        {
            PreviousValue = CurrentValue;
            Delta         = ComputeDelta(PreviousValue, newVal);
            CurrentValue  = newVal;
        }
    }

    private static string ComputeDelta(string prev, string next)
    {
        if (long.TryParse(prev, out long p) && long.TryParse(next, out long n))
            return (n - p >= 0 ? "+" : "") + (n - p).ToString();
        return "~";
    }
}
