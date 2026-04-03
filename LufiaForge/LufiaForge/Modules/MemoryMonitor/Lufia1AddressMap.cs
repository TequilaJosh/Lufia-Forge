using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LufiaForge.Modules.MemoryMonitor;

/// <summary>
/// Known SNES WRAM addresses for Lufia &amp; The Fortress of Doom (US).
/// All addresses are absolute SNES ($7Exxxx). To get WRAM offset: addr - 0x7E0000.
/// Supports user-added custom labels that persist to a JSON file.
/// </summary>
public static class Lufia1AddressMap
{
    private static readonly List<KnownAddress> _builtIn = BuildMap();
    private static readonly List<KnownAddress> _custom  = new();
    private static readonly string _customPath = GetCustomPath();

    public static IReadOnlyList<KnownAddress> All => _builtIn.Concat(_custom).ToList();

    /// <summary>Search by partial label (case-insensitive).</summary>
    public static IEnumerable<KnownAddress> Search(string query)
    {
        var all = All;
        if (string.IsNullOrWhiteSpace(query)) return all;
        var q = query.Trim();
        return all.Where(a =>
            a.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.AddressHex.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Get all entries in a specific category.</summary>
    public static IEnumerable<KnownAddress> ByCategory(string category)
        => All.Where(a => a.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

    /// <summary>All distinct categories.</summary>
    public static IReadOnlyList<string> Categories =>
        All.Select(a => a.Category).Distinct().OrderBy(c => c).ToList();

    /// <summary>Add a user-defined address label and persist to disk.</summary>
    public static void AddCustom(int address, string label, WatchSize size, string category = "Custom")
    {
        // Don't add duplicates
        if (_custom.Any(a => a.Address == address && a.Label == label)) return;
        _custom.Add(new KnownAddress(address, label, size, category));
        SaveCustom();
    }

    /// <summary>Remove a user-defined address label.</summary>
    public static bool RemoveCustom(int address, string label)
    {
        var match = _custom.FirstOrDefault(a => a.Address == address && a.Label == label);
        if (match == null) return false;
        _custom.Remove(match);
        SaveCustom();
        return true;
    }

    /// <summary>Load user-defined labels from disk.</summary>
    public static void LoadCustom()
    {
        _custom.Clear();
        if (!File.Exists(_customPath)) return;
        try
        {
            var json = File.ReadAllText(_customPath);
            var entries = JsonSerializer.Deserialize<List<CustomEntry>>(json);
            if (entries == null) return;
            foreach (var e in entries)
                _custom.Add(new KnownAddress(e.Address, e.Label, (WatchSize)e.Size, e.Category));
        }
        catch { /* corrupt file — ignore */ }
    }

    private static void SaveCustom()
    {
        try
        {
            var dir = Path.GetDirectoryName(_customPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var entries = _custom.Select(a => new CustomEntry
            {
                Address  = a.Address,
                Label    = a.Label,
                Size     = (int)a.Size,
                Category = a.Category,
            }).ToList();

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_customPath, json);
        }
        catch { /* non-fatal */ }
    }

    private static string GetCustomPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LufiaForge", "custom_addresses.json");
    }

    private class CustomEntry
    {
        public int    Address  { get; set; }
        public string Label    { get; set; } = "";
        public int    Size     { get; set; }
        public string Category { get; set; } = "Custom";
    }

    // -------------------------------------------------------------------------
    // Address database
    // -------------------------------------------------------------------------
    private static List<KnownAddress> BuildMap()
    {
        var map = new List<KnownAddress>();

        // ----- System / Game State -----
        AddRange(map, "System", [
            (0x7E0010, "Game Mode / State",        WatchSize.U8),
            (0x7E0012, "Screen Brightness",        WatchSize.U8),
            (0x7E0014, "Frame Counter",            WatchSize.U16),
            (0x7E0016, "Map ID",                   WatchSize.U16),
            (0x7E0018, "Previous Map ID",          WatchSize.U16),
            (0x7E001A, "Warp Destination Map",     WatchSize.U16),
            (0x7E001C, "Warp Destination X",       WatchSize.U8),
            (0x7E001D, "Warp Destination Y",       WatchSize.U8),
            (0x7E0020, "Game Clock Frames",        WatchSize.U16),
            (0x7E0022, "Game Clock Seconds",       WatchSize.U8),
            (0x7E0023, "Game Clock Minutes",       WatchSize.U8),
            (0x7E0024, "Game Clock Hours",         WatchSize.U16),
        ]);

        // ----- Map / Movement -----
        AddRange(map, "Map", [
            (0x7E0078, "Player X Position",        WatchSize.U16),
            (0x7E007A, "Player Y Position",        WatchSize.U16),
            (0x7E007C, "Player Direction",          WatchSize.U8),
            (0x7E007E, "Player Movement State",     WatchSize.U8),
            (0x7E0080, "Camera Scroll X",          WatchSize.U16),
            (0x7E0082, "Camera Scroll Y",          WatchSize.U16),
            (0x7E0084, "Tile Player Standing On",   WatchSize.U8),
            (0x7E0060, "NPC Count on Map",         WatchSize.U8),
            (0x7E0086, "Steps Taken",              WatchSize.U16),
            (0x7E0088, "Encounter Counter",        WatchSize.U16),
        ]);

        // ----- Party -----
        AddRange(map, "Party", [
            (0x7E0B80, "Party Size",               WatchSize.U8),
            (0x7E0B82, "Party Member 1 ID",        WatchSize.U8),
            (0x7E0B83, "Party Member 2 ID",        WatchSize.U8),
            (0x7E0B84, "Party Member 3 ID",        WatchSize.U8),
            (0x7E0B85, "Party Member 4 ID",        WatchSize.U8),
        ]);

        // ----- Hero (Character 1) -----
        const int hero = 0x7E0B86;
        AddCharacter(map, "Hero", hero);

        // ----- Lufia (Character 2) -----
        const int lufia = 0x7E0BF8;
        AddCharacter(map, "Lufia", lufia);

        // ----- Aguro (Character 3) -----
        const int aguro = 0x7E0C6A;
        AddCharacter(map, "Aguro", aguro);

        // ----- Jerin (Character 4) -----
        const int jerin = 0x7E0CDC;
        AddCharacter(map, "Jerin", jerin);

        // ----- Gold / Money -----
        AddRange(map, "Inventory", [
            (0x7E0BF4, "Gold",                     WatchSize.U32),
        ]);

        // ----- Inventory Slots (first 16) -----
        for (int i = 0; i < 16; i++)
        {
            map.Add(new KnownAddress(0x7E0D4E + i * 2, $"Item Slot {i + 1} ID", WatchSize.U8, "Inventory"));
            map.Add(new KnownAddress(0x7E0D4F + i * 2, $"Item Slot {i + 1} Qty", WatchSize.U8, "Inventory"));
        }

        // ----- Battle -----
        AddRange(map, "Battle", [
            (0x7E1000, "Battle State Flag",        WatchSize.U8),
            (0x7E1002, "In Battle Flag",           WatchSize.U8),
            (0x7E1004, "Turn Counter",             WatchSize.U16),
            (0x7E1010, "Enemy 1 HP",               WatchSize.U16),
            (0x7E1012, "Enemy 1 Max HP",           WatchSize.U16),
            (0x7E1014, "Enemy 1 ATK",              WatchSize.U16),
            (0x7E1016, "Enemy 1 DEF",              WatchSize.U16),
            (0x7E1018, "Enemy 1 AGI",              WatchSize.U16),
            (0x7E101A, "Enemy 1 INT",              WatchSize.U16),
            (0x7E1020, "Enemy 2 HP",               WatchSize.U16),
            (0x7E1022, "Enemy 2 Max HP",           WatchSize.U16),
            (0x7E1030, "Enemy 3 HP",               WatchSize.U16),
            (0x7E1032, "Enemy 3 Max HP",           WatchSize.U16),
            (0x7E1040, "Enemy 4 HP",               WatchSize.U16),
            (0x7E1042, "Enemy 4 Max HP",           WatchSize.U16),
            (0x7E1006, "Enemy Count",              WatchSize.U8),
            (0x7E1008, "Battle Reward EXP",        WatchSize.U16),
            (0x7E100A, "Battle Reward Gold",       WatchSize.U16),
        ]);

        // ----- Event Flags (sample) -----
        AddRange(map, "Events", [
            (0x7E0E00, "Event Flag Block 1",       WatchSize.U8),
            (0x7E0E01, "Event Flag Block 2",       WatchSize.U8),
            (0x7E0E02, "Event Flag Block 3",       WatchSize.U8),
            (0x7E0E03, "Event Flag Block 4",       WatchSize.U8),
            (0x7E0E10, "Story Progress Flag",      WatchSize.U16),
            (0x7E0E12, "Boss Defeated Flags",      WatchSize.U16),
            (0x7E0E14, "Chest Opened Flags 1",     WatchSize.U16),
            (0x7E0E16, "Chest Opened Flags 2",     WatchSize.U16),
        ]);

        // ----- Ancient Cave (if applicable) -----
        AddRange(map, "Dungeon", [
            (0x7E0E20, "Dungeon Floor",            WatchSize.U8),
            (0x7E0E22, "Dungeon Room ID",          WatchSize.U16),
        ]);

        return map;
    }

    /// <summary>Add a standard character stat block.</summary>
    private static void AddCharacter(List<KnownAddress> map, string name, int baseAddr)
    {
        AddRange(map, name, [
            (baseAddr + 0x00, $"{name} Level",     WatchSize.U8),
            (baseAddr + 0x02, $"{name} Current EXP", WatchSize.U32),
            (baseAddr + 0x06, $"{name} EXP to Next", WatchSize.U32),
            (baseAddr + 0x0A, $"{name} Max HP",    WatchSize.U16),
            (baseAddr + 0x0C, $"{name} Max MP",    WatchSize.U16),
            (baseAddr + 0x0E, $"{name} STR",       WatchSize.U16),
            (baseAddr + 0x10, $"{name} AGI",       WatchSize.U16),
            (baseAddr + 0x12, $"{name} INT",       WatchSize.U16),
            (baseAddr + 0x14, $"{name} GUT",       WatchSize.U16),
            (baseAddr + 0x16, $"{name} MGR",       WatchSize.U16),
            (baseAddr + 0x18, $"{name} HP",        WatchSize.U16),
            (baseAddr + 0x1A, $"{name} MP",        WatchSize.U16),
            (baseAddr + 0x1C, $"{name} ATK",       WatchSize.U16),
            (baseAddr + 0x1E, $"{name} DEF",       WatchSize.U16),
            (baseAddr + 0x20, $"{name} Status",    WatchSize.U8),
            (baseAddr + 0x22, $"{name} Weapon ID", WatchSize.U8),
            (baseAddr + 0x23, $"{name} Armor ID",  WatchSize.U8),
            (baseAddr + 0x24, $"{name} Shield ID", WatchSize.U8),
            (baseAddr + 0x25, $"{name} Helmet ID", WatchSize.U8),
            (baseAddr + 0x26, $"{name} Ring ID",   WatchSize.U8),
            (baseAddr + 0x27, $"{name} Jewel ID",  WatchSize.U8),
        ]);
    }

    private static void AddRange(List<KnownAddress> map, string category,
        (int addr, string label, WatchSize size)[] entries)
    {
        foreach (var (addr, label, size) in entries)
            map.Add(new KnownAddress(addr, label, size, category));
    }
}

public sealed class KnownAddress
{
    public int       Address    { get; }
    public string    Label      { get; }
    public WatchSize Size       { get; }
    public string    Category   { get; }
    public string    AddressHex => $"${Address:X6}";
    public string    Display    => $"{Label}  ({AddressHex})";

    public KnownAddress(int address, string label, WatchSize size, string category)
    {
        Address  = address;
        Label    = label;
        Size     = size;
        Category = category;
    }
}
