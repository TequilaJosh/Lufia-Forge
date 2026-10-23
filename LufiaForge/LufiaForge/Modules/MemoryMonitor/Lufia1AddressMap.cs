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
    private static readonly List<KnownAddress> _lufia2 = BuildLufia2Map();
    private static readonly List<KnownAddress> _custom  = new();
    private static readonly string _customPath = GetCustomPath();

    /// <summary>Which game's addresses are listed (set when a ROM is loaded).</summary>
    public static Core.GameKind Game { get; set; } = Core.GameKind.Lufia1;

    public static IReadOnlyList<KnownAddress> All => (Game == Core.GameKind.Lufia2 ? _lufia2 : _builtIn).Concat(_custom).ToList();

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
    // Address database — verified from PAR codes & SRAM research
    // Sources: Almar's Guides PAR codes, Data Crystal SRAM map,
    //          vegetaman SRAM offsets, GameHacking.org
    // -------------------------------------------------------------------------
    private static List<KnownAddress> BuildMap()
    {
        var map = new List<KnownAddress>();

        // =====================================================================
        // SYSTEM / TIMER
        // =====================================================================
        AddRange(map, "System", [
            (0x7E0543, "In-Game Timer (byte 1)",   WatchSize.U8),
            (0x7E0544, "In-Game Timer (byte 2)",   WatchSize.U8),
        ]);

        // =====================================================================
        // BATTLE / ENCOUNTERS
        // =====================================================================
        AddRange(map, "Battle", [
            (0x7E078C, "Random Encounter Flag",    WatchSize.U8),  // 05 = no encounters
        ]);

        // =====================================================================
        // GOLD (3 bytes, little-endian at $7E14CF)
        // =====================================================================
        AddRange(map, "Inventory", [
            (0x7E14CF, "Gold (low byte)",          WatchSize.U8),
            (0x7E14D0, "Gold (mid byte)",          WatchSize.U8),
            (0x7E14D1, "Gold (high byte)",         WatchSize.U8),
            (0x7E14CF, "Gold (full)",              WatchSize.U16), // low 2 bytes readable as U16
        ]);

        // =====================================================================
        // EXPERIENCE (party-wide)
        // =====================================================================
        AddRange(map, "Party", [
            (0x7E141A, "Experience (low byte)",    WatchSize.U8),
            (0x7E141B, "Experience (high byte)",   WatchSize.U8),
            (0x7E141A, "Experience",               WatchSize.U16),
        ]);

        // =====================================================================
        // HERO (Player 1) — verified PAR addresses
        // =====================================================================
        AddRange(map, "Hero", [
            (0x7E157F, "Hero Current HP",          WatchSize.U16),
            (0x7E1587, "Hero Current MP",          WatchSize.U16),
            (0x7E15F1, "Hero Max HP",              WatchSize.U16),
            (0x7E15F9, "Hero Max MP",              WatchSize.U16),
            (0x7E1710, "Hero STR",                 WatchSize.U16),
            (0x7E1700, "Hero AGL",                 WatchSize.U16),
            (0x7E1708, "Hero INT",                 WatchSize.U16),
            (0x7E1718, "Hero MGR",                 WatchSize.U16),
            (0x7E16F0, "Hero ATP",                 WatchSize.U16),
            (0x7E16F8, "Hero DFP",                 WatchSize.U16),
            (0x7E1691, "Hero EXP (low)",           WatchSize.U8),
            (0x7E1695, "Hero EXP (mid)",           WatchSize.U8),
            (0x7E1699, "Hero EXP (high)",          WatchSize.U8),
        ]);

        // =====================================================================
        // LUFIA (Player 3 in PAR numbering) — verified PAR addresses
        // =====================================================================
        AddRange(map, "Lufia", [
            (0x7E1581, "Lufia Current HP",         WatchSize.U16),
            (0x7E1589, "Lufia Current MP",         WatchSize.U16),
            (0x7E15F3, "Lufia Max HP",             WatchSize.U16),
            (0x7E15FB, "Lufia Max MP",             WatchSize.U16),
            (0x7E1714, "Lufia STR",                WatchSize.U16),
            (0x7E1704, "Lufia AGL",                WatchSize.U16),
            (0x7E170C, "Lufia INT",                WatchSize.U16),
            (0x7E171C, "Lufia MGR",                WatchSize.U16),
            (0x7E16F4, "Lufia ATP",                WatchSize.U16),
            (0x7E16FC, "Lufia DFP",                WatchSize.U16),
            (0x7E1693, "Lufia EXP (low)",          WatchSize.U8),
            (0x7E1697, "Lufia EXP (mid)",          WatchSize.U8),
            (0x7E169B, "Lufia EXP (high)",         WatchSize.U8),
        ]);

        // =====================================================================
        // AGURO (Player 2 in PAR numbering) — verified PAR addresses
        // =====================================================================
        AddRange(map, "Aguro", [
            (0x7E1583, "Aguro Current HP",         WatchSize.U16),
            (0x7E158B, "Aguro Current MP",         WatchSize.U16),
            (0x7E15F5, "Aguro Max HP",             WatchSize.U16),
            (0x7E15FD, "Aguro Max MP",             WatchSize.U16),
            (0x7E1712, "Aguro STR",                WatchSize.U16),
            (0x7E1702, "Aguro AGL",                WatchSize.U16),
            (0x7E170A, "Aguro INT",                WatchSize.U16),
            (0x7E171A, "Aguro MGR",                WatchSize.U16),
            (0x7E16F2, "Aguro ATP",                WatchSize.U16),
            (0x7E16FA, "Aguro DFP",                WatchSize.U16),
            (0x7E1692, "Aguro EXP (low)",          WatchSize.U8),
            (0x7E1696, "Aguro EXP (mid)",          WatchSize.U8),
            (0x7E169A, "Aguro EXP (high)",         WatchSize.U8),
        ]);

        // =====================================================================
        // JERIN (Player 4 in PAR numbering) — verified PAR addresses
        // =====================================================================
        AddRange(map, "Jerin", [
            (0x7E1585, "Jerin Current HP",         WatchSize.U16),
            (0x7E158D, "Jerin Current MP",         WatchSize.U16),
            (0x7E15F7, "Jerin Max HP",             WatchSize.U16),
            (0x7E15FF, "Jerin Max MP",             WatchSize.U16),
            (0x7E1716, "Jerin STR",                WatchSize.U16),
            (0x7E1706, "Jerin AGL",                WatchSize.U16),
            (0x7E170E, "Jerin INT",                WatchSize.U16),
            (0x7E171E, "Jerin MGR",                WatchSize.U16),
            (0x7E16F6, "Jerin ATP",                WatchSize.U16),
            (0x7E16FE, "Jerin DFP",                WatchSize.U16),
            (0x7E1694, "Jerin EXP (low)",          WatchSize.U8),
            (0x7E1698, "Jerin EXP (mid)",          WatchSize.U8),
            (0x7E169C, "Jerin EXP (high)",         WatchSize.U8),
        ]);

        return map;
    }

    /// <summary>Lufia II (US): LEdit's RAM notes (researched on the German game), checked in the US game in Tia's shop
    /// (2026-10-22): map 04 at 9,13, gold 120, the hero's name, level 1 with 20 HP, his equipment and the item list.</summary>
    private static List<KnownAddress> BuildLufia2Map()
    {
        var map = new List<KnownAddress>();
        AddRange(map, "Map", [
            (0x7E05AC, "Current map",              WatchSize.U8),
            (0x7E06BA, "Hero X (tiles)",           WatchSize.U8),
            (0x7E06E2, "Hero Y (tiles)",           WatchSize.U8),
            (0x7E11F2, "World map X",              WatchSize.U8),
            (0x7E11F4, "World map Y",              WatchSize.U8),
        ]);
        AddRange(map, "Inventory", [
            (0x7E0A8A, "Gold",                     WatchSize.U16),
            (0x7E0A8D, "Item slot 1 (item)",       WatchSize.U8),
            (0x7E0A8E, "Item slot 1 (amount x2, bit 0 = item + 256)", WatchSize.U8),
            (0x7E0A8F, "Item slot 2 (item)",       WatchSize.U8),
            (0x7E0A90, "Item slot 2 (amount x2, bit 0 = item + 256)", WatchSize.U8),
        ]);
        AddRange(map, "Maxim", [
            (0x7E0BBB, "Maxim Level",              WatchSize.U8),
            (0x7E0BBC, "Maxim Status",             WatchSize.U8),
            (0x7E0BBE, "Maxim HP",                 WatchSize.U16),
            (0x7E0BC0, "Maxim MP",                 WatchSize.U16),
            (0x7E0BD2, "Maxim Max HP",             WatchSize.U16),
            (0x7E0BD6, "Maxim Max MP",             WatchSize.U16),
            (0x7E0BD8, "Maxim ATP",                WatchSize.U16),
            (0x7E0BDA, "Maxim DFP",                WatchSize.U16),
            (0x7E0BDC, "Maxim STR",                WatchSize.U16),
            (0x7E0BDE, "Maxim AGL",                WatchSize.U16),
            (0x7E0BF0, "Maxim INT",                WatchSize.U16),
            (0x7E0BF2, "Maxim GUT",                WatchSize.U16),
            (0x7E0BF4, "Maxim MGR",                WatchSize.U16),
            (0x7E0C0C, "Maxim EXP (low 2 bytes)",  WatchSize.U16),
            (0x7E0C14, "Maxim Weapon",             WatchSize.U16),
            (0x7E0C69, "Maxim IP gauge",           WatchSize.U8),
        ]);
        AddRange(map, "Battle", [
            (0x7E1640, "Monster level (German notes)", WatchSize.U16),
            (0x7E166B, "Monster number (German notes)", WatchSize.U16),
            (0x7E1693, "Monster HP (German notes)", WatchSize.U16),
            (0x7E1695, "Monster MP (German notes)", WatchSize.U16),
        ]);
        return map;
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
