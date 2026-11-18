using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// the Encounters page's story battles (event command 53 xx): the six monsters of each one and their places
public partial class L2EncountersPage
{
    private List<L2Option> _storyMonsters = new();
    private bool _storyLoading;

    public ObservableCollection<string> StoryNames { get; } = new();
    [ObservableProperty] private int _selectedStory = -1;
    [ObservableProperty] private List<L2StorySlot> _storySlots = new();
    [ObservableProperty] private string _storyText = "";
    // the squares of the battle sprite buffer the battle's kinds need, and whether that's too much
    [ObservableProperty] private string _storyRoom = "";
    [ObservableProperty] private bool _storyRoomWarn;

    private void ShowStoryRoom(int battle)
    {
        var room = L2BattleRoom.Story(_rom!, battle, Sizes);
        StoryRoomWarn = room.TooBig || room.TooManyKinds;
        StoryRoom = room.Monsters.Count == 0 ? "" :
            room.TooManyKinds
                ? $"Too many kinds: {room.Kinds} different monsters. The game keeps {L2BattleRoom.MaxKinds} kinds per battle and drops back to its title screen on a 4th. Use at most {L2BattleRoom.MaxKinds} different monsters." +
                  (room.TooBig ? $" Their sprites would also need {room.Squares} of the {L2BattleRoom.Squares} squares." : "")
                : room.TooBig
                ? $"Too big: its kinds need {L2BattleRoom.Describe(room, _monsterList)}. All the kinds of monster in one battle share {L2BattleRoom.Squares} squares of 16 x 16 for their sprites; past that their pictures come out wrong."
                : $"Sprite room: its {room.Kinds} kind{(room.Kinds == 1 ? " of monster needs" : "s of monster need")} {room.Squares} of the {L2BattleRoom.Squares} squares of 16 x 16 (at most {L2BattleRoom.MaxKinds} kinds in a battle).";
    }

    private void InitStory()
    {
        _storyMonsters = new[] { new L2Option(0xFF, "-") }.Concat(_monsterList.Where(m => m.Index < 0xFF)
            .Select(m => new L2Option(m.Index, $"{m.Index:X2} " + (m.Unused ? "(unused slot)" : m.Name.Trim())))).ToList();
        _storyLoading = true;
        StoryNames.Clear();
        for (int battle = 0; battle < L2Encounters.StoryBattleCount(_rom!); battle++) StoryNames.Add(StoryLabel(battle));
        _storyLoading = false;
        SelectedStory = -1; SelectedStory = 4;
    }

    private string StoryLabel(int battle)
    {
        var room = L2BattleRoom.Story(_rom!, battle, Sizes);
        return $"{battle:X2}  " + string.Join(", ", L2Encounters.StoryBattle(_rom!, battle).Where(s => s.Monster != 0xFF)
            .Select(s => _storyMonsters.FirstOrDefault(o => o.Value == s.Monster)?.Label[3..] ?? s.Monster.ToString("X2"))) +
               (room.TooManyKinds ? $"  (! {room.Kinds} kinds)" : room.TooBig ? $"  (! {room.Squares} squares)" : "");
    }

    partial void OnSelectedStoryChanged(int value)
    {
        if (_rom == null || value < 0 || _storyLoading) return;
        StorySlots = L2Encounters.StoryBattle(_rom, value).Select((s, i) => new L2StorySlot
        {
            Label = $"Monster {i + 1}", Options = _storyMonsters, Monster = s.Monster, X = s.X, Y = s.Y, Changed = StoryEdited,
        }).ToList();
        var shared = L2Encounters.StorySharing(_rom, value);
        StoryText = $"Event command 53 {value:X2} (\"Start a story battle\" on the Events page) starts it. Each monster has its place on the screen (x, y; the game's go from about 30 to 200 across and 0 to 72 down)." +
                    (shared.Count > 0 ? $" Battles {string.Join(", ", shared.Select(b => b.ToString("X2")))} use the same record: a change here changes them too." : "");
        ShowStoryRoom(value);
    }

    // a new story battle: a copy of the one shown (so its settings stay valid), then picked
    [RelayCommand]
    private void AddStory()
    {
        if (_rom == null || _main == null) return;
        if (L2Encounters.CantAddStoryBattle(_rom) is { } why) { StoryText = "Not added: " + why; return; }
        try
        {
            bool alreadyMoved = L2Encounters.StoryTable(_rom) != L2Encounters.StoryBattles;
            int copiedFrom = Math.Max(0, SelectedStory);
            int battle = L2Encounters.AddStoryBattle(_rom, copiedFrom);
            _main.NotifyRomModified($"Lufia II: new story battle {battle:X2}");
            _readVersion = _rom.Version;
            _storyLoading = true;
            StoryNames.Add(StoryLabel(battle));
            _storyLoading = false;
            SelectedStory = battle;
            StoryText = $"New story battle {battle:X2}, a copy of battle {copiedFrom:X2}: change its monsters below. An event starts it with \"Start a story battle\" (53 {battle:X2}) on the Events or Cutscenes page." +
                        (alreadyMoved ? "" : " The story battles moved to space of their own to make room (up to 256).");
        }
        catch (Exception ex) { StoryText = "Not added: " + ex.Message; }
    }

    private void StoryEdited()
    {
        if (_rom == null || _main == null || SelectedStory < 0) return;
        L2Encounters.WriteStoryBattle(_rom, SelectedStory, StorySlots.Select(s => (s.Monster, Math.Clamp(s.X, 0, 255), Math.Clamp(s.Y, 0, 255))).ToList());
        _main.NotifyRomModified($"Lufia II: story battle {SelectedStory:X2}");
        _readVersion = _rom.Version;
        int edited = SelectedStory;
        _storyLoading = true;
        foreach (int battle in L2Encounters.StorySharing(_rom, edited).Append(edited)) StoryNames[battle] = StoryLabel(battle);
        SelectedStory = edited;
        _storyLoading = false;
        ShowStoryRoom(edited);
    }
}
