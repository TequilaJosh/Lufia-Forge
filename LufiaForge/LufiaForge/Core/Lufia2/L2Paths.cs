namespace LufiaForge.Core.Lufia2;

/// <summary>
/// A map's paths: what "A character walks a path" (event 33 path who) walks. A path is a record of the map info's +12
/// list, 6 bytes: id, x, y (the first place it heads for), flags, u16 offset of its walk program in the info block (the
/// game copies the block to $7E:F000 and runs the program from there, $80:A80F). Flags: 40 clear = the camera follows
/// the walker, 10 = the event goes on while it walks (else it waits), 20 = a walk in the background; the game's paths
/// have 80 and 08 set too (C8 / 88 / 48 / D8), which event 33 doesn't read.
/// The walk program (WalkScriptOps, $83:C7F8): 12 x y heads for block x,y (sideways first, then up or down; a step
/// the way is blocked waits until it is free), 00-03 one step up / down / left / right, 18 x y puts the walker on x,y,
/// 0C s sets the speed (8 is the usual 2 pixels a frame: 4 slow, 16 running), 08 d turns it to face d (action 18 + d),
/// 1B n waits n frames, 0F ends the walk and holds the walker. Checked on all 1,600 paths of the US game.
/// </summary>
public static class L2Paths
{
    public const int DefaultFlags = 0xC8;
    public const int FlagCameraStays = 0x40, FlagEventGoesOn = 0x10;
    public const int NormalSpeed = 8;

    /// <summary>A path record and its walk program (the bytes up to and with the end command).</summary>
    public sealed record Path(int Id, int X, int Y, int Flags, int Offset, byte[] Program);

    /// <summary>A path as the editor draws it: the places it heads for in turn, its speed, where it faces at the end
    /// (-1: as it last walked), a wait in frames before it sets off, and how the camera and the event go along.</summary>
    public sealed class Plan
    {
        public List<(int X, int Y)> Corners { get; init; } = new();
        public int Speed { get; set; } = NormalSpeed;
        public int FaceAtEnd { get; set; } = -1;
        public int WaitFirst { get; set; }
        public bool CameraFollows { get; set; }
        public bool EventGoesOn { get; set; }
        public int Flags => DefaultFlags & ~(CameraFollows ? FlagCameraStays : 0) | (EventGoesOn ? FlagEventGoesOn : 0);
    }

    /// <summary>Bytes of a walk command (with its arguments).</summary>
    public static int OpLength(int op) => op switch
    {
        0x08 or 0x0C or 0x1A or 0x1B or 0x1C or 0x1D or 0x1F or 0x23 or 0x24 or 0x2B or 0x47 or 0x4F => 2,
        0x09 or 0x0B or 0x12 or 0x18 or 0x22 or 0x29 or 0x2C or 0x2D or 0x25 or 0x26 or 0x27 or 0x32 or 0x33 => 3,
        0x11 or 0x13 or 0x31 or 0x36 or 0x37 or 0x3D or 0x44 or 0x46 or 0x21 or 0x28 or 0x3F => 4,
        0x41 => 5,
        0x20 => 6,
        0x2A => 9,
        _ => 1,
    };

    /// <summary>Commands after which the program doesn't go on in a line (an end, or a jump).</summary>
    public static bool Ends(int op) => op is 0x0F or 0x1E or 0x2F or 0x48 or 0x49 or 0x4E or 0x22 or 0x29;

    /// <summary>The walk program at <paramref name="offset"/> of an info block, up to and with its end command (at most
    /// 255 bytes; null when it runs past the block).</summary>
    public static byte[]? ProgramAt(byte[] info, int offset)
    {
        int p = offset;
        while (p >= 0 && p < info.Length && p - offset < 255)
        {
            int op = info[p];
            int n = OpLength(op);
            if (p + n > info.Length) return null;
            p += n;
            if (Ends(op)) return info.AsSpan(offset, p - offset).ToArray();
        }
        return null;
    }

    /// <summary>The paths of a map's info block (as <see cref="L2MapEdit.Info"/> holds it).</summary>
    public static List<Path> Read(byte[] info)
    {
        var list = new List<Path>();
        if (info.Length < 0x2C) return list;
        int p = info[L2MapEdit.Paths] | info[L2MapEdit.Paths + 1] << 8;
        for (int guard = 0; guard < 128 && p >= 0x2C && p + 6 <= info.Length && info[p] != 0xFF; guard++, p += 6)
        {
            int off = info[p + 4] | info[p + 5] << 8;
            list.Add(new Path(info[p], info[p + 1], info[p + 2], info[p + 3], off, ProgramAt(info, off) ?? Array.Empty<byte>()));
        }
        return list;
    }

    public static List<Path> Read(RomBuffer rom, int map)
    {
        int b = L2MapInfo.Block(rom, map);
        if (b < 0 || b + 2 > rom.Length) return new();
        int size = rom.ReadUInt16Le(b);
        return b + size <= rom.Length ? Read(rom.ReadBytes(b, size)) : new();
    }

    /// <summary>The walk program of a plan: speed, a wait, the places in turn, a turn at the end, end.</summary>
    public static byte[] Build(Plan plan)
    {
        var b = new List<byte>();
        if (plan.Speed != NormalSpeed) { b.Add(0x0C); b.Add((byte)plan.Speed); }
        if (plan.WaitFirst > 0) { b.Add(0x1B); b.Add((byte)Math.Min(255, plan.WaitFirst)); }
        foreach (var (x, y) in plan.Corners) { b.Add(0x12); b.Add((byte)x); b.Add((byte)y); }
        if (plan.FaceAtEnd is >= 0 and <= 3) { b.Add(0x08); b.Add((byte)plan.FaceAtEnd); }
        b.Add(0x0F);
        return b.ToArray();
    }

    /// <summary>A path as a plan the editor can draw, or null when its program does more than heading for places,
    /// single steps, speed, waits and a turn (those single steps become places, from <paramref name="startX"/>,
    /// <paramref name="startY"/>: where the walker stands when it sets off).</summary>
    public static Plan? ToPlan(Path path, int startX, int startY)
    {
        var plan = new Plan { CameraFollows = (path.Flags & FlagCameraStays) == 0, EventGoesOn = (path.Flags & FlagEventGoesOn) != 0 };
        int x = startX, y = startY;
        var prog = path.Program;
        bool started = false;
        int lastStep = -1;   // the way of the step before (-1: the command before wasn't a single step)
        for (int p = 0; p < prog.Length;)
        {
            int op = prog[p];
            if (op == 0x0F) return plan.Corners.Count > 0 ? plan : null;
            if (plan.FaceAtEnd >= 0) return null;   // a turn in the middle
            switch (op)
            {
                case 0x0C when p + 1 < prog.Length && !started: plan.Speed = prog[p + 1]; break;
                case 0x1B when p + 1 < prog.Length && !started && plan.WaitFirst == 0: plan.WaitFirst = prog[p + 1]; break;
                case 0x12 when p + 2 < prog.Length: x = prog[p + 1]; y = prog[p + 2]; plan.Corners.Add((x, y)); started = true; lastStep = -1; p += 3; continue;
                case <= 0x03:
                    x += Dx[op]; y += Dy[op];
                    // a run of steps the same way stays one place (out and back again is two)
                    if (lastStep == op) plan.Corners[^1] = (x, y);
                    else plan.Corners.Add((x, y));
                    started = true;
                    lastStep = op;
                    p++;
                    continue;
                case 0x08 when p + 1 < prog.Length && prog[p + 1] <= 3: plan.FaceAtEnd = prog[p + 1]; break;
                default: return null;
            }
            p += OpLength(op);
        }
        return null;
    }

    public static readonly int[] Dx = { 0, 0, -1, 1 }, Dy = { -1, 1, 0, 0 };

    /// <summary>The tiles a walk goes over from (<paramref name="x"/>, <paramref name="y"/>) to each place in turn, as
    /// 12 x y walks (sideways first, then up or down), with the way of each step (0 up, 1 down, 2 left, 3 right).</summary>
    public static List<(int X, int Y, int Dir)> Route(int x, int y, IEnumerable<(int X, int Y)> corners)
    {
        var list = new List<(int, int, int)>();
        foreach (var (tx, ty) in corners)
        {
            while (x != tx && list.Count < 4096) { int d = tx < x ? 2 : 3; x += Dx[d]; list.Add((x, y, d)); }
            while (y != ty && list.Count < 4096) { int d = ty < y ? 0 : 1; y += Dy[d]; list.Add((x, y, d)); }
        }
        return list;
    }

    /// <summary>Plays a walk program from (<paramref name="x"/>, <paramref name="y"/>): the tiles it ends on, the way it
    /// faces at the end, the tiles it went over and roughly how many frames it takes. Commands the stage can't follow
    /// (leader checks, random steps, jumps) end the walk where it is.</summary>
    public static (int X, int Y, int Dir, List<(int X, int Y, int Dir)> Tiles, int Frames) Play(byte[] program, int x, int y, int dir)
    {
        var tiles = new List<(int X, int Y, int Dir)>();
        int speed = NormalSpeed, frames = 0;
        for (int p = 0; p < program.Length;)
        {
            int op = program[p];
            int n = OpLength(op);
            if (p + n > program.Length) break;
            switch (op)
            {
                case <= 0x03:
                    x += Dx[op]; y += Dy[op]; dir = op; tiles.Add((x, y, op)); frames += 64 / Math.Max(1, speed);
                    break;
                case 0x12:
                {
                    var leg = Route(x, y, new[] { ((int)program[p + 1], (int)program[p + 2]) });
                    tiles.AddRange(leg);
                    if (leg.Count > 0) { (x, y, dir) = leg[^1]; }
                    frames += leg.Count * 64 / Math.Max(1, speed);
                    break;
                }
                case 0x18: x = program[p + 1]; y = program[p + 2]; tiles.Add((x, y, dir)); break;
                case 0x0C: speed = Math.Max(1, (int)program[p + 1]); break;
                case 0x08 when program[p + 1] <= 3: dir = program[p + 1]; break;
                case 0x1B: frames += program[p + 1]; break;
                case 0x08 or 0x09 or 0x0A or 0x0B or 0x0D or 0x0E or 0x1A or 0x1D or 0x2C or 0x4F or 0x4A or 0x4B or 0x4C or 0x4D: break;
                default: return (x, y, dir, tiles, frames);
            }
            if (Ends(op)) break;
            p += n;
        }
        return (x, y, dir, tiles, frames);
    }
}
