using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

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
    [ObservableProperty] private List<L2Slot> _curveSlots = new();
    [ObservableProperty] private List<L2Row> _curveRows = new();
    [ObservableProperty] private string _preview = "";
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
        var curves = Enumerable.Range(0, L2Capsules.CurveCount)
            .Select(c => new L2Option(c, $"{c:X2}: {string.Join(" ", L2Capsules.Curve(rom, c).Take(7))}")).ToList();
        CurveSlots = _r.Curves.Select((c, i) => new L2Slot { Label = L2Capsules.CurveStats[i], Options = curves, Value = c, Changed = Edited }).ToList();
        LoadCurves();
        AiText = ScriptsText(rom, L2AiScripts.Kind.CapsuleAi, L2AiScripts.Kind.CapsuleDefense, index);
        Refresh();
    }

    private void LoadCurves()
    {
        CurveRows = _r!.Curves.Select((c, i) => new L2Row
        {
            Label = $"{L2Capsules.CurveStats[i]} ({c:X2})",
            Cells = L2Capsules.Curve(Rom!, c).Select((v, b) => new L2Word { Label = $"{b * 16}", Bit = c, Value = v, Changed = CurveEdited }).ToList(),
        }).ToList();
    }

    private void Refresh()
    {
        if (_r == null) return;
        string Row(int lv) => $"Lv {lv,2}: " + string.Join("  ", L2Capsules.StatsAt(Rom!, _r, lv).Select((s, i) => $"{L2Capsules.StatNames[i]} {s}"));
        Preview = string.Join("\n", new[] { 1, 10, 20, 30, 50, 75, 99 }.Select(Row)) +
                  $"\n\nForm {_r.Form}: growth is full in the level bands below {_r.Form * 16}, half in {_r.Form * 16}-{_r.Form * 16 + 15}, a quarter above.";
    }

    protected override bool IsField(string p) => p is nameof(MonsterName) or nameof(Name) or nameof(Type);

    [RelayCommand] private void EditTurnScript() { if (_r != null) EditScript(L2AiScripts.Kind.CapsuleAi, _r.Index, $"{MonsterName} {_r.Form} {_r.Name}"); }
    [RelayCommand] private void EditHitScript() { if (_r != null) EditScript(L2AiScripts.Kind.CapsuleDefense, _r.Index, $"{MonsterName} {_r.Form} {_r.Name}"); }

    protected override string? Write()
    {
        if (_r == null) return null;
        _r.Name = Name; _r.Type = Type;
        for (int s = 0; s < 3; s++) { _r.Attacks[s] = AttackSlots[s].Value; _r.Learned[s] = AttackSlots[s + 3].Value; }
        for (int s = 0; s < 8; s++) _r.Stats[s] = Stats[s].Value;
        bool curvesChanged = false;
        for (int s = 0; s < 6; s++) { curvesChanged |= _r.Curves[s] != CurveSlots[s].Value; _r.Curves[s] = CurveSlots[s].Value; }
        L2Capsules.Write(Rom!, _r);
        L2Capsules.WriteMonsterName(Rom!, _r.Monster, MonsterName);
        if (curvesChanged) LoadCurves();
        Refresh();
        return $"capsule {_r.Name}";
    }

    private void CurveEdited()
    {
        if (Loading || Rom == null || Main == null || _r == null) return;
        foreach (var row in CurveRows) L2Capsules.WriteCurve(Rom, row.Cells[0].Bit, row.Cells.Select(c => c.Value).ToArray());
        Main.NotifyRomModified("Lufia II: capsule growth curves");
        Refresh();
    }
}
