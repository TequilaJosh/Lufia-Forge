using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Audio;
using LufiaForge.Core.Lufia2;
using Kind = LufiaForge.Core.Lufia2.L2AiScripts.Kind;
using Block = LufiaForge.Core.Lufia2.L2Attacks.Block;
using Use = LufiaForge.Core.Lufia2.L2Attacks.Use;

namespace LufiaForge.Modules.Lufia2;

// one effect of the attack shown (cause a status, raise a stat...), what it is plus its values to edit in place
public partial class L2AttackEffect : ObservableObject
{
    // its kind (L2Attacks.KeyOf: the command, or one of the action's flags)
    public int Key { get; init; }
    // which one of this kind in the attack (we change the same one in every monster)
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

// monster attacks: every named attack in the monsters' battle scripts (L2Attacks). each part is edited in place
// (name, target, how it hits, damage, element, criticals, animation, sound, effects), for the monster picked or for
// every monster using the attack. changes get written right away (Edit, Undo takes it back)
public partial class L2AttacksPage : L2Page
{
    private List<Use> _uses = new();
    private string[] _attackNames = Array.Empty<string>();
    private string[] _monsters = Array.Empty<string>();

    public List<L2Option> UseOptions { get; private set; } = new();
    public List<L2Option> TargetOptions { get; } = L2Attacks.Targets.Select((t, i) => new L2Option(i, t.Label)).ToList();
    public List<L2Option> HitOptions { get; } = L2Attacks.Hits.Select(h => new L2Option((int)h.Value, h.Label)).ToList();
    private static readonly List<L2Option> DamageKinds = new()
    {
        new((int)L2Attacks.DamageKind.Physical, "physical (the target's DFP blocks part)"),
        new((int)L2Attacks.DamageKind.Magic, "magic (+ its INT, MGR blocks part)"),
    };
    // physical and magic, plus 23 only if the attack already has it. in a monster's attack 23 hits unpredictably since
    // the game doesn't set the kind of action with it (seen in the game 2026-11-11)
    public List<L2Option> DamageKindOptions { get; private set; } = DamageKinds;
    public List<L2Option> AnimationOptions { get; private set; } = new();
    public List<L2Option> SoundOptions { get; private set; } = new();
    // the add menu (L2Attacks.AddMenu), value = the entry
    public List<L2Option> EffectKinds { get; } = L2Attacks.AddMenu.Select((m, i) => new L2Option(i, m.Label)).ToList();
    public string EffectHelp(int entry) => entry >= 0 && entry < L2Attacks.AddMenu.Length ? L2Attacks.AddMenu[entry].Help : "";
    public ObservableCollection<L2Flag> Elements { get; } = new();
    // the monster types (L2MonsterTypes) the attack does double damage to, by their elements
    public ObservableCollection<L2Flag> StrongAgainst { get; } = new();
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
    [ObservableProperty] private string _powerNote = "";
    [ObservableProperty] private int _spread;
    [ObservableProperty] private bool _canSpread;
    [ObservableProperty] private string _spreadNote = "";
    [ObservableProperty] private bool _hasOwnName;
    [ObservableProperty] private bool _hasCritical;
    [ObservableProperty] private int _criticalChance;
    [ObservableProperty] private int _criticalMultiplier;
    [ObservableProperty] private int _animation;
    [ObservableProperty] private string _animationNote = "";
    // the animation the attack shows (its own or its name's) for the preview, -1 = none
    [ObservableProperty] private int _previewAnimation = -1;
    [ObservableProperty] private int _previewMonster = -1;
    [ObservableProperty] private int _sound;
    [ObservableProperty] private string _hitNote = "";
    [ObservableProperty] private string _stepsText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEdited;

    protected override bool IsField(string property) => false;

    // the ROM, for the view's preview and Listen
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
        if (StrongAgainst.Count == 0)
            foreach (var type in L2MonsterTypes.Types.Where(t => t.Element != 0))
            {
                var flag = new L2Flag { Label = type.Name, Bit = type.Element };
                flag.Changed = () => StrongChanged(flag);
                StrongAgainst.Add(flag);
            }
    }

    protected override IEnumerable<string> ReadNames()
    {
        for (int nameId = 1; nameId < _attackNames.Length; nameId++)
        {
            int useCount = _uses.Count(u => u.Name == nameId);
            yield return $"{nameId:X2} {_attackNames[nameId]}" + (useCount == 0 ? "  (not used)" : useCount == 1 ? "" : $"  ({useCount})");
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
        bool wasLoading = Loading;
        Loading = true;
        try { ShowAttack(CurrentUse); } finally { Loading = wasLoading; }
    }

    // fills the fields from the attack of use
    private void ShowAttack(Use? use)
    {
        Effects.Clear();
        if (use == null || Rom == null) { StepsText = ""; PreviewAnimation = -1; IsEdited = false; return; }
        Block block;
        try { block = L2Attacks.Open(Rom, use); }
        catch (Exception e) { Status = e.Message; return; }
        var names = L2AiScripts.NameLists.From(Rom);
        IsEdited = L2AiScripts.Load(Rom, use.Kind, use.Monster).Edited;
        string key = L2Attacks.Target(block);
        Target = Math.Max(0, Array.FindIndex(L2Attacks.Targets, t => t.Key == key));
        var hit = L2Attacks.HowItHits(block);
        Hit = (int)hit;
        HitNote = hit switch
        {
            L2Attacks.Hit.Attack => "A normal attack (its ATP against the target's DFP) with this name. Its effects need \"only its effects\" or its own damage.",
            L2Attacks.Hit.EffectsOnly => "No damage: only the effects below happen.",
            L2Attacks.Hit.ShowOnly => "The name shows and nothing else happens (the game's taunts and laughs).",
            _ => "",
        };
        var damage = L2Attacks.Damage(block);
        HasDamage = damage != null;
        DamageKindOptions = damage?.Kind == L2Attacks.DamageKind.Fixed
            ? DamageKinds.Append(new L2Option((int)L2Attacks.DamageKind.Fixed, "23: power only (unpredictable in a monster's attack)")).ToList() : DamageKinds;
        OnPropertyChanged(nameof(DamageKindOptions));
        DamageKind = (int)(damage?.Kind ?? L2Attacks.DamageKind.Physical);
        Power = damage == null ? "" : damage.Value.Power?.ToString() ?? "(worked out by its script)";
        Spread = damage?.Spread ?? L2Attacks.GameSpread;
        CanSpread = damage != null;
        PowerNote = damage?.Kind switch
        {
            L2Attacks.DamageKind.Magic => "Damage is about (power + its INT - the target's MGR / 2) / 2; against several targets each takes a share (2: 57%, 3: 50%, 4: 45%).",
            null => "",
            _ => "Damage is about (power - the target's DFP / 2) / 2; a target in the back row or defending takes less. Gades's Destructo-wave is 350.",
        };
        SpreadNote = $"Up to {Spread * 100 / 256}% more at random (of 256: the game's attacks all have 32, about 12%; 0 = always the same).";
        HasOwnName = L2AttackNames.IsAdded(Rom, use.Name);
        int elements = L2Attacks.Element(block);
        foreach (var flag in Elements) flag.IsChecked = (elements & flag.Bit) != 0;
        foreach (var flag in StrongAgainst) flag.IsChecked = (elements & flag.Bit) != 0;
        var critical = L2Attacks.Critical(block);
        HasCritical = critical != null;
        CriticalChance = critical?.Chance ?? 10;
        CriticalMultiplier = critical?.Multiplier ?? 32;
        int? anim = L2Attacks.Animation(block);
        int nameAnim = L2Attacks.NameAnimation(Rom, use.Name);
        Animation = anim ?? -1;
        if (anim is int a && AnimationOptions.All(o => o.Value != a)) { AnimationOptions = AnimationOptions.Append(new L2Option(a, $"{a:X2} animation")).ToList(); OnPropertyChanged(nameof(AnimationOptions)); }
        AnimationNote = anim != null ? "" : nameAnim == 0xE0 ? "Its name brings no animation: none plays." : $"Its name brings animation {nameAnim:X2} ({(nameAnim < names.Animations.Length ? names.Animations[nameAnim] : "?")}).";
        PreviewMonster = use.Monster;
        PreviewAnimation = anim ?? (nameAnim == 0xE0 ? -1 : nameAnim);
        Sound = L2Attacks.Sound(block) ?? -1;
        var counts = new Dictionary<int, int>();
        foreach (var step in L2Attacks.Effects(block))
        {
            int effectKey = L2Attacks.KeyOf(step);
            int ordinal = counts.GetValueOrDefault(effectKey);
            counts[effectKey] = ordinal + 1;
            Effects.Add(MakeEffect(step, effectKey, ordinal));
        }
        StepsText = string.Join("\n", block.All.Select(s => L2AiScripts.Describe(s, block.Steps, names)));
    }

    private static List<L2Option> Opts(string[] list) => list.Select((n, i) => new L2Option(i, n)).ToList();

    // the result messages to pick from: the ones the game uses by name, then the rest by number
    private static readonly List<L2Option> MessageOptions = Enumerable.Range(0, 0x30)
        .Select(i => new L2Option(i, $"{i:X2} {L2AiScripts.MessageText(i)}")).ToList();

    private L2AttackEffect MakeEffect(L2AiScripts.Step step, int key, int ordinal)
    {
        var kind = L2Attacks.EffectKinds.First(k => k.Key == key);
        string title = kind.Label, help = kind.Help;
        L2AttackEffect effect = key switch
        {
            0x27 or 0x26 => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Hint = help, Choices = Opts(L2AiScripts.Statuses), NumberLabel = "chance %" },
            0x25 => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Hint = help, Choices = Opts(L2AiScripts.Restores), NumberLabel = "by % (minus lowers)" },
            0x24 => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Choices = Opts(L2AiScripts.Restores), NumberLabel = "by", HasSpread = true,
                Hint = "How much it heals (the Potion's is 30). Up to: HP and MP only, up to this many 256ths more at random (the Potion's 11 is about 4%)." },
            0x4A => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Hint = help, NumberLabel = "frames" },
            0x5D or 0x5E => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Hint = help, Choices = Opts(L2StatusPatch.Names), NumberLabel = "chance %" },
            0x60 => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Hint = help, Choices = Opts(L2StatusPatch.Names) },
            0x4C => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, NumberLabel = "number",
                Hint = "One of the battle's screen effects on the targets (the RAN-RAN step weapon uses 3); which number does what isn't named yet: try them with Test in the game." },
            0x35 => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Hint = help, Choices = MessageOptions },
            _ => new L2AttackEffect { Key = key, Ordinal = ordinal, Title = title, Hint = help },
        };
        effect.Loading = true;
        switch (key)
        {
            case 0x27 or 0x26: effect.Choice = step.Args[0]; effect.Number = step.Args[1]; break;
            case 0x25: effect.Choice = step.Args[0]; effect.Number = (sbyte)step.Args[1]; break;
            case 0x24: effect.Choice = step.Args[0]; effect.Number = (short)(step.Args[1] | step.Args[2] << 8); effect.Spread = step.Args[3]; break;
            case 0x4A or 0x4C: effect.Number = step.Args[0]; break;
            case 0x35: effect.Choice = step.Args[0]; break;
            case 0x5D or 0x5E: effect.Choice = step.Args[0]; effect.Number = step.Args[1]; break;
            case 0x60: effect.Choice = step.Args[0]; break;
        }
        effect.Loading = false;
        effect.Changed = EffectChanged;
        effect.Removed = EffectRemoved;
        return effect;
    }

    // writing

    // runs op on the attack of the monster picked (or of every monster that uses it) and writes their scripts
    private void Change(string what, Action<Block> op)
    {
        if (Loading || Rom == null || Main == null || CurrentUse is not { } current) return;
        var targets = ApplyAll ? UsesOfName : new List<Use> { current };
        try
        {
            // everything gets changed on copies first (each script once), so if one fails every monster stays as it was
            L2Attacks.ChangeAll(Rom, targets, op);
        }
        catch (Exception e) { Status = "Not changed: " + e.Message; Reshow(); return; }
        string who = targets.Count == 1 ? UseLabel(targets[0]) : $"{targets.Count} monsters";
        Main.NotifyRomModified($"Lufia II: {what} of the attack {_attackNames[NameId]} ({who})");
        Status = $"Changed for {who}.";
        MarkRead();
        Reshow();
    }

    // reads the uses again (steps moved) and shows the same monster's attack
    private void Reshow()
    {
        if (Rom == null) return;
        // find the same monster's use again by who it is. Revert or a script edit can take uses out or add some before
        // it, and going by its place in the list showed another monster's attack (and edits went to that one)
        var previous = CurrentUse;
        bool all = ApplyAll;
        _uses = L2Attacks.Uses(Rom);
        var uses = UsesOfName;
        int useIndex = previous == null ? -1 : uses.FindIndex(u => u.Kind == previous.Kind && u.Monster == previous.Monster && u.Ordinal == previous.Ordinal);
        if (useIndex < 0 && previous != null) useIndex = uses.FindIndex(u => u.Kind == previous.Kind && u.Monster == previous.Monster);
        Loading = true;
        try
        {
            UseOptions = uses.Select((u, i) => new L2Option(i, UseLabel(u))).ToList();
            OnPropertyChanged(nameof(UseOptions));
            HasUses = uses.Count > 0;
            UsedByText = uses.Count == 0 ? "No monster uses this attack. Its name can still be changed."
                       : uses.Count == 1 ? "Used by 1 monster." : $"Used by {uses.Count} monsters.";
            ApplyAllText = $"Change it for all {uses.Count} at once (only the part changed: each keeps its other values)";
            ApplyAll = all;
            SelectedUse = -1;
            SelectedUse = useIndex >= 0 ? useIndex : uses.Count > 0 ? 0 : -1;
            ShowAttack(CurrentUse);
        }
        finally { Loading = false; }
    }

    partial void OnNameTextChanged(string value)
    {
        if (Loading || Rom == null || Main == null || SelectedIndex < 0 || value == _attackNames[NameId]) return;
        if (L2Attacks.Rename(Rom, NameId, value) is { } why) { Status = "Not renamed: " + why; return; }
        Main.NotifyRomModified($"Lufia II: attack {NameId:X2} renamed to {value.Trim()}");
        MarkRead();
        Status = "Renamed (every monster that uses it shows the new name).";
        int index = SelectedIndex;
        RefreshList(index);
    }

    partial void OnTargetChanged(int value) => Change("the target", b => L2Attacks.SetTarget(b, L2Attacks.Targets[Math.Clamp(value, 0, L2Attacks.Targets.Length - 1)].Key));
    partial void OnHitChanged(int value) => Change("how it hits", b => L2Attacks.SetHowItHits(b, (L2Attacks.Hit)value));
    partial void OnDamageKindChanged(int value) => Change("the kind of damage", b => L2Attacks.SetDamageKind(b, (L2Attacks.DamageKind)value));

    partial void OnPowerChanged(string value)
    {
        if (Loading) return;
        if (!int.TryParse(value.Trim(), out int power) || power < 0 || power > 32767) { Status = "The damage is a number from 0 to 32767."; return; }
        Change("the damage", b => L2Attacks.SetPower(b, power));
    }

    partial void OnSpreadChanged(int value) => Change("the damage spread", b => L2Attacks.SetSpread(b, value));

    private void ElementsChanged()
    {
        if (Loading) return;
        int elements = Elements.Where(f => f.IsChecked).Sum(f => f.Bit);
        Change("the element", b => L2Attacks.SetElement(b, elements));
    }

    // a type got ticked or unticked: its element is added to or taken off each attack changed (they keep their other
    // elements). we also fix the game's dragon, insect, flying and hard shell checks so an attack with other elements counts too
    private void StrongChanged(L2Flag flag)
    {
        if (Loading || Rom == null) return;
        L2MonsterTypes.FixGameChecks(Rom);
        Change($"strong against {flag.Label}", b => L2Attacks.SetElement(b, flag.IsChecked ? L2Attacks.Element(b) | flag.Bit : L2Attacks.Element(b) & ~flag.Bit));
    }

    partial void OnHasCriticalChanged(bool value) => Change("the criticals", b => L2Attacks.SetCritical(b, value ? (CriticalChance, CriticalMultiplier) : null));
    partial void OnCriticalChanceChanged(int value) { if (HasCritical) Change("the critical chance", b => L2Attacks.SetCritical(b, (value, L2Attacks.Critical(b)?.Multiplier ?? CriticalMultiplier))); }
    partial void OnCriticalMultiplierChanged(int value) { if (HasCritical) Change("the critical damage", b => L2Attacks.SetCritical(b, (L2Attacks.Critical(b)?.Chance ?? CriticalChance, value))); }
    partial void OnAnimationChanged(int value) => Change("the animation", b => L2Attacks.SetAnimation(b, value < 0 ? null : value));
    partial void OnSoundChanged(int value) => Change("the sound", b => L2Attacks.SetSound(b, value < 0 ? null : value));

    [RelayCommand]
    private void AddEffect(object? entry)
    {
        if (entry is not L2Option option) return;
        Change("an added effect", b => L2Attacks.AddEffect(b, option.Value));
        if (L2Attacks.AddMenu[option.Value].Key < 0) Status += " Critical hits: 100%, 16 (x1): every hit is a critical one, which goes through DFP.";
    }

    private void EffectChanged(L2AttackEffect effect) => Change("an effect", b =>
    {
        var step = L2Attacks.EffectStep(b, effect.Key, effect.Ordinal) ?? L2Attacks.AddEffectOfKind(b, effect.Key);
        switch (effect.Key)
        {
            case 0x27 or 0x26: step.Args[0] = (byte)Math.Clamp(effect.Choice, 0, 7); step.Args[1] = (byte)Math.Clamp(effect.Number, 0, 100); break;
            case 0x25: step.Args[0] = (byte)Math.Clamp(effect.Choice, 0, 10); step.Args[1] = (byte)(sbyte)Math.Clamp(effect.Number, -128, 127); break;
            case 0x24:
                int amount = Math.Clamp(effect.Number, -32768, 32767);
                step.Args[0] = (byte)Math.Clamp(effect.Choice, 0, 10); step.Args[1] = (byte)amount; step.Args[2] = (byte)(amount >> 8); step.Args[3] = (byte)Math.Clamp(effect.Spread, 0, 255);
                break;
            case 0x4A or 0x4C: step.Args[0] = (byte)Math.Clamp(effect.Number, 0, 255); break;
            case 0x35: step.Args[0] = (byte)Math.Clamp(effect.Choice, 0, 255); break;
            case 0x5D or 0x5E: step.Args[0] = (byte)Math.Clamp(effect.Choice, 0, 15); step.Args[1] = (byte)Math.Clamp(effect.Number, 0, 100); break;
            case 0x60: step.Args[0] = (byte)Math.Clamp(effect.Choice, 0, 15); break;
        }
    });

    private void EffectRemoved(L2AttackEffect effect) => Change("a removed effect", b =>
    {
        if (L2Attacks.EffectStep(b, effect.Key, effect.Ordinal) is { } step) L2Attacks.Remove(b, step);
    });

    // gives the attack a name of its own (a new name in L2AttackNames, starting with the same text and animation) for
    // the monster picked or every monster that uses it. the list then shows the new name so you can type over it
    [RelayCommand]
    private void NewName()
    {
        if (Rom == null || Main == null || CurrentUse is not { } current) return;
        if (L2AttackNames.CantAdd(Rom) is { } whyNot) { Status = "No name added: " + whyNot; return; }
        var targets = ApplyAll ? UsesOfName : new List<Use> { current };
        int oldName = NameId, made;
        try
        {
            int added = 0;
            L2Rom.AllOrNothing(Rom, () =>
            {
                added = L2AttackNames.Add(Rom, _attackNames[oldName], L2Attacks.NameAnimation(Rom, oldName));
                L2Attacks.ChangeAll(Rom, targets, b => b.NameStep.Args[0] = (byte)added);
                return 0;
            });
            made = added;
        }
        catch (Exception e) { Status = "No name added: " + e.Message; return; }
        string who = targets.Count == 1 ? UseLabel(targets[0]) : $"{targets.Count} monsters";
        Main.NotifyRomModified($"Lufia II: the attack {_attackNames[oldName]} of {who} got a name of its own ({made:X2})");
        MarkRead();
        _attackNames = L2AiScripts.MonsterAttackNames(Rom);
        _uses = L2Attacks.Uses(Rom);
        RefreshList(made - 1);
        Status = $"A name of its own ({made:X2}) for {who}: type it in Name. " +
                 (UsesOfNameFor(oldName) > 0 ? $"The others keep \"{_attackNames[oldName]}\"." : "");
    }

    private int UsesOfNameFor(int name) => _uses.Count(u => u.Name == name);

    [RelayCommand]
    private void EditScript()
    {
        if (CurrentUse is not { } use) return;
        EditScript(use.Kind, use.Monster, use.Monster < _monsters.Length ? _monsters[use.Monster] : $"monster {use.Monster:X2}");
        Reshow();
    }

    [RelayCommand]
    private void Revert()
    {
        if (Rom == null || Main == null || CurrentUse is not { } use) return;
        if (!L2AiScripts.Revert(Rom, use.Kind, use.Monster)) return;
        Main.NotifyRomModified($"Lufia II: {UseLabel(use)}'s battle script back to the game's");
        MarkRead();
        Status = "Back to the game's script for this monster.";
        Reshow();
    }

    protected override string? Write() => null;
}
