using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Audio;
using LufiaForge.Core.Lufia2;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Block = LufiaForge.Core.Lufia2.L2Attacks.Block;
using Use = LufiaForge.Core.Lufia2.L2Attacks.Use;

namespace LufiaForge.Modules.Lufia2;

/// <summary>One effect of the attack shown (cause a status, raise a stat...): what it is and its values in place.</summary>
public partial class L2AttackEffect : ObservableObject
{
    public byte Op { get; init; }
    /// <summary>Which one of this kind in the attack (the same one is changed in every monster).</summary>
    public int Ordinal { get; init; }
    public string Title { get; init; } = "";
    public List<L2Option>? Choices { get; init; }
    public bool HasChoice => Choices != null;
    public string NumberLabel { get; init; } = "";
    public bool HasNumber => NumberLabel.Length > 0;
    public bool HasSpread { get; init; }
    public string Hint { get; init; } = "";
    [ObservableProperty] private int _choice;
    [ObservableProperty] private int _number;
    [ObservableProperty] private int _spread;
    public Action<L2AttackEffect>? Changed { get; set; }
    public Action<L2AttackEffect>? Removed { get; set; }
    public bool Loading { get; set; }
    partial void OnChoiceChanged(int value) { if (!Loading) Changed?.Invoke(this); }
    partial void OnNumberChanged(int value) { if (!Loading) Changed?.Invoke(this); }
    partial void OnSpreadChanged(int value) { if (!Loading) Changed?.Invoke(this); }
    [RelayCommand] private void Remove() => Removed?.Invoke(this);
}

/// <summary>
/// Monster attacks: every named attack of the monsters' battle scripts (<see cref="L2Attacks"/>), each part of it edited
/// in place (name, target, how it hits, damage, element, criticals, animation, sound, effects), for the monster picked
/// or for every monster that uses the attack. A change is written at once (Edit, Undo takes it back).
/// </summary>
public partial class L2AttacksPage : L2Page
{
    private List<Use> _uses = new();
    private string[] _attackNames = Array.Empty<string>();
    private string[] _monsters = Array.Empty<string>();

    public List<L2Option> UseOptions { get; private set; } = new();
    public List<L2Option> TargetOptions { get; } = L2Attacks.Targets.Select((t, i) => new L2Option(i, t.Label)).ToList();
    public List<L2Option> HitOptions { get; } = L2Attacks.Hits.Select(h => new L2Option((int)h.Value, h.Label)).ToList();
    public List<L2Option> DamageKindOptions { get; } = new()
    {
        new((int)L2Attacks.DamageKind.Physical, "physical (DFP blocks it)"),
        new((int)L2Attacks.DamageKind.Magic, "magic (like the spells)"),
        new((int)L2Attacks.DamageKind.Fixed, "fixed (exactly this much)"),
    };
    public List<L2Option> AnimationOptions { get; private set; } = new();
    public List<L2Option> SoundOptions { get; private set; } = new();
    public List<L2Option> EffectKinds { get; } = L2Attacks.EffectKinds.Select(k => new L2Option(k.Op, k.Label)).ToList();
    public ObservableCollection<L2Flag> Elements { get; } = new();
    public ObservableCollection<L2AttackEffect> Effects { get; } = new();

    [ObservableProperty] private int _selectedUse = -1;
    [ObservableProperty] private bool _applyAll;
    [ObservableProperty] private string _applyAllText = "";
    [ObservableProperty] private bool _hasUses;
    [ObservableProperty] private string _usedByText = "";
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private int _target;
    [ObservableProperty] private int _hit;
    [ObservableProperty] private bool _hasDamage;
    [ObservableProperty] private int _damageKind;
    [ObservableProperty] private string _power = "";
    [ObservableProperty] private int _spread;
    [ObservableProperty] private bool _canSpread;
    [ObservableProperty] private bool _hasCritical;
    [ObservableProperty] private int _criticalChance;
    [ObservableProperty] private int _criticalMultiplier;
    [ObservableProperty] private int _animation;
    [ObservableProperty] private string _animationNote = "";
    /// <summary>The animation the attack shows (its own or its name's), for the preview; -1 = none.</summary>
    [ObservableProperty] private int _previewAnimation = -1;
    [ObservableProperty] private int _previewMonster = -1;
    [ObservableProperty] private int _sound;
    [ObservableProperty] private string _hitNote = "";
    [ObservableProperty] private string _stepsText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEdited;

    protected override bool IsField(string property) => false;

    /// <summary>The ROM, for the view's preview and Listen.</summary>
    public Core.RomBuffer? CurrentRom => Rom;   // every field writes through Change

    protected override void OnListRead()
    {
        var rom = Rom!;
        _attackNames = L2AiScripts.MonsterAttackNames(rom);
        _monsters = L2Battle.Monsters(rom).Select(m => m.Unused ? "(unused slot)" : m.Name.Trim()).ToArray();
        _uses = L2Attacks.Uses(rom);
        AnimationOptions = new[] { new L2Option(-1, "the name's own animation") }
            .Concat(L2Magic.Animations(rom).Select(a => new L2Option(a.Value, $"{a.Value:X2} {a.Name}"))).ToList();
        SoundOptions = new[] { new L2Option(-1, "(none)") }
            .Concat(Enumerable.Range(0, SoundNames.Count(true)).Select(i => new L2Option(i, SoundNames.Label(true, i)))).ToList();
        OnPropertyChanged(nameof(AnimationOptions)); OnPropertyChanged(nameof(SoundOptions));
        if (Elements.Count == 0)
            foreach (var (bit, name) in L2AiScripts.Elements)
                Elements.Add(new L2Flag { Label = name, Bit = bit, Changed = ElementsChanged });
    }

    protected override IEnumerable<string> ReadNames()
    {
        for (int n = 1; n < _attackNames.Length; n++)
        {
            int c = _uses.Count(u => u.Name == n);
            yield return $"{n:X2} {_attackNames[n]}" + (c == 0 ? "  (not used)" : c == 1 ? "" : $"  ({c})");
        }
    }

    private int NameId => SelectedIndex + 1;
    private List<Use> UsesOfName => _uses.Where(u => u.Name == NameId).ToList();
    private Use? CurrentUse => SelectedUse >= 0 && SelectedUse < UsesOfName.Count ? UsesOfName[SelectedUse] : null;

    private string UseLabel(Use u) => $"{u.Monster:X2} {(u.Monster < _monsters.Length ? _monsters[u.Monster] : "?")}" +
                                      (u.Kind == Kind.MonsterDefense ? " (when it is hit)" : "") + (u.Ordinal > 0 ? $" (#{u.Ordinal + 1})" : "");

    protected override void Load(int index)
    {
        var uses = UsesOfName;
        UseOptions = uses.Select((u, i) => new L2Option(i, UseLabel(u))).ToList();
        OnPropertyChanged(nameof(UseOptions));
        HasUses = uses.Count > 0;
        UsedByText = uses.Count == 0 ? "No monster uses this attack. Its name can still be changed."
                   : uses.Count == 1 ? "Used by 1 monster." : $"Used by {uses.Count} monsters.";
        ApplyAllText = $"Change it for all {uses.Count} at once (only the part changed: each keeps its other values)";
        ApplyAll = false;
        NameText = index + 1 < _attackNames.Length ? _attackNames[index + 1] : "";
        SelectedUse = -1;
        SelectedUse = uses.Count > 0 ? 0 : -1;
        if (uses.Count == 0) ShowAttack(null);
    }

    partial void OnSelectedUseChanged(int value)
    {
        if (Rom == null || value < 0) return;
        bool was = Loading;
        Loading = true;
        try { ShowAttack(CurrentUse); } finally { Loading = was; }
    }

    /// <summary>Fills the fields from the attack of <paramref name="u"/>.</summary>
    private void ShowAttack(Use? u)
    {
        Effects.Clear();
        if (u == null || Rom == null) { StepsText = ""; PreviewAnimation = -1; IsEdited = false; return; }
        Block b;
        try { b = L2Attacks.Open(Rom, u); }
        catch (Exception e) { Status = e.Message; return; }
        var names = L2AiScripts.NameLists.From(Rom);
        IsEdited = L2AiScripts.Load(Rom, u.Kind, u.Monster).Edited;
        string key = L2Attacks.Target(b);
        Target = Math.Max(0, Array.FindIndex(L2Attacks.Targets, t => t.Key == key));
        var hit = L2Attacks.HowItHits(b);
        Hit = (int)hit;
        HitNote = hit switch
        {
            L2Attacks.Hit.Attack => "A normal attack (its ATP against the target's DFP) with this name. Its effects need \"only its effects\" or its own damage.",
            L2Attacks.Hit.EffectsOnly => "No damage: only the effects below happen.",
            L2Attacks.Hit.ShowOnly => "The name shows and nothing else happens (the game's taunts and laughs).",
            _ => "",
        };
        var dmg = L2Attacks.Damage(b);
        HasDamage = dmg != null;
        DamageKind = (int)(dmg?.Kind ?? L2Attacks.DamageKind.Physical);
        Power = dmg == null ? "" : dmg.Value.Power?.ToString() ?? "(worked out by its script)";
        Spread = dmg?.Spread ?? 0;
        CanSpread = dmg != null && dmg.Value.Kind != L2Attacks.DamageKind.Fixed;
        int el = L2Attacks.Element(b);
        foreach (var f in Elements) f.IsChecked = (el & f.Bit) != 0;
        var crit = L2Attacks.Critical(b);
        HasCritical = crit != null;
        CriticalChance = crit?.Chance ?? 10;
        CriticalMultiplier = crit?.Multiplier ?? 32;
        int? anim = L2Attacks.Animation(b);
        int own = L2Attacks.NameAnimation(Rom, u.Name);
        Animation = anim ?? -1;
        if (anim is int a && AnimationOptions.All(o => o.Value != a)) { AnimationOptions = AnimationOptions.Append(new L2Option(a, $"{a:X2} animation")).ToList(); OnPropertyChanged(nameof(AnimationOptions)); }
        AnimationNote = anim != null ? "" : own == 0xE0 ? "Its name brings no animation: none plays." : $"Its name brings animation {own:X2} ({(own < names.Animations.Length ? names.Animations[own] : "?")}).";
        PreviewMonster = u.Monster;
        PreviewAnimation = anim ?? (own == 0xE0 ? -1 : own);
        Sound = L2Attacks.Sound(b) ?? -1;
        var counts = new Dictionary<byte, int>();
        foreach (var s in L2Attacks.Effects(b))
        {
            int k = counts.GetValueOrDefault(s.Op);
            counts[s.Op] = k + 1;
            Effects.Add(MakeEffect(s, k));
        }
        StepsText = string.Join("\n", b.All.Select(s => L2AiScripts.Describe(s, b.Steps, names)));
    }

    private static List<L2Option> Opts(string[] list) => list.Select((n, i) => new L2Option(i, n)).ToList();

    private L2AttackEffect MakeEffect(L2AiScripts.Step s, int ordinal)
    {
        string title = L2Attacks.EffectKinds.First(k => k.Op == s.Op).Label;
        L2AttackEffect e = s.Op switch
        {
            0x27 or 0x26 => new L2AttackEffect { Op = s.Op, Ordinal = ordinal, Title = title, Choices = Opts(L2AiScripts.Statuses), NumberLabel = "chance %" },
            0x25 => new L2AttackEffect { Op = s.Op, Ordinal = ordinal, Title = title, Choices = Opts(L2AiScripts.Restores), NumberLabel = "by % (minus lowers)" },
            0x24 => new L2AttackEffect { Op = s.Op, Ordinal = ordinal, Title = title, Choices = Opts(L2AiScripts.Restores), NumberLabel = "by", HasSpread = true },
            0x4A => new L2AttackEffect { Op = s.Op, Ordinal = ordinal, Title = title, NumberLabel = "times" },
            0x4C or 0x35 => new L2AttackEffect { Op = s.Op, Ordinal = ordinal, Title = title, NumberLabel = "number",
                Hint = "One of the game's own; which number does what isn't named yet: try them with Test in the game." },
            _ => new L2AttackEffect { Op = s.Op, Ordinal = ordinal, Title = title },
        };
        e.Loading = true;
        switch (s.Op)
        {
            case 0x27 or 0x26: e.Choice = s.Args[0]; e.Number = s.Args[1]; break;
            case 0x25: e.Choice = s.Args[0]; e.Number = (sbyte)s.Args[1]; break;
            case 0x24: e.Choice = s.Args[0]; e.Number = (short)(s.Args[1] | s.Args[2] << 8); e.Spread = s.Args[3]; break;
            case 0x4A or 0x4C or 0x35: e.Number = s.Args[0]; break;
        }
        e.Loading = false;
        e.Changed = EffectChanged;
        e.Removed = EffectRemoved;
        return e;
    }

    // ── writing ───────────────────────────────────────────────────────────────

    /// <summary>Applies <paramref name="op"/> to the attack of the monster picked, or of every monster that uses it, and
    /// writes their scripts.</summary>
    private void Change(string what, Action<Block> op)
    {
        if (Loading || Rom == null || Main == null || CurrentUse is not { } cur) return;
        var targets = ApplyAll ? UsesOfName : new List<Use> { cur };
        try
        {
            // all are changed on copies first, so a failure leaves every monster as it was
            var done = new List<(Use, Block)>();
            foreach (var u in targets)
            {
                var b = L2Attacks.Open(Rom, u);
                op(b);
                done.Add((u, b));
            }
            foreach (var (u, b) in done) L2Attacks.Save(Rom, u, b);
        }
        catch (Exception e) { Status = "Not changed: " + e.Message; Reshow(); return; }
        string who = targets.Count == 1 ? UseLabel(targets[0]) : $"{targets.Count} monsters";
        Main.NotifyRomModified($"Lufia II: {what} of the attack {_attackNames[NameId]} ({who})");
        Status = $"Changed for {who}.";
        MarkRead();
        Reshow();
    }

    /// <summary>Reads the uses again (steps moved) and shows the same monster's attack.</summary>
    private void Reshow()
    {
        if (Rom == null) return;
        int keep = SelectedUse;
        _uses = L2Attacks.Uses(Rom);
        Loading = true;
        try { ShowAttack(CurrentUse); SelectedUse = keep; } finally { Loading = false; }
    }

    partial void OnNameTextChanged(string value)
    {
        if (Loading || Rom == null || Main == null || SelectedIndex < 0 || value == _attackNames[NameId]) return;
        if (L2Attacks.Rename(Rom, NameId, value) is { } why) { Status = "Not renamed: " + why; return; }
        Main.NotifyRomModified($"Lufia II: attack {NameId:X2} renamed to {value.Trim()}");
        MarkRead();
        Status = "Renamed (every monster that uses it shows the new name).";
        int i = SelectedIndex;
        RefreshList(i);
    }

    partial void OnTargetChanged(int value) => Change("the target", b => L2Attacks.SetTarget(b, L2Attacks.Targets[Math.Clamp(value, 0, L2Attacks.Targets.Length - 1)].Key));
    partial void OnHitChanged(int value) => Change("how it hits", b => L2Attacks.SetHowItHits(b, (L2Attacks.Hit)value));
    partial void OnDamageKindChanged(int value) => Change("the kind of damage", b => L2Attacks.SetDamageKind(b, (L2Attacks.DamageKind)value));

    partial void OnPowerChanged(string value)
    {
        if (Loading) return;
        if (!int.TryParse(value.Trim(), out int p) || p < 0 || p > 32767) { Status = "The damage is a number from 0 to 32767."; return; }
        Change("the damage", b => L2Attacks.SetPower(b, p));
    }

    partial void OnSpreadChanged(int value) => Change("the damage spread", b => L2Attacks.SetSpread(b, value));

    private void ElementsChanged()
    {
        if (Loading) return;
        int w = Elements.Where(f => f.IsChecked).Sum(f => f.Bit);
        Change("the element", b => L2Attacks.SetElement(b, w));
    }

    partial void OnHasCriticalChanged(bool value) => Change("the criticals", b => L2Attacks.SetCritical(b, value ? (CriticalChance, CriticalMultiplier) : null));
    partial void OnCriticalChanceChanged(int value) { if (HasCritical) Change("the critical chance", b => L2Attacks.SetCritical(b, (value, L2Attacks.Critical(b)?.Multiplier ?? CriticalMultiplier))); }
    partial void OnCriticalMultiplierChanged(int value) { if (HasCritical) Change("the critical damage", b => L2Attacks.SetCritical(b, (L2Attacks.Critical(b)?.Chance ?? CriticalChance, value))); }
    partial void OnAnimationChanged(int value) => Change("the animation", b => L2Attacks.SetAnimation(b, value < 0 ? null : value));
    partial void OnSoundChanged(int value) => Change("the sound", b => L2Attacks.SetSound(b, value < 0 ? null : value));

    [RelayCommand]
    private void AddEffect(object? op)
    {
        if (op is not L2Option o) return;
        Change("an added effect", b => L2Attacks.AddEffect(b, (byte)o.Value));
    }

    private void EffectChanged(L2AttackEffect e) => Change("an effect", b =>
    {
        var s = L2Attacks.EffectStep(b, e.Op, e.Ordinal) ?? L2Attacks.AddEffect(b, e.Op);
        switch (e.Op)
        {
            case 0x27 or 0x26: s.Args[0] = (byte)Math.Clamp(e.Choice, 0, 7); s.Args[1] = (byte)Math.Clamp(e.Number, 0, 100); break;
            case 0x25: s.Args[0] = (byte)Math.Clamp(e.Choice, 0, 10); s.Args[1] = (byte)(sbyte)Math.Clamp(e.Number, -128, 127); break;
            case 0x24:
                int v = Math.Clamp(e.Number, -32768, 32767);
                s.Args[0] = (byte)Math.Clamp(e.Choice, 0, 10); s.Args[1] = (byte)v; s.Args[2] = (byte)(v >> 8); s.Args[3] = (byte)Math.Clamp(e.Spread, 0, 255);
                break;
            case 0x4A or 0x4C or 0x35: s.Args[0] = (byte)Math.Clamp(e.Number, 0, 255); break;
        }
    });

    private void EffectRemoved(L2AttackEffect e) => Change("a removed effect", b =>
    {
        if (L2Attacks.EffectStep(b, e.Op, e.Ordinal) is { } s) L2Attacks.Remove(b, s);
    });

    [RelayCommand]
    private void EditScript()
    {
        if (CurrentUse is not { } u) return;
        EditScript(u.Kind, u.Monster, u.Monster < _monsters.Length ? _monsters[u.Monster] : $"monster {u.Monster:X2}");
        Reshow();
    }

    [RelayCommand]
    private void Revert()
    {
        if (Rom == null || Main == null || CurrentUse is not { } u) return;
        if (!L2AiScripts.Revert(Rom, u.Kind, u.Monster)) return;
        Main.NotifyRomModified($"Lufia II: {UseLabel(u)}'s battle script back to the game's");
        MarkRead();
        Status = "Back to the game's script for this monster.";
        Reshow();
    }

    protected override string? Write() => null;
}
