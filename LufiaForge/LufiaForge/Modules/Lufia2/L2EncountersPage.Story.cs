using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    private void InitStory()
    {
        _storyMonsters = new[] { new L2Option(0xFF, "-") }.Concat(_monsterList.Where(m => m.Index < 0xFF)
            .Select(m => new L2Option(m.Index, $"{m.Index:X2} " + (m.Unused ? "(unused slot)" : m.Name.Trim())))).ToList();
        _storyLoading = true;
        StoryNames.Clear();
        for (int b = 0; b < L2Encounters.StoryBattleCount; b++) StoryNames.Add(StoryLabel(b));
        _storyLoading = false;
        SelectedStory = -1; SelectedStory = 4;
    }

    private string StoryLabel(int b) =>
        $"{b:X2}  " + string.Join(", ", L2Encounters.StoryBattle(_rom!, b).Where(s => s.Monster != 0xFF)
            .Select(s => _storyMonsters.FirstOrDefault(o => o.Value == s.Monster)?.Label[3..] ?? s.Monster.ToString("X2")));

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
    }
}
