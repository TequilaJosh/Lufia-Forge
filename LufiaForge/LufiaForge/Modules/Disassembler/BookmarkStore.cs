using System.IO;
using System.Text.Json;

namespace LufiaForge.Modules.Disassembler;

// keeps address bookmarks (labels) in a file next to the ROM: <rom-filename>.lfbookmarks.json
// they go on top of the disassembly by themselves: any decoded address with a bookmark shows its label as a comment
public sealed class BookmarkStore
{
    private readonly Dictionary<int, Bookmark> _bookmarks = new();
    private string? _filePath;

    // load / save

    // ties the store to a ROM file path and loads the bookmarks already there
    public void Open(string romFilePath)
    {
        _filePath = Path.ChangeExtension(romFilePath, null) + ".lfbookmarks.json";
        _bookmarks.Clear();

        if (!File.Exists(_filePath)) return;

        try
        {
            string json = File.ReadAllText(_filePath);
            var list    = JsonSerializer.Deserialize<List<Bookmark>>(json);
            if (list == null) return;
            foreach (var bookmark in list)
                _bookmarks[bookmark.SnesAddress] = bookmark;
        }
        catch
        {
            // broken file, just start fresh instead of crashing
        }
    }

    public void Save()
    {
        if (_filePath == null) return;
        try
        {
            string json = JsonSerializer.Serialize(_bookmarks.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { /* not worth crashing over */ }
    }

    // add / remove / look up

    public void Add(int snesAddress, string label, string? comment = null, string colorHex = "#C8942A")
    {
        // a bookmark put on a line marked as data keeps the data mark (it was lost, and the line turned back into code)
        int dataLength = _bookmarks.TryGetValue(snesAddress, out var existing) ? existing.DataLength : 0;
        _bookmarks[snesAddress] = new Bookmark
        {
            SnesAddress = snesAddress,
            Label       = label,
            Comment     = comment,
            ColorHex    = colorHex,
            DataLength  = dataLength,
        };
        _regions = null;
        Save();
    }

    public void Remove(int snesAddress)
    {
        _bookmarks.Remove(snesAddress);
        _regions = null;
        Save();
    }

    public Bookmark? GetByAddress(int snesAddress)
        => _bookmarks.TryGetValue(snesAddress, out var bookmark) ? bookmark : null;

    public IReadOnlyList<Bookmark> GetAll()
        => _bookmarks.Values.OrderBy(b => b.SnesAddress).ToList();

    public bool HasAddress(int snesAddress)
        => _bookmarks.ContainsKey(snesAddress);

    // data regions

    private List<(int Start, int End, int Snes)>? _regions;

    private List<(int Start, int End, int Snes)> Regions => _regions ??= _bookmarks.Values
        .Where(b => b.DataLength > 0)
        .Select(b => { int fileOffset = LufiaForge.Core.Lufia1Constants.SnesAddressToFileOffset(b.SnesAddress); return (fileOffset, fileOffset + b.DataLength, b.SnesAddress); })
        .Where(r => r.Item1 >= 0)
        .OrderBy(r => r.Item1).ToList();

    // the data region (file offsets, end exclusive) that has fileOffset in it, or null
    public (int Start, int End)? DataRegionAtFile(int fileOffset)
    {
        foreach (var region in Regions)
        {
            if (region.Start > fileOffset) break;
            if (fileOffset < region.End) return (region.Start, region.End);
        }
        return null;
    }

    // marks bytes as data. joins a region that ends right before them or starts right after them
    public void MarkData(int snesAddress, int length)
    {
        int fileOffset = LufiaForge.Core.Lufia1Constants.SnesAddressToFileOffset(snesAddress);
        if (fileOffset < 0 || length <= 0) return;
        var before = Regions.FirstOrDefault(r => r.End == fileOffset);
        var after = Regions.FirstOrDefault(r => r.Start == fileOffset + length);
        Bookmark target;
        if (before.End == fileOffset && before.End != 0 && _bookmarks.TryGetValue(before.Snes, out var prev))
        {
            prev.DataLength += length;
            target = prev;
        }
        else
        {
            target = new Bookmark { SnesAddress = snesAddress, Label = $"DATA_{snesAddress:X6}", Comment = "Marked as data", ColorHex = "#C0392B", DataLength = length };
            _bookmarks[snesAddress] = target;
        }
        if (after.Start == fileOffset + length && after.End != 0 && _bookmarks.TryGetValue(after.Snes, out var next) && next != target)
        {
            target.DataLength += next.DataLength;
            _bookmarks.Remove(after.Snes);
        }
        _regions = null;
        Save();
    }

    // labels in the listing

    // puts the bookmark labels on decoded lines as inline comments: a line whose SNES address has a bookmark
    // gets the label in front of its comment
    public void AnnotateLines(IEnumerable<DisassemblyLine> lines)
    {
        foreach (var line in lines)
        {
            if (!_bookmarks.TryGetValue(line.SnesAddress, out var bookmark)) continue;

            string bookmarkComment = $"; {bookmark.Label}";
            if (!string.IsNullOrEmpty(bookmark.Comment))
                bookmarkComment += $"  ({bookmark.Comment})";

            // label goes first, the comment already there stays (like the REP/SEP note)
            line.Comment = string.IsNullOrEmpty(line.Comment)
                ? bookmarkComment
                : $"{bookmarkComment}  {line.Comment}";
        }
    }
}
