using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;
using LufiaForge.Core.Maps;

namespace LufiaForge.Modules.MapEditor;

// alt + mouse wheel turns the brush block: normal, flipped across, turned 180 degrees, flipped up/down. map cells
// can't flip a block, so a turned brush is another block of the tileset: its four quarters swapped, each tile
// flipped and the walls swapped to match. if the tileset has that block already the brush becomes it, if not it gets
// added at the end of the tileset (other maps using the tileset don't change, Edit > Undo takes it back)
public partial class MapEditorViewModel
{
    private int _turnBase = -1, _turnIndex;
    private bool _turning;

    private static readonly (bool X, bool Y, string Name)[] BrushTurns =
        { (false, false, "normal"), (true, false, "flipped across"), (true, true, "turned 180°"), (false, true, "flipped up/down") };

    // the 9 bytes of block flipped across and/or up/down
    private static byte[] Turned(BlockSet set, int block, bool flipX, bool flipY)
    {
        var bytes = new byte[BlockSet.Size];
        for (int q = 0; q < 4; q++)
        {
            var (qx, qy) = BlockSet.Quadrants[q];
            ushort word = set.Word(block, q);
            int newQuadrant = Array.IndexOf(BlockSet.Quadrants, (flipX ? 1 - qx : qx, flipY ? 1 - qy : qy));
            ushort newWord = BlockSet.MakeWord(BlockSet.TileOf(word), BlockSet.RowOf(word), BlockSet.Priority(word), BlockSet.FlipX(word) ^ flipX, BlockSet.FlipY(word) ^ flipY);
            bytes[newQuadrant * 2] = (byte)newWord; bytes[newQuadrant * 2 + 1] = (byte)(newWord >> 8);
        }
        int attr = set.Attribute(block), newAttr = attr & ~(BlockSet.AttrLeft | BlockSet.AttrRight | BlockSet.AttrUp | BlockSet.AttrDown);
        bool left = (attr & BlockSet.AttrLeft) != 0, right = (attr & BlockSet.AttrRight) != 0, up = (attr & BlockSet.AttrUp) != 0, down = (attr & BlockSet.AttrDown) != 0;
        if (flipX) (left, right) = (right, left);
        if (flipY) (up, down) = (down, up);
        newAttr |= (left ? BlockSet.AttrLeft : 0) | (right ? BlockSet.AttrRight : 0) | (up ? BlockSet.AttrUp : 0) | (down ? BlockSet.AttrDown : 0);
        bytes[8] = (byte)newAttr;
        return bytes;
    }

    // alt + mouse wheel: the brush steps to the next (or previous) way the block can face
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
            var (flipX, flipY, name) = BrushTurns[_turnIndex];
            int turning = _turnBase, turnIndex = _turnIndex;
            var want = Turned(set, _turnBase, flipX, flipY);
            int found = -1;
            for (int block = 0; block < set.BlockCount && found < 0; block++)
            {
                bool same = true;
                for (int k = 0; k < BlockSet.Size && same; k++) same = set.Blocks[block * BlockSet.Size + k] == want[k];
                if (same) found = block;
            }
            string how;
            if (found >= 0) how = found == _turnBase ? "" : $" (block {found:X3}, already in the tileset)";
            else
            {
                if (!set.CanAddBlocks)
                {
                    if (set.CompositeThreshold < 0 || !MakeRoomFor($"The tileset has no {name} copy of block {_turnBase:X3}, and no room for a new block yet."))
                    {
                        if (set.CompositeThreshold < 0) Status = $"The tileset has no {name} copy of block {_turnBase:X3}, and it is full (1024 blocks).";
                        _turnIndex = ((_turnIndex - step) % 4 + 4) % 4;
                        return true;
                    }
                    // making room reloads the map, which resets the brush, so carry on with the block being turned
                    _turnBase = turning; _turnIndex = turnIndex;
                    set = BlockSet.FromMap(_rom, _map!);
                    if (!set.CanAddBlocks) return true;
                }
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

    // rotate / flip (Q, F, D): the brush, or the piece being stamped (a preset, a building, a paste, a palette rectangle)

    [RelayCommand] private void RotateBrush() => TurnBy(1, false, "turned a quarter clockwise");
    [RelayCommand] private void FlipBrushAcross() => TurnBy(0, true, "flipped across");
    [RelayCommand] private void FlipBrushUpDown() => TurnBy(2, true, "flipped up/down");

    // Q, F and D rotate or flip the brush
    private bool TurnKey(System.Windows.Input.Key key)
    {
        switch (key)
        {
            case System.Windows.Input.Key.Q: RotateBrush(); return true;
            case System.Windows.Input.Key.F: FlipBrushAcross(); return true;
            case System.Windows.Input.Key.D: FlipBrushUpDown(); return true;
            default: return false;
        }
    }

    // turns the brush block or the whole piece being stamped: each block becomes its turned copy (found in the
    // tileset or added, see BlockTurn) and the cells move to their turned places
    private void TurnBy(int quarters, bool flip, string what)
    {
        if (_rom == null || _map == null || _tileset == null) return;
        if (_map.IsWorld) { Status = "World map blocks can't be turned (the world map has a fixed set of blocks)."; return; }
        var stamp = _stamp;
        if (stamp != null && stamp.TilesetKey != TilesetKey)
        { Status = "This piece was copied on another tileset and is being pasted as it is: copy it again from this map to turn it."; return; }
        int width = stamp?.W ?? 1, height = stamp?.H ?? 1;
        var cells = stamp != null ? stamp.Tiles.Select(c => c == Prefabs.Keep ? c : (ushort)(c & 0x3FF)).ToArray() : new[] { (ushort)Brush };
        var ids = cells.Where(c => c != Prefabs.Keep).Select(c => (int)c).Distinct().ToList();
        if (ids.Count == 0) return;
        int before = cells[0];
        if (!WritePendingFirst()) return;
        BlockTurn.Result result;
        try
        {
            try { result = BlockTurn.Turn(_rom, _map, ids, quarters, flip); }
            catch (BlockBring.NeedsRoomException ex)
            {
                int threshold = _map.CompositeThreshold;
                if (!MakeRoomFor(ex.Message)) return;
                if (_map == null) return;
                // making room puts the spares where the map's composites started, so those move up
                int shift = _map.CompositeThreshold - threshold;
                if (threshold >= 0 && shift != 0)
                {
                    cells = cells.Select(c => c != Prefabs.Keep && c >= threshold ? (ushort)(c + shift) : c).ToArray();
                    ids = ids.Select(b => b >= threshold ? b + shift : b).ToList();
                }
                result = BlockTurn.Turn(_rom, _map, ids, quarters, flip);
            }
        }
        catch (Exception ex) { Status = "Can't turn it: " + ex.Message; return; }
        if (result.Added > 0)
        {
            _mainVm?.NotifyRomModified($"Tileset {_map.Tileset}: {result.Added} turned block{(result.Added == 1 ? "" : "s")}");
            _tileset = MapTileset.ForMap(_rom, _map);
            BuildPalette();
        }
        var turnedBlock = ids.Select((b, i) => (b, result.Blocks[i])).ToDictionary(x => x.b, x => x.Item2);
        var (newWidth, newHeight) = L2BlockTurn.Size(width, height, quarters);
        var newCells = new ushort[newWidth * newHeight];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (tx, ty) = L2BlockTurn.Point(x, y, width, height, quarters, flip);
                ushort cell = cells[y * width + x];
                newCells[ty * newWidth + tx] = cell == Prefabs.Keep ? cell : (ushort)turnedBlock[cell];
            }
        string added = result.Added > 0
            ? $" ({result.Added} turned block{(result.Added == 1 ? "" : "s")} added to tileset {_map.Tileset}" +
              (result.NewTiles > 0 ? $", {result.NewTiles} new tile{(result.NewTiles == 1 ? "" : "s")}" : "") + "; Edit > Undo takes them out)"
            : "";
        if (stamp != null)
        {
            if (Tool != MapTool.Paint) Tool = MapTool.Paint;
            StartStamp(new BlockPattern(newWidth, newHeight, newCells, false, TilesetKey, stamp.Source, _map.MapId));
            BrushBoxW = BrushBoxH = 16;
            OnBrushBoxChanged();
            Status = $"The piece is {what}{added}. Click to stamp it; Q turns it again, F / D flip it; Esc stops.";
            return;
        }
        Brush = newCells[0];
        Status = $"Brush: block {before:X3} {what} is block {newCells[0]:X3}{added}. Q turns it again, F / D flip it.";
    }
}
