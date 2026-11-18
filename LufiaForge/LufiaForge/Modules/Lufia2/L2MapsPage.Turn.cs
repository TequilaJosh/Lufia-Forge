using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

// turning the brush (one block or several) or the piece being placed (a preset, a pasted copy, a piece of a map):
// a quarter clockwise (Q), flipped across (F) or up/down (D). the turned blocks are blocks of the map's block set,
// found if the set has them, else added, with quarter-turned tiles added to the tile set (L2BlockTurn). a piece's
// walls turn with it
public partial class L2MapsPage
{
    [RelayCommand] private void RotateBrush() => Turn(1, false, "turned a quarter clockwise");
    [RelayCommand] private void FlipBrushAcross() => Turn(0, true, "flipped across");
    [RelayCommand] private void FlipBrushUpDown() => Turn(2, true, "flipped up/down");

    private void Turn(int quarters, bool flip, string what)
    {
        if (_rom == null || _edit == null || _main == null) return;
        if (_world != null) { Status = "World map blocks can't be turned (big blocks are fixed pictures)."; return; }
        bool isPiece = Stamping && _clip != null;
        if (isPiece && (_clip!.BlockSet != _edit.BlockSetIndex || _clip.TileSet != _edit.TileSetIndex))
        { Status = "This piece is on other graphics: place it once (its blocks come in), then turn it."; return; }
        int w = isPiece ? _clip!.W : BrushW, h = isPiece ? _clip!.H : BrushH;
        var ids = isPiece ? _clip!.Ground.Concat(_clip.Upper).ToList() : _brush.ToList();
        L2BlockTurn.Prepared prepared;
        try { prepared = L2BlockTurn.Prepare(_rom, _edit.MapId, _edit.BlockSetIndex, _edit.TileSetIndex, ids, quarters, flip); }
        catch (Exception ex) { Status = "Can't turn it: " + ex.Message; return; }
        if (prepared.Added > 0)
        {
            // new blocks: write the map's edits first, so Edit > Undo takes the blocks out while nothing uses them yet
            if (WritePendingEdits() is { } why) { Status = "The map couldn't be written first: " + why; return; }
            try { L2Rom.AllOrNothing(_rom, () => { prepared.Write(); return 0; }); }
            catch (Exception ex) { Status = "Can't turn it: " + ex.Message; return; }
            _main.NotifyRomModified($"Lufia II: {prepared.Added} turned block{(prepared.Added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}" +
                                    (prepared.NewTiles > 0 ? $" and {prepared.NewTiles} tile{(prepared.NewTiles == 1 ? "" : "s")} to tile set {_edit.TileSetIndex}" : ""));
            _stats.Clear();
            ReloadBlocks();
        }
        var (newW, newH) = L2BlockTurn.Size(w, h, quarters);
        int[] Place(int[] cells, int offset)
        {
            var turnedCells = new int[newW * newH];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var (tx, ty) = L2BlockTurn.Point(x, y, w, h, quarters, flip);
                    turnedCells[ty * newW + tx] = prepared.Blocks[offset + y * w + x];
                }
            return turnedCells;
        }
        string added = prepared.Added > 0 ? $" ({prepared.Added} turned block{(prepared.Added == 1 ? "" : "s")} added to block set {_edit.BlockSetIndex}" +
                                     (prepared.NewTiles > 0 ? $", {prepared.NewTiles} new tile{(prepared.NewTiles == 1 ? "" : "s")}" : "") + "; Edit, Undo takes them out)" : "";
        if (isPiece)
        {
            var clip = _clip!;
            var turned = new Clip(newW, newH, Place(clip.Ground, 0), Place(clip.Upper, clip.W * clip.H), L2BlockTurn.TurnFlags(clip.Flags, clip.W, clip.H, quarters, flip),
                                  clip.BlockSet, clip.TileSet, clip.SourceMap, clip.PaletteSet);
            StartStamp(turned);
            Status = $"The piece is {what}{added}, walls and all. Click to place it; Q turns, F / D flip; Esc stops.";
            return;
        }
        _brush = Place(_brush, 0);
        BrushW = newW; BrushH = newH;
        SelectedBlock = _brush[0];
        BrushChanged();
        Status = $"The brush is {what}{added}. Q turns it again, F / D flip it.";
    }
}
