using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>
/// Turning: the brush (one block or several) or the piece being placed (a preset, a pasted copy, a piece of a map) turned
/// a quarter clockwise (Q), flipped across (F) or up/down (D). The turned blocks are blocks of the map's block set: found
/// when the set has them, else added, tiles turned a quarter added to the tile set (<see cref="L2BlockTurn"/>). A piece's
/// walls turn with it.
/// </summary>
public partial class L2MapsPage
{
    [RelayCommand] private void RotateBrush() => Turn(1, false, "turned a quarter clockwise");
    [RelayCommand] private void FlipBrushAcross() => Turn(0, true, "flipped across");
    [RelayCommand] private void FlipBrushUpDown() => Turn(2, true, "flipped up/down");

    private void Turn(int quarters, bool flip, string what)
    {
        if (_rom == null || _edit == null || _main == null) return;
        if (_world != null) { Status = "World map blocks can't be turned (big blocks are fixed pictures)."; return; }
        bool piece = Stamping && _clip != null;
        if (piece && (_clip!.BlockSet != _edit.BlockSetIndex || _clip.TileSet != _edit.TileSetIndex))
        { Status = "This piece is on other graphics: place it once (its blocks come in), then turn it."; return; }
        int w = piece ? _clip!.W : BrushW, h = piece ? _clip!.H : BrushH;
        var ids = piece ? _clip!.Ground.Concat(_clip.Upper).ToList() : _brush.ToList();
        L2BlockTurn.Prepared p;
        try { p = L2BlockTurn.Prepare(_rom, _edit.MapId, _edit.BlockSetIndex, _edit.TileSetIndex, ids, quarters, flip); }
        catch (Exception ex) { Status = "Can't turn it: " + ex.Message; return; }
        if (p.Added > 0)
        {
            // new blocks: the map's edits are written first, so Edit > Undo takes the blocks out with nothing using them
            if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
            try { L2Rom.AllOrNothing(_rom, () => { p.Write(); return 0; }); }
            catch (Exception ex) { Status = "Can't turn it: " + ex.Message; return; }
            _main.NotifyRomModified($"Lufia II: {p.Added} turned block{(p.Added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}" +
                                    (p.NewTiles > 0 ? $" and {p.NewTiles} tile{(p.NewTiles == 1 ? "" : "s")} to tile set {_edit.TileSetIndex}" : ""));
            _stats.Clear();
            ReloadBlocks();
        }
        var (nw, nh) = L2BlockTurn.Size(w, h, quarters);
        int[] Place(int[] cells, int offset)
        {
            var o = new int[nw * nh];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var (tx, ty) = L2BlockTurn.Point(x, y, w, h, quarters, flip);
                    o[ty * nw + tx] = p.Blocks[offset + y * w + x];
                }
            return o;
        }
        string added = p.Added > 0 ? $" ({p.Added} turned block{(p.Added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}" +
                                     (p.NewTiles > 0 ? $", {p.NewTiles} new tile{(p.NewTiles == 1 ? "" : "s")}" : "") + "; Edit, Undo takes them out)" : "";
        if (piece)
        {
            var c = _clip!;
            var turned = new Clip(nw, nh, Place(c.Ground, 0), Place(c.Upper, c.W * c.H), L2BlockTurn.TurnFlags(c.Flags, c.W, c.H, quarters, flip),
                                  c.BlockSet, c.TileSet, c.SourceMap, c.PaletteSet);
            StartStamp(turned);
            Status = $"The piece is {what}{added}, walls and all. Click to place it; Q turns, F / D flip; Esc stops.";
            return;
        }
        _brush = Place(_brush, 0);
        BrushW = nw; BrushH = nh;
        SelectedBlock = _brush[0];
        BrushChanged();
        Status = $"The brush is {what}{added}. Q turns it again, F / D flip it.";
    }
}
