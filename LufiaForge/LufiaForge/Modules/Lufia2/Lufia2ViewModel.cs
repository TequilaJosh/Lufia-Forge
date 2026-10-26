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
    public L2EventsPage Events { get; } = new();
    public L2ChestsPage Chests { get; } = new();
    public L2ExitsPage Exits { get; } = new();
    public L2CreditsPage Credits { get; } = new();
    public L2SettingsPage Settings { get; } = new();
    public L2MusicPage Music { get; } = new();
    public L2GraphicsPage Graphics { get; } = new();

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        Overview = $"{GameDetect.Name(rom.Game)}, {rom.Length / 1024} KB, title \"{rom.ReadAscii(GameDetect.HeaderTitle, GameDetect.TitleLength).TrimEnd()}\".\n\n" +
                   "Maps: every map drawn and editable (paint blocks on either layer; Ctrl+Z takes back strokes). Text: the lines of the maps' scenes. Events: every event in plain words, edited step by step, and new events (talking to a character, stepping on an area, after another event). " +
                   "Graphics: every picture, painted pixel by pixel or through PNG, new palettes and new map sprites. Battles: monsters, items, spells and IP attacks, with their effects edited step by step (weapons, armor, spells, IP attacks, monster and capsule AI), new spells and new IP attacks. " +
                   "Heroes: starting stats, magic and equipment, and growth on a chart you can drag. Capsule monsters: all 35 forms with their growth curves. Also shops, treasure, exits, encounters, music, credits and game settings. Edit > Start from a Clean Slate takes the story out. " +
                   "Every change goes straight into the ROM; Edit > Undo takes it back and Save Project keeps it.\n\n" +
                   "The Lufia 1 editors are hidden for this game.";
        try { Monsters.SetRom(rom, main); Items.SetRom(rom, main); Spells.SetRom(rom, main); IpAttacks.SetRom(rom, main); Maps.SetRom(rom, main); Text.SetRom(rom, main); Encounters.SetRom(rom, main); Shops.SetRom(rom, main); Characters.SetRom(rom, main); Capsules.SetRom(rom, main); Events.SetRom(rom, main); Chests.SetRom(rom, main); Exits.SetRom(rom, main); Credits.SetRom(rom, main); Settings.SetRom(rom, main); Music.SetRom(rom, main); Graphics.SetRom(rom, main); }
        catch (Exception ex) { Overview += $"\n\nCouldn't read part of the ROM: {ex.Message}"; }
    }
}
