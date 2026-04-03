using CommunityToolkit.Mvvm.ComponentModel;

namespace LufiaForge.Modules.MemoryMonitor;

public enum WatchSize { U8, U16, U32, S8, S16 }

public partial class WatchItem : ObservableObject
{
    public int       SnesAddr { get; }
    public string    Label    { get; set; }
    public WatchSize Size     { get; set; }

    [ObservableProperty] private string _currentValue = "--";
    [ObservableProperty] private string _previousValue = "--";
    [ObservableProperty] private string _delta = "0";

    public string AddressHex => $"${SnesAddr:X6}";

    public WatchItem(int snesAddr, string label, WatchSize size)
    {
        SnesAddr = snesAddr;
        Label    = label;
        Size     = size;
    }
}
