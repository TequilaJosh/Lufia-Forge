namespace LufiaForge.Core.Lufia2.Snes;

// plays a lufia 2 battle animation by running the game's own animation code ($81:895E) on SnesMachine. every frame the
// game waits for ($85:EC81 polls the frame counter $40) the console's frame interrupt runs (the battle's own, $85:8DC5
// through $0067, copies the scroll, window and colour registers and the queued graphics) and the picture gets drawn
public sealed class L2AnimPlayer
{
    public const int PlayEntry = 0x81895E;
    // the battle's PPU register list ($97:B5D3: register, value, a value from $80 ends it), set at the start of a
    // battle by $81:8633
    public const int BattleRegisters = 0x97B5D3;
    private static readonly int[] WaitLoops = { 0x85EC96, 0x85ECB9 };

    public readonly SnesMachine Machine;
    public readonly List<uint[]> Frames = new();
    public string? Problem;
    public bool Finished;
    // called after each frame is drawn (for tests)
    public Action<int>? OnFrame;
    // drawn behind the sprites (see SnesRenderer.Render)
    public uint[]? Underlay;
    public bool HideTable1;

    public L2AnimPlayer(byte[] rom) { Machine = new SnesMachine(rom); }

    // sets the PPU up the way a battle does
    public void BattleScreen()
    {
        var machine = Machine;
        for (int i = 0; i < 0x80; i += 2)
        {
            int register = machine.Read(BattleRegisters + i);
            if (register >= 0x80) break;
            machine.Write(0x2100 + register, machine.Read(BattleRegisters + i + 1));
        }
    }

    // runs animation id for maxFrames frames at most
    public void Play(int id, int maxFrames = 600, long maxInstructions = 60_000_000)
    {
        var cpu = Machine.Cpu;
        cpu.E = false; cpu.P = 0x24 | 0x20; cpu.FX = false; cpu.FM = true;
        cpu.S = 0x1FF0; cpu.D = 0; cpu.DB = 0x7E;
        // return to $FE:0000 when the animation ends
        Machine.Write(cpu.S, 0xFE); cpu.S--;
        Machine.Write(cpu.S, 0xFF); cpu.S--;
        Machine.Write(cpu.S, 0xFF); cpu.S--;
        cpu.PB = PlayEntry >> 16; cpu.PC = PlayEntry & 0xFFFF;
        cpu.A = id & 0xFF;
        long sinceFrame = 0;
        while (cpu.Instructions < maxInstructions)
        {
            if (cpu.PB == 0xFE) { Finished = true; return; }
            int pc = cpu.PB << 16 | cpu.PC;
            if (cpu.Waiting || Array.IndexOf(WaitLoops, pc) >= 0 || sinceFrame > 400_000)
            {
                Frame();
                sinceFrame = 0;
                if (Problem != null || Frames.Count >= maxFrames) return;
                if (Array.IndexOf(WaitLoops, pc) < 0 && !cpu.Waiting) continue;
            }
            cpu.Step();
            sinceFrame++;
            if (cpu.Crashed >= 0) { Problem = $"The game's code stopped at ${cpu.Crashed >> 16:X2}:{cpu.Crashed & 0xFFFF:X4}."; return; }
        }
        Problem = "The animation didn't end.";
    }

    // one frame: the frame interrupt, then the picture
    private void Frame()
    {
        var cpu = Machine.Cpu;
        int stack = cpu.S, pb = cpu.PB, pc = cpu.PC;
        Machine.NmiFlag = true;
        cpu.Interrupt(nmi: true);
        long limit = cpu.Instructions + 2_000_000;
        while (!(cpu.S == stack && cpu.PB == pb && cpu.PC == pc))
        {
            cpu.Step();
            if (cpu.Crashed >= 0) { Problem = $"The frame interrupt stopped at ${cpu.Crashed >> 16:X2}:{cpu.Crashed & 0xFFFF:X4}."; return; }
            if (cpu.Instructions > limit) { Problem = "The frame interrupt didn't end."; return; }
        }
        Frames.Add(SnesRenderer.Render(Machine, Underlay, HideTable1));
        OnFrame?.Invoke(Frames.Count);
    }
}
