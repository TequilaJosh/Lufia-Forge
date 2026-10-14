using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.MapEditor;

/// <summary>
/// Alt + mouse wheel turns the brush block: normal, flipped across, turned 180 degrees, flipped up/down. Map cells
/// can't flip a block, so a turned brush is another block of the tileset: its four quarters swapped and each tile
/// flipped, walls swapped to match. If the tileset already has that block the brush becomes it; otherwise it is added
/// at the end of the tileset (other maps using the tileset don't change; Edit &gt; Undo takes it back).
/// </summary>
public partial class MapEditorViewModel
{
    private int _turnBase = -1, _turnIndex;
    private bool _turning;

    private static readonly (bool X, bool Y, string Name)[] BrushTurns =
        { (false, false, "normal"), (true, false, "flipped across"), (true, true, "turned 180°"), (false, true, "flipped up/down") };

    /// <summary>The 9 bytes of block <paramref name="b"/> flipped across and/or up/down.</summary>
    private static byte[] Turned(BlockSet set, int b, bool fx, bool fy)
    {
        var r = new byte[BlockSet.Size];
        for (int q = 0; q < 4; q++)
        {
            var (qx, qy) = BlockSet.Quadrants[q];
            ushort w = set.Word(b, q);
            int nq = Array.IndexOf(BlockSet.Quadrants, (fx ? 1 - qx : qx, fy ? 1 - qy : qy));
            ushort nw = BlockSet.MakeWord(BlockSet.TileOf(w), BlockSet.RowOf(w), BlockSet.Priority(w), BlockSet.FlipX(w) ^ fx, BlockSet.FlipY(w) ^ fy);
            r[nq * 2] = (byte)nw; r[nq * 2 + 1] = (byte)(nw >> 8);
        }
        int a = set.Attribute(b), na = a & ~(BlockSet.AttrLeft | BlockSet.AttrRight | BlockSet.AttrUp | BlockSet.AttrDown);
        bool l = (a & BlockSet.AttrLeft) != 0, rt = (a & BlockSet.AttrRight) != 0, u = (a & BlockSet.AttrUp) != 0, d = (a & BlockSet.AttrDown) != 0;
        if (fx) (l, rt) = (rt, l);
        if (fy) (u, d) = (d, u);
        na |= (l ? BlockSet.AttrLeft : 0) | (rt ? BlockSet.AttrRight : 0) | (u ? BlockSet.AttrUp : 0) | (d ? BlockSet.AttrDown : 0);
        r[8] = (byte)na;
        return r;
    }

    /// <summary>Alt + mouse wheel: the brush steps to the next (or previous) way the block can face.</summary>
    public bool TurnBrush(int step)
    {
        if (_rom == null || _map == null || _tileset == null) return false;
        if (_map.IsWorld) { Status = "World map blocks can't be turned (the world map has a fixed set of blocks)."; return true; }
        if (_stamp != null) { Status = "Turning works on a single brush block (not on a pasted or multi-block brush)."; return true; }
        try
        {
            var set = BlockSet.FromMap(_rom, _map);
            if (_turnBase < 0 || _turnBase >= set.BlockCount) { _turnBase = Brush; _turnIndex = 0; }
            _turnIndex = ((_turnIndex + step) % 4 + 4) % 4;
            var (fx, fy, name) = BrushTurns[_turnIndex];
            var want = Turned(set, _turnBase, fx, fy);
            int found = -1;
            for (int b = 0; b < set.BlockCount && found < 0; b++)
            {
                bool same = true;
                for (int k = 0; k < BlockSet.Size && same; k++) same = set.Blocks[b * BlockSet.Size + k] == want[k];
                if (same) found = b;
            }
            string how;
            if (found >= 0) how = found == _turnBase ? "" : $" (block {found:X3}, already in the tileset)";
            else
            {
                if (!set.CanAddBlocks) { Status = $"The tileset has no {name} copy of block {_turnBase:X3}, and blocks can't be added to it (it has composite blocks at the end)."; return true; }
                found = set.AddBlock(_turnBase);
                for (int k = 0; k < BlockSet.Size; k++) set.Blocks[found * BlockSet.Size + k] = want[k];
                set.SetAttribute(found, want[8]);   // marks the blocks changed
                set.Save(_rom, allowExpand: true);
                _mainVm?.NotifyRomModified($"Tileset {_map.Tileset}: block {found:X3} ({name} copy of block {_turnBase:X3})");
                _tileset = MapTileset.ForMap(_rom, _map);
                BuildPalette();
                how = $" (new block {found:X3} added to tileset {_map.Tileset})";
            }
            _turning = true;
            Brush = found;
            _turning = false;
            Status = $"Brush: block {_turnBase:X3} {name}{how}. Alt + mouse wheel turns it again.";
        }
        catch (Exception ex) { Status = "Turn brush: " + ex.Message; }
        return true;
    }
}
