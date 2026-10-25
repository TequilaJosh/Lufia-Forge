using System.IO;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Editable Lufia II monster and capsule monster battle scripts (AI: what it does on its turn; defense: what happens
/// when it is hit). Found 2026-10-24 by reading the game's code:
/// <list type="bullet">
/// <item>A monster's turn ($81:B1C9): offset = combatant +$4A (tag 07 of the record), base = the record (from the
/// monster id at +$50, $81:FCE2), bank $96; offset 0 runs the built-in plain attack $85:EF20.</item>
/// <item>When hit ($85:D153 and $85:D194): offset +$4C (tag 08), base and bank = the record pointer at +$45/+$47.</item>
/// <item>A capsule monster's turn ($81:B1A3): offset $1129 (record +$25), base and bank $1124/$1126.</item>
/// <item>The script runner $81:FAC9 starts at base + offset (offset 0 = none); jumps count from the base.</item>
/// </list>
/// Scripts had to sit right behind their record (banks $96 and $97 have no room left), so edited scripts go to the
/// added space instead: four small hooks look up (record, kind) in a table there and run the script from there when
/// it has one; everything else runs as before. The game's own bytes are never changed, so scripts shared between
/// monsters stay as they were, a monster without a script can get one, and "back to the game's script" just drops
/// the table entry. A script in the table is stored as [capacity u16][00][script]: its jumps count from the 00
/// (the runner is given offset 1, as offset 0 would mean "no script").
/// </summary>
public static class L2AiScripts
{
    public enum Kind { MonsterAttack = 0, MonsterDefense = 1, CapsuleAi = 2, CapsuleDefense = 3 }

    public static string KindName(Kind k) => k switch
    {
        Kind.MonsterAttack or Kind.CapsuleAi => "its turn (AI)",
        _ => "when it is hit (defense)",
    };

    // ── commands ─────────────────────────────────────────────────────────────

    /// <summary>What a command's argument is (for the editor's fields; the size is in <see cref="Size"/>).</summary>
    public enum Arg { Byte, Word, Register, Value, Jump, Chance, Percent, Spell, Monster, CapsuleAttack, Element, Stat, Status, Target, Item, Library, AttackName, Animation, Sound, Restore }

    public static int Size(Arg a) => a is Arg.Word or Arg.Value or Arg.Jump or Arg.Element or Arg.Item or Arg.Library ? 2 : 1;

    public sealed record OpDef(byte Op, string Name, string Group, Arg[] Args, string Help = "")
    {
        public int Length => 1 + Args.Sum(Size);
        public bool IsJump => Args.Contains(Arg.Jump);
        /// <summary>The script stops here (00 end, 4F quit, 03 go to, 43 return).</summary>
        public bool EndsPath => Op is 0x00 or 0x4F or 0x03 or 0x43;
    }

    public static readonly OpDef[] Ops = BuildOps();
    private static readonly Dictionary<byte, OpDef> ByOp = Ops.ToDictionary(o => o.Op);

    public static OpDef? Def(byte op) => ByOp.GetValueOrDefault(op);

    private static OpDef[] BuildOps()
    {
        var l = new List<OpDef>();
        void O(int op, string group, string name, string help, params Arg[] args) => l.Add(new OpDef((byte)op, name, group, args, help));
        const string Act = "Actions", Flow = "Flow", Reg = "Registers", Eff = "Effects", Show = "Show and sound", Misc = "Other";
        O(0x00, Flow, "End: do the chosen action", "Ends the script; the action chosen above happens.");
        O(0x01, Flow, "Do the action now, go on", "Does the action chosen so far and carries on (more than one action in a turn).");
        O(0x03, Flow, "Go to", "", Arg.Jump);
        O(0x04, Flow, "If the last action failed, go to", "For example no room to call a monster.", Arg.Jump);
        O(0x05, Flow, "With a chance, go to", "Chance out of 256.", Arg.Chance, Arg.Jump);
        O(0x06, Flow, "If register = value, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x07, Flow, "If register ≠ value, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x08, Flow, "If register > value, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x09, Flow, "If register < value, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x0A, Flow, "If register ≥ value, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x0B, Flow, "If register ≤ value, go to", "", Arg.Register, Arg.Value, Arg.Jump);
        O(0x3F, Flow, "If capsule attack slot is learned, go to", "Slot 1-3 (capsule monsters).", Arg.Byte, Arg.Jump);
        O(0x46, Flow, "If battle byte is 0, go to", "", Arg.Byte, Arg.Jump);
        O(0x42, Flow, "Call a shared script", "The game's shared scripts: 11 = immune to the 5 statuses, 25-27 = the capsule monsters' common parts...", Arg.Library);
        O(0x43, Flow, "Return (end of a shared script)", "");
        O(0x4F, Flow, "Quit: do nothing this turn", "");
        O(0x32, Act, "Choose the target", "01 one random foe, 05 (after 01) all foes, 06 itself, 06 then 05 all its side.", Arg.Target);
        O(0x28, Act, "Attack (physical)", "");
        O(0x37, Act, "Weapon attack", "");
        O(0x29, Act, "Defend", "");
        O(0x2A, Act, "Run away", "");
        O(0x2C, Act, "Cast a spell", "Needs the MP; without them it attacks.", Arg.Spell);
        O(0x54, Act, "Cast a spell (no MP, IP style)", "", Arg.Spell);
        O(0x2B, Act, "Use an item", "", Arg.Item);
        O(0x2D, Act, "Call a monster", "Fails when there is no room (see 'If the last action failed').", Arg.Monster);
        O(0x3E, Act, "Use a capsule monster attack", "", Arg.CapsuleAttack);
        O(0x41, Act, "Wait (check the situation)", "");
        O(0x2E, Act, "Action 0D", "");
        O(0x53, Act, "Action 9", "", Arg.Byte);
        O(0x0C, Reg, "Register = value", "", Arg.Register, Arg.Value);
        O(0x0D, Reg, "Register += value", "", Arg.Register, Arg.Value);
        O(0x0E, Reg, "Register -= value", "", Arg.Register, Arg.Value);
        O(0x0F, Reg, "Register ×= value", "", Arg.Register, Arg.Value);
        O(0x10, Reg, "Register ÷= value", "", Arg.Register, Arg.Value);
        O(0x55, Reg, "Register ÷= value (unsigned)", "", Arg.Register, Arg.Value);
        O(0x16, Reg, "Register AND= value", "", Arg.Register, Arg.Value);
        O(0x17, Reg, "Register OR= value", "", Arg.Register, Arg.Value);
        O(0x18, Reg, "Register XOR= value", "", Arg.Register, Arg.Value);
        O(0x11, Reg, "Register = random below itself", "", Arg.Register);
        O(0x12, Reg, "Register = ratio of two registers", "", Arg.Register, Arg.Register);
        O(0x19, Reg, "Register = its absolute value", "", Arg.Register);
        O(0x1A, Reg, "Register = minus itself", "", Arg.Register);
        O(0x1B, Reg, "Register = its sign (-1, 0, 1)", "", Arg.Register);
        O(0x1C, Reg, "Register = battle counter", "", Arg.Register);
        O(0x13, Reg, "Register = target's stat", "Choose the target first.", Arg.Register, Arg.Stat);
        O(0x14, Reg, "Target's stat = register", "", Arg.Register, Arg.Stat);
        O(0x15, Reg, "Register = its own stat", "", Arg.Register, Arg.Stat);
        O(0x4E, Reg, "Register = attacker's stat", "", Arg.Register, Arg.Stat);
        O(0x2F, Reg, "Register = combatants flagged 04", "", Arg.Register);
        O(0x30, Reg, "Register = side count", "", Arg.Register);
        O(0x40, Reg, "Register = learned capsule attacks", "", Arg.Register);
        O(0x45, Reg, "Register = battle byte", "", Arg.Register, Arg.Byte);
        O(0x44, Reg, "Battle byte = value", "", Arg.Byte, Arg.Byte);
        O(0x21, Eff, "Physical damage", "Element, damage (a value or a register, as a negative number), spread.", Arg.Element, Arg.Value, Arg.Byte);
        O(0x22, Eff, "Magic damage", "Element, damage, spread.", Arg.Element, Arg.Value, Arg.Byte);
        O(0x23, Eff, "Fixed damage", "", Arg.Word);
        O(0x1F, Eff, "Element of the attack", "", Arg.Element);
        O(0x20, Eff, "Critical chance and multiplier", "", Arg.Percent, Arg.Byte);
        O(0x24, Eff, "Restore", "What, amount, spread.", Arg.Restore, Arg.Value, Arg.Byte);
        O(0x25, Eff, "Stat up or down (%)", "", Arg.Byte, Arg.Byte);
        O(0x26, Eff, "Cure a status", "", Arg.Status, Arg.Percent);
        O(0x27, Eff, "Cause a status", "", Arg.Status, Arg.Percent);
        O(0x1D, Eff, "Do the effect step", "");
        O(0x3C, Eff, "Add the weapon's damage", "");
        O(0x50, Eff, "Keep only allowed targets", "");
        O(0x4D, Eff, "Drop targets that can't be hit", "");
        O(0x51, Eff, "Dark reflector", "");
        O(0x52, Eff, "Reflect flag 4", "");
        O(0x57, Eff, "Item boosts this spell", "", Arg.Item, Arg.Byte);
        O(0x58, Eff, "Eerie light", "");
        O(0x59, Eff, "Effect $85:DA9C", "");
        O(0x5B, Eff, "Lose if it kills itself", "");
        O(0x5C, Eff, "Hide the damage number", "");
        O(0x48, Eff, "Effect $85:9671", "");
        O(0x1E, Show, "Show an attack name", "", Arg.AttackName);
        O(0x56, Show, "Message time", "", Arg.Value);
        O(0x5A, Show, "Battle animation", "", Arg.Animation);
        O(0x3D, Show, "Sound effect", "", Arg.Sound);
        O(0x35, Show, "Special effect / message", "", Arg.Byte);
        O(0x36, Show, "If the last action failed: special effect", "", Arg.Byte);
        O(0x4A, Show, "Flash / shake", "Times.", Arg.Byte);
        O(0x4B, Show, "Screen effect 4", "");
        O(0x4C, Show, "Screen effect", "", Arg.Byte);
        O(0x47, Misc, "Effect timer, timed effect", "", Arg.Value);
        O(0x49, Misc, "Wait for the effect timer", "");
        return l.ToArray();
    }

    public static readonly string[] Targets = { "00", "01 one random foe", "02", "03", "04", "05 all of that side (after 01 or 06)", "06 itself", "07", "08" };
    public static readonly string[] Stats = { "HP", "MP", "max HP", "max MP", "ATP", "DFP", "STR", "AGL", "INT", "GUT", "MGR", "IP", "level",
        "ATP+ (battle)", "DFP+ (battle)", "STR+ (battle)", "AGL+ (battle)", "INT+ (battle)", "GUT+ (battle)", "MGR+ (battle)", "status" };
    public static readonly string[] Statuses = { "poison", "silence", "death", "paralysis", "confusion", "sleep", "status 6", "mirror" };
    public static readonly string[] Restores = { "HP", "MP", "max HP", "max MP", "ATP", "DFP", "STR", "AGL", "INT", "GUT", "MGR" };
    public static readonly (int Bit, string Name)[] Elements =
    {
        (0x0001, "vs dragons"), (0x0002, "vs insects"), (0x0004, "ice"), (0x0008, "thunder"), (0x0010, "earth"), (0x0020, "vs sea"),
        (0x0100, "wind"), (0x0200, "fire"), (0x0400, "water"), (0x0800, "soil"), (0x1000, "light"), (0x2000, "shadow"),
        (0x4000, "vs hard"), (0x8000, "vs flying"),
    };

    public static string ElementText(int w) =>
        w == 0 ? "neutral" : string.Join(" + ", Elements.Where(e => (w & e.Bit) != 0).Select(e => e.Name).DefaultIfEmpty($"{w:X4}"));

    /// <summary>A value operand: 80 in the high byte = register (low byte), else a number.</summary>
    public static string ValueText(int v) => (v >> 8) == 0x80 ? $"register {v & 0xFF:X2}" : $"{(short)v}";

    // ── steps ────────────────────────────────────────────────────────────────

    /// <summary>One command. Jumps point at a step (<see cref="Targets"/>), not at an offset, so steps can be added,
    /// removed and moved; a jump that leaves the script keeps its offset in <see cref="OutsideTargets"/>.</summary>
    public sealed class Step
    {
        public byte Op { get; set; }
        public byte[] Args { get; set; } = Array.Empty<byte>();
        /// <summary>Per jump argument (in order): the step it lands on, or null.</summary>
        public Step?[] Targets { get; set; } = Array.Empty<Step?>();
        /// <summary>Per jump argument: the raw offset when the target isn't a step of this script.</summary>
        public int[] OutsideTargets { get; set; } = Array.Empty<int>();
        public int Offset { get; set; }
        public OpDef? Def => L2AiScripts.Def(Op);
        public int Length => 1 + Args.Length;

        public Step Clone() => new() { Op = Op, Args = (byte[])Args.Clone(), Targets = (Step?[])Targets.Clone(), OutsideTargets = (int[])OutsideTargets.Clone() };
    }

    /// <summary>Byte position of argument <paramref name="index"/> in a command's arguments.</summary>
    public static int ArgAt(OpDef d, int index) => d.Args.Take(index).Sum(Size);

    /// <summary>Reads the script at file offset <paramref name="start"/>; jumps count from file offset
    /// <paramref name="baseAt"/>. Stops at 00, 4F, 03 or 43 once no jump goes further.</summary>
    public static List<Step> Parse(RomBuffer rom, int start, int baseAt)
    {
        var steps = new List<Step>();
        int p = start, furthest = start;
        var jumps = new List<(Step S, int Arg, int To)>();
        for (int guard = 0; guard < 2000 && p < rom.Length; guard++)
        {
            byte op = rom.ReadByte(p);
            var d = Def(op) ?? throw new InvalidDataException($"Unknown command {op:X2} at 0x{p:X6}.");
            var s = new Step { Op = op, Args = rom.ReadBytes(p + 1, d.Length - 1), Offset = p - baseAt };
            int jumpN = d.Args.Count(a => a == Arg.Jump);
            s.Targets = new Step?[jumpN];
            s.OutsideTargets = Enumerable.Repeat(-1, jumpN).ToArray();
            int j = 0;
            for (int i = 0; i < d.Args.Length; i++)
            {
                if (d.Args[i] != Arg.Jump) continue;
                int at = ArgAt(d, i), to = s.Args[at] | s.Args[at + 1] << 8;
                jumps.Add((s, j++, to));
                furthest = Math.Max(furthest, baseAt + to);
            }
            steps.Add(s);
            p += d.Length;
            if (d.EndsPath && p > furthest) break;
        }
        var byOffset = steps.ToDictionary(s => s.Offset);
        foreach (var (s, j, to) in jumps)
            if (byOffset.TryGetValue(to, out var t)) s.Targets[j] = t; else s.OutsideTargets[j] = to;
        return steps;
    }

    /// <summary>The script's bytes, laid out from offset <paramref name="first"/> (counted from the jump base).</summary>
    public static byte[] Build(IReadOnlyList<Step> steps, int first)
    {
        var at = new Dictionary<Step, int>();
        int o = first;
        foreach (var s in steps) { at[s] = o; o += s.Length; }
        var bytes = new List<byte>();
        foreach (var s in steps)
        {
            var args = (byte[])s.Args.Clone();
            var d = s.Def;
            if (d != null)
            {
                int j = 0;
                for (int i = 0; i < d.Args.Length; i++)
                {
                    if (d.Args[i] != Arg.Jump) continue;
                    var t = j < s.Targets.Length ? s.Targets[j] : null;
                    if (t != null && !at.ContainsKey(t)) throw new InvalidOperationException("A \"go to\" points at a step that was removed.");
                    int to = t != null ? at[t] : (j < s.OutsideTargets.Length && s.OutsideTargets[j] >= 0 ? s.OutsideTargets[j]
                        : throw new InvalidOperationException("A \"go to\" has no step to go to."));
                    int a = ArgAt(d, i);
                    args[a] = (byte)to; args[a + 1] = (byte)(to >> 8);
                    j++;
                }
            }
            bytes.Add(s.Op);
            bytes.AddRange(args);
        }
        return bytes.ToArray();
    }

    /// <summary>A new command with zero arguments (jumps unset).</summary>
    public static Step New(byte op)
    {
        var d = Def(op) ?? throw new ArgumentException($"Unknown command {op:X2}.");
        int jumps = d.Args.Count(a => a == Arg.Jump);
        return new Step { Op = op, Args = new byte[d.Length - 1], Targets = new Step?[jumps], OutsideTargets = Enumerable.Repeat(-1, jumps).ToArray() };
    }

    // ── owners ───────────────────────────────────────────────────────────────

    /// <summary>File offset of the record that owns the script.</summary>
    public static int RecordAt(RomBuffer rom, Kind kind, int index) => kind switch
    {
        Kind.MonsterAttack or Kind.MonsterDefense => L2Battle.ReadMonster(rom, index).Offset,
        _ => L2Capsules.At(rom, index),
    };

    /// <summary>File offset where the game's own script starts, or -1 when it has none.</summary>
    public static int OriginalAt(RomBuffer rom, Kind kind, int index)
    {
        switch (kind)
        {
            case Kind.MonsterAttack: return L2Battle.ReadMonster(rom, index).AttackScript;
            case Kind.MonsterDefense: return L2Battle.ReadMonster(rom, index).DefenseScript;
            default:
                var c = L2Capsules.Read(rom, index);
                int off = kind == Kind.CapsuleAi ? c.AiScript : c.DefenseScript;
                return off == 0 ? -1 : c.At + off;
        }
    }

    /// <summary>The script as it runs now: Lufia Forge's (from the table) or the game's; empty when there is none.</summary>
    public static (List<Step> Steps, bool Edited) Load(RomBuffer rom, Kind kind, int index)
    {
        if (Override(rom, kind, index) is int far) return (Parse(rom, far + 1, far), true);
        int at = OriginalAt(rom, kind, index);
        return at < 0 ? (new List<Step>(), false) : (Parse(rom, at, RecordAt(rom, kind, index)), false);
    }

    /// <summary>The game's own script (empty when there is none).</summary>
    public static List<Step> LoadOriginal(RomBuffer rom, Kind kind, int index)
    {
        int at = OriginalAt(rom, kind, index);
        return at < 0 ? new List<Step>() : Parse(rom, at, RecordAt(rom, kind, index));
    }

    // ── the hooks and the table ─────────────────────────────────────────────

    private const string Magic = "LF2AI";
    private const int CodeAt = 0x10, TableAt = 0x200, BlockSize = 0x1400, EntrySize = 6;
    private const int TableCapacity = (BlockSize - TableAt) / EntrySize - 1;

    private const int AttackSite = 0xB1D4, CapsuleSite = 0xB1A3, DefenseSite1 = 0x2D165, DefenseSite2 = 0x2D1A6;
    private static readonly byte[] AttackOriginal = Hex("bc 4a 00 f0 0e bd 50 00 22 e2 fc 81 8e 42 0a a9 96 80 08 8c 42 0a a0 20 ef a9 85 8d 44 0a 22 c9 fa 81 6b");
    private static readonly byte[] CapsuleOriginal = Hex("ac 29 11 f0 0e ae 24 11 8e 42 0a ad 26 11 8d 44 0a 80 0e a2 00 00 a0 20 ef 8e 42 0a a9 85 8d 44 0a 22 c9 fa 81 6b");
    private static readonly byte[] DefenseOriginal = Hex("bd 47 00 8d 44 0a bc 4c 00 22 c9 fa 81");
    /// <summary>Where the monster records' bank is in the attack site's code (LDA #$96): the hacks that moved the monster
    /// data (Frue, Kureji, Spekkio, Wrath of Foomy: bank $D0) changed only that byte there.</summary>
    private const int MonsterBankAt = 16;

    private static byte[] Hex(string s) => s.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();

    /// <summary>File offset of Lufia Forge's script block (hooks and table), or -1 when it isn't in the ROM.</summary>
    public static int Block(RomBuffer rom)
    {
        if (rom.Length <= AttackSite + 4 || rom.ReadByte(AttackSite) != 0x5C) return -1;
        int code = L2Rom.SnesToFile((int)rom.ReadUInt24Le(AttackSite + 1));
        int block = code - CodeAt - HookAOffset;
        return block >= 0 && block + BlockSize <= rom.Length && System.Text.Encoding.ASCII.GetString(rom.ReadBytes(block, Magic.Length)) == Magic ? block : -1;
    }

    public static bool IsInstalled(RomBuffer rom) => Block(rom) >= 0;

    /// <summary>Whether the hooks can go in (the four places still have the game's code).</summary>
    public static bool CanInstall(RomBuffer rom, out string why)
    {
        why = "";
        if (IsInstalled(rom)) return true;
        bool Same(int at, byte[] b) => at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);
        bool attack = AttackSite + AttackOriginal.Length <= rom.Length &&
                      Enumerable.Range(0, AttackOriginal.Length).All(i => i == MonsterBankAt || rom.ReadByte(AttackSite + i) == AttackOriginal[i]);
        if (!attack || !Same(CapsuleSite, CapsuleOriginal) || !Same(DefenseSite1, DefenseOriginal) || !Same(DefenseSite2, DefenseOriginal))
        {
            why = "This ROM's battle code isn't the US game's where the script hooks go (another patch or hack changed it), so edited scripts can't be added.";
            return false;
        }
        return true;
    }

    // where each routine starts in the code (set by Assemble)
    private static int HookAOffset, HookCOffset, HookDOffset;

    static L2AiScripts() => Assemble(0x808000, 0x808000, 0x96);

    /// <summary>The hook code for a block whose code starts at SNES <paramref name="code"/> and whose table is at SNES
    /// <paramref name="table"/>. Routines: Find (the table lookup), A (a monster's turn), C (a capsule monster's turn),
    /// D (being hit). All run with an 8-bit accumulator and 16-bit index registers, as the game's code there.</summary>
    private static byte[] Assemble(int code, int table, int monsterBank)
    {
        var a = new Asm(code);
        void Long(byte op, int addr) => a.B(op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16));
        // Find: A = kind, X = record (16-bit). Found: $0A42/$0A44 = the script's base, Y = 1, carry set; else X, Y kept, carry clear.
        a.Label("find");
        a.B(0x5A, 0xDA, 0x48);                 // PHY PHX PHA  (1,S kind, 2,S record)
        a.B(0xA2, 0x00, 0x00);                 // LDX #0
        a.Label("loop");
        Long(0xBF, table + 2);                 // LDA table+2,X (kind)
        a.B(0xC9, 0xFF); a.Br(0xF0, "missing"); // CMP #$FF / BEQ
        a.B(0xC3, 0x01); a.Br(0xD0, "next");    // CMP 1,S / BNE
        a.B(0xC2, 0x20);                       // REP #$20
        Long(0xBF, table);                     // LDA table,X (record)
        a.B(0xC3, 0x02);                       // CMP 2,S
        a.B(0xE2, 0x20); a.Br(0xD0, "next");    // SEP #$20 / BNE
        a.B(0xC2, 0x20);
        Long(0xBF, table + 3);                 // LDA table+3,X (script lo/hi)
        a.B(0x8D, 0x42, 0x0A);                 // STA $0A42
        a.B(0xE2, 0x20);
        Long(0xBF, table + 5);                 // LDA table+5,X (script bank)
        a.B(0x8D, 0x44, 0x0A);                 // STA $0A44
        a.B(0x68, 0xFA, 0x7A);                 // PLA PLX PLY
        a.B(0xA0, 0x01, 0x00, 0x38, 0x60);     // LDY #1 / SEC / RTS
        a.Label("next");
        a.B(0xE8, 0xE8, 0xE8, 0xE8, 0xE8, 0xE8); a.Br(0x80, "loop");
        a.Label("missing");
        a.B(0x68, 0xFA, 0x7A, 0x18, 0x60);     // PLA PLX PLY / CLC / RTS

        // A: a monster's turn (JML from $81:B1D4; X = the combatant)
        a.Label("hookA");
        a.B(0xBC, 0x4A, 0x00);                 // LDY $004A,X (attack script offset)
        a.B(0xBD, 0x50, 0x00);                 // LDA $0050,X (monster id)
        Long(0x22, 0x81FCE2);                  // JSL $81:FCE2 -> X = record
        a.B(0xA9, (byte)Kind.MonsterAttack); a.Jsr("find");
        a.Br(0xB0, "runA");
        a.B(0xC0, 0x00, 0x00); a.Br(0xF0, "plainA");   // CPY #0 / BEQ
        a.B(0x8E, 0x42, 0x0A, 0xA9, (byte)monsterBank, 0x8D, 0x44, 0x0A);   // STX $0A42 / LDA #bank / STA $0A44
        a.Label("runA"); Long(0x5C, 0x81B1F2);  // JML (JSL $81:FAC9 / RTL)
        a.Label("plainA"); Long(0x5C, 0x81B1E7); // JML (the built-in plain attack)

        // C: a capsule monster's turn (JML from $81:B1A3)
        a.Label("hookC");
        a.B(0xAC, 0x29, 0x11, 0xAE, 0x24, 0x11);   // LDY $1129 / LDX $1124
        a.B(0xA9, (byte)Kind.CapsuleAi); a.Jsr("find");
        a.Br(0xB0, "runC");
        a.B(0xC0, 0x00, 0x00); a.Br(0xF0, "plainC");
        a.B(0x8E, 0x42, 0x0A, 0xAD, 0x26, 0x11, 0x8D, 0x44, 0x0A);   // STX $0A42 / LDA $1126 / STA $0A44
        a.Label("runC"); Long(0x5C, 0x81B1C4);
        a.Label("plainC"); Long(0x5C, 0x81B1B6);

        // D: being hit (JSL from $85:D165 and $85:D1A6, in place of LDA $0047,X / STA $0A44 / LDY $004C,X)
        a.Label("hookD");
        a.B(0xBD, 0x47, 0x00, 0x8D, 0x44, 0x0A, 0xBC, 0x4C, 0x00);
        a.B(0xDA, 0x48);                       // PHX / PHA (bank)
        a.B(0xC2, 0x20, 0xBD, 0x45, 0x00, 0xAA, 0xE2, 0x20);   // REP / LDA $0045,X / TAX / SEP
        a.B(0x68, 0xC9, 0x97); a.Br(0xF0, "capD");             // PLA / CMP #$97 / BEQ
        a.B(0xA9, (byte)Kind.MonsterDefense); a.Br(0x80, "findD");
        a.Label("capD"); a.B(0xA9, (byte)Kind.CapsuleDefense);
        a.Label("findD"); a.Jsr("find");
        a.B(0xFA, 0x6B);                       // PLX / RTL

        var bytes = a.Done();
        HookAOffset = a.At("hookA"); HookCOffset = a.At("hookC"); HookDOffset = a.At("hookD");
        return bytes;
    }

    /// <summary>A tiny 65816 assembler: bytes, labels, 8-bit branches and JSRs within the block.</summary>
    private sealed class Asm(int origin)
    {
        private readonly List<byte> _b = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int At, string Label, bool Jsr)> _fix = new();
        public void B(params byte[] b) => _b.AddRange(b);
        public void Label(string l) => _labels[l] = _b.Count;
        public void Br(byte op, string l) { _b.Add(op); _fix.Add((_b.Count, l, false)); _b.Add(0); }
        public void Jsr(string l) { _b.Add(0x20); _fix.Add((_b.Count, l, true)); _b.Add(0); _b.Add(0); }
        public int At(string l) => _labels[l];
        public byte[] Done()
        {
            foreach (var (at, l, jsr) in _fix)
            {
                int to = _labels[l];
                if (jsr) { int addr = (origin + to) & 0xFFFF; _b[at] = (byte)addr; _b[at + 1] = (byte)(addr >> 8); continue; }
                int rel = to - (at + 1);
                if (rel is < -128 or > 127) throw new InvalidOperationException($"Branch to {l} too far.");
                _b[at] = (byte)rel;
            }
            return _b.ToArray();
        }
    }

    /// <summary>Puts the hooks and the empty table in (once).</summary>
    public static int Install(RomBuffer rom)
    {
        int have = Block(rom);
        if (have >= 0) return have;
        if (!CanInstall(rom, out var why)) throw new InvalidOperationException(why);
        int block = L2Rom.Allocate(rom, BlockSize);
        int code = L2Rom.FileToSnes(block + CodeAt), table = L2Rom.FileToSnes(block + TableAt);
        int bank = rom.ReadByte(AttackSite + MonsterBankAt);
        var bytes = Assemble(code, table, bank);
        if (CodeAt + bytes.Length > TableAt) throw new InvalidOperationException("The hook code doesn't fit.");
        rom.WriteBytes(block, new byte[BlockSize]);
        rom.WriteBytes(block, System.Text.Encoding.ASCII.GetBytes(Magic));
        rom.WriteByte(block + Magic.Length, 1);   // version
        rom.WriteByte(block + Magic.Length + 1, (byte)bank);   // the monster records' bank (the hook overwrites where it was)
        rom.WriteBytes(block + CodeAt, bytes);
        rom.WriteBytes(block + TableAt, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] Jump(byte op, int addr) => new[] { op, (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16) };
        rom.WriteBytes(AttackSite, Jump(0x5C, code + HookAOffset));
        rom.WriteBytes(CapsuleSite, Jump(0x5C, code + HookCOffset));
        var d = Jump(0x22, code + HookDOffset).Concat(Enumerable.Repeat((byte)0xEA, 5)).ToArray();
        rom.WriteBytes(DefenseSite1, d);
        rom.WriteBytes(DefenseSite2, d);
        return block;
    }

    private static int RecordKey(RomBuffer rom, Kind kind, int index) => L2Rom.FileToSnes(RecordAt(rom, kind, index)) & 0xFFFF;

    private static IEnumerable<(int At, int Record, Kind Kind, int Script)> Entries(RomBuffer rom, int block)
    {
        for (int i = 0; i < TableCapacity; i++)
        {
            int e = block + TableAt + i * EntrySize;
            byte k = rom.ReadByte(e + 2);
            if (k == 0xFF) yield break;
            yield return (e, rom.ReadUInt16Le(e), (Kind)k, L2Rom.SnesToFile((int)rom.ReadUInt24Le(e + 3)));
        }
    }

    /// <summary>File offset of the script's base (its 00) when Lufia Forge's table has one for it, else null.</summary>
    public static int? Override(RomBuffer rom, Kind kind, int index)
    {
        int block = Block(rom);
        if (block < 0) return null;
        int key = RecordKey(rom, kind, index);
        foreach (var e in Entries(rom, block))
            if (e.Record == key && e.Kind == kind) return e.Script;
        return null;
    }

    /// <summary>Writes the script for (kind, index) to the added space and points the table at it (the game's bytes
    /// stay as they are). Returns the file offset of the script.</summary>
    public static int Save(RomBuffer rom, Kind kind, int index, IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0) throw new InvalidOperationException("The script is empty: it needs at least an End.");
        var body = Build(steps, 1);
        if (body.Length > 0x7F00) throw new InvalidOperationException("The script is too long.");
        int block = Install(rom);
        int key = RecordKey(rom, kind, index);
        var entry = Entries(rom, block).FirstOrDefault(e => e.Record == key && e.Kind == kind);
        int baseAt;
        if (entry.At > 0 && rom.ReadUInt16Le(entry.Script - 2) >= body.Length + 1)
            baseAt = entry.Script;   // fits where it was
        else
        {
            int cap = Math.Max(body.Length + 1 + 16, 32);
            int at = L2Rom.Allocate(rom, cap + 2);
            rom.WriteUInt16Le(at, (ushort)cap);
            baseAt = at + 2;
        }
        int capacity = rom.ReadUInt16Le(baseAt - 2);
        rom.WriteBytes(baseAt, new byte[capacity]);
        rom.WriteByte(baseAt, 0x00);
        rom.WriteBytes(baseAt + 1, body);
        int snes = L2Rom.FileToSnes(baseAt);
        int e = entry.At > 0 ? entry.At : block + TableAt + Entries(rom, block).Count() * EntrySize;
        if (entry.At <= 0)
        {
            if ((e - block - TableAt) / EntrySize >= TableCapacity) throw new InvalidOperationException("Lufia Forge's script table is full.");
            rom.WriteBytes(e + EntrySize, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        }
        rom.WriteBytes(e, new[] { (byte)key, (byte)(key >> 8), (byte)kind, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16) });
        return baseAt;
    }

    /// <summary>Back to the game's own script (or none): drops the table entry (its space is left as it is).</summary>
    public static bool Revert(RomBuffer rom, Kind kind, int index)
    {
        int block = Block(rom);
        if (block < 0) return false;
        int key = RecordKey(rom, kind, index);
        var all = Entries(rom, block).ToList();
        int i = all.FindIndex(e => e.Record == key && e.Kind == kind);
        if (i < 0) return false;
        // move the last entry into its place, end the table one entry earlier
        var last = all[^1];
        if (i != all.Count - 1) rom.WriteBytes(all[i].At, rom.ReadBytes(last.At, EntrySize));
        rom.WriteBytes(last.At, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        return true;
    }

    /// <summary>Every (kind, index) with an edited script.</summary>
    public static int EditedCount(RomBuffer rom) => Block(rom) is var b && b >= 0 ? Entries(rom, b).Count() : 0;

    // ── text ─────────────────────────────────────────────────────────────────

    /// <summary>Names used to show arguments (spells, monsters, items, capsule attacks).</summary>
    public sealed class NameLists
    {
        public string[] Spells { get; init; } = Array.Empty<string>();
        public string[] Monsters { get; init; } = Array.Empty<string>();
        public string[] Items { get; init; } = Array.Empty<string>();
        public string[] CapsuleAttacks { get; init; } = Array.Empty<string>();

        public static NameLists From(RomBuffer rom) => new()
        {
            Spells = L2Battle.Spells(rom).Select(s => s.Name).ToArray(),
            Monsters = L2Battle.Monsters(rom).Select(m => m.Name).ToArray(),
            Items = L2Battle.Items(rom).Select(i => i.Name).ToArray(),
            CapsuleAttacks = L2Capsules.AttackList(rom),
        };
    }

    private static string Pick(string[] names, int i) => i >= 0 && i < names.Length ? $"{i:X2} {names[i]}" : $"{i:X2}";

    /// <summary>An argument as text; <paramref name="step"/> numbers a jump's target step (1-based), or -1.</summary>
    public static string ArgText(Arg a, int v, NameLists n, int step = -1) => a switch
    {
        Arg.Jump => step > 0 ? $"step {step}" : $"offset {v:X4}",
        Arg.Chance => $"{v}/256 ({v * 100 / 256}%)",
        Arg.Percent => $"{v}%",
        Arg.Register => $"register {v:X2}",
        Arg.Value => ValueText(v),
        Arg.Spell => Pick(n.Spells, v),
        Arg.Monster => Pick(n.Monsters, v),
        Arg.Item => Pick(n.Items, v & 0x1FF),
        Arg.CapsuleAttack => Pick(n.CapsuleAttacks, v),
        Arg.Element => ElementText(v),
        Arg.Stat => v < Stats.Length ? Stats[v] : $"stat {v:X2}",
        Arg.Status => v < Statuses.Length ? Statuses[v] : $"status {v:X2}",
        Arg.Target => v < Targets.Length ? Targets[v] : $"{v:X2}",
        Arg.Restore => v < Restores.Length ? Restores[v] : $"{v:X2}",
        Arg.Library => $"shared script {v:X2}",
        Arg.AttackName => $"attack name {v:X2}",
        Arg.Animation => $"animation {v:X2}",
        Arg.Sound => $"sound {v:X2}",
        Arg.Word => $"{v}",
        _ => $"{v}",
    };

    /// <summary>The value of argument <paramref name="i"/> of a step.</summary>
    public static int ArgValue(Step s, int i)
    {
        var d = s.Def!;
        int at = ArgAt(d, i);
        return Size(d.Args[i]) == 2 ? s.Args[at] | s.Args[at + 1] << 8 : s.Args[at];
    }

    public static void SetArg(Step s, int i, int v)
    {
        var d = s.Def!;
        int at = ArgAt(d, i);
        s.Args[at] = (byte)v;
        if (Size(d.Args[i]) == 2) s.Args[at + 1] = (byte)(v >> 8);
    }

    /// <summary>One line for a step, e.g. "Cast a spell: 03 Fire" or "With a chance, go to: 81/256 (31%), step 7".</summary>
    public static string Describe(Step s, IReadOnlyList<Step> all, NameLists n)
    {
        var d = s.Def;
        if (d == null) return $"command {s.Op:X2}";
        var parts = new List<string>();
        int j = 0;
        for (int i = 0; i < d.Args.Length; i++)
        {
            int step = -1;
            if (d.Args[i] == Arg.Jump) { var t = j < s.Targets.Length ? s.Targets[j] : null; step = t == null ? -1 : IndexOf(all, t) + 1; j++; }
            parts.Add(ArgText(d.Args[i], ArgValue(s, i), n, step));
        }
        return parts.Count == 0 ? d.Name : $"{d.Name}: {string.Join(", ", parts)}";
    }

    private static int IndexOf(IReadOnlyList<Step> all, Step t) { for (int i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], t)) return i; return -1; }

    /// <summary>The whole script as numbered lines.</summary>
    public static string Listing(IReadOnlyList<Step> steps, NameLists n) =>
        steps.Count == 0 ? "(none)" : string.Join(Environment.NewLine, steps.Select((s, i) => $"{i + 1,3}  {Describe(s, steps, n)}"));
}
