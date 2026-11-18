using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Battle;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

// status effects on equipment: a weapon's chance to inflict ailments on hit (property 12) and an item's
// ailment resistance (property 0D), both through ItemStatusPatch
public partial class ItemEditorViewModel
{
    public ObservableCollection<AilmentFlag> InflictFlags { get; } = new(
        GameDataLabels.Ailments.Select((n, i) => new AilmentFlag { Name = n, Bit = 1 << i }));
    public ByteOption[] ResistChoices { get; } =
        new[] { new ByteOption(0, "None") }.Concat(GameDataLabels.Ailments.Select((n, i) => new ByteOption(i + 1, n))).ToArray();
    [ObservableProperty] private int _inflictChance;
    [ObservableProperty] private int _resistAilment;
    [ObservableProperty] private int _resistPercent;
    [ObservableProperty] private string _statusNote = "";

    private (int Bits, int Chance, int Resist, int Percent) _loadedStatus;
    private (int Bits, int Chance, int Resist, int Percent) StatusNow() =>
        (InflictFlags.Where(f => f.IsSet).Sum(f => f.Bit), InflictChance, ResistAilment, ResistPercent);

    private void LoadStatus()
    {
        if (Ctx == null || SelectedIndex < 0) return;
        var props = ItemTable.Properties(Ctx.Rom, SelectedIndex) ?? new();
        var onHit = props.FirstOrDefault(p => p.Id == ItemStatusPatch.OnHitProperty);
        foreach (var flag in InflictFlags) flag.IsSet = (onHit.A & flag.Bit) != 0;
        InflictChance = onHit.Id == 0 ? 0 : (int)Math.Round(onHit.B * 100 / 255.0);
        var resist = props.FirstOrDefault(p => p.Id == ItemStatusPatch.ResistProperty);
        ResistAilment = resist.Id == 0 ? 0 : resist.A;
        ResistPercent = resist.Id == 0 ? 0 : resist.B == 0xFF ? 0 : resist.B * 100 / 16;
        _loadedStatus = StatusNow();
        StatusNote = ItemStatusPatch.IsApplied(Ctx.Rom)
            ? "Any weapon, armor or accessory can carry these."
            : "In the game only an accessory's resistance counts and no weapon can inflict ailments; applying here adds the \"Status effects on equipment\" patch.";
    }

    [RelayCommand]
    private void ApplyStatus()
    {
        // the item's typed values (price, name...) go in with it. writing the status reads the record again, which
        // would replace them with the ROM's. the page's own write does both (and puts the status edits back after it)
        if (HasPendingEdits) { if (WritePendingEdits() is string error) Status = error; return; }
        if (TryApplyStatus() is string why) Status = why;
    }

    // writes the status effects showing. returns null, or why they can't be written
    private string? TryApplyStatus()
    {
        if (Ctx == null || SelectedIndex < 0) return null;
        var props = ItemTable.Properties(Ctx.Rom, SelectedIndex);
        if (props == null) return "This item has no property list (it's used, not equipped).";
        if (StatusNow() == _loadedStatus) return null;   // nothing changed (writing the list again would reorder it)
        int bits = InflictFlags.Where(f => f.IsSet).Sum(f => f.Bit);
        if (bits != 0 && (InflictChance < 1 || InflictChance > 100)) return "Chance on hit: 1-100%.";
        if (ResistAilment != 0 && (ResistPercent < 0 || ResistPercent > 1500)) return "Resist: 0 (immune) to 1500%.";
        var list = props.Where(p => p.Id != ItemStatusPatch.OnHitProperty && p.Id != ItemStatusPatch.ResistProperty).ToList();
        if (bits != 0) list.Add((ItemStatusPatch.OnHitProperty, bits, Math.Clamp((int)Math.Round(InflictChance * 255 / 100.0), 1, 255)));
        if (ResistAilment != 0) list.Add((ItemStatusPatch.ResistProperty, ResistAilment, ResistPercent == 0 ? 0xFF : Math.Clamp(ResistPercent * 16 / 100, 1, 0xFE)));
        int id = SelectedIndex;
        bool needPatch = bits != 0 || ResistAilment != 0;
        if (!Commit($"Item {id:X2} status effects", () =>
            {
                if (needPatch) ItemStatusPatch.Apply(Ctx.Rom);
                ItemTable.SetProperties(Ctx.Rom, id, list);
            }))
            return Status;
        LoadSelected();
        return null;
    }

    public override string? WritePendingEdits()
    {
        if (!HasPendingEdits) return null;
        var status = StatusNow();
        var flags = InflictFlags.Select(f => f.IsSet).ToArray();
        string? error = base.WritePendingEdits();
        if (error != null) return error;   // nothing got reloaded, the status edits are still showing and pending
        if (status != _loadedStatus && Ctx != null)
        {
            // writing the record reloaded the panel, so put the edits back and write them
            for (int i = 0; i < flags.Length; i++) InflictFlags[i].IsSet = flags[i];
            InflictChance = status.Chance; ResistAilment = status.Resist; ResistPercent = status.Percent;
            if (TryApplyStatus() is string why) { Status = why; Edited = true; return $"{PageName}: {why}"; }
            Edited = false;
        }
        return null;
    }
}