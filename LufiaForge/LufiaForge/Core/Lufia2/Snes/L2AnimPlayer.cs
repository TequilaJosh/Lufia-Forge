namespace LufiaForge.Core.Lufia2.Snes;

/// <summary>
/// Plays a Lufia II battle animation by running the game's own animation code ($81:895E) on <see cref="SnesMachine"/>:
/// every frame the game waits for ($85:EC81 polls the frame counter $40) the console's frame interrupt runs (the
/// battle's own, $85:8DC5 through $0067, copies the scroll, window and colour registers and queued graphics) and the
/// picture is drawn.
/// </summary>
public sealed class L2AnimPlayer
{
    public const int PlayEntry = 0x81895E;
    /// <summary>The battle's PPU register list ($97:B5D3: register, value; a value from $80 ends it), set at the start of
    /// a battle by $81:8633.</summary>
    public const int BattleRegisters = 0x97B5D3;
    private static readonly int[] WaitLoops = { 0x85EC96, 0x85ECB9 };

    public readonly SnesMachine Machine;
    public readonly List<uint[]> Frames = new();
    public string? Problem;
    public bool Finished;
    /// <summary>Called after each frame is drawn (for tests).</summary>
    public Action<int>? OnFrame;
    /// <summary>Drawn behind the sprites (see <see cref="SnesRenderer.Render"/>).</summary>
    public uint[]? Underlay;
    public bool HideTable1;

    public L2AnimPlayer(byte[] rom) { Machine = new SnesMachine(rom); }

    /// <summary>Sets the PPU up as a battle does.</summary>
    public void BattleScreen()
    {
        var m = Machine;
        for (int i = 0; i < 0x80; i += 2)
        {
            int r = m.Read(BattleRegisters + i);
            if (r >= 0x80) break;
            m.Write(0x2100 + r, m.Read(BattleRegisters + i + 1));
        }
    }

    /// <summary>Runs animation <paramref name="id"/> for at most <paramref name="maxFrames"/> frames.</summary>
    public void Play(int id, int maxFrames = 600, long maxInstructions = 60_000_000)
    {
        var c = Machine.Cpu;
        c.E = false; c.P = 0x24 | 0x20; c.FX = false; c.FM = true;
        c.S = 0x1FF0; c.D = 0; c.DB = 0x7E;
        // return to $FE:0000 when the animation ends
        Machine.Write(c.S, 0xFE); c.S--;
        Machine.Write(c.S, 0xFF); c.S--;
        Machine.Write(c.S, 0xFF); c.S--;
        c.PB = PlayEntry >> 16; c.PC = PlayEntry & 0xFFFF;
        c.A = id & 0xFF;
        long sinceFrame = 0;
        while (c.Instructions < maxInstructions)
        {
            if (c.PB == 0xFE) { Finished = true; return; }
            int pc = c.PB << 16 | c.PC;
            if (c.Waiting || Array.IndexOf(WaitLoops, pc) >= 0 || sinceFrame > 400_000)
            {
                Frame();
                sinceFrame = 0;
                if (Problem != null || Frames.Count >= maxFrames) return;
                if (Array.IndexOf(WaitLoops, pc) < 0 && !c.Waiting) continue;
            }
            c.Step();
            sinceFrame++;
            if (c.Crashed >= 0) { Problem = $"The game's code stopped at ${c.Crashed >> 16:X2}:{c.Crashed & 0xFFFF:X4}."; return; }
        }
        Problem = "The animation didn't end.";
    }

    /// <summary>One frame: the frame interrupt, then the picture.</summary>
    private void Frame()
    {
        var c = Machine.Cpu;
        int s = c.S, pb = c.PB, pc = c.PC;
        Machine.NmiFlag = true;
        c.Interrupt(nmi: true);
        long limit = c.Instructions + 2_000_000;
        while (!(c.S == s && c.PB == pb && c.PC == pc))
        {
            c.Step();
            if (c.Crashed >= 0) { Problem = $"The frame interrupt stopped at ${c.Crashed >> 16:X2}:{c.Crashed & 0xFFFF:X4}."; return; }
            if (c.Instructions > limit) { Problem = "The frame interrupt didn't end."; return; }
        }
        Frames.Add(SnesRenderer.Render(Machine, Underlay, HideTable1));
        OnFrame?.Invoke(Frames.Count);
    }
}
