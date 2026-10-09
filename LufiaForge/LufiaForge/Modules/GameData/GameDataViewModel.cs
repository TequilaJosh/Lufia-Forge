using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.GameData;

/// <summary>Hosts the Game Data editors (the L1ME feature set) and hands them the loaded ROM.</summary>
public partial class GameDataViewModel : ObservableObject
{
    public CharacterEditorViewModel Characters     { get; } = new();
    public CharacterMagicViewModel  CharacterMagic { get; } = new();
    public GrowthEditorViewModel    Growth         { get; } = new();
    public SpriteEditorViewModel    Sprites        { get; } = new();
    public ItemEditorViewModel      Items          { get; } = new();
    public PartyItemsViewModel      PartyItems     { get; } = new();
    public ShopEditorViewModel      Shops          { get; } = new();
    public SpellEditorViewModel     Spells         { get; } = new();
    public MonsterEditorViewModel   Monsters       { get; } = new();
    public EncounterEditorViewModel Encounters     { get; } = new();
    public GameSettingsViewModel    Settings       { get; } = new();

    [ObservableProperty] private bool _isRomLoaded;

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        var ctx = new GameDataContext(rom, mainVm);
        foreach (var editor in new GameDataEditorBase[]
                 { Characters, CharacterMagic, Growth, Sprites, Items, PartyItems, Shops, Spells, Monsters, Encounters, Settings })
            editor.Attach(ctx);
        IsRomLoaded = true;
    }
}
