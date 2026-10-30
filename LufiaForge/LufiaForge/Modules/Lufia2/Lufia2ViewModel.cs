using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core;
using LufiaForge.ViewModels;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The Lufia II tab: shown instead of the Lufia 1 editors when a Lufia II ROM is loaded.</summary>
public partial class Lufia2ViewModel : ObservableObject
{
    [ObservableProperty] private string _overview = "Load a Lufia II ROM.";
    /// <summary>Shown above the pages when another hack moved tables some pages edit (those stay read-only).</summary>
    [ObservableProperty] private string _lockBanner = "";

    public L2MonstersPage Monsters { get; } = new();
    public L2ItemsPage Items { get; } = new();
    public L2SpellsPage Spells { get; } = new();
    public L2IpPage IpAttacks { get; } = new();
    public L2AnimationsPage Animations { get; } = new();
    public L2MapsPage Maps { get; } = new();
    public L2TextPage Text { get; } = new();
    public L2EncountersPage Encounters { get; } = new();
    public L2ShopsPage Shops { get; } = new();
    public L2CharactersPage Characters { get; } = new();
    public L2CapsulesPage Capsules { get; } = new();
    public L2EventsPage Events { get; } = new();
    public L2CutscenesPage Cutscenes { get; } = new();
    public L2ChestsPage Chests { get; } = new();
    public L2ExitsPage Exits { get; } = new();
    public L2CreditsPage Credits { get; } = new();
    public L2SettingsPage Settings { get; } = new();
    public L2GameStartPage GameStart { get; } = new();
    public L2MusicPage Music { get; } = new();
    public L2GraphicsPage Graphics { get; } = new();

    /// <summary>Asks the view to show a page (by its header).</summary>
    public event Action<string>? ShowPage;
    /// <summary>Asks the view to pop up the event editor on an event of a map (the Maps page's characters and areas).</summary>
    public event Action<int, int, int>? OpenEditor;

    public Lufia2ViewModel()
    {
        // the Maps tab's "Talk event..." / "Event..." and double-clicking a character or an area: the event editor pops up
        Maps.OpenEventRequested += (map, head, id) => OpenEditor?.Invoke(map, head, id);
        // the Maps tab's battle zone list: "Monsters..." shows the zone on the Encounters page
        Maps.OpenZoneRequested += zone => { Encounters.ShowZone(zone); ShowPage?.Invoke("Encounters"); };
        // a monster added on the Monsters page: the Encounters page offers it
        Monsters.MonstersChanged += () => { Encounters.RefreshMonsters(); Graphics.RefreshMonsterOptions(); };
        // a monster sprite added, painted, given a palette or a movement, or given to a monster on the Graphics page:
        // the Monsters page lists it and shows the monster again, the Encounters page draws the new pictures
        Graphics.MonsterSpritesChanged += () => { Monsters.RefreshSprites(); Encounters.RefreshMonsters(); };
        // the Treasure tab and the Maps tab edit the same treasure: the map's edits are written before Treasure reads it
        Chests.BeforeRead = () => Maps.WritePendingEdits();
    }

    public void SetRom(RomBuffer rom, MainViewModel main)
    {
        Overview = $"{GameDetect.Name(rom.Game)}, {rom.Length / 1024} KB, title \"{rom.ReadAscii(GameDetect.HeaderTitle, GameDetect.TitleLength).TrimEnd()}\".\n\n" +
                   "Maps: every map drawn and edited as in Lufia 1's map editor: blocks on either layer (brushes, copy and paste, pieces of other maps, presets), collision, exits, step-on areas, characters, preset points and treasure, the block editor, graphics and size, a test start, and the world map with its battle zones. Text: the lines of the maps' scenes. Events: every event in plain words, edited step by step, and new events (talking to a character, stepping on an area, after another event). Cutscenes: a map as a stage with its characters, cutscenes made from steps in plain words (appear, walk, face, act, speak, camera, fades, music, flags, go to another map), played back, and tested from a new game. Game start: the map, cutscene, point and story flags a new game starts with. " +
                   "Graphics: every picture, painted pixel by pixel or through PNG, new palettes, new map sprites and new monster battle sprites. Battles: monsters, items, spells and IP attacks, with their effects edited step by step (weapons, armor, spells, IP attacks, monster and capsule AI), new spells and new IP attacks, battle animations played as the game plays them, painted, recoloured and copied into new ones. " +
                   "Heroes: starting stats, magic and equipment, and growth on a chart you can drag. Capsule monsters: all 35 forms with their growth curves. Also shops, treasure, exits, encounters, music, credits and game settings. Edit > Start from a Clean Slate takes the story out. " +
                   "Every change goes straight into the ROM; Edit > Undo takes it back and Save Project keeps it.\n\n" +
                   "The Lufia 1 editors are hidden for this game.";
        var identity = RomIdentity.Identify(rom.Original ?? rom.ToArray(), rom.Game);
        Overview += $"\n\nThis project started from {identity.What}" + (identity.ChangedParts.Count > 0 ? $" (changed: {string.Join(", ", identity.ChangedParts)})." : ".");
        var moved = TableGuard.CheckAll(rom).Where(r => r.State == TableGuard.State.MovedByAnotherHack).ToList();
        string movedList = string.Join(", ", moved.Select(r => r.Table.Name));
        LockBanner = moved.Count == 0 ? "" : $"Another hack changed where this game keeps its {movedList}. The pages that edit them are read-only here, so nothing that hack put in the game's old places gets written over.";
        if (moved.Count > 0) Overview += $" It changed where the game keeps its {movedList}: the pages that edit them stay read-only.";
        try { Monsters.SetRom(rom, main); Items.SetRom(rom, main); Spells.SetRom(rom, main); IpAttacks.SetRom(rom, main); Animations.SetRom(rom, main); Maps.SetRom(rom, main); Text.SetRom(rom, main); Encounters.SetRom(rom, main); Shops.SetRom(rom, main); Characters.SetRom(rom, main); Capsules.SetRom(rom, main); Events.SetRom(rom, main); Cutscenes.SetRom(rom, main); Chests.SetRom(rom, main); Exits.SetRom(rom, main); Credits.SetRom(rom, main); Settings.SetRom(rom, main); GameStart.SetRom(rom, main); Music.SetRom(rom, main); Graphics.SetRom(rom, main); }
        catch (Exception ex) { Overview += $"\n\nCouldn't read part of the ROM: {ex.Message}"; }
    }
}
