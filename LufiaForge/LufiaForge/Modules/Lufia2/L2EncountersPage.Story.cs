using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>The Encounters page's story battles (event command 53 xx): the six monsters of each and their places.</summary>
public partial class L2EncountersPage
{
    private List<L2Option> _storyMonsters = new();
    private bool _storyLoading;

    public ObservableCollection<string> StoryNames { get; } = new();
    [ObservableProperty] private int _selectedStory = -1;
    [ObservableProperty] private List<L2StorySlot> _storySlots = new();
    [ObservableProperty] private string _storyText = "";
    /// <summary>The squares of the battle sprite buffer the battle's kinds need, and whether it is too much.</summary>
    [ObservableProperty] private string _storyRoom = "";
    [ObservableProperty] private bool _storyRoomWarn;

    private void ShowStoryRoom(int b)
    {
        var n = L2BattleRoom.Story(_rom!, b, Sizes);
        StoryRoomWarn = n.TooBig || n.TooManyKinds;
        StoryRoom = n.Monsters.Count == 0 ? "" :
            n.TooManyKinds
                ? $"Too many kinds: {n.Kinds} different monsters. The game keeps {L2BattleRoom.MaxKinds} kinds per battle and drops back to its title screen on a 4th. Use at most {L2BattleRoom.MaxKinds} different monsters." +
                  (n.TooBig ? $" Their sprites would also need {n.Squares} of the {L2BattleRoom.Squares} squares." : "")
                : n.TooBig
                ? $"Too big: its kinds need {L2BattleRoom.Describe(n, _monsterList)}. All the kinds of monster in one battle share {L2BattleRoom.Squares} squares of 16 x 16 for their sprites; past that their pictures come out wrong."
                : $"Sprite room: its {n.Kinds} kind{(n.Kinds == 1 ? " of monster needs" : "s of monster need")} {n.Squares} of the {L2BattleRoom.Squares} squares of 16 x 16 (at most {L2BattleRoom.MaxKinds} kinds in a battle).";
    }

    private void InitStory()
    {
        _storyMonsters = new[] { new L2Option(0xFF, "-") }.Concat(_monsterList.Where(m => m.Index < 0xFF)
            .Select(m => new L2Option(m.Index, $"{m.Index:X2} " + (m.Unused ? "(unused slot)" : m.Name.Trim())))).ToList();
        _storyLoading = true;
        StoryNames.Clear();
        for (int b = 0; b < L2Encounters.StoryBattleCount(_rom!); b++) StoryNames.Add(StoryLabel(b));
        _storyLoading = false;
        SelectedStory = -1; SelectedStory = 4;
    }

    private string StoryLabel(int b)
    {
        var n = L2BattleRoom.Story(_rom!, b, Sizes);
        return $"{b:X2}  " + string.Join(", ", L2Encounters.StoryBattle(_rom!, b).Where(s => s.Monster != 0xFF)
            .Select(s => _storyMonsters.FirstOrDefault(o => o.Value == s.Monster)?.Label[3..] ?? s.Monster.ToString("X2"))) +
               (n.TooManyKinds ? $"  (! {n.Kinds} kinds)" : n.TooBig ? $"  (! {n.Squares} squares)" : "");
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

    /// <summary>A new story battle: a copy of the one shown (its settings stay valid), then picked.</summary>
    [RelayCommand]
    private void AddStory()
    {
        if (_rom == null || _main == null) return;
        if (L2Encounters.CantAddStoryBattle(_rom) is { } why) { StoryText = "Not added: " + why; return; }
        try
        {
            bool moved = L2Encounters.StoryTable(_rom) != L2Encounters.StoryBattles;
            int from = Math.Max(0, SelectedStory);
            int b = L2Encounters.AddStoryBattle(_rom, from);
            _main.NotifyRomModified($"Lufia II: new story battle {b:X2}");
            _storyLoading = true;
            StoryNames.Add(StoryLabel(b));
            _storyLoading = false;
            SelectedStory = b;
            StoryText = $"New story battle {b:X2}, a copy of battle {from:X2}: change its monsters below. An event starts it with \"Start a story battle\" (53 {b:X2}) on the Events or Cutscenes page." +
                        (moved ? "" : " The story battles moved to space of their own to make room (up to 256).");
        }
        catch (Exception ex) { StoryText = "Not added: " + ex.Message; }
    }

    private void StoryEdited()
    {
        if (_rom == null || _main == null || SelectedStory < 0) return;
        L2Encounters.WriteStoryBattle(_rom, SelectedStory, StorySlots.Select(s => (s.Monster, Math.Clamp(s.X, 0, 255), Math.Clamp(s.Y, 0, 255))).ToList());
        _main.NotifyRomModified($"Lufia II: story battle {SelectedStory:X2}");
        int keep = SelectedStory;
        _storyLoading = true;
        foreach (int b in L2Encounters.StorySharing(_rom, keep).Append(keep)) StoryNames[b] = StoryLabel(b);
        SelectedStory = keep;
        _storyLoading = false;
        ShowStoryRoom(keep);
    }
}
