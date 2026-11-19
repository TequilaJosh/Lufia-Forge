using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.Core.Maps;
using LufiaForge.Modules.MemoryMonitor;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;

namespace LufiaForge.Modules.Events;

// one flag in the live list
public partial class LiveFlagRow : ObservableObject
{
    public EventFlag Flag { get; init; } = null!;
    public string IdText => Flag.Id >= 0 ? $"{Flag.Id:X2}" : "user";
    public string RamText => $"${Flag.RamAddress:X4} bit {Flag.BitIndex}";
    public string Name => Flag.Name;
    public string Description => Flag.Description;
    [ObservableProperty] private bool _value;
    [ObservableProperty] private bool _known;          // read from the game at least once
    [ObservableProperty] private bool _changed;        // changed since tracking started
    [ObservableProperty] private bool _flashOn;
    [ObservableProperty] private bool _flashOff;
    public DateTime FlashUntil { get; set; }
    public string ValueText => !Known ? "-" : Value ? "✅" : "❌";
    partial void OnValueChanged(bool value) => OnPropertyChanged(nameof(ValueText));
    partial void OnKnownChanged(bool value) => OnPropertyChanged(nameof(ValueText));
}

// one flag change seen in the running game
public sealed record FlagLogEntry(uint Frame, DateTime Time, string Flag, bool Old, bool New)
{
    public string TimeText => Time.ToString("HH:mm:ss.f");
    public string Change => $"{(Old ? "on" : "off")} → {(New ? "on" : "off")}";
}

// live story flag tracker (phase 6.4). reads the game's RAM from BizHawk (through the Lufia Forge tool's shared
// memory), shows every flag's value, flashes the changes and logs them with the frame number
public partial class LiveFlagsViewModel : ObservableObject
{
    private readonly BizHawkBridge _bridge = new();
    private readonly EventFlagStore _store = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private List<LiveFlagRow> _all = new();
    private RomBuffer? _rom;

    public ObservableCollection<LiveFlagRow> Rows { get; } = new();
    public ObservableCollection<FlagLogEntry> Log { get; } = new();
    public IReadOnlyList<string> Filters { get; } = new[] { "All flags", "Changed while tracking", "Only ON" };

    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _status = "Not connected.";
    [ObservableProperty] private string _running = "";
    [ObservableProperty] private string _newAddress = "";
    [ObservableProperty] private string _newBit = "0";
    [ObservableProperty] private string _newName = "";

    // fires after a log line got added (the view scrolls to it)
    public event Action<FlagLogEntry>? Logged;

    public LiveFlagsViewModel()
    {
        _timer.Tick += (_, _) => Poll();
    }

    public void SetRom(RomBuffer rom)
    {
        _rom = rom;
        _store.Open(rom.FilePath);
        RebuildRows();
        _timer.Start();
    }

    private void RebuildRows()
    {
        var old = _all.ToDictionary(r => (r.Flag.RamAddress, r.Flag.BitIndex, r.Flag.Id));
        _all = _store.GetAll().Select(f =>
        {
            var row = new LiveFlagRow { Flag = f };
            if (old.TryGetValue((f.RamAddress, f.BitIndex, f.Id), out var oldRow)) { row.Value = oldRow.Value; row.Known = oldRow.Known; row.Changed = oldRow.Changed; }
            return row;
        }).ToList();
        ApplyFilter();
    }

    partial void OnFilterIndexChanged(int value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Rows.Clear();
        string term = Search.Trim();
        foreach (var row in _all)
        {
            if (FilterIndex == 1 && !row.Changed) continue;
            if (FilterIndex == 2 && !(row.Known && row.Value)) continue;
            if (term.Length > 0 && !row.IdText.Equals(term, StringComparison.OrdinalIgnoreCase) && !row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(row);
        }
    }

    private void Poll()
    {
        if (!_bridge.IsConnected) _bridge.TryConnect();
        bool fresh = _bridge.ReadFrame();
        var wram = _bridge.Wram;
        if (!_bridge.IsConnected || wram == null)
        {
            Status = "Not connected: start BizHawk from the Memory Monitor tab (with the Lufia Forge tool open) and load the game.";
            Running = "";
            return;
        }
        Status = $"Connected · frame {_bridge.FrameCount:N0}";
        var now = DateTime.Now;
        if (fresh)
        {
            bool filterDirty = false;
            foreach (var row in _all)
            {
                bool isOn = EventFlagStore.Read(wram, row.Flag);
                if (!row.Known) { row.Value = isOn; row.Known = true; continue; }
                if (isOn == row.Value) continue;
                var entry = new FlagLogEntry(_bridge.FrameCount, now, row.Flag.Id >= 0 ? $"{row.IdText} {row.Name}".Trim() : $"{row.RamText} {row.Name}".Trim(), row.Value, isOn);
                row.Value = isOn; row.Changed = true;
                row.FlashOn = isOn; row.FlashOff = !isOn; row.FlashUntil = now.AddSeconds(1.5);
                Log.Add(entry);
                if (Log.Count > 5000) Log.RemoveAt(0);
                Logged?.Invoke(entry);
                filterDirty |= FilterIndex != 0;
            }
            if (filterDirty) ApplyFilter();
            // what the engine is running: script pointer T at $0D12-$0D14 (FF = none), map at $0D15
            int scriptPointer = wram[0x0D12] | (wram[0x0D13] << 8) | (wram[0x0D14] << 16);
            Running = wram[0x0D14] == 0xFF ? $"Map {wram[0x0D15]:X2} · no event running"
                : $"Map {wram[0x0D15]:X2} · event script running at ROM 0x{0x18000 + scriptPointer:X6}";
        }
        foreach (var row in _all.Where(r => (r.FlashOn || r.FlashOff) && now > r.FlashUntil)) { row.FlashOn = false; row.FlashOff = false; }
    }

    // ROM offset of the script the game is running, or -1 (the event editor uses it to follow the game)
    public int RunningScriptOffset
    {
        get
        {
            var wram = _bridge.Wram;
            if (!_bridge.IsConnected || wram == null || wram[0x0D14] == 0xFF) return -1;
            return 0x18000 + (wram[0x0D12] | (wram[0x0D13] << 8) | (wram[0x0D14] << 16));
        }
    }

    [RelayCommand]
    private void AddFlag()
    {
        var addressText = NewAddress.Trim().TrimStart('$');
        if (addressText.StartsWith("7E", StringComparison.OrdinalIgnoreCase) && addressText.Length == 6) addressText = addressText[2..];
        if (!int.TryParse(addressText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int address) || address < 0 || address >= BizHawkBridge.WramSize)
        { Status = "Address: enter a WRAM address in hex, e.g. 1296 or 7E1296."; return; }
        if (!int.TryParse(NewBit.Trim(), out int bit) || bit is < 0 or > 7) { Status = "Bit: 0-7."; return; }
        _store.AddUserFlag(address, bit, NewName.Trim().Length > 0 ? NewName.Trim() : $"${address:X4}.{bit}");
        NewName = "";
        RebuildRows();
        Status = $"Watching ${address:X4} bit {bit}.";
    }

    [RelayCommand]
    private void RemoveFlag(LiveFlagRow? row)
    {
        if (row == null || row.Flag.Id >= 0) return;
        _store.RemoveUserFlag(row.Flag);
        RebuildRows();
    }

    [RelayCommand]
    private void ImportCsv()
    {
        var dialog = new OpenFileDialog { Title = "Import flag names", Filter = "CSV (*.csv)|*.csv|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        try { int imported = _store.ImportCsv(dialog.FileName); RebuildRows(); Status = $"Imported {imported} line(s)."; }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    [RelayCommand]
    private void ExportLog()
    {
        if (Log.Count == 0) { Status = "The log is empty."; return; }
        var dialog = new SaveFileDialog { Title = "Export flag log", Filter = "CSV (*.csv)|*.csv", FileName = "Lufia flag log.csv" };
        if (dialog.ShowDialog() != true) return;
        using var writer = new StreamWriter(dialog.FileName);
        writer.WriteLine("frame,time,flag,old,new");
        foreach (var entry in Log) writer.WriteLine($"{entry.Frame},{entry.Time:yyyy-MM-dd HH:mm:ss.fff},\"{entry.Flag.Replace("\"", "'")}\",{(entry.Old ? 1 : 0)},{(entry.New ? 1 : 0)}");
        Status = $"Exported {Log.Count} change(s) to {dialog.FileName}.";
    }

    [RelayCommand]
    private void ClearLog() { Log.Clear(); foreach (var row in _all) row.Changed = false; ApplyFilter(); }
}
