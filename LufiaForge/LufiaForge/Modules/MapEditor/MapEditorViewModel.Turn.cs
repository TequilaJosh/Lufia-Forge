using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;
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
            int turning = _turnBase, turnIndex = _turnIndex;
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
                if (!set.CanAddBlocks)
                {
                    if (set.CompositeThreshold < 0 || !MakeRoomFor($"The tileset has no {name} copy of block {_turnBase:X3}, and no room for a new block yet."))
                    {
                        if (set.CompositeThreshold < 0) Status = $"The tileset has no {name} copy of block {_turnBase:X3}, and it is full (1024 blocks).";
                        _turnIndex = ((_turnIndex - step) % 4 + 4) % 4;
                        return true;
                    }
                    // (making room reloads the map, which resets the brush: carry on with the block being turned)
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

    // ── Rotate / Flip (Q, F, D): the brush, or the piece being stamped (a preset, a building, a paste, a palette rectangle) ──

    [RelayCommand] private void RotateBrush() => TurnBy(1, false, "turned a quarter clockwise");
    [RelayCommand] private void FlipBrushAcross() => TurnBy(0, true, "flipped across");
    [RelayCommand] private void FlipBrushUpDown() => TurnBy(2, true, "flipped up/down");

    /// <summary>Q, F and D: rotate or flip the brush.</summary>
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

    /// <summary>Turns the brush block or the whole piece being stamped: each block becomes its turned copy (found in the
    /// tileset or added, <see cref="BlockTurn"/>), and the cells move to their turned places.</summary>
    private void TurnBy(int quarters, bool flip, string what)
    {
        if (_rom == null || _map == null || _tileset == null) return;
        if (_map.IsWorld) { Status = "World map blocks can't be turned (the world map has a fixed set of blocks)."; return; }
        var p = _stamp;
        if (p != null && p.TilesetKey != TilesetKey)
        { Status = "This piece was copied on another tileset and is being pasted as it is: copy it again from this map to turn it."; return; }
        int w = p?.W ?? 1, h = p?.H ?? 1;
        var cells = p != null ? p.Tiles.Select(c => c == Prefabs.Keep ? c : (ushort)(c & 0x3FF)).ToArray() : new[] { (ushort)Brush };
        var ids = cells.Where(c => c != Prefabs.Keep).Select(c => (int)c).Distinct().ToList();
        if (ids.Count == 0) return;
        int before = cells[0];
        if (!WritePendingFirst()) return;
        BlockTurn.Result r;
        try
        {
            try { r = BlockTurn.Turn(_rom, _map, ids, quarters, flip); }
            catch (BlockBring.NeedsRoomException ex)
            {
                int threshold = _map.CompositeThreshold;
                if (!MakeRoomFor(ex.Message)) return;
                if (_map == null) return;
                // making room puts the spares where the map's composites started: they move up
                int d = _map.CompositeThreshold - threshold;
                if (threshold >= 0 && d != 0)
                {
                    cells = cells.Select(c => c != Prefabs.Keep && c >= threshold ? (ushort)(c + d) : c).ToArray();
                    ids = ids.Select(b => b >= threshold ? b + d : b).ToList();
                }
                r = BlockTurn.Turn(_rom, _map, ids, quarters, flip);
            }
        }
        catch (Exception ex) { Status = "Can't turn it: " + ex.Message; return; }
        if (r.Added > 0)
        {
            _mainVm?.NotifyRomModified($"Tileset {_map.Tileset}: {r.Added} turned block{(r.Added == 1 ? "" : "s")}");
            _tileset = MapTileset.ForMap(_rom, _map);
            BuildPalette();
        }
        var to = ids.Select((b, i) => (b, r.Blocks[i])).ToDictionary(x => x.b, x => x.Item2);
        var (nw, nh) = L2BlockTurn.Size(w, h, quarters);
        var o = new ushort[nw * nh];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (tx, ty) = L2BlockTurn.Point(x, y, w, h, quarters, flip);
                ushort c = cells[y * w + x];
                o[ty * nw + tx] = c == Prefabs.Keep ? c : (ushort)to[c];
            }
        string added = r.Added > 0
            ? $" ({r.Added} turned block{(r.Added == 1 ? "" : "s")} added to tileset {_map.Tileset}" +
              (r.NewTiles > 0 ? $", {r.NewTiles} new tile{(r.NewTiles == 1 ? "" : "s")}" : "") + "; Edit > Undo takes them out)"
            : "";
        if (p != null)
        {
            if (Tool != MapTool.Paint) Tool = MapTool.Paint;
            StartStamp(new BlockPattern(nw, nh, o, false, TilesetKey, p.Source, _map.MapId));
            BrushBoxW = BrushBoxH = 16;
            OnBrushBoxChanged();
            Status = $"The piece is {what}{added}. Click to stamp it; Q turns it again, F / D flip it; Esc stops.";
            return;
        }
        Brush = o[0];
        Status = $"Brush: block {before:X3} {what} is block {o[0]:X3}{added}. Q turns it again, F / D flip it.";
    }
}
