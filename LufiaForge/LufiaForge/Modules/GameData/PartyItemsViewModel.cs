using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace LufiaForge.Modules.GameData;

public partial class PartyItemSlot : ObservableObject
{
    public int Number { get; init; }
    [ObservableProperty] private int _item;
    [ObservableProperty] private int _quantity;
}

/// <summary>
/// The items Maxim's party carries in the prologue. Stored as ten "give item" event commands
/// (3E item quantity), so only the item and quantity bytes are edited.
/// </summary>
public partial class PartyItemsViewModel : GameDataEditorBase
{
    private const byte GiveItemCommand = 0x3E;

    public ObservableCollection<PartyItemSlot> Slots { get; } = new();

    [ObservableProperty] private string _offsetText = "";

    protected override void OnRomLoaded() => LoadSelected();

    protected override void LoadSelected()
    {
        Slots.Clear();
        if (Ctx == null) return;
        var rom = Ctx.Rom;

        bool layoutOk = true;
        for (int i = 0; i < GameDataOffsets.FinalPartyItemCount; i++)
        {
            int o = GameDataOffsets.FinalPartyItems + i * 3;
            if (rom.ReadByte(o - 1) != GiveItemCommand) layoutOk = false;
            Slots.Add(new PartyItemSlot { Number = i + 1, Item = rom.ReadByte(o), Quantity = rom.ReadByte(o + 1) });
        }

        OffsetText = $"Event commands at 0x{GameDataOffsets.FinalPartyItems - 1:X6} (3E item qty) x {GameDataOffsets.FinalPartyItemCount}.";
        if (!layoutOk)
            Status = "Warning: the bytes here don't look like the original give-item commands. This ROM may be modified.";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null) return;
        Commit("Prologue party items", () =>
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                int o = GameDataOffsets.FinalPartyItems + i * 3;
                Ctx.Rom.WriteByte(o,     (byte)Clamp(Slots[i].Item, 0, 255));
                Ctx.Rom.WriteByte(o + 1, (byte)(Slots[i].Item == 0 ? 0 : Clamp(Slots[i].Quantity, 0, 99)));
            }
        });
    }
}
