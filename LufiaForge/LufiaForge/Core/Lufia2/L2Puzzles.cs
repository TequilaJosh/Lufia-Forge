namespace LufiaForge.Core.Lufia2;

/// <summary>
/// Lufia II dungeon puzzle scripts (US ROM, read from the code 2026-10-23; all 3007 scripts parse): switches,
/// pressure plates, doors, movable objects, tile changes, bomb / sword / arrow / hammer / action-button spots. Run by
/// the third interpreter ($80:CC35, table $80:E5A4, opcodes 00-BE) in up to 8 threads. Map table $8F:8000 (file
/// 0x78000): 3 bytes per map (u16 lo, u8 hi; block at file 0x78000 + hi * 0x8000 + lo, FFFFFF = none). A block
/// starts with 16 u16 offsets, one per category (00 engine events: id 02 sets the room up on load; 02 action
/// button, 04 sword, 06 arrow, 08 bomb, 0E hammer spots by the map info's spot id; 0A object groups, byte lists),
/// each to (id, u16 offset) records ended by FF. All offsets and jumps count from the block start.
/// Puzzle flags are bits at $7F:D100, variables bytes at $7F:D074.
/// </summary>
public static class L2Puzzles
{
    public const int Table = 0x78000;
    public static readonly string[] Categories =
    {
        "Event", "Action button spot", "Sword spot", "Arrow spot", "Bomb spot", "Object group", "(category 0C)", "Hammer spot",
        "(category 10)", "(category 12)", "(category 14)", "(category 16)", "(category 18)", "(category 1A)", "(category 1C)", "(category 1E)",
    };

    /// <summary>Argument bytes (-1: an FF-ended list then a u16 target), where its u16 targets sit among the
    /// arguments, the argument letters (f flag, p place, w target, v variable, b byte, r rectangle, o object,
    /// a actor) and the meaning.</summary>
    public static readonly Dictionary<int, (int Args, int[] Jumps, string Form, string Name)> Ops = new()
    {
        [0x00] = (0, new int[] {  }, "0", "end thread (also 07 2C 2D 2E 56 62 93 9B AC AD, same handler)"),
        [0x01] = (3, new int[] { 1 }, "f w", "if flag f set goto w"),
        [0x02] = (1, new int[] {  }, "b", "door b: open"),
        [0x03] = (1, new int[] {  }, "b", "door b: close"),
        [0x04] = (4, new int[] { 2 }, "p m w", "test collision bits at p (groups: OR of members); if any set goto w"),
        [0x05] = (2, new int[] {  }, "p m", "same test, result + 'changed' into the thread result (see E3D9 below)"),
        [0x06] = (2, new int[] {  }, "p b", "tile-block operation at p with argument b (guess)"),
        [0x07] = (0, new int[] {  }, "0", "end thread"),
        [0x08] = (1, new int[] {  }, "f", "set flag f"),
        [0x09] = (1, new int[] {  }, "f", "clear flag f"),
        [0x0A] = (2, new int[] { 0 }, "w", "goto w"),
        [0x0B] = (1, new int[] {  }, "b", "sound effect b"),
        [0x0C] = (3, new int[] { 1 }, "f w", "if flag f clear goto w"),
        [0x0D] = (3, new int[] { 1 }, "b w", "if $0692 == b goto w"),
        [0x0E] = (2, new int[] {  }, "p b", "like 06 with $AE = FF (guess)"),
        [0x0F] = (3, new int[] { 1 }, "p w", "$83:FB51 at p; if bit0 goto w (guess)"),
        [0x10] = (0, new int[] {  }, "0", "$83:8E66, $1273"),
        [0x11] = (1, new int[] {  }, "n", "wait n frames (yield)"),
        [0x12] = (3, new int[] { 1 }, "p w", "occupancy test of p; if false goto w"),
        [0x13] = (1, new int[] {  }, "p", "same test -> result/changed"),
        [0x14] = (3, new int[] { 1 }, "p w", "if flag p set goto w; else test char/object (mask 09) on p, if none goto w"),
        [0x15] = (1, new int[] {  }, "p", "test char or object on p -> result/changed"),
        [0x16] = (3, new int[] { 1 }, "p w", "same test; if false goto w"),
        [0x17] = (1, new int[] {  }, "p", "test object (& 08) on p -> result/changed"),
        [0x18] = (3, new int[] { 1 }, "p w", "same; if false goto w"),
        [0x19] = (2, new int[] { 0 }, "w", "if result false goto w"),
        [0x1A] = (1, new int[] {  }, "b", "run the map-info entry b of list $06 (warp/exit) via $80:BFAA, $83:B76E"),
        [0x1B] = (2, new int[] {  }, "a b", "write b to PPU register $2100 + a"),
        [0x1C] = (2, new int[] {  }, "a b", "screen effect: $1261"),
        [0x1D] = (0, new int[] {  }, "0", "JSL $80:B97E (guess)"),
        [0x1E] = (4, new int[] { 0, 2 }, "wA wB", "if not changed goto wA; else if result false goto wB (two jump targets)"),
        [0x1F] = (3, new int[] {  }, "x y b", "tile-block op at literal x, y (guess)"),
        [0x20] = (-1, new int[] {  }, "[ids FF] w", "result = FF; for each party/actor id: if $0736 bit 20 clear -> result 0; then if result false goto w"),
        [0x21] = (2, new int[] {  }, "x y", "tile op at literal x, y (guess)"),
        [0x22] = (1, new int[] {  }, "p", "tile op at p (guess)"),
        [0x23] = (1, new int[] {  }, "b", "player actor: action via $83:D350, sets walk flags (guess)"),
        [0x24] = (3, new int[] {  }, "x y o", "place object o at literal x, y (guess)"),
        [0x25] = (2, new int[] {  }, "p o", "place object o at p (guess)"),
        [0x26] = (-1, new int[] {  }, "[params FF] w", "if result bit0 (changed) spawn a thread at w, else skip"),
        [0x27] = (-1, new int[] {  }, "[params FF] w", "spawn: new thread at w, params -> its vars FC.. (FB-FF in the list = this thread's vars); the new thread runs at once"),
        [0x28] = (1, new int[] {  }, "b", "door b: open if result true, else close (jumps into 02/03)"),
        [0x29] = (0, new int[] {  }, "0", "store result bit7 into flag (thread var FB = last tested operand)"),
        [0x2A] = (2, new int[] {  }, "p b", "add movable object at p with tile b (D69C..), draw it"),
        [0x2B] = (2, new int[] { 0 }, "w", "if result is not (changed and false) goto w"),
        [0x2C] = (0, new int[] {  }, "0", "end thread"),
        [0x2D] = (0, new int[] {  }, "0", "end thread"),
        [0x2E] = (0, new int[] {  }, "0", "end thread"),
        [0x2F] = (2, new int[] {  }, "v x", "var v = value x (x resolved: A0-BF = another var)"),
        [0x30] = (2, new int[] {  }, "v b", "var v += b"),
        [0x31] = (2, new int[] {  }, "v b", "var v -= b"),
        [0x32] = (1, new int[] {  }, "v", "var v += 1"),
        [0x33] = (1, new int[] {  }, "v", "var v -= 1"),
        [0x34] = (2, new int[] {  }, "v b", "var v = b"),
        [0x35] = (4, new int[] { 2 }, "v b w", "if var v ? literal b goto w: 35 ==, 36 !=, 37 >, 38 <, 39 >=, 3A <="),
        [0x36] = (4, new int[] { 2 }, "v b w", "if var v ? literal b goto w: 35 ==, 36 !=, 37 >, 38 <, 39 >=, 3A <="),
        [0x37] = (4, new int[] { 2 }, "v b w", "if var v ? literal b goto w: 35 ==, 36 !=, 37 >, 38 <, 39 >=, 3A <="),
        [0x38] = (4, new int[] { 2 }, "v b w", "if var v ? literal b goto w: 35 ==, 36 !=, 37 >, 38 <, 39 >=, 3A <="),
        [0x39] = (4, new int[] { 2 }, "v b w", "if var v ? literal b goto w: 35 ==, 36 !=, 37 >, 38 <, 39 >=, 3A <="),
        [0x3A] = (4, new int[] { 2 }, "v b w", "if var v ? literal b goto w: 35 ==, 36 !=, 37 >, 38 <, 39 >=, 3A <="),
        [0x3B] = (4, new int[] { 2 }, "v x w", "same six compares (3B ==, 3C !=, 3D >, 3E <, 3F >=, 40 <=) against a resolved value x"),
        [0x3C] = (4, new int[] { 2 }, "v x w", "same six compares (3B ==, 3C !=, 3D >, 3E <, 3F >=, 40 <=) against a resolved value x"),
        [0x3D] = (4, new int[] { 2 }, "v x w", "same six compares (3B ==, 3C !=, 3D >, 3E <, 3F >=, 40 <=) against a resolved value x"),
        [0x3E] = (4, new int[] { 2 }, "v x w", "same six compares (3B ==, 3C !=, 3D >, 3E <, 3F >=, 40 <=) against a resolved value x"),
        [0x3F] = (4, new int[] { 2 }, "v x w", "same six compares (3B ==, 3C !=, 3D >, 3E <, 3F >=, 40 <=) against a resolved value x"),
        [0x40] = (4, new int[] { 2 }, "v x w", "same six compares (3B ==, 3C !=, 3D >, 3E <, 3F >=, 40 <=) against a resolved value x"),
        [0x41] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x42] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x43] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x44] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x45] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x46] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x47] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x48] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x49] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x4A] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x4B] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x4C] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x4D] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x4E] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x4F] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x50] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x51] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x52] = (2, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x53] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x54] = (1, new int[] {  }, "r [b]", "rectangle register r (E0-FF): 41-45 x1, 46-4A y1, 4B-4F x2, 50-54 y2; sub-op (op-41)%5: 0 = b, 1 += b, 2 -= b (2 args), 3 ++, 4 -- (1 arg)"),
        [0x55] = (2, new int[] {  }, "r p", "rect r = p as raw 4 bytes"),
        [0x56] = (0, new int[] {  }, "0", "end thread"),
        [0x57] = (0, new int[] {  }, "0", "nop"),
        [0x58] = (5, new int[] {  }, "s dx dy a b", "per-layer scroll/camera move: $7FD0CE/D0D6 + 2s += dx/dy, speeds a/b, $1261"),
        [0x59] = (0, new int[] {  }, "0", "finish/reset that scroll"),
        [0x5A] = (2, new int[] {  }, "o b", "object o face/turn: direction 04/00/02/06 (guess)"),
        [0x5B] = (2, new int[] {  }, "o b", "object o face/turn: direction 04/00/02/06 (guess)"),
        [0x5C] = (2, new int[] {  }, "o b", "object o face/turn: direction 04/00/02/06 (guess)"),
        [0x5D] = (2, new int[] {  }, "o b", "object o face/turn: direction 04/00/02/06 (guess)"),
        [0x5E] = (2, new int[] {  }, "v p", "var v = number of the movable object at p, FF if none"),
        [0x5F] = (1, new int[] {  }, "o", "wait until movable object o has stopped (re-runs itself)"),
        [0x60] = (1, new int[] {  }, "a", "actor a (+4F) action $8E:BB17 (guess)"),
        [0x61] = (1, new int[] {  }, "a", "actor a (+4F) action $8E:BAF6 (guess)"),
        [0x62] = (0, new int[] {  }, "0", "end thread"),
        [0x63] = (0, new int[] {  }, "0", "start category-00 id-00 script as a new thread, end this one"),
        [0x64] = (3, new int[] {  }, "p a s", "actor a moves to p, direction 0/1/2/3, speed s"),
        [0x65] = (3, new int[] {  }, "p a s", "actor a moves to p, direction 0/1/2/3, speed s"),
        [0x66] = (3, new int[] {  }, "p a s", "actor a moves to p, direction 0/1/2/3, speed s"),
        [0x67] = (3, new int[] {  }, "p a s", "actor a moves to p, direction 0/1/2/3, speed s"),
        [0x68] = (1, new int[] {  }, "o", "wait until actor o has stopped"),
        [0x69] = (2, new int[] {  }, "r p", "rect r = p (x1,y1,x2,y2)"),
        [0x6A] = (1, new int[] {  }, "b", "player (actor 0) action b (guess)"),
        [0x6B] = (0, new int[] {  }, "0", "wait until the player has stopped"),
        [0x6C] = (3, new int[] { 1 }, "p w", "$83:FB51 at p; if bit0 clear goto w (guess)"),
        [0x6D] = (3, new int[] { 1 }, "p w", "occupancy test of p; if true goto w"),
        [0x6E] = (3, new int[] { 1 }, "p w", "if char or object on p goto w"),
        [0x6F] = (3, new int[] { 1 }, "p w", "if object on p goto w"),
        [0x70] = (4, new int[] { 2 }, "p m w", "collision bits test; if none goto w"),
        [0x71] = (3, new int[] { 1 }, "b w", "if $0692 != b goto w"),
        [0x72] = (1, new int[] {  }, "p", "test character (& 01) on p -> result/changed"),
        [0x73] = (3, new int[] { 1 }, "p w", "same; if false goto w"),
        [0x74] = (3, new int[] { 1 }, "p w", "same; if true goto w"),
        [0x75] = (1, new int[] {  }, "b", "result =: last tool/attack hit id == b (guess)"),
        [0x76] = (3, new int[] { 1 }, "b w", "same test; if false goto w"),
        [0x77] = (3, new int[] { 1 }, "b w", "same test; if true goto w"),
        [0x78] = (3, new int[] {  }, "p b x", "$80:D533 (move/slide something from p, b, value x) (guess)"),
        [0x79] = (1, new int[] {  }, "b", "$7FE316 = b (object speed) (guess)"),
        [0x7A] = (2, new int[] {  }, "p b", "tile-block op (load block b at p, redraw) (guess)"),
        [0x7B] = (0, new int[] {  }, "0", "JSL $8E:BD77 (guess)"),
        [0x7C] = (4, new int[] {  }, "p a s t", "as 64-67 plus $7FE316,a = t"),
        [0x7D] = (4, new int[] {  }, "p a s t", "as 64-67 plus $7FE316,a = t"),
        [0x7E] = (4, new int[] {  }, "p a s t", "as 64-67 plus $7FE316,a = t"),
        [0x7F] = (4, new int[] {  }, "p a s t", "as 64-67 plus $7FE316,a = t"),
        [0x80] = (4, new int[] {  }, "p a s t", "move with no direction (cmd 5C)"),
        [0x81] = (3, new int[] {  }, "p a s", "move, direction 3, cmd 5C"),
        [0x82] = (2, new int[] {  }, "r o", "rect r = 1x1 cell of movable object o (if present)"),
        [0x83] = (2, new int[] {  }, "r b", "rect r.x2 = r.x1 + b"),
        [0x84] = (2, new int[] {  }, "r b", "rect r.y2 = r.y1 + b"),
        [0x85] = (2, new int[] {  }, "p b", "warp destination: $05BD/$05BE = p, $05BF = b, $7FD0BF = FF"),
        [0x86] = (0, new int[] {  }, "0", "start that warp ($05B5"),
        [0x87] = (3, new int[] {  }, "m p p", "compare the tiles at two places -> result"),
        [0x88] = (5, new int[] { 3 }, "m p p w", "same; if false goto w"),
        [0x89] = (5, new int[] { 3 }, "m p p w", "same; if true goto w"),
        [0x8A] = (1, new int[] {  }, "o", "actor o: action 5D, $83:F7D4 (guess)"),
        [0x8B] = (1, new int[] {  }, "o", "actor o: stop/hide ($0622"),
        [0x8C] = (0, new int[] {  }, "0", "redraw the player sprite (guess)"),
        [0x8D] = (3, new int[] {  }, "mode p r", "copy a tile rectangle and redraw"),
        [0x8E] = (6, new int[] { 3 }, "mode p p w h", "same with explicit width/height"),
        [0x8F] = (1, new int[] {  }, "b", "apply tile-change set b from $91:B8B5"),
        [0x90] = (1, new int[] {  }, "r", "tile op on rectangle r (guess)"),
        [0x91] = (2, new int[] {  }, "p b", "variant of 06 (guess)"),
        [0x92] = (3, new int[] {  }, "mode p r", "variant of 8D (guess)"),
        [0x93] = (0, new int[] {  }, "0", "end thread"),
        [0x94] = (2, new int[] {  }, "p b", "tile compare 2 -> result (guess)"),
        [0x95] = (4, new int[] { 2 }, "p b w", "same; if false goto w"),
        [0x96] = (4, new int[] { 2 }, "p b w", "same; if true goto w"),
        [0x97] = (1, new int[] {  }, "a", "actor a (+4F): one step down/left/up/right"),
        [0x98] = (1, new int[] {  }, "a", "actor a (+4F): one step down/left/up/right"),
        [0x99] = (1, new int[] {  }, "a", "actor a (+4F): one step down/left/up/right"),
        [0x9A] = (1, new int[] {  }, "a", "actor a (+4F): one step down/left/up/right"),
        [0x9B] = (0, new int[] {  }, "0", "end thread"),
        [0x9C] = (2, new int[] {  }, "v p", "var v = tile/attribute at p"),
        [0x9D] = (3, new int[] {  }, "v r k", "var v = byte k of rect register r (k = 0/40/80/C0 for x1/y1/x2/y2)"),
        [0x9E] = (3, new int[] {  }, "v r k", "that byte = var v"),
        [0x9F] = (2, new int[] {  }, "p p", "D10B bit 7 = (the two places are the same cell)"),
        [0xA0] = (1, new int[] {  }, "b", "door b open, no animation"),
        [0xA1] = (1, new int[] {  }, "b", "door b close, no animation"),
        [0xA2] = (0, new int[] {  }, "0", "$05B5"),
        [0xA3] = (4, new int[] {  }, "p o s t", "move object o to p (direction 0-3), speed s, $7FE316 = t"),
        [0xA4] = (4, new int[] {  }, "p o s t", "move object o to p (direction 0-3), speed s, $7FE316 = t"),
        [0xA5] = (4, new int[] {  }, "p o s t", "move object o to p (direction 0-3), speed s, $7FE316 = t"),
        [0xA6] = (4, new int[] {  }, "p o s t", "move object o to p (direction 0-3), speed s, $7FE316 = t"),
        [0xA7] = (1, new int[] {  }, "p", "$7FD133 = $83:FB71 at p (tile type) (guess)"),
        [0xA8] = (2, new int[] {  }, "p x", "tile-block op, b resolved (guess)"),
        [0xA9] = (-1, new int[] {  }, "[params FF] w", "call w (pushes return address, saves FB-FE and result; params -> FC..)"),
        [0xAA] = (0, new int[] {  }, "0", "return from A9 (restores vars/result)"),
        [0xAB] = (0, new int[] {  }, "0", "$7FD0BF = FF (guess)"),
        [0xAC] = (0, new int[] {  }, "0", "end thread"),
        [0xAD] = (0, new int[] {  }, "0", "end thread"),
        [0xAE] = (1, new int[] {  }, "b", "actor $7FD0A2 action b (guess)"),
        [0xAF] = (3, new int[] {  }, "p o s", "actor o walks (direction 0-3) to p, speed s (cmd 74+d, $0736"),
        [0xB0] = (3, new int[] {  }, "p o s", "actor o walks (direction 0-3) to p, speed s (cmd 74+d, $0736"),
        [0xB1] = (3, new int[] {  }, "p o s", "actor o walks (direction 0-3) to p, speed s (cmd 74+d, $0736"),
        [0xB2] = (3, new int[] {  }, "p o s", "actor o walks (direction 0-3) to p, speed s (cmd 74+d, $0736"),
        [0xB3] = (3, new int[] {  }, "p o s", "actor o to p, $83:F5EA (jump/teleport?) (guess)"),
        [0xB4] = (3, new int[] {  }, "dx dy b", "camera target = player + (dx, dy), $05A8 = b, $1261"),
        [0xB5] = (0, new int[] {  }, "0", "camera release"),
        [0xB6] = (0, new int[] {  }, "0", "player sprite flag 02 on (guess)"),
        [0xB7] = (0, new int[] {  }, "0", "player sprite flag 02 off (guess)"),
        [0xB8] = (0, new int[] {  }, "0", "$72"),
        [0xB9] = (0, new int[] {  }, "0", "$72 &= ~1, then as 8C"),
        [0xBA] = (2, new int[] {  }, "t p", "move map-info treasure record t to p"),
        [0xBB] = (1, new int[] {  }, "b", "$099C &= ~80, start event/text b"),
        [0xBC] = (2, new int[] {  }, "a p", "actor a (+4F) goes to p"),
        [0xBD] = (1, new int[] {  }, "b", "JSL $83:E033 with b (guess)"),
        [0xBE] = (2, new int[] {  }, "dx dy", "scroll the map view by dx, dy from the player (guess)"),
    };

    private static readonly HashSet<int> Ends = new() { 0x00, 0x07, 0x2C, 0x2D, 0x2E, 0x56, 0x62, 0x93, 0x9B, 0xAC, 0xAD, 0x0A, 0x63, 0xAA };

    /// <summary>File offset of map <paramref name="map"/>'s puzzle block, or -1.</summary>
    public static int Block(RomBuffer rom, int map)
    {
        int e = Table + map * 3;
        int lo = rom.ReadUInt16Le(e), hi = rom.ReadByte(e + 2);
        if (lo == 0xFFFF && hi == 0xFF) return -1;
        int at = Table + hi * 0x8000 + lo;
        return at < rom.Length ? at : -1;
    }

    public static List<(int Category, int Id, int Offset)> Entries(RomBuffer rom, int block)
    {
        var list = new List<(int, int, int)>();
        for (int c = 0; c < 16; c++)
        {
            int at = block + rom.ReadUInt16Le(block + c * 2);
            for (int i = 0; i < 128 && at + i * 3 < rom.Length; i++)
            {
                int id = rom.ReadByte(at + i * 3);
                if (id == 0xFF) break;
                list.Add((c, id, rom.ReadUInt16Le(at + i * 3 + 1)));
            }
        }
        return list;
    }

    public static int Length(RomBuffer rom, int p)
    {
        int op = rom.ReadByte(p);
        if (!Ops.TryGetValue(op, out var d)) return 1;
        if (d.Args >= 0) return 1 + d.Args;
        int q = p + 1;
        while (q < rom.Length && rom.ReadByte(q) != 0xFF) q++;
        return q + 1 + 2 - p;
    }

    private static List<int> Targets(RomBuffer rom, int p, int len, int block)
    {
        int op = rom.ReadByte(p);
        var t = new List<int>();
        if (!Ops.TryGetValue(op, out var d)) return t;
        if (d.Args < 0) t.Add(block + rom.ReadUInt16Le(p + len - 2));
        else foreach (int j in d.Jumps) t.Add(block + rom.ReadUInt16Le(p + 1 + j));
        return t;
    }

    /// <summary>Every command reachable from <paramref name="start"/>, in address order, as listing lines.</summary>
    public static List<L2Script.Op> Walk(RomBuffer rom, int start, int block, int blockEnd)
    {
        var ops = new SortedDictionary<int, L2Script.Op>();
        var todo = new Stack<int>(); todo.Push(start);
        var seen = new HashSet<int>();
        while (todo.Count > 0)
        {
            int p = todo.Pop();
            for (int guard = 0; guard < 2000 && p >= block && p < blockEnd && seen.Add(p); guard++)
            {
                int op = rom.ReadByte(p), len = Length(rom, p);
                var targets = Targets(rom, p, len, block);
                foreach (var t in targets) todo.Push(t);
                string bytes = string.Join(" ", rom.ReadBytes(p + 1, len - 1).Select(b => b.ToString("X2")));
                string name = Ops.TryGetValue(op, out var d) ? d.Name : "(not a command)";
                string jumps = targets.Count > 0 ? "  -> " + string.Join(", ", targets.Select(t => t.ToString("X6"))) : "";
                ops[p] = new L2Script.Op(p, len, $"{op:X2} {bytes,-24} {name}{jumps}");
                p += len;
                if (Ends.Contains(op) || !Ops.ContainsKey(op)) break;
            }
        }
        return ops.Values.ToList();
    }

    /// <summary>A map's puzzle scripts by category and id (object groups shown as their member lists).</summary>
    public static List<(string Title, List<L2Script.Op> Ops)> MapScripts(RomBuffer rom, int map)
    {
        var list = new List<(string, List<L2Script.Op>)>();
        int block = Block(rom, map);
        if (block < 0) return list;
        int end = NextBlock(rom, block);
        foreach (var (cat, id, off) in Entries(rom, block))
        {
            string title = $"Puzzle: {Categories[cat]} {id:X2}{(cat == 0 && id == 2 ? " (room set-up)" : "")}";
            if (cat == 5)
            {
                var members = new List<string>();
                for (int p = block + off; p < end && rom.ReadByte(p) != 0xFF && members.Count < 64; p++) members.Add(rom.ReadByte(p).ToString("X2"));
                list.Add((title, new List<L2Script.Op> { new(block + off, members.Count + 1, "members: " + string.Join(" ", members)) }));
                continue;
            }
            list.Add((title, Walk(rom, block + off, block, end)));
        }
        return list;
    }

    private static int NextBlock(RomBuffer rom, int block)
    {
        int next = 0x83E64;   // the end of the puzzle data
        for (int m = 0; m < 0xF2; m++) { int b = Block(rom, m); if (b > block && b < next) next = b; }
        return next;
    }
}
