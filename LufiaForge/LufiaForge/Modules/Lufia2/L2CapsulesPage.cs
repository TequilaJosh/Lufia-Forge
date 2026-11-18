using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;
using LufiaForge.Modules.Common;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.Lufia2;

// capsule monsters: each of the 35 forms (name, type, attacks, level 1 stats, growth curves, AI), the monsters'
// default names, the growth curves themselves (shared, so changing one changes every form using it), and feeding
// (L2CapsulesPage.Feed.cs)
public partial class L2CapsulesPage : L2Page
{
    private L2Capsules.Record? _record;
    private string[] _attackNames = Array.Empty<string>(), _attacks = Array.Empty<string>();

    public List<L2Option> TypeOptions { get; } = L2Capsules.Types.Select((t, i) => new L2Option(i == 0 ? 0 : 1 << (i - 1), t)).ToList();

    [ObservableProperty] private string _monsterName = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _type;
    [ObservableProperty] private List<L2Slot> _attackSlots = new();
    [ObservableProperty] private List<L2Word> _stats = new();
    // growth: the stat shown, its curve as gains per level (shared by the forms that use it), the chart and a table
    public List<L2Option> GrowthStats { get; } = L2Capsules.CurveStats.Select((s, i) => new L2Option(i, s)).ToList();
    [ObservableProperty] private int _growthStat;
    [ObservableProperty] private List<L2Option> _curveOptions = new();
    [ObservableProperty] private int _curve;
    [ObservableProperty] private List<L2Band> _bands = new();
    [ObservableProperty] private string _curveNote = "";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _chart;
    // the levels where you can drag the chart's line: the end of each band of 16 levels
    public IReadOnlyList<int> ChartHandles { get; } = Enumerable.Range(0, 7).Select(BandEnd).ToList();
    [ObservableProperty] private string _levelTable = "";
    private static readonly int[] StatOfCurve = { 0, 3, 4, 5, 6, 7 };   // HP STR AGL INT GUT MGR in the record's stats
    private static readonly Color[] Colors =
    {
        Color.FromRgb(0xE8, 0x60, 0x60), Color.FromRgb(0xF0, 0xB0, 0x40), Color.FromRgb(0x60, 0xD0, 0x80),
        Color.FromRgb(0xB0, 0x80, 0xF0), Color.FromRgb(0xD0, 0xD0, 0x60), Color.FromRgb(0x60, 0xD0, 0xD0),
    };
    [ObservableProperty] private string _aiText = "";
    [ObservableProperty] private BitmapSource? _pictureImage;

    protected override IEnumerable<string> ReadNames()
    {
        _attackNames = L2Capsules.AttackNameList(Rom!);
        _attacks = L2Capsules.AttackList(Rom!);
        return L2Capsules.All(Rom!).Select(r => $"{L2Capsules.MonsterName(Rom!, r.Monster),-6}{r.Form}  {r.Name}");
    }

    protected override void Load(int index)
    {
        var rom = Rom!;
        _record = L2Capsules.Read(rom, index);
        MonsterName = L2Capsules.MonsterName(rom, _record.Monster);
        Name = _record.Name; Type = _record.Type;
        PictureImage = Picture(L2Gfx.CapsuleSprite(rom, index));
        var names = _attackNames.Select((n, i) => new L2Option(i, i == 0 ? "(none)" : $"{i:X2} {n}")).ToList();
        AttackSlots = Enumerable.Range(0, 3).Select(slot => new L2Slot { Label = $"Attack {slot + 1}", Options = names, Value = _record.Attacks[slot], Changed = Edited })
            .Concat(Enumerable.Range(0, 3).Select(slot => new L2Slot { Label = $"Learned {slot + 1}", Options = names, Value = _record.Learned[slot], Changed = Edited })).ToList();
        Stats = _record.Stats.Select((v, i) => new L2Word { Label = L2Capsules.StatNames[i], Bit = i, Value = v, Changed = Edited }).ToList();
        CurveOptions = Enumerable.Range(0, L2Capsules.CurveCount).Select(c => new L2Option(c, CurveLabel(rom, c))).ToList();
        AiText = ScriptsText(rom, L2AiScripts.Kind.CapsuleAi, L2AiScripts.Kind.CapsuleDefense, index);
        ShowCurve();
        Refresh();
        LoadFeed();
    }

    // a curve in words: its gain per level in the first, middle and last bands, and how many forms use it
    private static string CurveLabel(Core.RomBuffer rom, int curve)
    {
        var gains = L2Capsules.Curve(rom, curve);
        int users = L2Capsules.All(rom).Sum(r => r.Curves.Count(x => x == curve));
        return $"{curve:X2}: +{gains[0] / 16.0:0.##} a level early, +{gains[3] / 16.0:0.##} mid, +{gains[6] / 16.0:0.##} late  ({users} use{(users == 1 ? "s" : "")} it)";
    }

    partial void OnGrowthStatChanged(int value)
    {
        if (_record == null || Rom == null) return;
        bool wasLoading = Loading; Loading = true;
        try { ShowCurve(); } finally { Loading = wasLoading; }
        Refresh();
    }

    // the curve of the stat shown: its 7 bands of 16 levels as gains per level, and how much of each this form gets
    private void ShowCurve()
    {
        int stat = Math.Clamp(GrowthStat, 0, 5), curve = _record!.Curves[stat];
        Curve = curve;
        var gains = L2Capsules.Curve(Rom!, curve);
        Bands = Enumerable.Range(0, 7).Select(band => new L2Band
        {
            Label = $"Levels {Math.Max(2, band * 16 + 1)}-{Math.Min(band * 16 + 16, 99)}",
            Unit = band < _record.Form ? "per level (all of it in this form)" : band == _record.Form ? "per level (half of it in this form)" : "per level (a quarter in this form)",
            Value = Math.Round(gains[band] / 16.0, 2),
            Changed = CurveEdited,
        }).ToList();
        var users = L2Capsules.All(Rom!).Where(r => r.Curves.Contains(curve)).Select(r => $"{L2Capsules.MonsterName(Rom!, r.Monster)} {r.Form}").ToList();
        CurveNote = $"Curve {curve:X2} gives {L2Capsules.CurveStats[stat]} its growth. A form grows fully in the bands below its form number x 16 levels, " +
                    $"by half in the next and by a quarter after (this is form {_record.Form}). " +
                    (users.Count > 1 ? $"Shared by {users.Count} stats of: {string.Join(", ", users.Distinct())}. A change here changes them all; pick another curve to change just this one." : "Only this stat uses it.");
    }

    private void Refresh()
    {
        if (_record == null) return;
        var stats = Enumerable.Range(1, 99).Select(lv => L2Capsules.StatsAt(Rom!, _record, lv)).ToArray();
        Chart = Enumerable.Range(0, 6).Select(i => new ChartSeries(L2Capsules.CurveStats[i], stats.Select(x => (double)x[StatOfCurve[i]]).ToArray(), Colors[i], i == GrowthStat)).ToList();
        string Row(int lv) => $"{lv,5}  " + string.Join("", stats[lv - 1].Select(x => $"{x,6}"));
        LevelTable = "Level" + string.Join("", L2Capsules.StatNames.Select(x => $"{x,6}")) + "\n" +
                     string.Join("\n", new[] { 1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99 }.Select(Row)) +
                     "\n(ATP and DFP don't grow; the game adds equipment-like bonuses on top)";
    }

    protected override bool IsField(string p) => p is nameof(MonsterName) or nameof(Name) or nameof(Type) or nameof(Curve);

    [RelayCommand] private void EditTurnScript() { if (_record != null) EditScript(L2AiScripts.Kind.CapsuleAi, _record.Index, $"{MonsterName} {_record.Form} {_record.Name}"); }
    [RelayCommand] private void EditHitScript() { if (_record != null) EditScript(L2AiScripts.Kind.CapsuleDefense, _record.Index, $"{MonsterName} {_record.Form} {_record.Name}"); }

    protected override string? Write()
    {
        if (_record == null) return null;
        _record.Name = Name; _record.Type = Type;
        for (int slot = 0; slot < 3; slot++) { _record.Attacks[slot] = AttackSlots[slot].Value; _record.Learned[slot] = AttackSlots[slot + 3].Value; }
        for (int stat = 0; stat < 8; stat++) _record.Stats[stat] = Stats[stat].Value;
        bool curveChanged = _record.Curves[Math.Clamp(GrowthStat, 0, 5)] != Curve;
        _record.Curves[Math.Clamp(GrowthStat, 0, 5)] = Curve;
        L2Capsules.Write(Rom!, _record);
        L2Capsules.WriteMonsterName(Rom!, _record.Monster, MonsterName);
        if (curveChanged) { bool wasLoading = Loading; Loading = true; try { ShowCurve(); } finally { Loading = wasLoading; } }
        Refresh();
        return $"capsule {_record.Name}";
    }

    private static int BandEnd(int band) => Math.Min(band * 16 + 16, 99);

    // a chart handle got dragged to value. the band before it gets the gain that reaches that value, the band after it
    // the gain that still reaches the next handle. the form gets all, half or a quarter of a band's gain, so the
    // curve's value is the gain divided by that
    public void DragBand(int band, double value)
    {
        if (Rom == null || _record == null || band < 0 || band >= Bands.Count) return;
        int stat = Math.Clamp(GrowthStat, 0, 5);
        int Stat(int lv) => L2Capsules.StatsAt(Rom, _record, lv)[StatOfCurve[stat]];
        double Share(int b) => b < _record.Form ? 1 : b == _record.Form ? 0.5 : 0.25;
        double Clamp(double gain) => Math.Round(Math.Clamp(gain, 0, 255 / 16.0), 2);
        int start = band == 0 ? 1 : BandEnd(band - 1), end = BandEnd(band);
        double after = band + 1 < Bands.Count ? Stat(BandEnd(band + 1)) : 0;
        Loading = true;
        try
        {
            Bands[band].Value = Clamp((value - Stat(start)) / Math.Max(1, end - start) / Share(band));
            if (band + 1 < Bands.Count) Bands[band + 1].Value = Clamp((after - value) / Math.Max(1, BandEnd(band + 1) - end) / Share(band + 1));
        }
        finally { Loading = false; }
        CurveEdited();
    }

    private void CurveEdited()
    {
        if (Loading || Rom == null || Main == null || _record == null) return;
        var gains = L2Capsules.Curve(Rom, Curve);
        for (int b = 0; b < Bands.Count; b++) gains[b] = Math.Clamp((int)Math.Round(Bands[b].Value * 16), 0, 255);
        L2Capsules.WriteCurve(Rom, Curve, gains);
        Main.NotifyRomModified($"Lufia II: capsule growth curve {Curve:X2}");
        CurveOptions[Curve] = new L2Option(Curve, CurveLabel(Rom, Curve));
        Refresh();
    }
}
