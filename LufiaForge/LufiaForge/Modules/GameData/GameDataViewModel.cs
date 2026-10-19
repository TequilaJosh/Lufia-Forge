using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.GameData;

/// <summary>Hosts the Game Data editors (the L1ME feature set) and hands them the loaded ROM.</summary>
public partial class GameDataViewModel : ObservableObject, LufiaForge.ViewModels.IPendingEdits
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
    public BattleBackgroundEditorViewModel BattleBackgrounds { get; } = new();
    public MusicEditorViewModel     Music          { get; } = new();
    public CreditsEditorViewModel   Credits        { get; } = new();
    public TitleEditorViewModel     Title          { get; } = new();
    public GraphicsEditorViewModel  Graphics       { get; } = new();
    public PaletteEditorViewModel   Palettes       { get; } = new();
    public GameSettingsViewModel    Settings       { get; } = new();

    [ObservableProperty] private bool _isRomLoaded;

    private RomBuffer? _rom;

    private GameDataEditorBase[] Editors => new GameDataEditorBase[]
        { Characters, CharacterMagic, Growth, Sprites, Items, PartyItems, Shops, Spells, Monsters, Encounters, BattleBackgrounds, Music, Credits, Title, Graphics, Palettes, Settings };

    public bool HasPendingEdits => _rom != null && Editors.Any(e => e.HasPendingEdits);

    public string? WritePendingEdits()
    {
        var errors = Editors.Where(e => e.HasPendingEdits).Select(e => e.WritePendingEdits()).Where(e => e != null).ToList();
        return errors.Count == 0 ? null : "Game Data: " + string.Join("; ", errors);
    }

    public void SetRom(RomBuffer rom, MainViewModel mainVm)
    {
        // the same ROM again (undo / redo): every editor keeps its selected record
        bool reload = ReferenceEquals(rom, _rom);
        _rom = rom;
        var ctx = new GameDataContext(rom, mainVm);
        foreach (var editor in new GameDataEditorBase[]
                 { Characters, CharacterMagic, Growth, Sprites, Items, PartyItems, Shops, Spells, Monsters, Encounters, BattleBackgrounds, Music, Credits, Title, Graphics, Palettes, Settings })
            if (reload) editor.Reattach(ctx); else editor.Attach(ctx);
        IsRomLoaded = true;
    }
}
