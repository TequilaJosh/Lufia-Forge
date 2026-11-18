namespace LufiaForge.Core.Lufia2;

// a map's paths, what "A character walks a path" (event 33 path who) walks. a path is a 6-byte record of the map
// info's +12 list: id, x, y (the first place it heads for), flags, u16 offset of its walk program in the info block
// (the game copies the block to $7E:F000 and runs the program from there, $80:A80F).
// flags: 40 clear = the camera follows the walker, 10 = the event goes on while it walks (otherwise it waits),
// 20 = a walk in the background. the game's paths have 80 and 08 set too (C8 / 88 / 48 / D8), event 33 doesn't read those.
// the walk program (WalkScriptOps, $83:C7F8):
// - 12 x y heads for block x,y (sideways first, then up or down, a step whose way is blocked waits until it's free)
// - 00-03 one step up / down / left / right
// - 18 x y puts the walker on x,y
// - 0C s sets the speed (8 is the usual 2 pixels a frame, 4 slow, 16 running)
// - 08 d turns it to face d (action 18 + d)
// - 1B n waits n frames
// - 0F ends the walk and holds the walker
// checked on all 1,600 paths of the US game
public static class L2Paths
{
    public const int DefaultFlags = 0xC8;
    public const int FlagCameraStays = 0x40, FlagEventGoesOn = 0x10;
    public const int NormalSpeed = 8;

    // a path record and its walk program (the bytes up to and including the end command)
    public sealed record Path(int Id, int X, int Y, int Flags, int Offset, byte[] Program);

    // a path the way the editor draws it: the places it heads for in turn, its speed, where it faces at the end
    // (-1 = however it last walked), a wait in frames before it sets off, and how the camera and the event go along
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

    // length of a walk command in bytes (with its arguments)
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

    // commands after which the program doesn't carry on in a line (an end, or a jump)
    public static bool Ends(int op) => op is 0x0F or 0x1E or 0x2F or 0x48 or 0x49 or 0x4E or 0x22 or 0x29;

    // the walk program at offset in an info block, up to and including its end command (255 bytes at most, null if
    // it runs past the block)
    public static byte[]? ProgramAt(byte[] info, int offset)
    {
        int p = offset;
        while (p >= 0 && p < info.Length && p - offset < 255)
        {
            int op = info[p];
            int length = OpLength(op);
            if (p + length > info.Length) return null;
            p += length;
            if (Ends(op)) return info.AsSpan(offset, p - offset).ToArray();
        }
        return null;
    }

    // the paths of a map's info block (the way L2MapEdit.Info holds it)
    public static List<Path> Read(byte[] info)
    {
        var list = new List<Path>();
        if (info.Length < 0x2C) return list;
        int record = info[L2MapEdit.Paths] | info[L2MapEdit.Paths + 1] << 8;
        for (int guard = 0; guard < 128 && record >= 0x2C && record + 6 <= info.Length && info[record] != 0xFF; guard++, record += 6)
        {
            int programOffset = info[record + 4] | info[record + 5] << 8;
            list.Add(new Path(info[record], info[record + 1], info[record + 2], info[record + 3], programOffset, ProgramAt(info, programOffset) ?? Array.Empty<byte>()));
        }
        return list;
    }

    public static List<Path> Read(RomBuffer rom, int map)
    {
        int block = L2MapInfo.Block(rom, map);
        if (block < 0 || block + 2 > rom.Length) return new();
        int size = rom.ReadUInt16Le(block);
        return block + size <= rom.Length ? Read(rom.ReadBytes(block, size)) : new();
    }

    // the walk program of a plan: speed, a wait, the places in turn, a turn at the end, end
    public static byte[] Build(Plan plan)
    {
        var program = new List<byte>();
        if (plan.Speed != NormalSpeed) { program.Add(0x0C); program.Add((byte)plan.Speed); }
        if (plan.WaitFirst > 0) { program.Add(0x1B); program.Add((byte)Math.Min(255, plan.WaitFirst)); }
        foreach (var (x, y) in plan.Corners) { program.Add(0x12); program.Add((byte)x); program.Add((byte)y); }
        if (plan.FaceAtEnd is >= 0 and <= 3) { program.Add(0x08); program.Add((byte)plan.FaceAtEnd); }
        program.Add(0x0F);
        return program.ToArray();
    }

    // a path as a plan the editor can draw, or null if its program does more than head for places, single steps,
    // speed, waits and a turn. single steps become places, counted from (startX, startY), where the walker stands
    // when it sets off
    public static Plan? ToPlan(Path path, int startX, int startY)
    {
        var plan = new Plan { CameraFollows = (path.Flags & FlagCameraStays) == 0, EventGoesOn = (path.Flags & FlagEventGoesOn) != 0 };
        int x = startX, y = startY;
        var program = path.Program;
        bool started = false;
        int lastStep = -1;   // the way of the step before (-1 = the command before wasn't a single step)
        for (int p = 0; p < program.Length;)
        {
            int op = program[p];
            if (op == 0x0F) return plan.Corners.Count > 0 ? plan : null;
            if (plan.FaceAtEnd >= 0) return null;   // a turn in the middle
            switch (op)
            {
                case 0x0C when p + 1 < program.Length && !started: plan.Speed = program[p + 1]; break;
                case 0x1B when p + 1 < program.Length && !started && plan.WaitFirst == 0: plan.WaitFirst = program[p + 1]; break;
                case 0x12 when p + 2 < program.Length: x = program[p + 1]; y = program[p + 2]; plan.Corners.Add((x, y)); started = true; lastStep = -1; p += 3; continue;
                case <= 0x03:
                    x += Dx[op]; y += Dy[op];
                    // a run of steps the same way stays one place (out and back again is two)
                    if (lastStep == op) plan.Corners[^1] = (x, y);
                    else plan.Corners.Add((x, y));
                    started = true;
                    lastStep = op;
                    p++;
                    continue;
                case 0x08 when p + 1 < program.Length && program[p + 1] <= 3: plan.FaceAtEnd = program[p + 1]; break;
                default: return null;
            }
            p += OpLength(op);
        }
        return null;
    }

    public static readonly int[] Dx = { 0, 0, -1, 1 }, Dy = { -1, 1, 0, 0 };

    // the tiles a walk goes over from (x, y) to each place in turn, as 12 x y walks (sideways first, then up or down),
    // with the way of each step (0 up, 1 down, 2 left, 3 right)
    public static List<(int X, int Y, int Dir)> Route(int x, int y, IEnumerable<(int X, int Y)> corners)
    {
        var list = new List<(int, int, int)>();
        foreach (var (targetX, targetY) in corners)
        {
            while (x != targetX && list.Count < 4096) { int dir = targetX < x ? 2 : 3; x += Dx[dir]; list.Add((x, y, dir)); }
            while (y != targetY && list.Count < 4096) { int dir = targetY < y ? 0 : 1; y += Dy[dir]; list.Add((x, y, dir)); }
        }
        return list;
    }

    // plays a walk program from (x, y): the tile it ends on, the way it faces at the end, the tiles it went over and
    // about how many frames it takes. commands the stage can't follow (leader checks, random steps, jumps) end the
    // walk where it is
    public static (int X, int Y, int Dir, List<(int X, int Y, int Dir)> Tiles, int Frames) Play(byte[] program, int x, int y, int dir)
    {
        var tiles = new List<(int X, int Y, int Dir)>();
        int speed = NormalSpeed, frames = 0;
        for (int p = 0; p < program.Length;)
        {
            int op = program[p];
            int length = OpLength(op);
            if (p + length > program.Length) break;
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
            p += length;
        }
        return (x, y, dir, tiles, frames);
    }
}
