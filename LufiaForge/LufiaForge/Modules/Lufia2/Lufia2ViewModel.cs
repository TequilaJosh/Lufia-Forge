using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The Lufia II tab: shown instead of the Lufia 1 editors when a Lufia II ROM is loaded.</summary>
public partial class Lufia2ViewModel : ObservableObject
{
    [ObservableProperty] private string _overview = "Load a Lufia II ROM.";

    public L2MonstersPage Monsters { get; } = new();
    public L2ItemsPage Items { get; } = new();
    public L2SpellsPage Spells { get; } = new();
    public L2IpPage IpAttacks { get; } = new();
    public L2MapsPage Maps { get; } = new();
    public L2TextPage Text { get; } = new();
    public L2EncountersPage Encounters { get; } = new();
    public L2ShopsPage Shops { get; } = new();
    public L2CharactersPage Characters { get; } = new();
    public L2CapsulesPage Capsules { get; } = new();

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        Overview = $"{GameDetect.Name(rom.Game)}, {rom.Length / 1024} KB, title \"{rom.ReadAscii(GameDetect.HeaderTitle, GameDetect.TitleLength).TrimEnd()}\".\n\n" +
                   "Maps: every map drawn (ground and upper layers, collision) and editable (paint blocks on either layer). Text: the lines of the maps' scenes, editable in place. Editable so far: monsters (stats, rewards, drops, battle sprite), items (names, prices, use, who can equip, " +
                   "stat bonuses), spells (names, MP, prices, who learns them), IP attacks (cost), the descriptions of items, spells and IP attacks, shops (what each sells), the heroes (starting stats, growth per level, EXP curve, what each starts with), the capsule monsters (all 35 forms, attacks, stats, growth), and encounters (the 216 monster groups, and which group each monster on a map fights). " +
                   "Every change goes straight into the ROM; Edit > Undo takes it back and Save Project keeps it.\n\n" +
                   "The Lufia 1 editors are hidden for this game. More Lufia II editors are on the way.";
        try { Monsters.SetRom(rom, main); Items.SetRom(rom, main); Spells.SetRom(rom, main); IpAttacks.SetRom(rom, main); Maps.SetRom(rom, main); Text.SetRom(rom, main); Encounters.SetRom(rom, main); Shops.SetRom(rom, main); Characters.SetRom(rom, main); Capsules.SetRom(rom, main); }
        catch (Exception ex) { Overview += $"\n\nCouldn't read part of the ROM: {ex.Message}"; }
    }
}
