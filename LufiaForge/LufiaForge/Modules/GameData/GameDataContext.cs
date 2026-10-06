using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core;
using LufiaForge.ViewModels;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

/// <summary>
/// Shared state for all Game Data editors: the ROM, name lists used by several editors' dropdowns,
/// and the fixed-length name codec. Names are stored as raw single-byte characters; '@' is the
/// in-game space inside item and monster names.
/// </summary>
public sealed class GameDataContext
{
    private readonly MainViewModel? _mainVm;

    public RomBuffer Rom { get; }

    /// <summary>"XX Name" for all 256 items (index = item id).</summary>
    public ObservableCollection<string> ItemNames { get; } = new();

    /// <summary>"XX Name" for all spells (index = spell id).</summary>
    public ObservableCollection<string> SpellNames { get; } = new();

    /// <summary>Raised after an editor renames items/spells/characters, so other editors refresh.</summary>
    public event Action? NamesChanged;

    public GameDataContext(RomBuffer rom, MainViewModel? mainVm)
    {
        Rom     = rom;
        _mainVm = mainVm;
        RefreshNames();
    }

    public void RefreshNames()
    {
        Fill(ItemNames, GameDataOffsets.ItemCount,
             i => $"{i:X2} {ReadName(ItemOffset(i), GameDataOffsets.ItemNameLen)}");
        Fill(SpellNames, GameDataOffsets.SpellOffsets.Length,
             i => $"{i:X2} {ReadName(GameDataOffsets.SpellOffsets[i], GameDataOffsets.SpellNameLen)}");
    }

    /// <summary>Call after any write so the window title shows unsaved changes.</summary>
    public void MarkModified(bool namesChanged = false)
    {
        if (namesChanged)
        {
            RefreshNames();
            NamesChanged?.Invoke();
        }
        _mainVm?.NotifyRomModified("Game data edit (characters / items / shops / spells / monsters)");
    }

    // ── Record locators ─────────────────────────────────────────────────────

    public int ItemOffset(int id) =>
        GameDataOffsets.ItemPointerTable + Rom.ReadUInt16Le(GameDataOffsets.ItemPointerTable + id * 2);

    public int MonsterOffset(int id) =>
        GameDataOffsets.MonsterPointerTable + Rom.ReadUInt16Le(GameDataOffsets.MonsterPointerTable + id * 2);

    public int ShopOffset(int id) => Rom.ReadUInt16Le(GameDataOffsets.ShopPointerTable + id * 2);

    public string CharacterName(int slot)
    {
        if (slot == 0) return "Hero";
        return ReadName(GameDataOffsets.CharacterNames + (slot - 1) * GameDataOffsets.CharacterNameLen,
                        GameDataOffsets.CharacterNameLen);
    }

    // ── Name codec ──────────────────────────────────────────────────────────

    public string ReadName(int offset, int length)
    {
        var bytes = Rom.ReadBytes(offset, length);
        var chars = bytes.Select(b => (char)b).ToArray();
        return new string(chars).TrimEnd(' ', '\0');
    }

    /// <summary>Writes a fixed-length name, truncating or padding with <paramref name="pad"/>.</summary>
    public void WriteName(int offset, int length, string name, byte pad = (byte)' ')
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            if (i < name.Length)
                bytes[i] = name[i] <= 0xFF ? (byte)name[i] : (byte)'?';
            else
                bytes[i] = pad;
        }
        Rom.WriteBytes(offset, bytes);
    }

    private static void Fill(ObservableCollection<string> target, int count, Func<int, string> make)
    {
        // Update in place so ComboBoxes bound to the collection keep their selection.
        for (int i = 0; i < count; i++)
        {
            string s = make(i);
            if (i < target.Count) { if (target[i] != s) target[i] = s; }
            else target.Add(s);
        }
        while (target.Count > count) target.RemoveAt(target.Count - 1);
    }
}

/// <summary>Base class for the individual Game Data editors.</summary>
public abstract partial class GameDataEditorBase : ObservableObject
{
    protected GameDataContext? Ctx { get; private set; }

    [ObservableProperty] private bool   _isLoaded;
    [ObservableProperty] private string _status = "Open a ROM to edit.";

    public ObservableCollection<string> ItemNames  => Ctx?.ItemNames  ?? EmptyList;
    public ObservableCollection<string> SpellNames => Ctx?.SpellNames ?? EmptyList;

    private static readonly ObservableCollection<string> EmptyList = new();

    public void Attach(GameDataContext ctx)
    {
        if (Ctx != null) Ctx.NamesChanged -= HandleNamesChanged;
        Ctx = ctx;
        Ctx.NamesChanged += HandleNamesChanged;
        OnPropertyChanged(nameof(ItemNames));
        OnPropertyChanged(nameof(SpellNames));
        IsLoaded = true;
        Status   = "";
        OnRomLoaded();
    }

    /// <summary>Build record lists and load the first record.</summary>
    protected abstract void OnRomLoaded();

    /// <summary>Re-read the currently selected record from the ROM.</summary>
    protected abstract void LoadSelected();

    /// <summary>
    /// Renaming replaces entries in the shared name lists, which clears ComboBox selections bound to
    /// them, so every editor re-reads its current record afterwards.
    /// </summary>
    protected virtual void OnNamesChanged() => LoadSelected();

    private void HandleNamesChanged()
    {
        string status = Status;
        OnNamesChanged();
        Status = status;
    }

    [RelayCommand]
    private void Revert()
    {
        if (Ctx == null) return;
        LoadSelected();
        Status = "Reverted to the values in the ROM.";
    }

    /// <summary>Runs a write action, reporting success or the error in <see cref="Status"/>.</summary>
    protected void Commit(string what, Action write, bool namesChanged = false)
    {
        if (Ctx == null) return;
        try
        {
            write();
            Ctx.MarkModified(namesChanged);
            Status = $"{what} written to ROM (unsaved).";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    protected static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
}
