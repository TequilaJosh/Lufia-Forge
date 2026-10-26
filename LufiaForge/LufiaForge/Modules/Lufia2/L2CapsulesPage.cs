using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;
using LufiaForge.Modules.Common;
using Color = System.Windows.Media.Color;

namespace LufiaForge.Modules.Lufia2;

/// <summary>Capsule monsters: each of the 35 forms (name, type, attacks, level-1 stats, growth curves, AI), the
/// monsters' default names, and the growth curves themselves (shared, so a change there affects every form using it).</summary>
public partial class L2CapsulesPage : L2Page
{
    private L2Capsules.Record? _r;
    private string[] _attackNames = Array.Empty<string>(), _attacks = Array.Empty<string>();

    public List<L2Option> TypeOptions { get; } = L2Capsules.Types.Select((t, i) => new L2Option(i == 0 ? 0 : 1 << (i - 1), t)).ToList();

    [ObservableProperty] private string _monsterName = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _type;
    [ObservableProperty] private List<L2Slot> _attackSlots = new();
    [ObservableProperty] private List<L2Word> _stats = new();
    // growth: the stat shown, its curve (shared by the forms that use it) as gains per level, the chart and a table
    public List<L2Option> GrowthStats { get; } = L2Capsules.CurveStats.Select((s, i) => new L2Option(i, s)).ToList();
    [ObservableProperty] private int _growthStat;
    [ObservableProperty] private List<L2Option> _curveOptions = new();
    [ObservableProperty] private int _curve;
    [ObservableProperty] private List<L2Band> _bands = new();
    [ObservableProperty] private string _curveNote = "";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _chart;
    /// <summary>The levels the chart's line can be dragged at: the end of each band of 16 levels.</summary>
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
        _r = L2Capsules.Read(rom, index);
        MonsterName = L2Capsules.MonsterName(rom, _r.Monster);
        Name = _r.Name; Type = _r.Type;
        PictureImage = Picture(L2Gfx.CapsuleSprite(rom, index));
        var names = _attackNames.Select((n, i) => new L2Option(i, i == 0 ? "(none)" : $"{i:X2} {n}")).ToList();
        AttackSlots = Enumerable.Range(0, 3).Select(s => new L2Slot { Label = $"Attack {s + 1}", Options = names, Value = _r.Attacks[s], Changed = Edited })
            .Concat(Enumerable.Range(0, 3).Select(s => new L2Slot { Label = $"Learned {s + 1}", Options = names, Value = _r.Learned[s], Changed = Edited })).ToList();
        Stats = _r.Stats.Select((v, i) => new L2Word { Label = L2Capsules.StatNames[i], Bit = i, Value = v, Changed = Edited }).ToList();
        CurveOptions = Enumerable.Range(0, L2Capsules.CurveCount).Select(c => new L2Option(c, CurveLabel(rom, c))).ToList();
        AiText = ScriptsText(rom, L2AiScripts.Kind.CapsuleAi, L2AiScripts.Kind.CapsuleDefense, index);
        ShowCurve();
        Refresh();
    }

    /// <summary>A curve in words: its gain per level in the first, middle and last bands, and how many forms use it.</summary>
    private static string CurveLabel(Core.RomBuffer rom, int c)
    {
        var v = L2Capsules.Curve(rom, c);
        int users = L2Capsules.All(rom).Sum(r => r.Curves.Count(x => x == c));
        return $"{c:X2}: +{v[0] / 16.0:0.##} a level early, +{v[3] / 16.0:0.##} mid, +{v[6] / 16.0:0.##} late  ({users} use{(users == 1 ? "s" : "")} it)";
    }

    partial void OnGrowthStatChanged(int value)
    {
        if (_r == null || Rom == null) return;
        bool l = Loading; Loading = true;
        try { ShowCurve(); } finally { Loading = l; }
        Refresh();
    }

    /// <summary>The curve of the stat shown: its 7 bands of 16 levels as gains per level, and what this form gets of each.</summary>
    private void ShowCurve()
    {
        int st = Math.Clamp(GrowthStat, 0, 5), c = _r!.Curves[st];
        Curve = c;
        var v = L2Capsules.Curve(Rom!, c);
        Bands = Enumerable.Range(0, 7).Select(b => new L2Band
        {
            Label = $"Levels {Math.Max(2, b * 16 + 1)}-{Math.Min(b * 16 + 16, 99)}",
            Unit = b < _r.Form ? "per level (all of it in this form)" : b == _r.Form ? "per level (half of it in this form)" : "per level (a quarter in this form)",
            Value = Math.Round(v[b] / 16.0, 2),
            Changed = CurveEdited,
        }).ToList();
        var users = L2Capsules.All(Rom!).Where(r => r.Curves.Contains(c)).Select(r => $"{L2Capsules.MonsterName(Rom!, r.Monster)} {r.Form}").ToList();
        CurveNote = $"Curve {c:X2} gives {L2Capsules.CurveStats[st]} its growth. A form grows fully in the bands below its form number x 16 levels, " +
                    $"by half in the next and by a quarter after (this is form {_r.Form}). " +
                    (users.Count > 1 ? $"Shared by {users.Count} stats of: {string.Join(", ", users.Distinct())}. A change here changes them all; pick another curve to change just this one." : "Only this stat uses it.");
    }

    private void Refresh()
    {
        if (_r == null) return;
        var stats = Enumerable.Range(1, 99).Select(lv => L2Capsules.StatsAt(Rom!, _r, lv)).ToArray();
        Chart = Enumerable.Range(0, 6).Select(i => new ChartSeries(L2Capsules.CurveStats[i], stats.Select(x => (double)x[StatOfCurve[i]]).ToArray(), Colors[i], i == GrowthStat)).ToList();
        string Row(int lv) => $"{lv,5}  " + string.Join("", stats[lv - 1].Select(x => $"{x,6}"));
        LevelTable = "Level" + string.Join("", L2Capsules.StatNames.Select(x => $"{x,6}")) + "\n" +
                     string.Join("\n", new[] { 1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99 }.Select(Row)) +
                     "\n(ATP and DFP don't grow; the game adds equipment-like bonuses on top)";
    }

    protected override bool IsField(string p) => p is nameof(MonsterName) or nameof(Name) or nameof(Type) or nameof(Curve);

    [RelayCommand] private void EditTurnScript() { if (_r != null) EditScript(L2AiScripts.Kind.CapsuleAi, _r.Index, $"{MonsterName} {_r.Form} {_r.Name}"); }
    [RelayCommand] private void EditHitScript() { if (_r != null) EditScript(L2AiScripts.Kind.CapsuleDefense, _r.Index, $"{MonsterName} {_r.Form} {_r.Name}"); }

    protected override string? Write()
    {
        if (_r == null) return null;
        _r.Name = Name; _r.Type = Type;
        for (int s = 0; s < 3; s++) { _r.Attacks[s] = AttackSlots[s].Value; _r.Learned[s] = AttackSlots[s + 3].Value; }
        for (int s = 0; s < 8; s++) _r.Stats[s] = Stats[s].Value;
        bool curveChanged = _r.Curves[Math.Clamp(GrowthStat, 0, 5)] != Curve;
        _r.Curves[Math.Clamp(GrowthStat, 0, 5)] = Curve;
        L2Capsules.Write(Rom!, _r);
        L2Capsules.WriteMonsterName(Rom!, _r.Monster, MonsterName);
        if (curveChanged) { bool l = Loading; Loading = true; try { ShowCurve(); } finally { Loading = l; } }
        Refresh();
        return $"capsule {_r.Name}";
    }

    private static int BandEnd(int b) => Math.Min(b * 16 + 16, 99);

    /// <summary>A handle of the chart was dragged to <paramref name="value"/>: the band before it gets the gain that
    /// reaches that value, the band after it the gain that still reaches the next handle. The form gets all, half or a
    /// quarter of a band's gain, so the curve's value is the gain divided by that.</summary>
    public void DragBand(int band, double value)
    {
        if (Rom == null || _r == null || band < 0 || band >= Bands.Count) return;
        int st = Math.Clamp(GrowthStat, 0, 5);
        int Stat(int lv) => L2Capsules.StatsAt(Rom, _r, lv)[StatOfCurve[st]];
        double Share(int b) => b < _r.Form ? 1 : b == _r.Form ? 0.5 : 0.25;
        double Clamp(double g) => Math.Round(Math.Clamp(g, 0, 255 / 16.0), 2);
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
        if (Loading || Rom == null || Main == null || _r == null) return;
        var v = L2Capsules.Curve(Rom, Curve);
        for (int b = 0; b < Bands.Count; b++) v[b] = Math.Clamp((int)Math.Round(Bands[b].Value * 16), 0, 255);
        L2Capsules.WriteCurve(Rom, Curve, v);
        Main.NotifyRomModified($"Lufia II: capsule growth curve {Curve:X2}");
        CurveOptions[Curve] = new L2Option(Curve, CurveLabel(Rom, Curve));
        Refresh();
    }
}
